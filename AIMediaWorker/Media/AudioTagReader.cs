using System.Globalization;
using System.Text;
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
        var text = ReadWithTagLib(path);
        if (text is not null) return text;
        // Some MP3 files (unusual ID3v2.4 frames, corrupt headers, APE-tagged files) make
        // TagLib fail or yield nothing. Fall back to a small manual ID3v2/ID3v1 parser.
        return string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)
            ? ReadMp3Fallback(path)
            : null;
    }

    private static string? ReadWithTagLib(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var year = tag.Year > 0 ? tag.Year.ToString(CultureInfo.InvariantCulture) : null;
            return BuildDisplayText(tag.Title, tag.FirstPerformer, tag.Album, year);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? BuildDisplayText(string? title, string? artist, string? album, string? year)
    {
        var parts = new List<string>(3);
        Add(parts, title);
        Add(parts, artist);
        Add(parts, album);
        var text = string.Join(" - ", parts);
        if (!string.IsNullOrWhiteSpace(year))
            text = text.Length == 0 ? year : $"{text} ({year})";
        return text.Length == 0 ? null : text;
    }

    private static void Add(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) parts.Add(value.Trim());
    }

    // ---- MP3 fallback: manual ID3v2 (2.2/2.3/2.4) + ID3v1 parsing ----

    private static string? ReadMp3Fallback(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frames = ReadId3v2Frames(stream);
            if (frames is not null)
            {
                var year = NormalizeYear(Pick(frames, "TYER", "TDRC", "TYE"));
                return BuildDisplayText(
                    Pick(frames, "TIT2", "TT2"),
                    Pick(frames, "TPE1", "TP1"),
                    Pick(frames, "TALB", "TAL"),
                    year);
            }
            return ReadId3v1(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Dictionary<string, string>? ReadId3v2Frames(Stream stream)
    {
        var header = new byte[10];
        if (stream.Read(header, 0, 10) < 10 || header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3')
            return null;
        var major = header[3];
        var tagSize = SyncSafe(header[6], header[7], header[8], header[9]);
        if (tagSize <= 0 || tagSize > 64 * 1024 * 1024) return null;
        var frames = new Dictionary<string, string>(StringComparer.Ordinal);
        var remaining = tagSize;
        while (remaining >= 10)
        {
            var frameHeader = new byte[10];
            if (stream.Read(frameHeader, 0, 10) < 10) break;
            remaining -= 10;
            string id;
            int size;
            if (major == 2)
            {
                id = Encoding.ASCII.GetString(frameHeader, 0, 3);
                size = (frameHeader[3] << 16) | (frameHeader[4] << 8) | frameHeader[5];
            }
            else
            {
                id = Encoding.ASCII.GetString(frameHeader, 0, 4);
                size = major == 4
                    ? SyncSafe(frameHeader[4], frameHeader[5], frameHeader[6], frameHeader[7])
                    : (frameHeader[4] << 24) | (frameHeader[5] << 16) | (frameHeader[6] << 8) | frameHeader[7];
            }
            if (size < 0 || size > remaining) break;
            var data = new byte[size];
            if (stream.Read(data, 0, size) < size) break;
            remaining -= size;
            if (id.Length > 0 && id[0] == 'T' && id != "TXXX" && !frames.ContainsKey(id))
            {
                var value = DecodeTextFrame(data);
                if (!string.IsNullOrWhiteSpace(value)) frames[id] = value;
            }
        }
        return frames.Count > 0 ? frames : null;
    }

    private static string? ReadId3v1(Stream stream)
    {
        if (stream.Length < 128) return null;
        stream.Position = stream.Length - 128;
        var tag = new byte[128];
        if (stream.Read(tag, 0, 128) < 128 || tag[0] != (byte)'T' || tag[1] != (byte)'A' || tag[2] != (byte)'G') return null;
        var title = Encoding.Latin1.GetString(tag, 3, 30).TrimEnd('\0', ' ');
        var artist = Encoding.Latin1.GetString(tag, 33, 30).TrimEnd('\0', ' ');
        var album = Encoding.Latin1.GetString(tag, 63, 30).TrimEnd('\0', ' ');
        var year = Encoding.Latin1.GetString(tag, 93, 4).TrimEnd('\0', ' ');
        return BuildDisplayText(title, artist, album, year);
    }

    private static string? DecodeTextFrame(byte[] data)
    {
        if (data.Length < 2) return null;
        var payload = data.AsSpan(1);
        return data[0] switch
        {
            0 => Encoding.Latin1.GetString(payload).TrimEnd('\0'),
            1 => DecodeUtf16(payload),
            2 => DecodeUtf16BigEndian(payload),
            3 => Encoding.UTF8.GetString(payload).TrimEnd('\0'),
            _ => null,
        };
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data[2..]).TrimEnd('\0');
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data[2..]).TrimEnd('\0');
        return Encoding.Unicode.GetString(data).TrimEnd('\0');
    }

    private static string DecodeUtf16BigEndian(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data[2..]).TrimEnd('\0');
        return Encoding.BigEndianUnicode.GetString(data).TrimEnd('\0');
    }

    private static int SyncSafe(byte b1, byte b2, byte b3, byte b4) =>
        ((b1 & 0x7F) << 21) | ((b2 & 0x7F) << 14) | ((b3 & 0x7F) << 7) | (b4 & 0x7F);

    private static string? Pick(Dictionary<string, string> frames, params string[] ids)
    {
        foreach (var id in ids)
            if (frames.TryGetValue(id, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }

    private static string? NormalizeYear(string? year)
    {
        if (string.IsNullOrWhiteSpace(year)) return null;
        year = year.Trim();
        return year.Length >= 4 && int.TryParse(year.AsSpan(0, 4), out _) ? year[..4] : year;
    }
}