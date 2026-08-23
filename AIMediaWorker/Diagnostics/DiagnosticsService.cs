using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AIMediaWorker.Asr;
using AIMediaWorker.Playback;

namespace AIMediaWorker.Diagnostics;

public sealed record DiagnosticSnapshot(IReadOnlyDictionary<string, string> Values)
{
    public override string ToString() => string.Join(Environment.NewLine, Values.Select(item => $"{item.Key}: {item.Value}"));
}

public sealed class DiagnosticsService
{
    public async Task<DiagnosticSnapshot> CollectAsync(IPlaybackEngine playback, AsrWorkerState workerState, string pythonExecutable, string? asrModel, string? alignerModel, CancellationToken cancellationToken = default)
    {
        var graphics = new GraphicsCapabilityService().DetectRtxVideoSuperResolution();
        var values = new Dictionary<string, string>
        {
            ["Application version"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            ["Windows"] = Environment.OSVersion.VersionString,
            ["Architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            [".NET"] = Environment.Version.ToString(),
            ["Windows App SDK"] = typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version?.ToString() ?? "unknown",
            ["libmpv"] = playback.IsAvailable ? playback.LibraryVersion ?? "loaded" : "not loaded",
            ["Decoder"] = playback.DecoderDescription ?? "not playing",
            ["FFmpeg"] = await GetFirstLineAsync("ffmpeg", ["-version"], cancellationToken).ConfigureAwait(false),
            ["Python"] = await GetFirstLineAsync(pythonExecutable, ["--version"], cancellationToken).ConfigureAwait(false),
            ["ASR worker"] = workerState.ToString(),
            ["ASR model"] = asrModel ?? "not configured",
            ["Aligner model"] = alignerModel ?? "not configured",
            ["GPU"] = graphics.Adapters.Count == 0 ? "not detected" : string.Join("; ", graphics.Adapters.Select(adapter => $"{adapter.Name} ({adapter.DriverVersion ?? "unknown driver"})")),
            ["RTX Video Super Resolution"] = graphics.Status,
            ["Log directory"] = AppLog.DirectoryPath
        };
        var torch = await GetPythonRuntimeAsync(pythonExecutable, cancellationToken).ConfigureAwait(false);
        foreach (var item in torch) values[item.Key] = item.Value;
        return new DiagnosticSnapshot(values);
    }

    private static async Task<IReadOnlyDictionary<string, string>> GetPythonRuntimeAsync(string executable, CancellationToken cancellationToken)
    {
        const string code = "import json\ntry:\n import torch; print(json.dumps({'PyTorch':torch.__version__,'CUDA available':str(torch.cuda.is_available()),'CUDA runtime':str(torch.version.cuda)}))\nexcept Exception as e: print(json.dumps({'PyTorch':'unavailable','CUDA available':'false'}))";
        var output = await RunAsync(executable, ["-c", code], cancellationToken).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(output) ?? new Dictionary<string, string>(); }
        catch (JsonException) { return new Dictionary<string, string> { ["PyTorch"] = "unavailable" }; }
    }

    private static async Task<string> GetFirstLineAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
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
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken); var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false); var stderr = await error.ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(stdout) ? (string.IsNullOrWhiteSpace(stderr) ? "unavailable" : stderr.Trim()) : stdout.Trim();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return "unavailable"; }
    }
}
