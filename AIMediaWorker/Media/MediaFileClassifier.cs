namespace AIMediaWorker.Media;

public static class MediaFileClassifier
{
    public const string FileIconGlyph = "\uE8A5";
    public const string VideoIconGlyph = "\uE714";
    public const string AudioIconGlyph = "\uE8D6";

    public static bool IsSubtitle(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa" or ".smi";

    public static bool IsAudio(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".mp3" or ".flac" or ".wav" or ".m4a" or ".aac" or ".ogg" or ".opus";

    public static bool IsPlayable(string path) =>
        IsAudio(path) ||
        Path.GetExtension(path).ToLowerInvariant() is
            ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".wmv" or ".m4v" or ".ts" or ".m2ts";

    public static string GetFileIconGlyph(string path) =>
        IsAudio(path) ? AudioIconGlyph : IsPlayable(path) ? VideoIconGlyph : FileIconGlyph;
}
