using AIMediaWorker.Subtitle;

namespace AIMediaWorker.Asr;

/// <summary>
/// Splits the continuously accumulated live transcript into stable display
/// units. Japanese ASR frequently omits sentence punctuation, so timed ASR
/// segment boundaries and bounded clause chunks are used as fallbacks.
/// </summary>
public static class LiveCaptionDisplaySegmenter
{
    private const int MinimumJapaneseChunkCharacters = 14;
    private const int MaximumJapaneseChunkCharacters = 32;

    public static IReadOnlyList<string> Split(
        string text,
        string? language = null,
        IReadOnlyList<AsrSegment>? currentSegments = null)
    {
        var normalized = CollapseWhitespace(text);
        if (normalized.Length == 0) return [];

        var segmentBoundaries = FindSegmentBoundaries(normalized, currentSegments);
        var sentences = new List<string>();
        var japanese = IsJapaneseText(language, normalized);
        var start = 0;
        for (var index = 0; index < normalized.Length; index++)
        {
            var sentenceEnd = SubtitlePunctuation.IsSentenceTerminator(normalized[index]);
            var commaBoundary = japanese && SubtitlePunctuation.IsComma(normalized[index]);
            if (!sentenceEnd && !commaBoundary) continue;
            while (index + 1 < normalized.Length &&
                (sentenceEnd && SubtitlePunctuation.IsSentenceTerminator(normalized[index + 1]) ||
                 commaBoundary && SubtitlePunctuation.IsComma(normalized[index + 1]) ||
                 SubtitlePunctuation.IsClosingCharacter(normalized[index + 1]))) index++;
            AddDisplayUnit(sentences, normalized[start..(index + 1)], start, language, segmentBoundaries);
            start = index + 1;
        }

        if (start < normalized.Length)
            AddDisplayUnit(sentences, normalized[start..], start, language, segmentBoundaries);
        return sentences;
    }

    private static void AddDisplayUnit(
        List<string> output,
        string value,
        int absoluteStart,
        string? language,
        IReadOnlySet<int> segmentBoundaries)
    {
        var leadingWhitespace = value.Length - value.TrimStart().Length;
        var unit = value.Trim();
        if (unit.Length == 0) return;
        if (!IsJapaneseText(language, unit))
        {
            output.Add(unit);
            return;
        }

        var unitAbsoluteStart = absoluteStart + leadingWhitespace;
        var localStart = 0;
        foreach (var boundary in segmentBoundaries
            .Where(boundary => boundary > unitAbsoluteStart && boundary < unitAbsoluteStart + unit.Length)
            .Order())
        {
            var localBoundary = boundary - unitAbsoluteStart;
            AddBoundedJapaneseChunks(output, unit[localStart..localBoundary]);
            localStart = localBoundary;
        }
        AddBoundedJapaneseChunks(output, unit[localStart..]);
    }

    private static void AddBoundedJapaneseChunks(List<string> output, string value)
    {
        var unit = value.Trim();
        var start = 0;
        while (unit.Length - start > MaximumJapaneseChunkCharacters)
        {
            var minimum = start + MinimumJapaneseChunkCharacters;
            var maximum = start + MaximumJapaneseChunkCharacters;
            var split = FindPreferredBoundary(unit, minimum, maximum);
            output.Add(unit[start..split].Trim());
            start = split;
            while (start < unit.Length && char.IsWhiteSpace(unit[start])) start++;
        }
        if (start < unit.Length) output.Add(unit[start..].Trim());
    }

    private static int FindPreferredBoundary(string text, int minimum, int maximum)
    {
        for (var index = maximum; index >= minimum; index--)
        {
            if (index < text.Length && SubtitlePunctuation.IsClauseTerminator(text[index - 1])) return index;
        }
        return maximum;
    }

    private static IReadOnlySet<int> FindSegmentBoundaries(string text, IReadOnlyList<AsrSegment>? segments)
    {
        var boundaries = new HashSet<int>();
        if (segments is not { Count: > 1 }) return boundaries;

        var cursor = 0;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            var segment = CollapseWhitespace(segments[index].Text);
            if (segment.Length == 0) continue;
            var match = text.IndexOf(segment, cursor, StringComparison.Ordinal);
            if (match < 0) continue;
            cursor = match + segment.Length;
            boundaries.Add(cursor);
        }
        return boundaries;
    }

    public static bool IsJapaneseText(string? language, string text)
    {
        if (!string.IsNullOrWhiteSpace(language))
        {
            var normalized = language.Trim().ToLowerInvariant();
            if (normalized.StartsWith("ja", StringComparison.Ordinal) ||
                normalized.Contains("japanese", StringComparison.Ordinal) ||
                normalized.Contains("日本", StringComparison.Ordinal) ||
                normalized.Contains("일본", StringComparison.Ordinal)) return true;
        }
        if (text.Any(character => character is >= '\u3040' and <= '\u30ff' || "、。｡「」『』".Contains(character)))
            return true;
        return !text.Any(char.IsWhiteSpace) &&
            text.Any(character => character is >= '\u3400' and <= '\u9fff') &&
            text.Any(SubtitlePunctuation.IsComma);
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
