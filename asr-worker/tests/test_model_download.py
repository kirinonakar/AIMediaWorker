from __future__ import annotations

import sys
import tempfile
import time
import types
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from engines import model_download


class ModelDownloadTests(unittest.TestCase):
    def test_local_model_does_not_contact_hugging_face(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            updates: list[tuple[float, int, int]] = []
            result = model_download.download_model(folder, lambda *value: updates.append(value))

        self.assertEqual(folder, result)
        self.assertEqual([(1.0, 0, 0)], updates)

    def test_remote_download_reports_byte_progress(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            cache = Path(folder)
            repository = cache / "models--Test--Model" / "blobs"
            snapshot = cache / "snapshot"
            sibling = types.SimpleNamespace(size=100)

            class FakeApi:
                def model_info(self, reference: str, files_metadata: bool = False) -> object:
                    self.reference = reference
                    self.files_metadata = files_metadata
                    return types.SimpleNamespace(siblings=[sibling])

            def fake_snapshot_download(reference: str, cache_dir: Path) -> str:
                repository.mkdir(parents=True)
                partial = repository / "weights.incomplete"
                partial.write_bytes(b"x" * 40)
                time.sleep(0.35)
                partial.write_bytes(b"x" * 100)
                snapshot.mkdir()
                return str(snapshot)

            fake_hub = types.ModuleType("huggingface_hub")
            fake_hub.HfApi = FakeApi
            fake_hub.snapshot_download = fake_snapshot_download
            updates: list[tuple[float, int, int]] = []
            with patch.dict(sys.modules, {"huggingface_hub": fake_hub}), patch.object(model_download, "MODEL_CACHE_DIR", cache):
                result = model_download.download_model("Test/Model", lambda *value: updates.append(value))

        self.assertEqual(str(snapshot), result)
        self.assertEqual((1.0, 100, 100), updates[-1])
        self.assertTrue(any(0 < value[0] < 1 for value in updates))


if __name__ == "__main__":
    unittest.main()
