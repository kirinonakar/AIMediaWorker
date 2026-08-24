namespace AIMediaWorker.Asr;

public static class PythonEnvironment
{
    public static string ResolveExecutable(string configuredExecutable, string? anchorPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredExecutable);
        return Path.GetFullPath(AsrRuntimePaths.GetPythonExecutable(anchorPath));
    }
}
