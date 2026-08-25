using System.Text;
using AIMediaWorker.Settings;

namespace AIMediaWorker.Subtitle.Writing;

public static class AssWriter
{
    private const string HeaderPrefix = "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n";
    private const string EventsHeader = "\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    public static string Write(SubtitleTrack track, string? fontFamily = null, SubtitleDisplayMode displayMode = SubtitleDisplayMode.Original)
    {
        ArgumentNullException.ThrowIfNull(track);
        return Write(track.Cues.Select(cue => new AssCueSnapshot(cue.Id, cue.StartMicroseconds, cue.EndMicroseconds, cue.GetDisplayText(displayMode), cue.Style, cue.Speaker)).ToArray(), track.NativeHeader, fontFamily);
    }

    public static string Write(SubtitleTrack track, SubtitleDisplayMode displayMode, string? fontFamily = null) =>
        Write(track, fontFamily, displayMode);

    public static string Write(IReadOnlyList<AssCueSnapshot> cues, string? nativeHeader, string? fontFamily = null)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var builder = new StringBuilder(string.IsNullOrWhiteSpace(nativeHeader) || !nativeHeader.Contains("[Events]", StringComparison.OrdinalIgnoreCase) ? CreateHeader(fontFamily) : nativeHeader.TrimEnd() + "\n");
        foreach (var cue in cues.OrderBy(c => c.StartMicroseconds))
        {
            if (cue.StartMicroseconds < 0 || cue.EndMicroseconds <= cue.StartMicroseconds) throw new InvalidDataException("Invalid subtitle time range.");
            var text = cue.Text.Replace("\r\n", "\\N").Replace("\n", "\\N").Replace("\r", "\\N");
            builder.Append("Dialogue: 0,").Append(SubtitleTime.FormatAss(cue.StartMicroseconds)).Append(',').Append(SubtitleTime.FormatAss(cue.EndMicroseconds))
                .Append(',').Append(string.IsNullOrWhiteSpace(cue.Style) ? "Default" : cue.Style).Append(',').Append(cue.Speaker ?? string.Empty)
                .Append(",0,0,0,,").AppendLine(text);
        }
        return builder.ToString();
    }

    private static string CreateHeader(string? fontFamily)
    {
        var font = string.IsNullOrWhiteSpace(fontFamily) ? SubtitleSettings.DefaultFontFamily : fontFamily.Trim();
        font = font.Replace(',', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return $"{HeaderPrefix}Style: Default,{font},54,&H00FFFFFF,&H000000FF,&H00101010,&H80000000,0,0,0,0,100,100,0,0,1,2,1,2,60,60,45,1\n{EventsHeader}";
    }
}

public readonly record struct AssCueSnapshot(Guid Id, long StartMicroseconds, long EndMicroseconds, string Text, string? Style, string? Speaker);
