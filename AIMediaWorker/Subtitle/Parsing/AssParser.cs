namespace AIMediaWorker.Subtitle.Parsing;

public static class AssParser
{
    public static SubtitleDocument Parse(string text)
    {
        var document = new SubtitleDocument();
        var track = document.EnsureTrack("ass");
        track.NativeHeader = string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Where(line => !line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))).TrimEnd() + "\n";
        var inEvents = false;
        string[] format = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith('[')) { inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inEvents) continue;
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                format = line[7..].Split(',').Select(x => x.Trim()).ToArray();
                continue;
            }
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = line[9..].Split(',', format.Length);
            if (fields.Length != format.Length) continue;
            var values = format.Select((name, index) => (name, value: fields[index].Trim())).ToDictionary(x => x.name, x => x.value, StringComparer.OrdinalIgnoreCase);
            if (!values.TryGetValue("Start", out var start) || !values.TryGetValue("End", out var end) || !values.TryGetValue("Text", out var cueText)) continue;
            var cue = new SubtitleCue
            {
                StartMicroseconds = SubtitleTime.Parse(start),
                EndMicroseconds = SubtitleTime.Parse(end),
                Text = cueText.Replace("\\N", Environment.NewLine).Replace("\\n", Environment.NewLine),
                Style = values.GetValueOrDefault("Style"),
                Speaker = values.GetValueOrDefault("Name")
            };
            cue.Validate();
            track.Cues.Add(cue);
        }
        SubtitleDocument.Sort(track);
        document.MarkSaved();
        return document;
    }
}
