namespace AIMediaWorker.Media;

internal sealed record LocalMediaSearchResult(string Path, bool IsDirectory, string RelativePath);

internal static class LocalMediaSearchService
{
    public const int MaximumResults = 5000;

    public static Task<IReadOnlyList<LocalMediaSearchResult>> SearchAsync(
        string rootDirectory,
        string query,
        bool useRegex,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Search(rootDirectory, SearchPatternMatcher.Create(query, useRegex), cancellationToken), cancellationToken);

    private static IReadOnlyList<LocalMediaSearchResult> Search(
        string rootDirectory,
        SearchPatternMatcher matcher,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        var results = new List<LocalMediaSearchResult>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && results.Count < MaximumResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] directories;
            string[] files;
            try
            {
                directories = Directory.GetDirectories(directory);
                files = Directory.GetFiles(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var childDirectory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(root, childDirectory);
                if (matcher.IsMatch(relativePath))
                    results.Add(new LocalMediaSearchResult(childDirectory, true, relativePath));
                if (results.Count >= MaximumResults) break;

                try
                {
                    if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(childDirectory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }

            if (results.Count >= MaximumResults) break;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MediaFileClassifier.IsPlayable(file)) continue;
                var relativePath = Path.GetRelativePath(root, file);
                if (matcher.IsMatch(relativePath))
                    results.Add(new LocalMediaSearchResult(file, false, relativePath));
                if (results.Count >= MaximumResults) break;
            }
        }

        return results;
    }
}
