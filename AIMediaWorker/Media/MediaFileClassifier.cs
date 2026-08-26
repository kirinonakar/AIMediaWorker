namespace AIMediaWorker.Media;

public static class MediaFileClassifier
{
    public static bool IsSubtitle(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa" or ".smi";

    public static bool IsPlayable(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".wmv" or ".m4v" or ".ts" or ".m2ts" or
            ".mp3" or ".flac" or ".wav" or ".m4a" or ".aac" or ".ogg" or ".opus";
}
