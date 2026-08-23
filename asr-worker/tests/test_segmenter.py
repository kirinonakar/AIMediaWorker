from __future__ import annotations

import sys
import unittest
import threading
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from protocol.messages import WordTimestamp
from subtitle.segmenter import SegmentationOptions, SubtitleSegmenter
from audio.ffmpeg_audio import FfmpegCancelled, extract_window


class SubtitleSegmenterTests(unittest.TestCase):
    def test_audio_extraction_honors_pre_cancelled_job(self) -> None:
        cancelled = threading.Event()
        cancelled.set()
        with self.assertRaises(FfmpegCancelled):
            extract_window("does-not-need-to-exist", 0, 30, cancel_event=cancelled)

    def test_long_unspaced_text_is_never_truncated(self) -> None:
        text = "가나다라마바사아자차카타파하" * 6
        segmenter = SubtitleSegmenter(SegmentationOptions(maximum_lines=2, target_characters_per_line=12))
        segments = segmenter.segment([WordTimestamp(0, 6_000_000, text)])
        self.assertEqual(text, "".join(segment.text.replace("\n", "") for segment in segments))
        self.assertTrue(all(len(segment.text.splitlines()) <= 2 for segment in segments))

    def test_fallback_text_is_split_without_loss(self) -> None:
        text = "日本語字幕テスト" * 20
        segmenter = SubtitleSegmenter(SegmentationOptions(maximum_lines=2, target_characters_per_line=10))
        segments = segmenter.segment([], text, 0, 10_000_000)
        self.assertEqual(text, "".join(segment.text.replace("\n", "") for segment in segments))
        self.assertGreater(len(segments), 1)


if __name__ == "__main__":
    unittest.main()
