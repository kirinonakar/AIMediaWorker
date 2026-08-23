namespace AIMediaWorker.Timeline;

public sealed class TimelineTransform
{
    public const double MinimumPixelsPerSecond = 1;
    public const double MaximumPixelsPerSecond = 2_000;

    public double PixelsPerSecond { get; private set; } = 100;
    public long ViewStartMicroseconds { get; private set; }

    public double TimeToX(long microseconds) => ((microseconds - ViewStartMicroseconds) / 1_000_000d) * PixelsPerSecond;
    public long XToTime(double x) => Math.Max(0, ViewStartMicroseconds + checked((long)Math.Round(x / PixelsPerSecond * 1_000_000d)));

    public void PanTo(long startMicroseconds) => ViewStartMicroseconds = Math.Max(0, startMicroseconds);

    public bool EnsureVisible(long microseconds, double viewportWidth, double edgeMarginFraction = 0.1)
    {
        if (microseconds < 0) throw new ArgumentOutOfRangeException(nameof(microseconds));
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0) throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        if (!double.IsFinite(edgeMarginFraction) || edgeMarginFraction < 0 || edgeMarginFraction >= 0.5) throw new ArgumentOutOfRangeException(nameof(edgeMarginFraction));

        var visible = VisibleRange(viewportWidth);
        var span = Math.Max(1, visible.End - visible.Start);
        var margin = checked((long)Math.Round(span * edgeMarginFraction));
        if (microseconds >= visible.Start + margin && microseconds <= visible.End - margin) return false;

        var nextStart = Math.Max(0, microseconds - span / 2);
        if (nextStart == ViewStartMicroseconds) return false;
        ViewStartMicroseconds = nextStart;
        return true;
    }

    public void ZoomAt(double factor, double anchorX)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        var anchorTime = XToTime(anchorX);
        PixelsPerSecond = Math.Clamp(PixelsPerSecond * factor, MinimumPixelsPerSecond, MaximumPixelsPerSecond);
        ViewStartMicroseconds = Math.Max(0, anchorTime - checked((long)Math.Round(anchorX / PixelsPerSecond * 1_000_000d)));
    }

    public (long Start, long End) VisibleRange(double width) => (ViewStartMicroseconds, XToTime(Math.Max(0, width)));
}
