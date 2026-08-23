from __future__ import annotations

import re
from dataclasses import dataclass

from protocol.messages import SubtitleSegment, WordTimestamp


@dataclass(frozen=True, slots=True)
class SegmentationOptions:
    minimum_duration_seconds: float = 1.0
    maximum_duration_seconds: float = 6.0
    maximum_lines: int = 2
    target_characters_per_line: int = 24
    silence_split_seconds: float = 0.6
    maximum_characters_per_second: float = 20.0


class SubtitleSegmenter:
    def __init__(self, options: SegmentationOptions | None = None) -> None:
        self.options = options or SegmentationOptions()

    def segment(self, words: list[WordTimestamp], fallback_text: str = "", fallback_start_us: int = 0, fallback_end_us: int = 0) -> list[SubtitleSegment]:
        if not words:
            text = fallback_text.strip()
            if not text:
                return []
            return self._segment_fallback(text, fallback_start_us, fallback_end_us)
        segments: list[SubtitleSegment] = []
        current: list[WordTimestamp] = []
        max_chars = self.options.maximum_lines * self.options.target_characters_per_line
        words = self._restore_text_boundaries(words, fallback_text)
        words = self._expand_long_words(words, max_chars)
        for word in words:
            if not current:
                current.append(word)
                continue
            candidate_text = self._join(current + [word])
            duration = (word.end_us - current[0].start_us) / 1_000_000
            silence = (word.start_us - current[-1].end_us) / 1_000_000
            sentence_end = bool(re.search(r"[.!?。！？…]$", current[-1].text.strip()))
            characters_per_second = len(candidate_text.replace("\n", "")) / max(duration, 0.001)
            should_split = duration > self.options.maximum_duration_seconds or len(candidate_text) > max_chars or characters_per_second > self.options.maximum_characters_per_second or silence >= self.options.silence_split_seconds or sentence_end and duration >= self.options.minimum_duration_seconds
            if should_split:
                segments.append(self._make(current))
                current = [word]
            else:
                current.append(word)
        if current:
            segments.append(self._make(current))
        return segments

    def _make(self, words: list[WordTimestamp]) -> SubtitleSegment:
        start = words[0].start_us
        natural_end = words[-1].end_us
        minimum_end = start + round(self.options.minimum_duration_seconds * 1_000_000)
        text = self._join(words)
        readable_end = start + round(len(text.replace("\n", "")) / max(0.1, self.options.maximum_characters_per_second) * 1_000_000)
        maximum_end = start + round(self.options.maximum_duration_seconds * 1_000_000)
        return SubtitleSegment(start, min(maximum_end, max(natural_end, minimum_end, readable_end)), self._wrap(text), words=words.copy())

    def _segment_fallback(self, text: str, start_us: int, end_us: int) -> list[SubtitleSegment]:
        maximum_characters = max(1, self.options.maximum_lines * self.options.target_characters_per_line)
        chunks = [text[index:index + maximum_characters] for index in range(0, len(text), maximum_characters)]
        total_duration = max(1, end_us - start_us)
        results: list[SubtitleSegment] = []
        for index, chunk in enumerate(chunks):
            start = start_us + round(total_duration * index / len(chunks))
            natural_end = start_us + round(total_duration * (index + 1) / len(chunks))
            readable_end = start + round(len(chunk) / max(0.1, self.options.maximum_characters_per_second) * 1_000_000)
            maximum_end = start + round(self.options.maximum_duration_seconds * 1_000_000)
            end = min(maximum_end, max(natural_end, start + round(self.options.minimum_duration_seconds * 1_000_000), readable_end))
            results.append(SubtitleSegment(start, max(start + 1, end), self._wrap(chunk)))
        return results

    @staticmethod
    def _restore_text_boundaries(words: list[WordTimestamp], fallback_text: str) -> list[WordTimestamp]:
        source = fallback_text.strip()
        if not source or not words:
            return words
        matches: list[tuple[int, int]] = []
        cursor = 0
        for word in words:
            token = word.text.strip()
            if not token:
                return words
            start = source.find(token, cursor)
            if start < 0:
                return words
            end = start + len(token)
            matches.append((start, end))
            cursor = end

        restored: list[WordTimestamp] = []
        for index, word in enumerate(words):
            start = 0 if index == 0 else matches[index][0]
            end = matches[index + 1][0] if index + 1 < len(matches) else len(source)
            restored.append(WordTimestamp(word.start_us, word.end_us, source[start:end]))
        return restored

    @staticmethod
    def _expand_long_words(words: list[WordTimestamp], maximum_characters: int) -> list[WordTimestamp]:
        expanded: list[WordTimestamp] = []
        maximum_characters = max(1, maximum_characters)
        for word in words:
            if len(word.text) <= maximum_characters:
                expanded.append(word)
                continue
            chunks = [word.text[index:index + maximum_characters] for index in range(0, len(word.text), maximum_characters)]
            duration = max(1, word.end_us - word.start_us)
            for index, chunk in enumerate(chunks):
                start = word.start_us + round(duration * index / len(chunks))
                end = word.start_us + round(duration * (index + 1) / len(chunks))
                expanded.append(WordTimestamp(start, max(start + 1, end), chunk))
        return expanded

    @staticmethod
    def _join(words: list[WordTimestamp]) -> str:
        raw_tokens = [word.text for word in words if word.text.strip()]
        if not raw_tokens:
            return ""
        tokens = [token.strip() for token in raw_tokens]
        text = tokens[0]
        for index, token in enumerate(tokens[1:], start=1):
            previous = tokens[index - 1]
            explicit_space = raw_tokens[index - 1][-1:].isspace() or raw_tokens[index][:1].isspace()
            if SubtitleSegmenter._needs_space(previous, token, explicit_space):
                text += " "
            text += token
        return text

    @staticmethod
    def _needs_space(previous: str, current: str, explicit_space: bool) -> bool:
        if not previous or not current:
            return False
        left, right = previous[-1], current[0]
        no_space_before = ",.!?;:%)]}>，。！？：；、…"
        no_space_after = "([{<“‘"
        word_joiners = "'’-/"
        if right in no_space_before or right in word_joiners:
            return False
        if left in no_space_after or left in word_joiners:
            return False
        if explicit_space:
            return True
        if left.isascii() and left.isalnum() and right.isascii() and right.isalnum():
            return True
        return left in no_space_before and right.isascii() and right.isalnum()

    def _wrap(self, text: str) -> str:
        target = self.options.target_characters_per_line
        if len(text) <= target:
            return text
        words = text.split()
        if len(words) == 1:
            return "\n".join(text[index:index + target] for index in range(0, len(text), target))
        lines: list[str] = []
        line = ""
        for word in words:
            candidate = f"{line} {word}".strip()
            if line and len(candidate) > target and len(lines) + 1 < self.options.maximum_lines:
                lines.append(line)
                line = word
            else:
                line = candidate
        if line:
            lines.append(line)
        return "\n".join(lines)
