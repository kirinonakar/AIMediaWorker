from __future__ import annotations

import logging
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class SpeechWindow:
    start_seconds: float
    end_seconds: float


class VadEngine:
    def __init__(self, enabled: bool = True) -> None:
        self.enabled = enabled
        self._available: bool | None = None
        self._model = None
        self._get_speech_timestamps = None
        self._read_audio = None

    def speech_windows(self, wav_path: str) -> list[SpeechWindow]:
        if not self.enabled:
            return [SpeechWindow(0.0, float("inf"))]
        try:
            if self._model is None:
                from silero_vad import get_speech_timestamps, load_silero_vad, read_audio
                self._model = load_silero_vad()
                self._get_speech_timestamps = get_speech_timestamps
                self._read_audio = read_audio
            audio = self._read_audio(wav_path, sampling_rate=16000)
            timestamps = self._get_speech_timestamps(audio, self._model, sampling_rate=16000, return_seconds=True)
            self._available = True
            return [SpeechWindow(float(item["start"]), float(item["end"])) for item in timestamps]
        except Exception as exception:
            if self._available is not False:
                logging.warning("Silero VAD is unavailable (%s); continuing without VAD", type(exception).__name__)
            self._available = False
            return [SpeechWindow(0.0, float("inf"))]
