using System.Globalization;

namespace AIMediaWorker.Subtitle.Writing;

/// <summary>Self-contained styling for a generated cue, independent of mpv's message OSD.</summary>
public static class GeneratedSubtitleAss
{
    public const int Width = 1280;
    public const int Height = 720;

    public static string Write(string text, string font, double size, string color, string background,
        double outline, int bottomMargin)
    {
        font = font.Replace('\\', ' ').Replace('{', ' ').Replace('}', ' ').Replace('\r', ' ').Replace('\n', ' ');
        // A zero-width character prevents literal backslashes from becoming ASS escapes.
        text = text.Replace("\\", "\\\uFEFF").Replace("{", "\\{").Replace("}", "\\}")
            .Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\N");
        var (foreground, alpha) = ConvertColor(color);
        var (back, backAlpha) = ConvertColor(background);
        return FormattableString.Invariant(
            $"{{\\rDefault\\an2\\pos({Width / 2},{Math.Clamp(Height - bottomMargin, 0, Height)})\\q0\\fn{font}\\fs{size:0.##}\\bord{outline:0.##}\\shad0\\1c&H{foreground}&\\1a&H{alpha}&\\3c&H{back}&\\3a&H{backAlpha}&}}{text}");
    }

    private static (string Color, string Alpha) ConvertColor(string color)
    {
        var hex = color.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            value = 0xFFFFFFFF;
        return ($"{value & 255:X2}{(value >> 8) & 255:X2}{(value >> 16) & 255:X2}", $"{255 - (value >> 24):X2}");
    }
}
