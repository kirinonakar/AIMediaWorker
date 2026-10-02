namespace AIMediaWorker.Media;

internal sealed record LocalBrowserBreadcrumb(string Label, string Path);

internal static class LocalBrowserPath
{
    // A virtual root above Windows drives; never pass it to filesystem path APIs.
    public const string Root = "/";

    public static bool AreSameDirectory(string first, string second) =>
        first == Root || second == Root
            ? first == second
            : string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.OrdinalIgnoreCase);

    public static string? GetParent(string directory) =>
        directory == Root ? null : Directory.GetParent(directory)?.FullName ?? Root;

    public static IReadOnlyList<LocalBrowserBreadcrumb> GetBreadcrumbs(string directory)
    {
        var entries = new List<LocalBrowserBreadcrumb> { new(Root, Root) };
        if (directory == Root) return entries;

        var fullPath = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullPath)!;
        entries.Add(new(root, root));
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".") return entries;

        var accumulatedPath = root;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;
            accumulatedPath = Path.Combine(accumulatedPath, segment);
            entries.Add(new(segment, accumulatedPath));
        }
        return entries;
    }

    public static int GetHiddenBreadcrumbCount(IReadOnlyList<double> widths, double availableWidth, double overflowWidth)
    {
        var remainingWidth = widths.Sum();
        if (remainingWidth <= availableWidth) return 0;

        // Keep the current location visible, even when its own label must be trimmed.
        var hiddenCount = 0;
        while (hiddenCount < widths.Count - 1 && remainingWidth + overflowWidth > availableWidth)
            remainingWidth -= widths[hiddenCount++];
        return hiddenCount;
    }
}
