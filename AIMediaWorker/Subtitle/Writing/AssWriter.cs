using System.Text;
using AIMediaWorker.Settings;

namespace AIMediaWorker.Subtitle.Writing;

public static class AssWriter
{
    private const string HeaderPrefix = "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n";
    private const string EventsHeader = "\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    public static string Write(SubtitleTrack track, string? fontFamily = null)
    {
        var builder = new StringBuilder(string.IsNullOrWhiteSpace(track.NativeHeader) || !track.NativeHeader.Contains("[Events]", StringComparison.OrdinalIgnoreCase) ? CreateHeader(fontFamily) : track.NativeHeader.TrimEnd() + "\n");
        foreach (var cue in track.Cues.OrderBy(c => c.StartMicroseconds))
        {
            cue.Validate();
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
