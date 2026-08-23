from __future__ import annotations

import tempfile
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from worker import AsrWorker


class StartPositionTests(unittest.TestCase):
    def test_file_transcription_starts_at_requested_media_position(self) -> None:
        worker = AsrWorker()
        events: list[dict] = []
        extraction_starts: list[float] = []
        worker.emit = events.append
        worker.engine = SimpleNamespace(transcribe=lambda *_: SimpleNamespace(text="hello", words=[]))

        with tempfile.NamedTemporaryFile(suffix=".mp4") as source:
            def fake_extract(_path: str, start: float, _duration: float, **_kwargs: object) -> str:
                extraction_starts.append(start)
                handle, path = tempfile.mkstemp(suffix=".wav")
                Path(path).touch()
                import os
                os.close(handle)
                return path

            with patch("worker.probe_duration", return_value=20.0), patch("worker.extract_window", side_effect=fake_extract):
                worker._transcribe_file("job", {
                    "input": source.name,
                    "language": "auto",
                    "vad": False,
                    "chunk_duration": 30,
                    "start_us": 12_000_000,
                }, threading.Event())

        self.assertEqual([12.0], extraction_starts)
        segment = next(event["segment"] for event in events if event["event"] == "segment")
        self.assertGreaterEqual(segment["start_us"], 12_000_000)
        self.assertEqual(1.0, [event for event in events if event["event"] == "progress"][-1]["progress"])

    def test_timestamped_transcription_stays_below_forced_aligner_boundary(self) -> None:
        worker = AsrWorker()
        extraction_durations: list[float] = []
        worker.emit = lambda _event: None
        worker.engine = SimpleNamespace(transcribe=lambda *_: SimpleNamespace(text="hello", words=[]))

        with tempfile.NamedTemporaryFile(suffix=".mp4") as source:
            def fake_extract(_path: str, _start: float, duration: float, **_kwargs: object) -> str:
                extraction_durations.append(duration)
                handle, path = tempfile.mkstemp(suffix=".wav")
                import os
                os.close(handle)
                return path

            with patch("worker.probe_duration", return_value=65.0), patch("worker.extract_window", side_effect=fake_extract):
                worker._transcribe_file("job", {
                    "input": source.name,
                    "language": "English",
                    "vad": False,
                    "timestamps": True,
                    "chunk_duration": 30,
                }, threading.Event())

        self.assertEqual([29.0, 29.0, 7.0], extraction_durations)
        self.assertTrue(all(duration < 30 for duration in extraction_durations))


if __name__ == "__main__":
    unittest.main()
