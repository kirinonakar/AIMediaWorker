namespace AIMediaWorker.Asr;

public static class PythonEnvironment
{
    public static string ResolveExecutable(string configuredExecutable, string? anchorPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredExecutable);
        var configured = configuredExecutable.Trim();
        if (!IsDefaultPythonCommand(configured)) return configured;

        foreach (var directory in EnumerateSearchDirectories(anchorPath))
        {
            var candidate = Path.Combine(directory, ".venv", "Scripts", "python.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return configured;
    }

    private static bool IsDefaultPythonCommand(string executable) =>
        executable.Equals("python", StringComparison.OrdinalIgnoreCase) ||
        executable.Equals("python.exe", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateSearchDirectories(string? anchorPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { GetAnchorDirectory(anchorPath), AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = string.IsNullOrWhiteSpace(start) ? null : new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (seen.Add(directory.FullName)) yield return directory.FullName;
                directory = directory.Parent;
            }
        }
    }

    private static string? GetAnchorDirectory(string? anchorPath)
    {
        if (string.IsNullOrWhiteSpace(anchorPath)) return null;
        var fullPath = Path.GetFullPath(anchorPath);
        return Path.HasExtension(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
    }
}
