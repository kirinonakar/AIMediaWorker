using AIMediaWorker.Subtitle.Parsing;
using AIMediaWorker.Subtitle.Writing;

namespace AIMediaWorker.Subtitle;

/// <summary>Owns subtitle text decoding, format parsing, serialization, and file I/O.</summary>
public sealed class SubtitleFileService
{
    public async Task<SubtitleDocument> LoadAsync(string path, string? encodingName, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var document = DecodeAndParse(path, bytes, encodingName);
        // SAMI is import-only. Detach it so Save As chooses a supported output format.
        document.MarkSaved(Path.GetExtension(path).Equals(".smi", StringComparison.OrdinalIgnoreCase) ? null : path);
        return document;
    }

    public SubtitleDocument DecodeAndParse(string pathOrUri, byte[] bytes, string? encodingName)
    {
        var detectKorean = Path.GetExtension(pathOrUri).Equals(".smi", StringComparison.OrdinalIgnoreCase);
        var text = SubtitleTextDecoder.Decode(bytes, ResolveEncoding(encodingName), detectKorean);
        return Parse(pathOrUri, text);
    }

    public static SubtitleDocument Parse(string pathOrUri, string text) =>
        Path.GetExtension(pathOrUri).ToLowerInvariant() switch
        {
            ".srt" => SrtParser.Parse(text),
            ".vtt" => VttParser.Parse(text),
            ".ass" or ".ssa" => AssParser.Parse(text),
            ".smi" => SmiParser.Parse(text),
            _ => throw new InvalidDataException("Unsupported subtitle format.")
        };

    public async Task<SubtitleSaveResult> SaveAsync(
        SubtitleTrack track,
        string path,
        SubtitleDisplayMode displayMode,
        string fontFamily,
        string? encodingName,
        CancellationToken cancellationToken = default)
    {
        var targetFormat = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".vtt" => "vtt",
            ".ass" or ".ssa" => "ass",
            _ => "srt"
        };
        var text = targetFormat switch
        {
            "vtt" => VttWriter.Write(track, displayMode),
            "ass" => AssWriter.Write(track, fontFamily, displayMode),
            _ => SrtWriter.Write(track, displayMode)
        };
        var styleLoss = !track.Format.Equals(targetFormat, StringComparison.OrdinalIgnoreCase) &&
                        track.Cues.Any(cue => !string.IsNullOrWhiteSpace(cue.Style));
        await File.WriteAllTextAsync(path, text, ResolveEncoding(encodingName), cancellationToken);
        return new SubtitleSaveResult(targetFormat, styleLoss);
    }

    public static System.Text.Encoding ResolveEncoding(string? encodingName)
    {
        var name = string.IsNullOrWhiteSpace(encodingName) ? "utf-8" : encodingName.Trim();
        return name.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || name.Equals("utf8", StringComparison.OrdinalIgnoreCase)
            ? new System.Text.UTF8Encoding(false, true)
            : System.Text.Encoding.GetEncoding(name, System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback);
    }
}

public sealed record SubtitleSaveResult(string TargetFormat, bool HasStyleLoss);
