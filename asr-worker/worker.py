from __future__ import annotations

import asyncio
import base64
import json
import logging
import os
import sys
import tempfile
import threading
import wave
from dataclasses import asdict
from dataclasses import replace
from pathlib import Path
from urllib.parse import urlparse
from typing import Any, Callable

from audio.ffmpeg_audio import FfmpegCancelled, FfmpegError, extract_window, probe_duration
from audio.vad import SpeechWindow, VadEngine
from engines.forced_aligner import ForcedAlignerEngine
from engines.model_defaults import DEFAULT_ASR_MODEL, DEFAULT_FORCED_ALIGNER_MODEL
from engines.qwen_asr import QwenAsrEngine
from protocol.messages import SubtitleSegment, WordTimestamp, require_string
from subtitle.segmenter import SegmentationOptions, SubtitleSegmenter


class JobCancelled(RuntimeError):
    pass


class AsrWorker:
    def __init__(self) -> None:
        self.engine = QwenAsrEngine()
        self.aligner = ForcedAlignerEngine()
        self.vad = VadEngine(True)
        self.segmenter = SubtitleSegmenter()
        self.jobs: dict[str, tuple[asyncio.Task[Any], threading.Event]] = {}
        self.streams: dict[str, bytearray] = {}
        self.stream_languages: dict[str, str] = {}
        self.stream_partial_sizes: dict[str, int] = {}
        self.stdout_lock = threading.Lock()
        self.shutdown_requested = False
        self.state = "ready"

    async def dispatch(self, request: dict[str, Any]) -> None:
        request_id = request.get("id")
        command = request.get("command")
        if not isinstance(request_id, str) or not request_id:
            self.emit({"id": request_id, "event": "error", "code": "PROTOCOL_ERROR", "message": "A non-empty request id is required."})
            return
        if not isinstance(command, str) or not command:
            self.emit({"id": request_id, "event": "error", "code": "PROTOCOL_ERROR", "message": "A command is required."})
            return
        immediate: dict[str, Callable[[str, dict[str, Any]], Any]] = {
            "initialize": self._initialize,
            "get_status": self._get_status,
            "cancel": self._cancel,
            "start_streaming": self._start_streaming,
            "push_audio": self._push_audio,
            "shutdown": self._shutdown,
        }
        background: dict[str, Callable[[str, dict[str, Any], threading.Event], Any]] = {
            "load_model": self._load_model,
            "unload_model": self._unload_model,
            "transcribe_file": self._transcribe_file,
            "transcribe_audio": self._transcribe_audio,
            "stop_streaming": self._stop_streaming,
            "align": self._align,
        }
        if command in immediate:
            try:
                await immediate[command](request_id, request)
            except Exception as exc:
                self._emit_error(request_id, exc)
            return
        handler = background.get(command)
        if handler is None:
            self.emit({"id": request_id, "event": "error", "code": "PROTOCOL_ERROR", "message": f"Unknown command: {command}"})
            return
        if request_id in self.jobs:
            self.emit({"id": request_id, "event": "error", "code": "PROTOCOL_ERROR", "message": "Duplicate request id."})
            return
        if command == "load_model":
            try:
                self.engine.prepare_runtime(lambda kind, name, value, downloaded, total: self.emit({
                    "id": request_id,
                    "event": "progress",
                    "stage": "loading",
                    "progress": 1.0,
                    "elapsed_seconds": max(0, int(value)),
                    "message": name,
                }))
            except Exception as exc:
                logging.exception("ASR runtime initialization failed")
                self._emit_error(request_id, exc)
                return
        cancel_event = threading.Event()
        task = asyncio.create_task(self._run_background(request_id, request, handler, cancel_event))
        self.jobs[request_id] = (task, cancel_event)

    async def close(self) -> None:
        for _, cancel in self.jobs.values():
            cancel.set()
        tasks = [job[0] for job in self.jobs.values()]
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)
        self.engine.unload()

    def emit(self, message: dict[str, Any]) -> None:
        payload = json.dumps(message, ensure_ascii=True, separators=(",", ":"))
        with self.stdout_lock:
            sys.stdout.write(payload + "\n")
            sys.stdout.flush()

    async def _run_background(self, request_id: str, request: dict[str, Any], handler: Callable[[str, dict[str, Any], threading.Event], Any], cancel_event: threading.Event) -> None:
        previous_state = self.state
        self.state = "busy"
        try:
            await asyncio.to_thread(handler, request_id, request, cancel_event)
        except (JobCancelled, FfmpegCancelled):
            self.emit({"id": request_id, "event": "cancelled"})
        except Exception as exc:
            logging.exception("ASR job %s failed", request_id)
            self._emit_error(request_id, exc)
        finally:
            self.jobs.pop(request_id, None)
            if not self.shutdown_requested:
                self.state = previous_state if previous_state != "busy" else "ready"

    async def _initialize(self, request_id: str, request: dict[str, Any]) -> None:
        self.emit({"id": request_id, "event": "ready", "state": self.state, "protocol_version": 1, "operations": [
            "initialize", "load_model", "unload_model", "transcribe_file", "transcribe_audio", "start_streaming",
            "push_audio", "stop_streaming", "cancel", "get_status", "align", "shutdown",
        ]})

    async def _get_status(self, request_id: str, request: dict[str, Any]) -> None:
        self.emit({"id": request_id, "event": "status", "state": self.state, "model": self.engine.model_path, "aligner": self.engine.aligner_path, "jobs": list(self.jobs)})

    def _load_model(self, request_id: str, request: dict[str, Any], cancel: threading.Event) -> None:
        model_path = request.get("model_path")
        if model_path is not None and not isinstance(model_path, str):
            raise ValueError("'model_path' must be a string or null")
        aligner_path = request.get("aligner_path")
        if aligner_path is not None and not isinstance(aligner_path, str):
            raise ValueError("'aligner_path' must be a string or null")
        self._check_cancel(cancel)
        self.emit({"id": request_id, "event": "progress", "stage": "download", "progress": 0.0, "message": "Qwen3-ASR-1.7B", "downloaded_bytes": 0, "total_bytes": 0})

        def report_model_progress(kind: str, name: str, value: float, downloaded: int, total: int) -> None:
            if kind == "loading":
                self.emit({"id": request_id, "event": "progress", "stage": "loading", "progress": 1.0, "elapsed_seconds": max(0, int(value)), "message": name})
                return
            base, weight = (0.0, 0.72) if kind == "asr" else (0.72, 0.28)
            self.emit({"id": request_id, "event": "progress", "stage": "download", "progress": base + weight * value, "model_progress": value, "message": name, "downloaded_bytes": downloaded, "total_bytes": total})

        self.engine.load(model_path or DEFAULT_ASR_MODEL, aligner_path or DEFAULT_FORCED_ALIGNER_MODEL, str(request.get("device", "auto")), str(request.get("precision", "auto")), report_model_progress)
        self._check_cancel(cancel)
        self.emit({"id": request_id, "event": "completed"})

    def _unload_model(self, request_id: str, request: dict[str, Any], cancel: threading.Event) -> None:
        self.engine.unload()
        self.emit({"id": request_id, "event": "completed"})

    def _transcribe_file(self, request_id: str, request: dict[str, Any], cancel: threading.Event) -> None:
        source = require_string(request, "input")
        is_remote = urlparse(source).scheme.lower() in {"http", "https"}
        if not is_remote and not Path(source).exists():
            raise FileNotFoundError(f"Input media not found: {source}")
        language = str(request.get("language", "auto"))
        timestamps = bool(request.get("timestamps", True))
        chunk_duration = min(180.0, max(5.0, float(request.get("chunk_duration", 30.0))))
        use_vad = bool(request.get("vad", True))
        options = request.get("segmentation") or {}
        segmenter = SubtitleSegmenter(SegmentationOptions(
            minimum_duration_seconds=float(options.get("minimum_cue_seconds", 1.0)),
            maximum_duration_seconds=float(options.get("maximum_cue_seconds", 6.0)),
            maximum_lines=int(options.get("maximum_lines", 2)),
            target_characters_per_line=int(options.get("target_characters_per_line", 24)),
            silence_split_seconds=float(options.get("silence_split_seconds", 0.6)),
            maximum_characters_per_second=float(options.get("maximum_characters_per_second", 20.0)),
        ))
        duration = probe_duration(source)
        start_seconds = min(duration, max(0.0, int(request.get("start_us", 0)) / 1_000_000))
        remaining_duration = max(0.0, duration - start_seconds)
        offset = start_seconds
        emitted_end = round(start_seconds * 1_000_000)
        while offset < duration:
            self._check_cancel(cancel)
            window_duration = min(chunk_duration, duration - offset)
            chunk_path = extract_window(source, offset, window_duration, cancel_event=cancel)
            try:
                windows = self.vad.speech_windows(chunk_path) if use_vad else [SpeechWindow(0.0, window_duration)]
                if not windows:
                    offset += window_duration
                    completed = offset - start_seconds
                    self.emit({"id": request_id, "event": "progress", "progress": min(1.0, completed / remaining_duration) if remaining_duration > 0 else 1.0})
                    continue
                for speech in windows:
                    self._check_cancel(cancel)
                    local_start = max(0.0, speech.start_seconds)
                    local_end = min(window_duration, speech.end_seconds)
                    if local_end <= local_start:
                        continue
                    speech_path = chunk_path
                    cleanup_speech = False
                    if local_start > 0.01 or local_end < window_duration - 0.01:
                        speech_path = extract_window(chunk_path, local_start, local_end - local_start, cancel_event=cancel)
                        cleanup_speech = True
                    try:
                        transcription = self.engine.transcribe(speech_path, language, timestamps)
                    finally:
                        if cleanup_speech:
                            Path(speech_path).unlink(missing_ok=True)
                    global_offset = round((offset + local_start) * 1_000_000)
                    words = [replace(word, start_us=word.start_us + global_offset, end_us=word.end_us + global_offset) for word in transcription.words]
                    fallback_end = round((offset + local_end) * 1_000_000)
                    segments = segmenter.segment(words, transcription.text, global_offset, fallback_end)
                    for segment in segments:
                        segment.start_us = max(emitted_end, segment.start_us)
                        segment.end_us = max(segment.start_us + 1, segment.end_us)
                        emitted_end = segment.end_us
                        self.emit({"id": request_id, "event": "segment", "segment": segment.to_dict()})
            finally:
                Path(chunk_path).unlink(missing_ok=True)
            offset += window_duration
            completed = offset - start_seconds
            self.emit({"id": request_id, "event": "progress", "progress": min(1.0, completed / remaining_duration) if remaining_duration > 0 else 1.0})
        self.emit({"id": request_id, "event": "completed"})

    def _transcribe_audio(self, request_id: str, request: dict[str, Any], cancel: threading.Event) -> None:
        audio = require_string(request, "audio_base64")
        raw = base64.b64decode(audio, validate=True)
        path = self._write_pcm_wav(raw, int(request.get("sample_rate", 16000)), int(request.get("channels", 1)))
        try:
            result = self.engine.transcribe(path, str(request.get("language", "auto")), bool(request.get("timestamps", True)))
            words = result.words
            for segment in self.segmenter.segment(words, result.text, 0, self._wav_duration_us(path)):
                self.emit({"id": request_id, "event": "segment", "segment": segment.to_dict()})
            self.emit({"id": request_id, "event": "completed"})
        finally:
            Path(path).unlink(missing_ok=True)

    async def _start_streaming(self, request_id: str, request: dict[str, Any]) -> None:
        stream_id = str(request.get("stream_id") or request_id)
        if stream_id in self.streams:
            raise ValueError("stream already exists")
        self.streams[stream_id] = bytearray()
        self.stream_languages[stream_id] = str(request.get("language", "auto"))
        self.stream_partial_sizes[stream_id] = 0
        self.emit({"id": request_id, "event": "completed", "stream_id": stream_id})

    async def _push_audio(self, request_id: str, request: dict[str, Any]) -> None:
        stream_id = require_string(request, "stream_id")
        if stream_id not in self.streams:
            raise ValueError("stream not found")
        data = base64.b64decode(require_string(request, "audio_base64"), validate=True)
        buffer = self.streams[stream_id]
        maximum = 16000 * 2 * 30
        if len(buffer) + len(data) > maximum:
            del buffer[: len(buffer) + len(data) - maximum]
        buffer.extend(data)
        if self.engine.model is not None and len(buffer) >= 16_000 * 2 * 3 and len(buffer) - self.stream_partial_sizes.get(stream_id, 0) >= 16_000 * 2 * 2:
            snapshot = bytes(buffer)
            self.stream_partial_sizes[stream_id] = len(buffer)
            text = await asyncio.to_thread(self._transcribe_pcm_snapshot, snapshot, self.stream_languages.get(stream_id, "auto"))
            if text:
                self.emit({"id": request_id, "event": "partial", "text": text})
        self.emit({"id": request_id, "event": "completed", "buffered_bytes": len(buffer)})

    def _stop_streaming(self, request_id: str, request: dict[str, Any], cancel: threading.Event) -> None:
        stream_id = require_string(request, "stream_id")
        raw = bytes(self.streams.pop(stream_id, bytearray()))
        self.stream_partial_sizes.pop(stream_id, None)
        language = self.stream_languages.pop(stream_id, str(request.get("language", "auto")))
        if not raw:
            self.emit({"id": request_id, "event": "completed"})
            return
        path = self._write_pcm_wav(raw, int(request.get("sample_rate", 16000)), int(request.get("channels", 1)))
        try:
            result = self.engine.transcribe(path, language, False)
            duration_us = self._wav_duration_us(path)
            self.emit({"id": request_id, "event": "final", "start_us": 0, "end_us": duration_us, "text": result.text})
            self.emit({"id": request_id, "event": "completed"})
        finally:
            Path(path).unlink(missing_ok=True)

    def _align(self, request_id: str, request: dict[str, Any], cancel: threading.Event) -> None:
        audio = require_string(request, "input")
        text = require_string(request, "text")
        model_path = request.get("aligner_path")
        if model_path is not None and not isinstance(model_path, str):
            raise ValueError("'aligner_path' must be a string or null")
        self.aligner.load(model_path, str(request.get("device", "auto")), str(request.get("precision", "auto")))
        words = self.aligner.align(audio, text, str(request.get("language", "English")))
        self.emit({"id": request_id, "event": "alignment", "words": [asdict(word) for word in words]})
        self.emit({"id": request_id, "event": "completed"})

    async def _cancel(self, request_id: str, request: dict[str, Any]) -> None:
        target_id = require_string(request, "target_id")
        job = self.jobs.get(target_id)
        if job:
            job[1].set()
        self.emit({"id": request_id, "event": "cancelled", "target_id": target_id})

    async def _shutdown(self, request_id: str, request: dict[str, Any]) -> None:
        self.shutdown_requested = True
        for _, cancel in self.jobs.values():
            cancel.set()
        self.emit({"id": request_id, "event": "completed"})

    @staticmethod
    def _write_pcm_wav(raw: bytes, sample_rate: int, channels: int) -> str:
        descriptor, path = tempfile.mkstemp(prefix="aimw-stream-", suffix=".wav")
        os.close(descriptor)
        with wave.open(path, "wb") as output:
            output.setnchannels(max(1, channels))
            output.setsampwidth(2)
            output.setframerate(max(8000, sample_rate))
            output.writeframes(raw)
        return path

    def _transcribe_pcm_snapshot(self, raw: bytes, language: str) -> str:
        path = self._write_pcm_wav(raw, 16_000, 1)
        try:
            return self.engine.transcribe(path, language, False).text
        finally:
            Path(path).unlink(missing_ok=True)

    @staticmethod
    def _wav_duration_us(path: str) -> int:
        with wave.open(path, "rb") as source:
            return round(source.getnframes() / source.getframerate() * 1_000_000)

    @staticmethod
    def _check_cancel(cancel: threading.Event) -> None:
        if cancel.is_set():
            raise JobCancelled("job cancelled")

    def _emit_error(self, request_id: str | None, exc: Exception) -> None:
        if isinstance(exc, FileNotFoundError):
            code = "MODEL_NOT_FOUND" if "model" in str(exc).lower() else "FFMPEG_ERROR"
        elif isinstance(exc, FfmpegError):
            code = "FFMPEG_ERROR"
        elif "out of memory" in str(exc).lower():
            code = "CUDA_OOM"
        elif isinstance(exc, (ValueError, json.JSONDecodeError)):
            code = "PROTOCOL_ERROR"
        else:
            code = "ASR_ERROR"
        self.emit({"id": request_id, "event": "error", "code": code, "message": str(exc)})
