using AIMediaWorker.Media;

namespace AIMediaWorker.Subtitle;

public static class SubtitleSidecar
{
    private static readonly string[] Extensions = [".srt", ".smi", ".ass", ".ssa", ".vtt"];

    public static bool IsSidecarFor(string mediaPath, string subtitlePath)
    {
        if (!MediaFileClassifier.IsSubtitle(subtitlePath)) return false;
        var mediaName = Path.GetFileNameWithoutExtension(mediaPath);
        var subtitleName = Path.GetFileNameWithoutExtension(subtitlePath);
        return subtitleName.Equals(mediaName, StringComparison.OrdinalIgnoreCase) ||
            subtitleName.StartsWith(mediaName + ".", StringComparison.OrdinalIgnoreCase);
    }

    public static string? FindMatch(string mediaPath, IEnumerable<string> candidates) => candidates
        .Where(candidate => IsSidecarFor(mediaPath, candidate))
        .OrderBy(candidate => !Path.GetFileNameWithoutExtension(candidate).Equals(
            Path.GetFileNameWithoutExtension(mediaPath), StringComparison.OrdinalIgnoreCase))
        .ThenBy(candidate => Array.IndexOf(Extensions, Path.GetExtension(candidate).ToLowerInvariant()))
        .ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    public static string? FindPath(string mediaPath)
    {
        var fullPath = Path.GetFullPath(mediaPath);
        var directory = Path.GetDirectoryName(fullPath);
        return Directory.Exists(directory) ? FindMatch(fullPath, Directory.EnumerateFiles(directory!)) : null;
    }
}
