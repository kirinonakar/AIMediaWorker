using System.Text;

namespace AIMediaWorker.Subtitle.Parsing;

public static class VttParser
{
    public static SubtitleDocument Parse(string text)
    {
        var normalized = text.TrimStart('\uFEFF').Replace("\r\n", "\n");
        if (!normalized.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Missing WEBVTT header.");
        var document = new SubtitleDocument();
        var track = document.EnsureTrack("vtt");
        using var reader = new StringReader(normalized);
        _ = reader.ReadLine();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("NOTE", StringComparison.Ordinal))
            {
                while ((line = reader.ReadLine()) is not null && !string.IsNullOrWhiteSpace(line)) { }
                continue;
            }
            if (!line.Contains("-->")) line = reader.ReadLine();
            if (line is null || !line.Contains("-->")) continue;
            var timing = line.Split("-->", 2, StringSplitOptions.TrimEntries);
            var endToken = timing[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var body = new StringBuilder();
            while ((line = reader.ReadLine()) is not null && !string.IsNullOrWhiteSpace(line))
            {
                if (body.Length > 0) body.Append('\n');
                body.Append(line);
            }
            var cue = new SubtitleCue { StartMicroseconds = SubtitleTime.Parse(timing[0]), EndMicroseconds = SubtitleTime.Parse(endToken), Text = body.ToString() };
            cue.Validate();
            track.Cues.Add(cue);
        }
        SubtitleDocument.Sort(track);
        document.MarkSaved();
        return document;
    }
}
