using System.Text;

namespace AIMediaWorker.Subtitle.Writing;

public static class AssWriter
{
    private const string Header = "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Segoe UI,54,&H00FFFFFF,&H000000FF,&H00101010,&H80000000,0,0,0,0,100,100,0,0,1,2,1,2,60,60,45,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    public static string Write(SubtitleTrack track)
    {
        var builder = new StringBuilder(string.IsNullOrWhiteSpace(track.NativeHeader) || !track.NativeHeader.Contains("[Events]", StringComparison.OrdinalIgnoreCase) ? Header : track.NativeHeader.TrimEnd() + "\n");
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
}
