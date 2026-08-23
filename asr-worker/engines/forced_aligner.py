from __future__ import annotations

from typing import Any

from engines.model_defaults import DEFAULT_FORCED_ALIGNER_MODEL, resolve_model_reference
from protocol.messages import WordTimestamp


class ForcedAlignerEngine:
    def __init__(self) -> None:
        self.model: Any | None = None

    def load(self, model_path: str | None = None, device: str = "auto", precision: str = "auto") -> None:
        model_reference = resolve_model_reference(model_path, DEFAULT_FORCED_ALIGNER_MODEL, "Forced aligner model")
        try:
            import torch
            from qwen_asr import Qwen3ForcedAligner
        except ImportError as exc:
            raise RuntimeError("qwen-asr and PyTorch are required for forced alignment") from exc
        requested_device = device.lower()
        if requested_device == "cuda" and not torch.cuda.is_available():
            raise RuntimeError("CUDA was requested, but the installed PyTorch build cannot use CUDA.")
        actual_device = "cuda:0" if requested_device in {"auto", "cuda"} and torch.cuda.is_available() else "cpu"
        dtype = torch.bfloat16 if precision.lower() in {"auto", "bfloat16"} and actual_device.startswith("cuda") else torch.float32
        self.model = Qwen3ForcedAligner.from_pretrained(model_reference, device_map=actual_device, dtype=dtype)

    def align(self, audio_path: str, text: str, language: str) -> list[WordTimestamp]:
        if self.model is None:
            raise RuntimeError("Forced aligner model is not loaded")
        results = self.model.align(audio=audio_path, text=text, language=language)
        return [WordTimestamp(round(float(item.start_time) * 1_000_000), round(float(item.end_time) * 1_000_000), str(item.text)) for item in (results[0] if results else [])]
