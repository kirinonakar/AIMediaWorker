using System.Text;

namespace AIMediaWorker.Subtitle.Writing;

public static class SrtWriter
{
    public static string Write(SubtitleTrack track, SubtitleDisplayMode displayMode = SubtitleDisplayMode.Original)
    {
        var builder = new StringBuilder();
        var index = 1;
        foreach (var cue in track.Cues.OrderBy(c => c.StartMicroseconds))
        {
            cue.Validate();
            builder.AppendLine(index++.ToString());
            builder.Append(SubtitleTime.FormatSrt(cue.StartMicroseconds)).Append(" --> ").AppendLine(SubtitleTime.FormatSrt(cue.EndMicroseconds));
            builder.AppendLine(cue.GetDisplayText(displayMode).Replace("\r\n", "\n").Replace('\r', '\n'));
            builder.AppendLine();
        }
        return builder.ToString();
    }

    public static Task WriteFileAsync(SubtitleTrack track, string path, CancellationToken cancellationToken = default) =>
        WriteFileAsync(track, path, SubtitleDisplayMode.Original, cancellationToken);

    public static Task WriteFileAsync(SubtitleTrack track, string path, SubtitleDisplayMode displayMode, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(path, Write(track, displayMode), new UTF8Encoding(false), cancellationToken);
}
