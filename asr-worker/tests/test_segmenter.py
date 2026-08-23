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

    def test_english_word_tokens_restore_spaces_without_spacing_punctuation(self) -> None:
        words = [
            WordTimestamp(0, 400_000, "Hello"),
            WordTimestamp(400_000, 800_000, "world"),
            WordTimestamp(900_000, 1_200_000, "this"),
            WordTimestamp(1_200_000, 1_400_000, "is"),
            WordTimestamp(1_400_000, 1_800_000, "English"),
        ]
        segmenter = SubtitleSegmenter(SegmentationOptions(maximum_duration_seconds=10, target_characters_per_line=100, maximum_characters_per_second=100))

        segments = segmenter.segment(words, "Hello world, this is English.")

        self.assertEqual("Hello world, this is English.", "".join(segment.text.replace("\n", "") for segment in segments))

    def test_korean_morpheme_tokens_restore_original_word_spacing(self) -> None:
        words = [
            WordTimestamp(0, 300_000, "안녕"),
            WordTimestamp(300_000, 700_000, "하세요"),
            WordTimestamp(700_000, 1_100_000, "여러분"),
        ]
        segmenter = SubtitleSegmenter(SegmentationOptions(maximum_characters_per_second=100))

        segments = segmenter.segment(words, "안녕하세요 여러분")

        self.assertEqual("안녕하세요 여러분", "".join(segment.text.replace("\n", "") for segment in segments))

    def test_cjk_character_tokens_remain_unspaced(self) -> None:
        words = [WordTimestamp(0, 300_000, "日"), WordTimestamp(300_000, 600_000, "本"), WordTimestamp(600_000, 900_000, "語")]
        segmenter = SubtitleSegmenter(SegmentationOptions(maximum_characters_per_second=100))

        self.assertEqual("日本語", segmenter.segment(words)[0].text)


if __name__ == "__main__":
    unittest.main()
