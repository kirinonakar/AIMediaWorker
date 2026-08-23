from __future__ import annotations

import io
import json
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from worker import AsrWorker


class Utf8ProtocolTests(unittest.TestCase):
    def test_worker_protocol_preserves_korean_text_on_any_code_page(self) -> None:
        output = io.StringIO()
        worker = AsrWorker()
        with patch("sys.stdout", output):
            worker.emit({"id": "utf8", "event": "segment", "text": "안녕하세요, 한글 자막입니다."})

        payload = output.getvalue().strip()
        payload.encode("ascii")
        self.assertEqual("안녕하세요, 한글 자막입니다.", json.loads(payload)["text"])


if __name__ == "__main__":
    unittest.main()
