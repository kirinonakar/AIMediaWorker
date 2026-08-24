using System.Net;
using System.Text.RegularExpressions;

namespace AIMediaWorker.Subtitle.Parsing;

public static partial class SmiParser
{
    private const long DefaultFinalCueDurationMicroseconds = 2_000_000;

    [GeneratedRegex(@"<sync\b[^>]*\bstart\s*=\s*[\""']?(?<start>\d+)[\""']?[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SyncRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"</?p\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    public static SubtitleDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var document = new SubtitleDocument();
        var track = document.EnsureTrack("smi");
        var matches = SyncRegex().Matches(text);

        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            if (!long.TryParse(match.Groups["start"].Value, out var startMilliseconds)) continue;

            var bodyStart = match.Index + match.Length;
            var bodyEnd = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
            var cueText = NormalizeCueText(text[bodyStart..bodyEnd]);
            if (string.IsNullOrWhiteSpace(cueText)) continue;

            var startMicroseconds = checked(startMilliseconds * 1_000);
            var endMicroseconds = index + 1 < matches.Count && long.TryParse(matches[index + 1].Groups["start"].Value, out var nextMilliseconds)
                ? checked(nextMilliseconds * 1_000)
                : checked(startMicroseconds + DefaultFinalCueDurationMicroseconds);
            if (endMicroseconds <= startMicroseconds) continue;

            track.Cues.Add(new SubtitleCue
            {
                StartMicroseconds = startMicroseconds,
                EndMicroseconds = endMicroseconds,
                Text = cueText,
                Source = SubtitleCueSource.Imported
            });
        }

        SubtitleDocument.Sort(track);
        document.MarkSaved();
        return document;
    }

    private static string NormalizeCueText(string html)
    {
        var text = BreakRegex().Replace(html, "\n");
        text = ParagraphRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
        return string.Join('\n', text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));
    }
}
