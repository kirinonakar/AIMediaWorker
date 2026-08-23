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

    [Fact]
    public void EnsureVisiblePansWhenPlaybackLeavesViewportMargin()
    {
        var transform = new TimelineTransform();

        Assert.True(transform.EnsureVisible(9_500_000, 1_000));
        Assert.Equal(4_500_000, transform.ViewStartMicroseconds);
        Assert.Equal(500, transform.TimeToX(9_500_000), 6);
    }

    [Fact]
    public void EnsureVisibleKeepsViewportStableWhilePlaybackIsVisible()
    {
        var transform = new TimelineTransform();

        Assert.False(transform.EnsureVisible(5_000_000, 1_000));
        Assert.Equal(0, transform.ViewStartMicroseconds);
    }
}
