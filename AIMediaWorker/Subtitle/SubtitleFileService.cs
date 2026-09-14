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
        var document = Parse(pathOrUri, text);
        document.IsLoadedFromFile = true;
        return document;
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

    public static string GetMediaBaseName(string? mediaSource, string fallback)
    {
        if (string.IsNullOrWhiteSpace(mediaSource)) return fallback;
        var path = Uri.TryCreate(mediaSource, UriKind.Absolute, out var uri)
            ? uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath)
            : mediaSource;
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        return name;
    }

    public async Task<SubtitleDocumentSaveResult> SaveDocumentAsync(
        SubtitleTrack track, string selectedPath, string? mediaSource, SubtitleDisplayMode displayMode,
        string fontFamily, string? encodingName, CancellationToken cancellationToken = default)
    {
        var hasBoth = track.Cues.Any(cue => !string.IsNullOrWhiteSpace(cue.Text)) &&
            track.Cues.Any(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText));
        if (!hasBoth)
        {
            var single = await SaveAsync(track, selectedPath, displayMode, fontFamily, encodingName, cancellationToken);
            return new(selectedPath, null, single.TargetFormat, single.HasStyleLoss);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(selectedPath))!;
        var name = GetMediaBaseName(mediaSource, Path.GetFileNameWithoutExtension(selectedPath));
        var extension = Path.GetExtension(selectedPath);
        var translationPath = Path.Combine(directory, name + extension);
        var originalPath = Path.Combine(directory, name + ".original" + extension);
        // Preserve the original before writing the translation to the video's matching filename.
        var original = await SaveAsync(track, originalPath, SubtitleDisplayMode.Original,
            fontFamily, encodingName, cancellationToken);
        var translation = await SaveAsync(track, translationPath, SubtitleDisplayMode.Translation,
            fontFamily, encodingName, cancellationToken);
        return new(translationPath, originalPath, translation.TargetFormat,
            original.HasStyleLoss || translation.HasStyleLoss);
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
public sealed record SubtitleDocumentSaveResult(
    string PrimaryPath, string? OriginalPath, string TargetFormat, bool HasStyleLoss);
