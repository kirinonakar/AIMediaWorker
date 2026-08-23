from __future__ import annotations

import os
from pathlib import Path


DEFAULT_ASR_MODEL = "Qwen/Qwen3-ASR-1.7B"
DEFAULT_FORCED_ALIGNER_MODEL = "Qwen/Qwen3-ForcedAligner-0.6B"
MODEL_CACHE_DIR = Path(__file__).resolve().parents[1] / "models"


def configure_model_cache() -> Path:
    cache_path = str(MODEL_CACHE_DIR)
    for variable in ("HF_HOME", "HF_HUB_CACHE", "HUGGINGFACE_HUB_CACHE"):
        os.environ[variable] = cache_path
    if "TRANSFORMERS_CACHE" in os.environ:
        os.environ["TRANSFORMERS_CACHE"] = cache_path
    return MODEL_CACHE_DIR


configure_model_cache()


def resolve_model_reference(value: str | None, default: str, description: str) -> str:
    reference = value.strip() if value and value.strip() else default
    path = Path(reference).expanduser()
    if path.is_absolute() and not path.exists():
        raise FileNotFoundError(f"{description} not found: {reference}")
    return reference
