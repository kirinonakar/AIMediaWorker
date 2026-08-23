using System.Text;

namespace AIMediaWorker.Subtitle.Writing;

public static class VttWriter
{
    public static string Write(SubtitleTrack track)
    {
        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in track.Cues.OrderBy(c => c.StartMicroseconds))
        {
            cue.Validate();
            builder.Append(SubtitleTime.FormatVtt(cue.StartMicroseconds)).Append(" --> ").AppendLine(SubtitleTime.FormatVtt(cue.EndMicroseconds));
            builder.AppendLine(cue.Text).AppendLine();
        }
        return builder.ToString();
    }
}
