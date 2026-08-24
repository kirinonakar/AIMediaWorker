namespace AIMediaWorker.Asr;

public static class AsrRuntimePaths
{
    public static string WorkerDirectory => Path.Combine(AppContext.BaseDirectory, "asr-worker");
    public static string WorkerScript => Path.Combine(WorkerDirectory, "main.py");
    public static string VirtualEnvironmentDirectory => Path.Combine(WorkerDirectory, ".venv");
    public static string PythonExecutable => Path.Combine(VirtualEnvironmentDirectory, "Scripts", "python.exe");
    public static string ModelsDirectory => Path.Combine(WorkerDirectory, "models");
    public static string RequirementsFile => Path.Combine(WorkerDirectory, "requirements.txt");
    public static string ModelInstallerScript => Path.Combine(WorkerDirectory, "install_models.py");

    public static string GetWorkerDirectory(string? anchorPath)
    {
        if (string.IsNullOrWhiteSpace(anchorPath)) return WorkerDirectory;

        var fullPath = Path.GetFullPath(anchorPath);
        if (File.Exists(fullPath) || Path.GetFileName(fullPath).Equals("main.py", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(fullPath)!;
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)).Equals("asr-worker", StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.Combine(fullPath, "asr-worker");
    }

    public static string GetPythonExecutable(string? anchorPath) =>
        Path.Combine(GetWorkerDirectory(anchorPath), ".venv", "Scripts", "python.exe");

    public static string GetModelsDirectory(string? anchorPath) =>
        Path.Combine(GetWorkerDirectory(anchorPath), "models");
}
