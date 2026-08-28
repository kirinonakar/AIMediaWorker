namespace AIMediaWorker.Capture;

internal readonly record struct PreparedOcrImage(byte[] Pixels, int Width, int Height);

/// <summary>Adds breathing room around tightly selected text and enlarges small captures for OCR.</summary>
internal static class OcrImagePreprocessor
{
    private const int PreferredPadding = 8;
    private const int PreferredScale = 2;

    public static PreparedOcrImage Prepare(byte[] bgraPixels, int width, int height, int maximumDimension)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (width <= 0 || height <= 0 || bgraPixels.Length < checked(width * height * 4))
            throw new ArgumentException("The OCR pixel buffer dimensions are invalid.", nameof(bgraPixels));

        var padding = width + PreferredPadding * 2 <= maximumDimension &&
                      height + PreferredPadding * 2 <= maximumDimension
            ? PreferredPadding
            : 0;
        var paddedWidth = width + padding * 2;
        var paddedHeight = height + padding * 2;
        var scale = paddedWidth * PreferredScale <= maximumDimension &&
                    paddedHeight * PreferredScale <= maximumDimension
            ? PreferredScale
            : 1;
        var targetWidth = paddedWidth * scale;
        var targetHeight = paddedHeight * scale;
        var target = GC.AllocateUninitializedArray<byte>(checked(targetWidth * targetHeight * 4));
        var background = EstimateBackground(bgraPixels, width, height);

        for (var offset = 0; offset < target.Length; offset += 4)
        {
            target[offset] = background.Blue;
            target[offset + 1] = background.Green;
            target[offset + 2] = background.Red;
            target[offset + 3] = 255;
        }

        for (var sourceY = 0; sourceY < height; sourceY++)
        {
            for (var sourceX = 0; sourceX < width; sourceX++)
            {
                var sourceOffset = (sourceY * width + sourceX) * 4;
                var targetX = (sourceX + padding) * scale;
                var targetY = (sourceY + padding) * scale;
                for (var repeatY = 0; repeatY < scale; repeatY++)
                {
                    for (var repeatX = 0; repeatX < scale; repeatX++)
                    {
                        var targetOffset = ((targetY + repeatY) * targetWidth + targetX + repeatX) * 4;
                        target[targetOffset] = bgraPixels[sourceOffset];
                        target[targetOffset + 1] = bgraPixels[sourceOffset + 1];
                        target[targetOffset + 2] = bgraPixels[sourceOffset + 2];
                        target[targetOffset + 3] = 255;
                    }
                }
            }
        }

        return new PreparedOcrImage(target, targetWidth, targetHeight);
    }

    private static (byte Blue, byte Green, byte Red) EstimateBackground(byte[] pixels, int width, int height)
    {
        int[] offsets =
        [
            0,
            (width - 1) * 4,
            (height - 1) * width * 4,
            ((height - 1) * width + width - 1) * 4
        ];
        var blue = 0;
        var green = 0;
        var red = 0;
        foreach (var offset in offsets)
        {
            blue += pixels[offset];
            green += pixels[offset + 1];
            red += pixels[offset + 2];
        }

        return ((byte)(blue / offsets.Length), (byte)(green / offsets.Length), (byte)(red / offsets.Length));
    }
}
