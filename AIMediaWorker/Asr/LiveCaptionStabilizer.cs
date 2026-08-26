namespace AIMediaWorker.Asr;

/// <summary>
/// Turns overlapping rolling-window ASR results into one continuous transcript.
/// Timestamped words older than the holdback are committed once; the recent tail
/// remains provisional and is replaced by every new inference result.
/// </summary>
public sealed class LiveCaptionStabilizer(long holdbackMicroseconds = 2_000_000)
{
    private readonly long _holdbackMicroseconds = Math.Max(0, holdbackMicroseconds);
    private readonly List<string> _confirmed = [];
    private long _confirmedUntilMicroseconds = -1;
    private string _lastConfirmedTimedText = string.Empty;
    private string _previousWindowText = string.Empty;

    public string ConfirmedText => Join(_confirmed);
    public string ProvisionalText { get; private set; } = string.Empty;
    public string DisplayText => MergeWithoutOverlap(ConfirmedText, ProvisionalText);

    public string Update(AsrEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);
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

        return DisplayText;
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

    private static IReadOnlyList<TimedText> CreateTimedUnits(IReadOnlyList<AsrSegment> segments)
    {
        var units = new List<TimedText>();
        foreach (var segment in segments.OrderBy(segment => segment.StartMicroseconds))
        {
            if (segment.Words is { Count: > 0 })
            {
                units.AddRange(segment.Words
                    .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                    .Select(word => new TimedText(word.EndMicroseconds, word.Text)));
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

    private static string MergeWithoutOverlap(string confirmed, string provisional)
    {
        if (confirmed.Length == 0) return provisional;
        if (provisional.Length == 0) return confirmed;
        var overlap = FindBestSuffixPrefixOverlap(confirmed, provisional);
        var tail = overlap.NewLength > 0 ? provisional[overlap.NewLength..].Trim() : provisional;
        return tail.Length == 0 ? confirmed : $"{confirmed} {tail}";
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string NormalizeForComparison(string text) =>
        new(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)).Select(char.ToLowerInvariant).ToArray());

    private static string Join(IEnumerable<string> values) => string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
    private sealed record TimedText(long EndMicroseconds, string Text);
}
