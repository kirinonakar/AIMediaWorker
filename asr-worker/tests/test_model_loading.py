from __future__ import annotations

import os
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from engines.forced_aligner import ForcedAlignerEngine
from engines.model_defaults import DEFAULT_ASR_MODEL, DEFAULT_FORCED_ALIGNER_MODEL, MODEL_CACHE_DIR, resolve_model_reference
from engines.qwen_asr import QwenAsrEngine


class _FakeCuda:
    def __init__(self, available: bool = False) -> None:
        self.available = available

    def is_available(self) -> bool:
        return self.available

    @staticmethod
    def empty_cache() -> None:
        pass


def _fake_torch(cuda_available: bool = False) -> types.ModuleType:
    module = types.ModuleType("torch")
    module.cuda = _FakeCuda(cuda_available)
    module.float32 = "float32"
    module.float16 = "float16"
    module.bfloat16 = "bfloat16"
    return module


class ModelLoadingTests(unittest.TestCase):
    def test_model_cache_is_inside_asr_worker(self) -> None:
        expected = Path(__file__).resolve().parents[1] / "models"
        self.assertEqual(expected, MODEL_CACHE_DIR)
        for variable in ("HF_HOME", "HF_HUB_CACHE", "HUGGINGFACE_HUB_CACHE"):
            self.assertEqual(str(expected), os.environ[variable])

    def test_asr_engine_uses_downloadable_default_model_ids(self) -> None:
        calls: list[tuple[str, dict[str, object]]] = []

        class FakeAsrModel:
            @staticmethod
            def from_pretrained(model_id: str, **kwargs: object) -> object:
                calls.append((model_id, kwargs))
                return object()

        qwen_asr = types.ModuleType("qwen_asr")
        qwen_asr.Qwen3ASRModel = FakeAsrModel
        with patch.dict(sys.modules, {"torch": _fake_torch(), "qwen_asr": qwen_asr}):
            engine = QwenAsrEngine()
            engine.load()

        self.assertEqual(DEFAULT_ASR_MODEL, calls[0][0])
        self.assertEqual(DEFAULT_FORCED_ALIGNER_MODEL, calls[0][1]["forced_aligner"])
        self.assertEqual(DEFAULT_ASR_MODEL, engine.model_path)
        self.assertEqual(DEFAULT_FORCED_ALIGNER_MODEL, engine.aligner_path)

    def test_forced_aligner_uses_downloadable_default_model_id(self) -> None:
        calls: list[tuple[str, dict[str, object]]] = []

        class FakeForcedAligner:
            @staticmethod
            def from_pretrained(model_id: str, **kwargs: object) -> object:
                calls.append((model_id, kwargs))
                return object()

        qwen_asr = types.ModuleType("qwen_asr")
        qwen_asr.Qwen3ForcedAligner = FakeForcedAligner
        with patch.dict(sys.modules, {"torch": _fake_torch(), "qwen_asr": qwen_asr}):
            ForcedAlignerEngine().load()

        self.assertEqual(DEFAULT_FORCED_ALIGNER_MODEL, calls[0][0])

    def test_explicit_cuda_does_not_silently_fall_back_to_cpu(self) -> None:
        qwen_asr = types.ModuleType("qwen_asr")
        qwen_asr.Qwen3ASRModel = object
        with patch.dict(sys.modules, {"torch": _fake_torch(), "qwen_asr": qwen_asr}):
            with self.assertRaisesRegex(RuntimeError, "cannot use CUDA"):
                QwenAsrEngine().load(device="cuda")

    def test_auto_device_uses_cuda_when_available(self) -> None:
        calls: list[dict[str, object]] = []

        class FakeAsrModel:
            @staticmethod
            def from_pretrained(model_id: str, **kwargs: object) -> object:
                calls.append(kwargs)
                return object()

        qwen_asr = types.ModuleType("qwen_asr")
        qwen_asr.Qwen3ASRModel = FakeAsrModel
        with patch.dict(sys.modules, {"torch": _fake_torch(cuda_available=True), "qwen_asr": qwen_asr}):
            QwenAsrEngine().load(device="auto")

        self.assertEqual("cuda:0", calls[0]["device_map"])
        self.assertEqual("bfloat16", calls[0]["dtype"])

    def test_missing_absolute_model_path_still_fails_without_downloading(self) -> None:
        missing = str(Path(tempfile.gettempdir(), "aimw-missing-model", "model").resolve())
        with self.assertRaises(FileNotFoundError):
            resolve_model_reference(missing, DEFAULT_ASR_MODEL, "ASR model")


if __name__ == "__main__":
    unittest.main()
