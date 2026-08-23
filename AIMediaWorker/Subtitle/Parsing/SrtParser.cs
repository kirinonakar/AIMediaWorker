using System.Text;
using System.Text.RegularExpressions;

namespace AIMediaWorker.Subtitle.Parsing;

public static partial class SrtParser
{
    [GeneratedRegex(@"^\s*(?<start>\d{1,3}:\d{2}:\d{2}[,.]\d{1,6})\s*-->\s*(?<end>\d{1,3}:\d{2}:\d{2}[,.]\d{1,6})", RegexOptions.CultureInvariant)]
    private static partial Regex TimingRegex();

    public static SubtitleDocument Parse(string text)
    {
        var document = new SubtitleDocument();
        var track = document.EnsureTrack("srt");
        using var reader = new StringReader(text.Replace("\r\n", "\n"));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var timing = TimingRegex().Match(line);
            if (!timing.Success)
            {
                var possibleTiming = reader.ReadLine();
                if (possibleTiming is null) break;
                timing = TimingRegex().Match(possibleTiming);
                if (!timing.Success) continue;
            }
            var body = new StringBuilder();
            while ((line = reader.ReadLine()) is not null && !string.IsNullOrWhiteSpace(line))
            {
                if (body.Length > 0) body.Append('\n');
                body.Append(line);
            }
            var cue = new SubtitleCue
            {
                StartMicroseconds = SubtitleTime.Parse(timing.Groups["start"].Value),
                EndMicroseconds = SubtitleTime.Parse(timing.Groups["end"].Value),
                Text = body.ToString(),
                Source = SubtitleCueSource.Imported
            };
            cue.Validate();
            track.Cues.Add(cue);
        }
        SubtitleDocument.Sort(track);
        document.MarkSaved();
        return document;
    }

    public static async Task<SubtitleDocument> ParseFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var document = Parse(text);
        document.MarkSaved(path);
        return document;
    }
}
