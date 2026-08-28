using AIMediaWorker.Capture;

namespace AIMediaWorker.Tests;

public sealed class OcrImagePreprocessorTests
{
    [Fact]
    public void PrepareAddsPaddingUpscalesAndMakesPixelsOpaque()
    {
        byte[] pixels =
        [
            10, 20, 30, 0,
            40, 50, 60, 0,
            70, 80, 90, 0,
            100, 110, 120, 0
        ];

        var result = OcrImagePreprocessor.Prepare(pixels, 2, 2, 100);

        Assert.Equal(36, result.Width);
        Assert.Equal(36, result.Height);
        Assert.All(result.Pixels.Where((_, index) => index % 4 == 3), alpha => Assert.Equal(255, alpha));

        var firstSourcePixelOffset = ((8 * 2) * result.Width + 8 * 2) * 4;
        Assert.Equal([10, 20, 30, 255], result.Pixels[firstSourcePixelOffset..(firstSourcePixelOffset + 4)]);
    }
}
