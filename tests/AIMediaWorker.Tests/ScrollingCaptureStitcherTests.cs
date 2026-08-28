using AIMediaWorker.Capture;

namespace AIMediaWorker.Tests;

public sealed class ScrollingCaptureStitcherTests
{
    [Fact]
    public void FindsContentShiftWhileIgnoringBrowserChromeAndStickyHeader()
    {
        const int width = 320;
        const int height = 240;
        const int contentTop = 65;
        const int shift = 84;
        var previous = CreateBrowserFrame(width, height, contentTop, 0);
        var current = CreateBrowserFrame(width, height, contentTop, shift);

        var detected = ScrollingCaptureStitcher.FindVerticalShift(previous, current, width, height);

        Assert.InRange(detected, shift - 1, shift + 1);
        Assert.False(ScrollingCaptureStitcher.AreEquivalent(previous, current, width, height));
    }

    [Fact]
    public void EquivalentFramesSignalTheBottomOfThePage()
    {
        const int width = 160;
        const int height = 120;
        var frame = CreateBrowserFrame(width, height, 30, 400);

        Assert.True(ScrollingCaptureStitcher.AreEquivalent(frame, frame.ToArray(), width, height));
    }

    private static byte[] CreateBrowserFrame(int width, int height, int contentTop, int contentOffset)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            var fixedChrome = y < contentTop;
            var sourceY = fixedChrome ? y : y + contentOffset;
            pixels[index] = (byte)((x * 17 + sourceY * 3) & 0xff);
            pixels[index + 1] = (byte)(((x / 4) * 29 + sourceY * 7) & 0xff);
            pixels[index + 2] = (byte)(((x / 9) * 41 + sourceY * 11) & 0xff);
            pixels[index + 3] = 255;
        }

        return pixels;
    }
}
