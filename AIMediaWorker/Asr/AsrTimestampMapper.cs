namespace AIMediaWorker.Asr;

internal static class AsrTimestampMapper
{
    private const long CentisecondMicroseconds = 10_000;
    private const long RoundingToleranceMicroseconds = 100_000;

    public static (int Start, int End) SampleRange(long startCentiseconds, long endCentiseconds, int sampleCount)
    {
        const int samplesPerCentisecond = 160; // 16 kHz mono PCM.
        var maximumCentiseconds = (sampleCount + (long)samplesPerCentisecond - 1) / samplesPerCentisecond;
        var start = (int)Math.Min(sampleCount, Math.Clamp(startCentiseconds, 0, maximumCentiseconds) * samplesPerCentisecond);
        var end = (int)Math.Min(sampleCount, Math.Clamp(endCentiseconds, 0, maximumCentiseconds) * samplesPerCentisecond);
        return end > start ? (start, end) : (0, sampleCount);
    }

    public static IReadOnlyList<AsrWord>? MapWords(
        IReadOnlyList<CrispAsrNative.NativeWord> words, long origin, long rangeStart, long rangeEnd)
    {
        if (rangeEnd <= rangeStart) return null;
        var result = new List<AsrWord>(words.Count);
        var maximumCentiseconds = (rangeEnd - origin + RoundingToleranceMicroseconds) / CentisecondMicroseconds;
        var hasDuration = false;
        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word.Text)) continue;
            // -1 means unavailable in the session ABI; it must not become 0 s.
            if (word.StartCentiseconds < 0 || word.EndCentiseconds < word.StartCentiseconds ||
                word.EndCentiseconds > maximumCentiseconds) return null;
            var rawStart = origin + word.StartCentiseconds * CentisecondMicroseconds;
            var rawEnd = origin + word.EndCentiseconds * CentisecondMicroseconds;
            if (rawStart < rangeStart - RoundingToleranceMicroseconds || rawStart >= rangeEnd + RoundingToleranceMicroseconds)
                return null;

            var start = Math.Clamp(rawStart, rangeStart, rangeEnd - 1);
            var end = Math.Clamp(rawEnd, start + 1, rangeEnd);
            if (result.Count > 0)
            {
                var previous = result[^1];
                // Preserve token order. Only repair small timestamp jitter;
                // grossly reversed alignment must use the fallback path.
                if (previous.StartMicroseconds - start > RoundingToleranceMicroseconds ||
                    previous.EndMicroseconds - end > RoundingToleranceMicroseconds) return null;
                start = Math.Max(start, previous.StartMicroseconds);
                end = Math.Max(end, previous.EndMicroseconds);
            }
            hasDuration |= word.EndCentiseconds > word.StartCentiseconds;
            result.Add(new AsrWord { Text = word.Text.Trim(), StartMicroseconds = start, EndMicroseconds = end });
        }
        if (result.Count == 0 || !hasDuration) return null;
        if (result.Count > 1 && rangeEnd - rangeStart >= 1_000_000 &&
            result[^1].EndMicroseconds - result[0].StartMicroseconds <= CentisecondMicroseconds) return null;
        return result;
    }
}
