using System.Globalization;
using TagLib;

namespace AIMediaWorker.Media;

/// <summary>
/// Reads audio metadata (ID3v1/v2, Vorbis comments, MP4 atoms, RIFF INFO) from local audio files.
/// </summary>
public static class AudioTagReader
{
    /// <summary>
    /// Returns a compact "Title - Artist (Album, Year)" display string, or null when the
    /// file has no readable tags. Never throws; unreadable or untagged files return null.
    /// </summary>
    public static string? ReadDisplayText(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var parts = new List<string>(3);
            Add(parts, tag.Title);
            Add(parts, tag.FirstPerformer);
            Add(parts, tag.Album);
            var text = string.Join(" - ", parts);
            if (tag.Year > 0)
            {
                var year = tag.Year.ToString(CultureInfo.InvariantCulture);
                text = text.Length == 0 ? year : $"{text} ({year})";
            }
            return text.Length == 0 ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Add(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) parts.Add(value.Trim());
    }
}