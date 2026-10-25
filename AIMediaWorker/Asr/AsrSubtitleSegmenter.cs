using AIMediaWorker.Subtitle;

namespace AIMediaWorker.Asr;

/// <summary>
/// Converts the timestamped ASR output into readable subtitle cues. CrispASR can
/// return a whole sentence group as one segment, so the application must apply
/// the user's subtitle segmentation settings after transcription.
/// </summary>
public static class AsrSubtitleSegmenter
{
    private const string OpeningPunctuation = "([{<「『【《（〔〈［｛";
    private const string JapaneseNonStarters = "ぁぃぅぇぉっゃゅょゎゕゖァィゥェォッャュョヮヵヶーゝゞヽヾ々〻\u3099\u309a";
    private const int MaximumShortJapaneseWordCharacters = 8;
    // Native transcription/forced alignment runs in audio windows of at most 30 seconds.
    private const long MaximumPrefixAlignmentGapMicroseconds = 30_000_000;

    public static IReadOnlyList<AsrSegment> Segment(
        IReadOnlyList<AsrSegment> segments,
        AsrSegmentationOptions? options)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (options is null) return segments.ToArray();

        var normalized = Normalize(options);
        var result = new List<AsrSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text)) continue;

            var words = segment.Words?
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .ToArray();
            if (words is { Length: > 0 })
                result.AddRange(SegmentWords(segment, words, normalized));
            else
                result.AddRange(SegmentText(segment, normalized));
        }

        return MergeDanglingSegments(result, normalized);
    }

    private static IReadOnlyList<AsrSegment> MergeDanglingSegments(
        IReadOnlyList<AsrSegment> segments, NormalizedOptions options)
    {
        if (segments.Count < 2) return segments;

        var result = new List<AsrSegment>(segments.Count);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            // A single kana can be the start of the following word (こ / の先),
            // so resolve that direction before considering a backward tail join.
            if (index + 1 < segments.Count && IsJapanesePrefixFragment(segment, segments[index + 1], options))
                segment = JoinPrefixSegments(segment, segments[++index], options);

            if (result.Count == 0 ||
                (!IsDanglingSegmentText(segment.Text) && !IsJapaneseContinuationFragment(result[^1], segment, options)))
            {
                result.Add(segment);
                continue;
            }

            result[^1] = JoinSegments(result[^1], segment);
        }

        return result;
    }

    internal static bool HasUnfinishedJapaneseText(string text) =>
        LiveCaptionDisplaySegmenter.IsJapaneseText(null, text) && !IsSentenceBoundary(text);

    internal static bool IsJapanesePrefixFragmentText(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 1 && IsHiragana(trimmed[0]) && !JapaneseNonStarters.Contains(trimmed[0]);
    }

    private static bool IsJapanesePrefixFragment(AsrSegment previous, AsrSegment next, NormalizedOptions options)
    {
        if (!IsJapanesePrefixFragmentText(previous.Text)) return false;
        var nextText = next.Text.TrimStart();
        if (nextText.Length == 0 || !IsHiragana(nextText[0])) return false;
        if (CanJoinJapaneseFragments(previous, next, options, isTeFormTail: false)) return true;

        // A brief こ before の… is a broken この, even when the aligner places
        // the first kana several seconds early. Judge the following speech's
        // duration separately from that gap. JoinPrefixSegments repairs the
        // displaced prefix time instead of displaying the sentence across it.
        return IsJapaneseDemonstrativePrefix(previous.Text.Trim()[0], nextText) &&
               previous.EndMicroseconds - previous.StartMicroseconds <= options.MinimumCueMicroseconds &&
               next.StartMicroseconds >= previous.StartMicroseconds &&
               next.StartMicroseconds - previous.EndMicroseconds <= MaximumPrefixAlignmentGapMicroseconds &&
               next.EndMicroseconds - next.StartMicroseconds <= SentenceCompletionMicroseconds(options) &&
               CountCharacters(AppendText(previous.Text, next.Text)) <= options.MaximumCharacters;
    }

    private static bool IsJapaneseDemonstrativePrefix(char prefix, string next) =>
        (prefix is 'こ' or 'そ' or 'あ' or 'ど') &&
        (next[0] is 'の' or 'れ' ||
         ((prefix is 'こ' or 'そ' or 'ど') && next[0] == 'こ') ||
         next.StartsWith("ちら", StringComparison.Ordinal) ||
         next.StartsWith("んな", StringComparison.Ordinal) ||
         next.StartsWith("っち", StringComparison.Ordinal));

    private static AsrSegment JoinPrefixSegments(AsrSegment previous, AsrSegment next, NormalizedOptions options)
    {
        var gap = next.StartMicroseconds - previous.EndMicroseconds;
        if (gap > Math.Max(options.SilenceSplitMicroseconds, options.MinimumCueMicroseconds))
        {
            // Only the long-gap demonstrative repair reaches this branch. Its
            // isolated kana is a timing outlier, not evidence of an early cue.
            // Estimate its position immediately before the following speech.
            var duration = Math.Clamp(previous.EndMicroseconds - previous.StartMicroseconds, 1, options.MinimumCueMicroseconds);
            var end = next.StartMicroseconds;
            var start = Math.Max(0, end - duration);
            previous = previous with
            {
                StartMicroseconds = start,
                EndMicroseconds = end,
                Words = previous.Words?.Select(word => word with
                {
                    StartMicroseconds = Math.Clamp(start + (word.StartMicroseconds - previous.StartMicroseconds), start, end - 1),
                    EndMicroseconds = Math.Clamp(start + (word.EndMicroseconds - previous.StartMicroseconds), start + 1, end)
                }).ToArray()
            };
        }
        return JoinSegments(previous, next);
    }

    private static AsrSegment JoinSegments(AsrSegment previous, AsrSegment next) => new()
    {
        StartMicroseconds = previous.StartMicroseconds,
        EndMicroseconds = Math.Max(previous.EndMicroseconds, next.EndMicroseconds),
        Text = AppendText(previous.Text, next.Text),
        Confidence = CombineConfidence(previous, next),
        Words = CombineWords(previous, next)
    };

    private static bool IsJapaneseContinuationFragment(
        AsrSegment previous, AsrSegment next, NormalizedOptions options)
    {
        // Native ASR segments can end inside an inflection, e.g. 悪くな / って / る。
        // Only repair short kana tails of an unfinished, nearby Japanese cue.
        if (!HasUnfinishedJapaneseText(previous.Text)) return false;
        var previousText = previous.Text.TrimEnd();
        if (previousText.Length == 0 || !IsNoSpaceCjk(previousText[^1])) return false;

        var kanaCount = 0;
        foreach (var character in next.Text)
        {
            if (char.IsWhiteSpace(character) || char.IsPunctuation(character)) continue;
            if (!IsHiragana(character) && !JapaneseNonStarters.Contains(character)) return false;
            if (++kanaCount > 3) return false;
        }
        if (kanaCount == 0) return false;

        var tail = string.Concat(next.Text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));
        var isTeFormTail = (previousText[^1] is 'て' or 'で') &&
            (tail is "る" or "いる" or "た" or "いた" or "ます" or "います" or "ない" or "いない");
        // A brief sentence-final kana tail can be any inflection or adjective
        // ending, not just a te-form (e.g. い / い。 or わから / ない。).
        var isShortFinalTail = IsSentenceBoundary(next.Text) &&
            next.EndMicroseconds - next.StartMicroseconds <= options.MinimumCueMicroseconds;
        return CanJoinJapaneseFragments(previous, next, options, isTeFormTail, isShortFinalTail);
    }

    private static bool CanJoinJapaneseFragments(
        AsrSegment previous, AsrSegment next, NormalizedOptions options, bool isTeFormTail, bool isShortFinalTail = false)
    {
        var text = AppendText(previous.Text, next.Text);
        var characterCount = CountCharacters(text);
        var maximumGap = Math.Max(options.SilenceSplitMicroseconds, options.MinimumCueMicroseconds);
        var maximumDuration = options.MaximumCueMicroseconds;
        if (isTeFormTail || isShortFinalTail)
        {
            // Tolerate alignment gaps within one cue. A short final kana tail
            // may overrun the time budget by at most the minimum cue duration;
            // text length still has to fit the configured character limit.
            maximumGap = Math.Max(maximumGap, options.MaximumCueMicroseconds);
            if (isShortFinalTail || (characterCount <= MaximumShortJapaneseWordCharacters &&
                !text.Any(character => char.IsWhiteSpace(character) || SubtitlePunctuation.IsComma(character))))
                maximumDuration = SentenceCompletionMicroseconds(options);
        }
        return next.StartMicroseconds >= previous.StartMicroseconds &&
               next.StartMicroseconds - previous.EndMicroseconds <= maximumGap &&
               Math.Max(previous.EndMicroseconds, next.EndMicroseconds) - previous.StartMicroseconds <= maximumDuration &&
               characterCount <= options.MaximumCharacters;
    }

    private static long SentenceCompletionMicroseconds(NormalizedOptions options) =>
        options.MaximumCueMicroseconds > long.MaxValue - options.MinimumCueMicroseconds
            ? long.MaxValue : options.MaximumCueMicroseconds + options.MinimumCueMicroseconds;

    private static bool IsDanglingSegmentText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;

        var hasNonPunctuation = false;
        foreach (var character in trimmed)
        {
            if (char.IsWhiteSpace(character) || char.IsPunctuation(character) || char.IsSymbol(character)) continue;
            if (!JapaneseNonStarters.Contains(character)) return false;
            hasNonPunctuation = true;
        }

        return hasNonPunctuation || trimmed.All(character =>
            char.IsWhiteSpace(character) || char.IsPunctuation(character) || char.IsSymbol(character));
    }

    private static double? CombineConfidence(AsrSegment first, AsrSegment second)
    {
        if (first.Confidence is null) return second.Confidence;
        if (second.Confidence is null) return first.Confidence;

        var firstDuration = Math.Max(1L, first.EndMicroseconds - first.StartMicroseconds);
        var secondDuration = Math.Max(1L, second.EndMicroseconds - second.StartMicroseconds);
        return (first.Confidence.Value * firstDuration + second.Confidence.Value * secondDuration) /
               (firstDuration + secondDuration);
    }

    private static IReadOnlyList<AsrWord>? CombineWords(AsrSegment first, AsrSegment second)
    {
        if (first.Words is null && second.Words is null) return null;
        // Keep alignment coverage for both sides when only one was aligned;
        // otherwise re-segmentation would assign the missing text the next
        // word's timestamp and could undo a prefix timing correction.
        return WordsOrSegment(first).Concat(WordsOrSegment(second)).ToArray();
    }

    private static IReadOnlyList<AsrWord> WordsOrSegment(AsrSegment segment) =>
        segment.Words is { Count: > 0 } words ? words :
        [new AsrWord { Text = segment.Text, StartMicroseconds = segment.StartMicroseconds, EndMicroseconds = segment.EndMicroseconds }];

    private static IReadOnlyList<AsrSegment> SegmentWords(
        AsrSegment source,
        IReadOnlyList<AsrWord> words,
        NormalizedOptions options)
    {
        var sourceTexts = TryAttachSourceText(source.Text, words);
        // If alignment tokens cannot be mapped, keep the transcript (including
        // its spacing) rather than reconstructing text from tokenizer fragments.
        if (sourceTexts is null)
            return SegmentText(source, options);

        var tokens = new List<WordToken>(words.Count);
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var text = sourceTexts[index];
            if (text.Length == 0) continue;

            var start = Math.Max(source.StartMicroseconds, word.StartMicroseconds);
            var end = Math.Max(start + 1, word.EndMicroseconds);
            tokens.Add(new WordToken(text, start, end, word));
        }

        if (tokens.Count == 0) return SegmentText(source, options);

        var result = new List<AsrSegment>();
        var current = new List<WordToken>();
        foreach (var token in tokens)
        {
            if (current.Count > 0 && ShouldBreakBefore(current, token, options))
            {
                AddCue(result, source, current);
                current.Clear();
            }

            current.Add(token);
            var duration = token.EndMicroseconds - current[0].StartMicroseconds;
            if (IsSentenceBoundary(token.Text) || duration >= options.MaximumCueMicroseconds)
            {
                AddCue(result, source, current);
                current.Clear();
            }
        }

        if (current.Count > 0) AddCue(result, source, current);
        return result;
    }

    private static bool ShouldBreakBefore(
        IReadOnlyList<WordToken> current,
        WordToken next,
        NormalizedOptions options)
    {
        var previous = current[^1];
        var candidateText = ComposeText(current.Select(token => token.Text).Append(next.Text));
        var duration = next.EndMicroseconds - current[0].StartMicroseconds;
        var currentDuration = previous.EndMicroseconds - current[0].StartMicroseconds;

        if (duration > options.MaximumCueMicroseconds) return true;
        if (CountCharacters(candidateText) > options.MaximumCharacters) return true;

        // Aligner "words" may be individual kana rather than linguistic words.
        // A speed threshold or a noisy alignment gap must not split an inflection
        // while the complete cue still fits the duration and character limits.
        if (IsJapaneseContinuationBoundary(previous.Text, next.Text)) return false;

        var hasSilenceGap = next.StartMicroseconds - previous.EndMicroseconds >= options.SilenceSplitMicroseconds;
        if (hasSilenceGap && currentDuration >= options.MinimumCueMicroseconds) return true;

        if (current.Count > 1 && duration > 0)
        {
            var charactersPerSecond = CountCharacters(candidateText) / (duration / 1_000_000d);
            if (charactersPerSecond > options.MaximumCharactersPerSecond && currentDuration >= options.MinimumCueMicroseconds)
                return true;
        }

        return false;
    }

    private static bool IsJapaneseContinuationBoundary(string previous, string next) =>
        previous.Length > 0 && next.Length > 0 &&
        IsNoSpaceCjk(previous[^1]) &&
        (IsHiragana(next[0]) || JapaneseNonStarters.Contains(next[0]));

    private static bool IsHiragana(char character) => character is >= '\u3041' and <= '\u3096';

    private static void AddCue(List<AsrSegment> result, AsrSegment source, IReadOnlyList<WordToken> tokens)
    {
        if (tokens.Count == 0) return;
        var text = ComposeText(tokens.Select(token => token.Text));
        if (text.Length == 0) return;

        var start = Math.Max(0, tokens[0].StartMicroseconds);
        var end = Math.Max(start + 1, tokens[^1].EndMicroseconds);
        result.Add(new AsrSegment
        {
            StartMicroseconds = start,
            EndMicroseconds = end,
            Text = text,
            Confidence = source.Confidence,
            Words = tokens.Select(token => token.Word).ToArray()
        });
    }

    private static IReadOnlyList<AsrSegment> SegmentText(AsrSegment source, NormalizedOptions options)
    {
        var text = source.Text.Trim();
        if (text.Length == 0) return [];

        var pieces = SplitSentences(text);
        var duration = Math.Max(1L, source.EndMicroseconds - source.StartMicroseconds);
        var result = new List<AsrSegment>();
        var offset = 0;
        foreach (var piece in pieces)
        {
            var pieceText = piece.Trim();
            if (pieceText.Length == 0)
            {
                offset += piece.Length;
                continue;
            }

            var pieceStart = source.StartMicroseconds + duration * offset / Math.Max(1, text.Length);
            var pieceEnd = source.StartMicroseconds + duration * (offset + piece.Length) / Math.Max(1, text.Length);
            pieceEnd = Math.Max(pieceStart + 1, pieceEnd);
            result.AddRange(SplitLongText(source, pieceText, pieceStart, pieceEnd, options));
            offset += piece.Length;
        }

        return result.Count == 0 ? [source] : result;
    }

    private static IReadOnlyList<AsrSegment> SplitLongText(
        AsrSegment source,
        string text,
        long startMicroseconds,
        long endMicroseconds,
        NormalizedOptions options)
    {
        var duration = Math.Max(1L, endMicroseconds - startMicroseconds);
        // Without alignment, splitting a sentence just over the time limit is
        // arbitrary. Use the same bounded completion allowance as kana repair.
        if (LiveCaptionDisplaySegmenter.IsJapaneseText(null, text) && IsSentenceBoundary(text) &&
            CountCharacters(text) <= options.MaximumCharacters && duration <= SentenceCompletionMicroseconds(options))
            return [CreateTextSegment(source, text, startMicroseconds, endMicroseconds)];

        var durationSeconds = duration / 1_000_000d;
        var partCount = Math.Max(
            1,
            Math.Max(
                (int)Math.Ceiling((double)CountCharacters(text) / options.MaximumCharacters),
                (int)Math.Ceiling(durationSeconds / options.MaximumCueSeconds)));
        if (partCount == 1)
            return [CreateTextSegment(source, text, startMicroseconds, endMicroseconds)];

        var parts = new List<(string Text, int Start, int End)>();
        var cursor = 0;
        while (cursor < text.Length)
        {
            var remaining = text.Length - cursor;
            var partsLeft = Math.Max(1, partCount - parts.Count);
            var targetLength = Math.Max(1, (int)Math.Ceiling((double)remaining / partsLeft));
            var desiredEnd = Math.Min(text.Length, cursor + targetLength);
            var breakAt = FindNaturalBreak(text, cursor, desiredEnd);
            if (breakAt <= cursor) breakAt = desiredEnd;
            parts.Add((text[cursor..breakAt].Trim(), cursor, breakAt));
            cursor = breakAt;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
        }

        var result = new List<AsrSegment>(parts.Count);
        foreach (var part in parts)
        {
            if (part.Text.Length == 0) continue;
            var partStart = startMicroseconds + duration * part.Start / Math.Max(1, text.Length);
            var partEnd = startMicroseconds + duration * part.End / Math.Max(1, text.Length);
            result.Add(CreateTextSegment(source, part.Text, partStart, Math.Max(partStart + 1, partEnd)));
        }
        return result;
    }

    private static AsrSegment CreateTextSegment(AsrSegment source, string text, long startMicroseconds, long endMicroseconds) => new()
    {
        StartMicroseconds = Math.Max(0, startMicroseconds),
        EndMicroseconds = Math.Max(startMicroseconds + 1, endMicroseconds),
        Text = text,
        Confidence = source.Confidence
    };

    private static List<string> SplitSentences(string text)
    {
        var result = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (!IsSentenceTerminator(text[index])) continue;
            var end = index + 1;
            while (end < text.Length &&
                (SubtitlePunctuation.IsSentenceTerminator(text[end]) ||
                 SubtitlePunctuation.IsClosingCharacter(text[end]))) end++;
            result.Add(text[start..end]);
            start = end;
            index = end - 1;
        }

        if (start < text.Length) result.Add(text[start..]);
        return result.Count == 0 ? [text] : result;
    }

    private static int FindNaturalBreak(string text, int start, int desiredEnd)
    {
        if (desiredEnd >= text.Length) return text.Length;
        for (var index = desiredEnd; index > start; index--)
            if (char.IsWhiteSpace(text[index - 1]) || ",，、;；".Contains(text[index - 1]))
                return index;
        return desiredEnd;
    }

    private static string ComposeText(IEnumerable<string> values)
    {
        // Source slices already contain the exact separators between tokens.
        return string.Concat(values).Trim();
    }

    private static string AppendText(string current, string next)
    {
        current = current.TrimEnd();
        next = next.Trim();
        if (current.Length == 0) return next;
        if (next.Length == 0) return current;

        var previousCharacter = current[^1];
        var nextCharacter = next[0];
        if (IsClosingPunctuationCharacter(nextCharacter) || IsSentenceTerminator(nextCharacter) || nextCharacter is '、' or '，' or '､' or ',')
            return current + next;
        if (OpeningPunctuation.Contains(previousCharacter)) return current + next;
        if (IsNoSpaceCjk(previousCharacter) && IsNoSpaceCjk(nextCharacter)) return current + next;
        return IsWordLike(previousCharacter) && IsWordLike(nextCharacter) ? current + " " + next : current + next;
    }

    private static string[]? TryAttachSourceText(string source, IReadOnlyList<AsrWord> words)
    {
        var normalizedSource = source.Trim();
        if (normalizedSource.Length == 0) return null;

        var starts = new int[words.Count];
        var cursor = 0;
        for (var index = 0; index < words.Count; index++)
        {
            var token = words[index].Text.Trim();
            if (token.Length == 0) return null;
            var start = FindToken(normalizedSource, token, cursor);
            if (start < 0) return null;
            starts[index] = start;
            cursor = start + token.Length;
        }

        var result = new string[words.Count];
        for (var index = 0; index < words.Count; index++)
        {
            var end = index + 1 < starts.Length ? starts[index + 1] : normalizedSource.Length;
            if (end <= starts[index]) return null;
            result[index] = normalizedSource[(index == 0 ? 0 : starts[index])..end];
            if (result[index].Length == 0) result[index] = words[index].Text.Trim();
        }
        return result;
    }

    private static int FindToken(string source, string token, int start)
    {
        var direct = source.IndexOf(token, Math.Clamp(start, 0, source.Length), StringComparison.OrdinalIgnoreCase);
        if (direct >= 0) return direct;

        for (var candidate = Math.Clamp(start, 0, source.Length); candidate < source.Length; candidate++)
        {
            var sourceIndex = candidate;
            var tokenIndex = 0;
            while (sourceIndex < source.Length && tokenIndex < token.Length)
            {
                if (char.IsWhiteSpace(source[sourceIndex])) { sourceIndex++; continue; }
                if (char.ToUpperInvariant(source[sourceIndex]) != char.ToUpperInvariant(token[tokenIndex])) break;
                sourceIndex++;
                tokenIndex++;
            }
            if (tokenIndex == token.Length) return candidate;
        }
        return -1;
    }

    private static NormalizedOptions Normalize(AsrSegmentationOptions options)
    {
        var minimumSeconds = PositiveFiniteOrDefault(options.MinimumCueSeconds, 1);
        var maximumSeconds = Math.Max(minimumSeconds, PositiveFiniteOrDefault(options.MaximumCueSeconds, 6));
        var maximumLines = Math.Max(1, options.MaximumLines);
        var targetCharacters = Math.Max(1, options.TargetCharactersPerLine);
        var silenceSeconds = PositiveFiniteOrDefault(options.SilenceSplitSeconds, 0.6);
        var maximumCharactersPerSecond = PositiveFiniteOrDefault(options.MaximumCharactersPerSecond, 20);
        return new NormalizedOptions(
            ToMicroseconds(minimumSeconds),
            ToMicroseconds(maximumSeconds),
            maximumSeconds,
            Math.Max(1, maximumLines * targetCharacters),
            ToMicroseconds(silenceSeconds),
            maximumCharactersPerSecond);
    }

    private static double PositiveFiniteOrDefault(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;

    private static long ToMicroseconds(double seconds)
    {
        var microseconds = seconds * 1_000_000d;
        if (!double.IsFinite(microseconds) || microseconds >= long.MaxValue) return long.MaxValue;
        return Math.Max(1, (long)Math.Round(microseconds));
    }

    private static int CountCharacters(string text) => text.Count(character => !char.IsWhiteSpace(character));

    private static bool IsSentenceBoundary(string text) => SubtitlePunctuation.EndsSentence(text);

    private static bool IsSentenceTerminator(char character) => SubtitlePunctuation.IsSentenceTerminator(character);

    private static bool IsClosingPunctuationCharacter(char character) => SubtitlePunctuation.IsClosingCharacter(character);

    private static bool IsWordLike(char character) => char.IsLetterOrDigit(character);

    private static bool IsNoSpaceCjk(char character) =>
        character is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff';

    private readonly record struct NormalizedOptions(
        long MinimumCueMicroseconds,
        long MaximumCueMicroseconds,
        double MaximumCueSeconds,
        int MaximumCharacters,
        long SilenceSplitMicroseconds,
        double MaximumCharactersPerSecond);

    private sealed record WordToken(string Text, long StartMicroseconds, long EndMicroseconds, AsrWord Word);
}
