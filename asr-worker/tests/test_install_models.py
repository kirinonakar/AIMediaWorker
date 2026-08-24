from __future__ import annotations

import io
import json
import sys
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import install_models


class InstallModelsTests(unittest.TestCase):
    def test_installer_checks_both_models_and_emits_completion(self) -> None:
        downloaded: list[str] = []

        def fake_download(reference: str, progress) -> str:
            downloaded.append(reference)
            progress(1.0, 100, 100)
            return reference

        output = io.StringIO()
        with patch.object(install_models, "download_model", fake_download), redirect_stdout(output):
            self.assertEqual(0, install_models.main())

        events = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual([install_models.DEFAULT_ASR_MODEL, install_models.DEFAULT_FORCED_ALIGNER_MODEL], downloaded)
        self.assertEqual("complete", events[-1]["kind"])


if __name__ == "__main__":
    unittest.main()
