from __future__ import annotations

import json
import os
import subprocess
import tempfile
import threading
from pathlib import Path


class FfmpegError(RuntimeError):
    pass


class FfmpegCancelled(RuntimeError):
    pass


def probe_duration(path: str, ffprobe: str = "ffprobe") -> float:
    network_options = ["-rw_timeout", "30000000"] if path.lower().startswith(("http://", "https://")) else []
    process = subprocess.run(
        [ffprobe, "-v", "error", *network_options, "-show_entries", "format=duration", "-of", "json", path],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    if process.returncode != 0:
        raise FfmpegError(process.stderr.strip() or "ffprobe could not read the media duration")
    try:
        duration = float(json.loads(process.stdout)["format"]["duration"])
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
        raise FfmpegError("ffprobe did not return a valid media duration") from exc
    if duration <= 0:
        raise FfmpegError("media duration is not positive")
    return duration


def extract_window(path: str, start_seconds: float, duration_seconds: float, ffmpeg: str = "ffmpeg", cancel_event: threading.Event | None = None) -> str:
    if cancel_event is not None and cancel_event.is_set():
        raise FfmpegCancelled("FFmpeg extraction was cancelled")
    descriptor, output = tempfile.mkstemp(prefix="aimw-audio-", suffix=".wav")
    os.close(descriptor)
    command = [
        ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", f"{max(0.0, start_seconds):.6f}",
        *(["-rw_timeout", "30000000"] if path.lower().startswith(("http://", "https://")) else []), "-i", path, "-t", f"{max(0.01, duration_seconds):.6f}", "-vn", "-ac", "1", "-ar", "16000",
        "-c:a", "pcm_s16le", "-y", output,
    ]
    try:
        process = subprocess.Popen(
            command,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
    except OSError as exception:
        Path(output).unlink(missing_ok=True)
        raise FfmpegError(f"FFmpeg could not start: {exception}") from exception
    while True:
        if cancel_event is not None and cancel_event.is_set():
            process.kill()
            process.communicate()
            Path(output).unlink(missing_ok=True)
            raise FfmpegCancelled("FFmpeg extraction was cancelled")
        try:
            _, stderr = process.communicate(timeout=0.2)
            break
        except subprocess.TimeoutExpired:
            continue
    if process.returncode != 0:
        Path(output).unlink(missing_ok=True)
        raise FfmpegError(stderr.strip() or "ffmpeg audio extraction failed")
    return output
