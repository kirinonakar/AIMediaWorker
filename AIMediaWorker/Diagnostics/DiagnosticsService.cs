using System.Diagnostics;
using System.Reflection;
using AIMediaWorker.Asr;
using AIMediaWorker.Playback;

namespace AIMediaWorker.Diagnostics;

public sealed record DiagnosticSnapshot(IReadOnlyDictionary<string, string> Values)
{
    public override string ToString() => string.Join(Environment.NewLine, Values.Select(item => $"{item.Key}: {item.Value}"));
}

public sealed class DiagnosticsService
{
    public async Task<DiagnosticSnapshot> CollectAsync(IPlaybackEngine playback, AsrWorkerState workerState, string crispAsrRuntimeDirectory,
                                                        string? asrModel, string? alignerModel, CancellationToken cancellationToken = default)
    {
        crispAsrRuntimeDirectory = Path.GetFullPath(crispAsrRuntimeDirectory);
        var crispAsrLibrary = Path.Combine(crispAsrRuntimeDirectory, "crispasr.dll");
        var graphics = new GraphicsCapabilityService().DetectRtxVideoSuperResolution();
        var ffmpegPath = AsrRuntimePaths.TryGetFfmpegPath(crispAsrRuntimeDirectory) ?? "ffmpeg";
        var values = new Dictionary<string, string>
        {
            ["Application version"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            ["Windows"] = Environment.OSVersion.VersionString,
            ["Architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            [".NET"] = Environment.Version.ToString(),
            ["Windows App SDK"] = typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version?.ToString() ?? "unknown",
            ["libmpv"] = playback.IsAvailable ? playback.LibraryVersion ?? "loaded" : "not loaded",
            ["Decoder"] = playback.DecoderDescription ?? "not playing",
            ["FFmpeg"] = await GetFirstLineAsync(ffmpegPath, ["-version"], cancellationToken).ConfigureAwait(false),
            ["CrispASR runtime"] = File.Exists(crispAsrLibrary) ? crispAsrLibrary : "not found",
            ["ASR engine"] = "WinUI3 C# P/Invoke -> CrispASR C ABI",
            ["ASR worker"] = workerState.ToString(),
            ["ASR model"] = asrModel ?? "not configured",
            ["Aligner model"] = alignerModel ?? "not configured",
            ["GPU"] = graphics.Adapters.Count == 0 ? "not detected" : string.Join("; ", graphics.Adapters.Select(adapter => $"{adapter.Name} ({adapter.DriverVersion ?? "unknown driver"})")),
            ["RTX Video Super Resolution"] = $"{graphics.Status} App filter: {playback.RtxVideoSuperResolutionStatus}",
            ["Cold startup to first frame"] = StartupProfiler.LatestSummary ?? "not measured in this process",
            ["Log directory"] = AppLog.DirectoryPath
        };
        return new DiagnosticSnapshot(values);
    }

    private static async Task<string> GetFirstLineAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable) && !string.Equals(executable, "ffmpeg", StringComparison.OrdinalIgnoreCase)) return "not found";
        var output = await RunAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unavailable";
    }

    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return "unavailable";
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(stdout) ? (string.IsNullOrWhiteSpace(stderr) ? "unavailable" : stderr.Trim()) : stdout.Trim();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return "unavailable"; }
    }
}
