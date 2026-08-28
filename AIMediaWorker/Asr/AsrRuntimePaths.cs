namespace AIMediaWorker.Asr;

public static class AsrRuntimePaths
{
    public const string AsrModelFileName = "Qwen3-ASR-1.7B-Q8_0.gguf";
    public const string AlignerModelFileName = "qwen3-forced-aligner-0.6b-q8_0.gguf";
    public const string WorkerFolderName = "asr-worker";

    private static string _workerDirectory = DefaultWorkerDirectory;

    /// <summary>The asr-worker folder currently used to locate the runtime, FFmpeg, and models.</summary>
    public static string WorkerDirectory => _workerDirectory;

    /// <summary>The default asr-worker folder beside the executable.</summary>
    public static string DefaultWorkerDirectory => Path.Combine(AppContext.BaseDirectory, WorkerFolderName);

    public static string CrispAsrRuntimeDirectory => Path.Combine(WorkerDirectory, "crispasr");
    public static string FfmpegDirectory => Path.Combine(WorkerDirectory, "ffmpeg");
    public static string ModelsDirectory => Path.Combine(WorkerDirectory, "models");
    public static string CrispAsrDllPath => Path.Combine(CrispAsrRuntimeDirectory, "crispasr.dll");
    public static string FfmpegPath => Path.Combine(FfmpegDirectory, "ffmpeg.exe");
    public static string AsrModelPath => Path.Combine(ModelsDirectory, AsrModelFileName);
    public static string AlignerModelPath => Path.Combine(ModelsDirectory, AlignerModelFileName);

    /// <summary>
    /// Resolves the configured asr-worker folder. Null, empty, or whitespace
    /// restores the default folder beside the executable.
    /// </summary>
    public static string ResolveWorkerDirectory(string? workerDirectory)
    {
        if (string.IsNullOrWhiteSpace(workerDirectory)) return DefaultWorkerDirectory;
        return Path.GetFullPath(workerDirectory.Trim());
    }

    /// <summary>
    /// Points the static asr-worker paths at a custom folder so the CrispASR
    /// runtime, FFmpeg, and models are found (and installed) there.
    /// </summary>
    public static void SetWorkerDirectory(string? workerDirectory) => _workerDirectory = ResolveWorkerDirectory(workerDirectory);

    public static string GetWorkerDirectory(string? anchorPath)
    {
        if (string.IsNullOrWhiteSpace(anchorPath)) return WorkerDirectory;

        var fullPath = Path.GetFullPath(anchorPath);
        var pathName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        if (pathName.Equals("asr-worker", StringComparison.OrdinalIgnoreCase)) return fullPath;
        if (pathName.Equals("crispasr", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(fullPath)?.FullName ?? WorkerDirectory;
        if (pathName.Equals("models", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(fullPath)?.FullName ?? WorkerDirectory;

        var directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath;
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        if (directoryName.Equals("asr-worker", StringComparison.OrdinalIgnoreCase)) return directory;
        if (directoryName.Equals("crispasr", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(directory)?.FullName ?? WorkerDirectory;
        if (directoryName.Equals("models", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(directory)?.FullName ?? WorkerDirectory;
        if (File.Exists(fullPath)) return directory;
        return Path.Combine(fullPath, "asr-worker");
    }

    public static string GetCrispAsrRuntimeDirectory(string? anchorPath)
    {
        var worker = GetWorkerDirectory(anchorPath);
        return Path.Combine(worker, "crispasr");
    }

    public static string GetFfmpegDirectory(string? anchorPath) =>
        Path.Combine(GetWorkerDirectory(anchorPath), "ffmpeg");

    public static string GetFfmpegPath(string? anchorPath) =>
        Path.Combine(GetFfmpegDirectory(anchorPath), "ffmpeg.exe");

    public static string? TryGetFfmpegPath(string? anchorPath)
    {
        var worker = GetWorkerDirectory(anchorPath);
        var localPath = Path.Combine(worker, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(localPath) && new FileInfo(localPath).Length > 0) return localPath;

        // Keep installations made by earlier builds usable if they placed the
        // executable directly under asr-worker.
        var legacyPath = Path.Combine(worker, "ffmpeg.exe");
        return File.Exists(legacyPath) && new FileInfo(legacyPath).Length > 0 ? legacyPath : null;
    }

    public static string GetModelsDirectory(string? anchorPath) =>
        Path.Combine(GetWorkerDirectory(anchorPath), "models");
}
