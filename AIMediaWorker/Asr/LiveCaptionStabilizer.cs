namespace AIMediaWorker.Asr;

public sealed record LiveCaptionUpdate(
    string DisplayText,
    string CommittedText,
    string CommittedDelta,
    string UnstableText,
    bool IsFinal);

/// <summary>
/// Turns overlapping rolling-window ASR results into one continuous transcript.
/// Timestamped words older than the holdback are committed once; the recent tail
/// remains provisional and is replaced by every new inference result.
/// </summary>
public sealed class LiveCaptionStabilizer
{
    public LiveCaptionStabilizer(long holdbackMicroseconds = 2_000_000, string? language = null)
    {
        _holdbackMicroseconds = Math.Max(0, holdbackMicroseconds);
        Language = language;
    }

    private readonly long _holdbackMicroseconds;
    private readonly List<string> _confirmed = [];
    private long _confirmedUntilMicroseconds = -1;
    private string _lastConfirmedTimedText = string.Empty;
    private string _previousWindowText = string.Empty;

    /// <summary>
    /// Optional ASR language hint. The text itself is still inspected so that
    /// auto-detected Japanese is rendered correctly as well.
    /// </summary>
    public string? Language { get; set; }

    public string ConfirmedText => Join(_confirmed);
    public string ProvisionalText { get; private set; } = string.Empty;
    public string DisplayText => MergeWithoutOverlap(ConfirmedText, ProvisionalText);

    public string Update(AsrEvent result) => UpdateState(result).DisplayText;

    /// <summary>
    /// Updates the rolling transcript and exposes only the text that became
    /// stable during this update. Consumers can send <see cref="LiveCaptionUpdate.CommittedDelta"/>
    /// downstream without retranslating the complete rolling hypothesis.
    /// </summary>
    public LiveCaptionUpdate UpdateState(AsrEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var previouslyConfirmed = ConfirmedText;
        var segments = result.Segments ?? (result.Segment is null ? [] : [result.Segment]);
        var units = CreateTimedUnits(segments);
        var isFinal = string.Equals(result.Event, "final", StringComparison.OrdinalIgnoreCase);

        if (units.Count > 0)
        {
            var newestEnd = units.Max(unit => unit.EndMicroseconds);
            var cutoff = isFinal ? long.MaxValue : Math.Max(0, newestEnd - _holdbackMicroseconds);
            foreach (var unit in units)
            {
                if (unit.EndMicroseconds <= _confirmedUntilMicroseconds || unit.EndMicroseconds > cutoff) continue;
                // Aligner boundaries can move slightly between overlapping
                // windows. Do not recommit the boundary word after such jitter.
                if (unit.EndMicroseconds <= _confirmedUntilMicroseconds + 500_000 &&
                    string.Equals(Normalize(unit.Text), _lastConfirmedTimedText, StringComparison.OrdinalIgnoreCase))
                {
                    _confirmedUntilMicroseconds = unit.EndMicroseconds;
                    continue;
                }
                AppendConfirmed(unit.Text);
                _confirmedUntilMicroseconds = unit.EndMicroseconds;
                _lastConfirmedTimedText = Normalize(unit.Text);
            }

            ProvisionalText = Join(units
                .Where(unit => unit.EndMicroseconds > _confirmedUntilMicroseconds)
                .Select(unit => unit.Text));
            if (isFinal)
            {
                AppendConfirmed(ProvisionalText);
                ProvisionalText = string.Empty;
                _confirmedUntilMicroseconds = Math.Max(_confirmedUntilMicroseconds, newestEnd);
            }
        }
        else
        {
            UpdateFromText(result.Text ?? result.Segment?.Text ?? string.Empty, isFinal);
        }

        var confirmed = ConfirmedText;
        var delta = GetAppendedText(previouslyConfirmed, confirmed);
        return new LiveCaptionUpdate(DisplayText, confirmed, delta, ProvisionalText, isFinal);
    }

    public void Reset()
    {
        _confirmed.Clear();
        _confirmedUntilMicroseconds = -1;
        _lastConfirmedTimedText = string.Empty;
        _previousWindowText = string.Empty;
        ProvisionalText = string.Empty;
    }

    private void UpdateFromText(string text, bool isFinal)
    {
        var current = Normalize(text);
        if (current.Length == 0) return;
        if (_previousWindowText.Length > 0)
        {
            var overlap = FindBestSuffixPrefixOverlap(_previousWindowText, current);
            if (overlap.OldStart > 0)
                AppendConfirmed(_previousWindowText[..overlap.OldStart]);
        }

        ProvisionalText = current;
        _previousWindowText = current;
        if (isFinal)
        {
            AppendConfirmed(current);
            ProvisionalText = string.Empty;
        }
    }

    private void AppendConfirmed(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return;
        var existing = Join(_confirmed);
        var overlap = FindBestSuffixPrefixOverlap(existing, normalized);
        var addition = overlap.NewLength > 0 ? normalized[overlap.NewLength..].Trim() : normalized;
        if (addition.Length > 0) _confirmed.Add(addition);
    }

    private static string GetAppendedText(string previous, string current)
    {
        if (current.Length == 0 || string.Equals(previous, current, StringComparison.Ordinal)) return string.Empty;
        if (previous.Length == 0) return current;
        if (current.StartsWith(previous, StringComparison.Ordinal)) return current[previous.Length..].TrimStart();

        // This is only a defensive fallback for a fuzzy boundary correction.
        // Confirmed text is append-only under normal operation.
        var overlap = FindBestSuffixPrefixOverlap(previous, current);
        return overlap.NewLength > 0 ? current[overlap.NewLength..].TrimStart() : current;
    }

    private static IReadOnlyList<TimedText> CreateTimedUnits(IReadOnlyList<AsrSegment> segments)
    {
        var units = new List<TimedText>();
        foreach (var segment in segments.OrderBy(segment => segment.StartMicroseconds))
        {
            if (segment.Words is { Count: > 0 })
            {
                // The aligner may return sub-word tokens. Reconstruct display
                // units from the original segment text and only use word
                // timestamps for the holdback calculation. This preserves the
                // ASR's Korean word spaces and prevents Japanese tokens from
                // turning into "こ ん に ち は".
                var sourceUnits = CreateSourceTimedUnits(segment);
                if (sourceUnits is not null)
                {
                    units.AddRange(sourceUnits);
                    continue;
                }

                units.Add(new TimedText(segment.EndMicroseconds, segment.Text));
            }
            else if (!string.IsNullOrWhiteSpace(segment.Text))
            {
                units.Add(new TimedText(segment.EndMicroseconds, segment.Text));
            }
        }
        return units;
    }

    private static (int OldStart, int NewLength) FindBestSuffixPrefixOverlap(string oldText, string newText)
    {
        if (string.IsNullOrWhiteSpace(oldText) || string.IsNullOrWhiteSpace(newText)) return (0, 0);
        var maximum = Math.Min(oldText.Length, newText.Length);
        var bestLength = 0;
        var bestOldStart = 0;
        var bestScore = 0d;
        for (var length = 4; length <= maximum; length++)
        {
            var oldPart = oldText[^length..];
            var newPart = newText[..length];
            var score = Similarity(oldPart, newPart);
            if (score < 0.72 || score < bestScore) continue;
            bestScore = score;
            bestLength = length;
            bestOldStart = oldText.Length - length;
        }
        return (bestOldStart, bestLength);
    }

    private static double Similarity(string first, string second)
    {
        var a = NormalizeForComparison(first);
        var b = NormalizeForComparison(second);
        if (a.Length == 0 || b.Length == 0) return 0;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return 1d - (double)previous[^1] / Math.Max(a.Length, b.Length);
    }

    private string MergeWithoutOverlap(string confirmed, string provisional)
    {
        if (confirmed.Length == 0) return provisional;
        if (provisional.Length == 0) return confirmed;
        var overlap = FindBestSuffixPrefixOverlap(confirmed, provisional);
        var tail = overlap.NewLength > 0 ? provisional[overlap.NewLength..].Trim() : provisional;
        return tail.Length == 0 ? confirmed : Join([confirmed, tail]);
    }

    private string Normalize(string text) => Join(text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string NormalizeForComparison(string text) =>
        new(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)).Select(char.ToLowerInvariant).ToArray());

    private string Join(IEnumerable<string> values)
    {
        var result = string.Empty;
        foreach (var value in values)
        {
            var normalized = CollapseWhitespace(value);
            if (normalized.Length == 0) continue;
            result = AppendText(result, normalized);
        }

        return result.Trim();
    }

    private string AppendText(string current, string next)
    {
        current = current.TrimEnd();
        next = next.Trim();
        if (current.Length == 0) return next;
        if (next.Length == 0) return current;

        var previousCharacter = current[^1];
        var nextCharacter = next[0];
        if (IsClosingPunctuation(nextCharacter) || IsSentenceTerminator(nextCharacter) || nextCharacter is '、' or '，' or ',')
            return current + next;
        if (IsOpeningPunctuation(previousCharacter)) return current + next;
        if (IsNoSpaceCjk(previousCharacter) && IsNoSpaceCjk(nextCharacter)) return current + next;

        // A comma/semicolon in Korean and Latin text is followed by a normal
        // word space. Japanese uses no space after its punctuation.
        if (IsSentenceTerminator(previousCharacter) && !IsJapaneseContext(current + next))
            return $"{current} {next}";
        if ((previousCharacter is ',' or ';' or ':') && !IsJapaneseContext(current + next))
            return $"{current} {next}";
        return IsWordLike(previousCharacter) && IsWordLike(nextCharacter)
            ? $"{current} {next}"
            : current + next;
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool IsSentenceTerminator(char character) => ".!?。！？…".Contains(character);

    private static bool IsClosingPunctuation(char character) => ")]}>」』】》）〕〉］｝】”’\"'".Contains(character);

    private static bool IsOpeningPunctuation(char character) => "([{<「『【《（〔〈［｛".Contains(character);

    private static bool IsWordLike(char character) => char.IsLetterOrDigit(character);

    private static bool IsNoSpaceCjk(char character) =>
        character is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff';

    private bool IsJapaneseContext(string text) =>
        IsJapaneseLanguage(Language) || text.Any(character => character is >= '\u3040' and <= '\u30ff');

    private static bool IsJapaneseLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        var normalized = language.Trim().ToLowerInvariant();
        return normalized.StartsWith("ja", StringComparison.Ordinal) ||
            normalized.Contains("japanese", StringComparison.Ordinal) ||
            normalized.Contains("日本", StringComparison.Ordinal) ||
            normalized.Contains("일본", StringComparison.Ordinal);
    }

    private static IReadOnlyList<TimedText>? CreateSourceTimedUnits(AsrSegment segment)
    {
        var source = segment.Text.Trim();
        var words = segment.Words?
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .ToArray();
        if (source.Length == 0 || words is not { Length: > 0 }) return null;

        var starts = new int[words.Length];
        var cursor = 0;
        for (var index = 0; index < words.Length; index++)
        {
            var token = words[index].Text.Trim();
            var start = FindToken(source, token, cursor);
            if (token.Length == 0 || start < 0) return null;
            starts[index] = start;
            cursor = start + token.Length;
        }

        var result = new List<TimedText>();
        // Keep punctuation that the aligner does not expose as a word (for
        // example an opening Japanese quote) in the rendered transcript.
        var groupStart = 0;
        for (var index = 1; index < words.Length; index++)
        {
            var gap = source[starts[index - 1]..starts[index]];
            if (!gap.Any(char.IsWhiteSpace)) continue;

            AddSourceUnit(result, source[groupStart..starts[index]], words[index - 1].EndMicroseconds);
            groupStart = starts[index];
        }

        AddSourceUnit(result, source[groupStart..], words[^1].EndMicroseconds);
        return result.Count == 0 ? null : result;
    }

    private static void AddSourceUnit(List<TimedText> result, string text, long endMicroseconds)
    {
        var normalized = text.Trim();
        if (normalized.Length > 0) result.Add(new TimedText(endMicroseconds, normalized));
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

    private sealed record TimedText(long EndMicroseconds, string Text);
}
