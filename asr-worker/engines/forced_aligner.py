from __future__ import annotations

from pathlib import Path
from typing import Any

from protocol.messages import WordTimestamp


class ForcedAlignerEngine:
    def __init__(self) -> None:
        self.model: Any | None = None

    def load(self, model_path: str, device: str = "auto", precision: str = "auto") -> None:
        if Path(model_path).is_absolute() and not Path(model_path).exists():
            raise FileNotFoundError(f"Forced aligner model not found: {model_path}")
        try:
            import torch
            from qwen_asr import Qwen3ForcedAligner
        except ImportError as exc:
            raise RuntimeError("qwen-asr and PyTorch are required for forced alignment") from exc
        actual_device = "cuda:0" if device.lower() in {"auto", "cuda"} and torch.cuda.is_available() else "cpu"
        dtype = torch.bfloat16 if precision.lower() in {"auto", "bfloat16"} and actual_device.startswith("cuda") else torch.float32
        self.model = Qwen3ForcedAligner.from_pretrained(model_path, device_map=actual_device, dtype=dtype)

    def align(self, audio_path: str, text: str, language: str) -> list[WordTimestamp]:
        if self.model is None:
            raise RuntimeError("Forced aligner model is not loaded")
        results = self.model.align(audio=audio_path, text=text, language=language)
        return [WordTimestamp(round(float(item.start_time) * 1_000_000), round(float(item.end_time) * 1_000_000), str(item.text)) for item in (results[0] if results else [])]
