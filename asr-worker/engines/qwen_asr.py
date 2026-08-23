from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from engines.model_defaults import DEFAULT_ASR_MODEL, DEFAULT_FORCED_ALIGNER_MODEL, resolve_model_reference
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

    def load(self, model_path: str | None = None, aligner_path: str | None = None, device: str = "auto", precision: str = "auto") -> None:
        model_reference = resolve_model_reference(model_path, DEFAULT_ASR_MODEL, "ASR model")
        aligner_reference = resolve_model_reference(aligner_path, DEFAULT_FORCED_ALIGNER_MODEL, "Forced aligner model")
        try:
            import torch
            from qwen_asr import Qwen3ASRModel
        except ImportError as exc:
            raise RuntimeError("qwen-asr and PyTorch are required. Install asr-worker/requirements.txt.") from exc
        requested_device = device.lower()
        if requested_device == "cuda" and not torch.cuda.is_available():
            raise RuntimeError("CUDA was requested, but the installed PyTorch build cannot use CUDA.")
        actual_device = "cuda:0" if requested_device in {"auto", "cuda"} and torch.cuda.is_available() else "cpu"
        dtype_map = {
            "float32": torch.float32,
            "float16": torch.float16,
            "bfloat16": torch.bfloat16,
            "auto": torch.bfloat16 if actual_device.startswith("cuda") else torch.float32,
        }
        dtype = dtype_map.get(precision.lower(), dtype_map["auto"])
        kwargs: dict[str, Any] = {"device_map": actual_device, "dtype": dtype, "max_inference_batch_size": 1}
        kwargs["forced_aligner"] = aligner_reference
        kwargs["forced_aligner_kwargs"] = {"device_map": actual_device, "dtype": dtype}
        self.model = Qwen3ASRModel.from_pretrained(model_reference, **kwargs)
        self.model_path = model_reference
        self.aligner_path = aligner_reference

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
