using AIMediaWorker.Timeline;

namespace AIMediaWorker.Tests;

public sealed class TimelineTests
{
    [Fact]
    public void CoordinateConversionRoundTrips()
    {
        var transform = new TimelineTransform();
        transform.PanTo(10_000_000);
        Assert.Equal(250, transform.TimeToX(12_500_000), 6);
        Assert.Equal(12_500_000, transform.XToTime(250));
    }

    [Fact]
    public void ZoomKeepsAnchorTimeStable()
    {
        var transform = new TimelineTransform();
        transform.PanTo(5_000_000);
        var before = transform.XToTime(400);
        transform.ZoomAt(2, 400);
        Assert.Equal(before, transform.XToTime(400));
    }
}
