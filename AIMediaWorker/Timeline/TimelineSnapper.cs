namespace AIMediaWorker.Timeline;

public static class TimelineSnapper
{
    public static long Snap(long value, IEnumerable<long> candidates, long toleranceMicroseconds)
    {
        var best = value;
        var bestDistance = toleranceMicroseconds + 1;
        foreach (var candidate in candidates)
        {
            var distance = Math.Abs(candidate - value);
            if (distance <= toleranceMicroseconds && distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }
}
