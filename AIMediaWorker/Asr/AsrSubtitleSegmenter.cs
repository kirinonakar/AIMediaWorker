namespace AIMediaWorker.Asr;

/// <summary>
/// Converts the timestamped ASR output into readable subtitle cues. CrispASR can
/// return a whole sentence group as one segment, so the application must apply
/// the user's subtitle segmentation settings after transcription.
/// </summary>
public static class AsrSubtitleSegmenter
{
    private const string SentenceTerminators = ".!?。！？…";
    private const string ClosingPunctuation = ")]}>」』】》）〕〉］｝】”’\"'";
    private const string OpeningPunctuation = "([{<「『【《（〔〈［｛";

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

        return result;
    }

    private static IReadOnlyList<AsrSegment> SegmentWords(
        AsrSegment source,
        IReadOnlyList<AsrWord> words,
        NormalizedOptions options)
    {
        var sourceTexts = TryAttachSourceText(source.Text, words);
        // Some aligner/tokenizer versions omit punctuation or use token markers
        // that cannot be mapped back to the transcript. In that case, retain the
        // transcript text and its sentence boundaries instead of emitting one
        // punctuation-free mega-cue.
        if (sourceTexts is null && source.Text.Any(IsSentenceTerminator))
            return SegmentText(source, options);

        var tokens = new List<WordToken>(words.Count);
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var text = sourceTexts?[index] ?? word.Text.Trim();
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
        var currentText = ComposeText(current.Select(token => token.Text));
        var candidateText = AppendText(currentText, next.Text);
        var duration = next.EndMicroseconds - current[0].StartMicroseconds;
        var currentDuration = previous.EndMicroseconds - current[0].StartMicroseconds;

        if (duration > options.MaximumCueMicroseconds) return true;
        if (CountCharacters(candidateText) > options.MaximumCharacters) return true;

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
            while (end < text.Length && ClosingPunctuation.Contains(text[end])) end++;
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
        var result = string.Empty;
        foreach (var value in values) result = AppendText(result, value);
        return result.Trim();
    }

    private static string AppendText(string current, string next)
    {
        current = current.TrimEnd();
        next = next.Trim();
        if (current.Length == 0) return next;
        if (next.Length == 0) return current;

        var previousCharacter = current[^1];
        var nextCharacter = next[0];
        if (IsClosingPunctuationCharacter(nextCharacter) || IsSentenceTerminator(nextCharacter) || nextCharacter is '、' or '，' or ',')
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
            result[index] = normalizedSource[starts[index]..end].Trim();
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

    private static bool IsSentenceBoundary(string text)
    {
        for (var index = text.Length - 1; index >= 0; index--)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character)) continue;
            if (ClosingPunctuation.Contains(character)) continue;
            return IsSentenceTerminator(character);
        }
        return false;
    }

    private static bool IsSentenceTerminator(char character) => SentenceTerminators.Contains(character);

    private static bool IsClosingPunctuationCharacter(char character) => ClosingPunctuation.Contains(character);

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
