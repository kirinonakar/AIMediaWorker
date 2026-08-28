using System.Runtime.InteropServices;

namespace AIMediaWorker.Capture;

internal sealed record ScrollingCaptureResult(byte[] Pixels, int Width, int Height);

/// <summary>Scrolls a selected window and joins newly exposed rows into one tall capture.</summary>
internal static class ScrollingCaptureService
{
    private const uint WmVScroll = 0x0115;
    private const uint WmMouseWheel = 0x020A;
    private const nuint SbTop = 6;
    private const uint GaRoot = 2;
    private const int WheelDelta = 120;
    private const int MaximumFrames = 60;
    private const int MaximumPixels = 80_000_000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint windowHandle, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    public static async Task<ScrollingCaptureResult> CaptureAsync(
        nint windowHandle,
        RECT bounds,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle == 0) throw new ArgumentException("A window handle is required.", nameof(windowHandle));
        if (bounds.Width <= 1 || bounds.Height <= 1) throw new ArgumentException("Valid capture bounds are required.", nameof(bounds));

        var centerX = bounds.Left + bounds.Width / 2;
        var centerY = bounds.Top + bounds.Height / 2;
        var recipient = WindowFromPoint(new POINT { X = centerX, Y = centerY });
        if (recipient == 0 || GetAncestor(recipient, GaRoot) != windowHandle) recipient = windowHandle;
        var pointParameter = MakePointParameter(centerX, centerY);

        // Standard controls honor SB_TOP; custom/browser surfaces usually honor the wheel fallback.
        PostMessage(windowHandle, WmVScroll, SbTop, 0);
        for (var index = 0; index < 12; index++)
            PostMessage(recipient, WmMouseWheel, MakeWheelParameter(WheelDelta * 4), pointParameter);
        await Task.Delay(220, cancellationToken).ConfigureAwait(false);

        var previous = ScreenCaptureInterop.CaptureRegion(bounds)
            ?? throw new InvalidOperationException("The selected window could not be captured.");
        var segments = new List<byte[]> { previous };
        var totalHeight = bounds.Height;
        var maximumHeight = Math.Min(32_000, Math.Max(bounds.Height, MaximumPixels / bounds.Width));

        for (var frame = 1; frame < MaximumFrames && totalHeight < maximumHeight; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var notch = 0; notch < 5; notch++)
                PostMessage(recipient, WmMouseWheel, MakeWheelParameter(-WheelDelta * 2), pointParameter);
            await Task.Delay(180, cancellationToken).ConfigureAwait(false);

            var current = ScreenCaptureInterop.CaptureRegion(bounds)
                ?? throw new InvalidOperationException("The selected window could not be captured while scrolling.");
            if (ScrollingCaptureStitcher.AreEquivalent(previous, current, bounds.Width, bounds.Height)) break;

            var shift = ScrollingCaptureStitcher.FindVerticalShift(previous, current, bounds.Width, bounds.Height);
            if (shift <= 0) break;
            shift = Math.Min(shift, maximumHeight - totalHeight);
            segments.Add(ScrollingCaptureStitcher.CopyBottomRows(current, bounds.Width, bounds.Height, shift));
            totalHeight += shift;
            previous = current;
        }

        var result = new byte[checked(bounds.Width * totalHeight * 4)];
        var offset = 0;
        foreach (var segment in segments)
        {
            Buffer.BlockCopy(segment, 0, result, offset, segment.Length);
            offset += segment.Length;
        }

        return new ScrollingCaptureResult(result, bounds.Width, totalHeight);
    }

    private static nuint MakeWheelParameter(int delta) => unchecked((nuint)(delta << 16));

    private static nint MakePointParameter(int x, int y) =>
        unchecked((nint)(((y & 0xffff) << 16) | (x & 0xffff)));
}

/// <summary>Pixel matching kept separate from Win32 scrolling so its behavior is deterministic.</summary>
internal static class ScrollingCaptureStitcher
{
    public static bool AreEquivalent(byte[] first, byte[] second, int width, int height)
    {
        Validate(first, second, width, height);
        long difference = 0;
        var samples = 0;
        for (var y = 0; y < height; y += 20)
        for (var x = 0; x < width; x += 20)
        {
            var index = (y * width + x) * 4;
            difference += ColorDistance(first, index, second, index);
            samples++;
        }

        return samples == 0 || difference / (double)(samples * 3) < 1.5;
    }

    public static int FindVerticalShift(byte[] previous, byte[] current, int width, int height)
    {
        Validate(previous, current, width, height);
        var minimumShift = Math.Max(12, height / 60);
        var maximumShift = Math.Max(minimumShift, height * 3 / 4);
        var bestShift = 0;
        var bestScore = 0.0;
        for (var shift = minimumShift; shift <= maximumShift; shift += 4)
        {
            var score = ScoreShift(previous, current, width, height, shift);
            if (score <= bestScore) continue;
            bestScore = score;
            bestShift = shift;
        }

        if (bestShift == 0) return 0;
        var coarseShift = bestShift;
        for (var shift = Math.Max(minimumShift, coarseShift - 3); shift <= Math.Min(maximumShift, coarseShift + 3); shift++)
        {
            var score = ScoreShift(previous, current, width, height, shift);
            if (score <= bestScore) continue;
            bestScore = score;
            bestShift = shift;
        }

        return bestScore >= 0.58 ? bestShift : 0;
    }

    public static byte[] CopyBottomRows(byte[] pixels, int width, int height, int rowCount)
    {
        if (rowCount <= 0 || rowCount > height) throw new ArgumentOutOfRangeException(nameof(rowCount));
        var rowBytes = checked(width * 4);
        var result = new byte[checked(rowBytes * rowCount)];
        Buffer.BlockCopy(pixels, checked((height - rowCount) * rowBytes), result, 0, result.Length);
        return result;
    }

    private static double ScoreShift(byte[] previous, byte[] current, int width, int height, int shift)
    {
        var topMargin = Math.Max(4, height / 12);
        var bottom = height - shift - Math.Max(4, height / 16);
        var left = Math.Max(2, width / 10);
        var right = width - left;
        var informative = 0;
        var matches = 0;
        for (var y = topMargin; y < bottom; y += 7)
        for (var x = left; x < right; x += 14)
        {
            var previousIndex = ((y + shift) * width + x) * 4;
            var neighborIndex = ((y + shift) * width + Math.Max(0, x - 2)) * 4;
            if (ColorDistance(previous, previousIndex, previous, neighborIndex) < 24) continue;
            informative++;
            var currentIndex = (y * width + x) * 4;
            if (ColorDistance(previous, previousIndex, current, currentIndex) <= 36) matches++;
        }

        return informative < 40 ? 0 : matches / (double)informative;
    }

    private static int ColorDistance(byte[] first, int firstIndex, byte[] second, int secondIndex) =>
        Math.Abs(first[firstIndex] - second[secondIndex]) +
        Math.Abs(first[firstIndex + 1] - second[secondIndex + 1]) +
        Math.Abs(first[firstIndex + 2] - second[secondIndex + 2]);

    private static void Validate(byte[] first, byte[] second, int width, int height)
    {
        var expected = checked(width * height * 4);
        if (width <= 0 || height <= 0 || first.Length < expected || second.Length < expected)
            throw new ArgumentException("Pixel buffers do not match the supplied dimensions.");
    }
}
