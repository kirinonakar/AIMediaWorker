from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

from protocol.messages import WordTimestamp


LANGUAGES = {
    "auto": None, "ko": "Korean", "korean": "Korean", "en": "English", "english": "English",
    "ja": "Japanese", "japanese": "Japanese", "zh": "Chinese", "chinese": "Chinese",
}


@dataclass(slots=True)
class Transcription:
    text: str
    words: list[WordTimestamp]


class QwenAsrEngine:
    def __init__(self) -> None:
        self.model: Any | None = None
        self.model_path: str | None = None
        self.aligner_path: str | None = None

    def load(self, model_path: str, aligner_path: str | None, device: str, precision: str) -> None:
        if Path(model_path).is_absolute() and not Path(model_path).exists():
            raise FileNotFoundError(f"ASR model not found: {model_path}")
        if aligner_path and Path(aligner_path).is_absolute() and not Path(aligner_path).exists():
            raise FileNotFoundError(f"Forced aligner model not found: {aligner_path}")
        try:
            import torch
            from qwen_asr import Qwen3ASRModel
        except ImportError as exc:
            raise RuntimeError("qwen-asr and PyTorch are required. Install asr-worker/requirements.txt.") from exc
        actual_device = "cuda:0" if device.lower() in {"auto", "cuda"} and torch.cuda.is_available() else "cpu"
        dtype_map = {
            "float32": torch.float32,
            "float16": torch.float16,
            "bfloat16": torch.bfloat16,
            "auto": torch.bfloat16 if actual_device.startswith("cuda") else torch.float32,
        }
        dtype = dtype_map.get(precision.lower(), dtype_map["auto"])
        kwargs: dict[str, Any] = {"device_map": actual_device, "dtype": dtype, "max_inference_batch_size": 1}
        if aligner_path:
            kwargs["forced_aligner"] = aligner_path
            kwargs["forced_aligner_kwargs"] = {"device_map": actual_device, "dtype": dtype}
        self.model = Qwen3ASRModel.from_pretrained(model_path, **kwargs)
        self.model_path = model_path
        self.aligner_path = aligner_path

    def unload(self) -> None:
        self.model = None
        self.model_path = None
        self.aligner_path = None
        try:
            import torch
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except ImportError:
            pass

    def transcribe(self, audio_path: str, language: str, timestamps: bool = True) -> Transcription:
        if self.model is None:
            raise RuntimeError("ASR model is not loaded")
        canonical_language = LANGUAGES.get(language.strip().lower(), language if language.strip() else None)
        results = self.model.transcribe(
            audio=audio_path,
            language=canonical_language,
            return_time_stamps=bool(timestamps and self.aligner_path),
        )
        if not results:
            return Transcription("", [])
        result = results[0]
        text = str(getattr(result, "text", "")).strip()
        words: list[WordTimestamp] = []
        for item in getattr(result, "time_stamps", None) or []:
            start = float(getattr(item, "start_time", 0.0))
            end = float(getattr(item, "end_time", start))
            token = str(getattr(item, "text", ""))
            if end > start and token:
                words.append(WordTimestamp(round(start * 1_000_000), round(end * 1_000_000), token))
        return Transcription(text, words)
