using System.Text;

namespace AIMediaWorker.Subtitle.Writing;

public static class SrtWriter
{
    public static string Write(SubtitleTrack track)
    {
        var builder = new StringBuilder();
        var index = 1;
        foreach (var cue in track.Cues.OrderBy(c => c.StartMicroseconds))
        {
            cue.Validate();
            builder.AppendLine(index++.ToString());
            builder.Append(SubtitleTime.FormatSrt(cue.StartMicroseconds)).Append(" --> ").AppendLine(SubtitleTime.FormatSrt(cue.EndMicroseconds));
            builder.AppendLine(cue.Text.Replace("\r\n", "\n").Replace('\r', '\n'));
            builder.AppendLine();
        }
        return builder.ToString();
    }

    public static Task WriteFileAsync(SubtitleTrack track, string path, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(path, Write(track), new UTF8Encoding(false), cancellationToken);
}
