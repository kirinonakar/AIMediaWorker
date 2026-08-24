using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIMediaWorker.Asr;

public sealed record AsrInstallationProgress(string Stage, string Message, double? Progress = null, long DownloadedBytes = 0, long TotalBytes = 0);

public sealed class AsrInstallationService
{
    private static readonly string[] RequiredModules = ["qwen_asr", "torch", "torchaudio", "numpy", "soundfile", "silero_vad"];
    private readonly string _workerDirectory;
    private readonly string _pythonExecutable;
    private readonly string _requirementsFile;
    private readonly string _modelInstallerScript;
    private readonly string _modelsDirectory;

    public AsrInstallationService(string? workerDirectory = null)
    {
        _workerDirectory = Path.GetFullPath(workerDirectory ?? AsrRuntimePaths.WorkerDirectory);
        _pythonExecutable = Path.Combine(_workerDirectory, ".venv", "Scripts", "python.exe");
        _requirementsFile = Path.Combine(_workerDirectory, "requirements.txt");
        _modelInstallerScript = Path.Combine(_workerDirectory, "install_models.py");
        _modelsDirectory = Path.Combine(_workerDirectory, "models");
    }

    public async Task InstallAsync(IProgress<AsrInstallationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateWorkerFiles();
        Directory.CreateDirectory(_workerDirectory);
        Directory.CreateDirectory(_modelsDirectory);

        if (File.Exists(_pythonExecutable) && await TryRunProcessAsync(_pythonExecutable, ["--version"], cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(new("environment-skipped", _pythonExecutable, 0.1));
        }
        else
        {
            progress?.Report(new("environment", _workerDirectory, 0.02));
            await CreateVirtualEnvironmentAsync(progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("environment-complete", _pythonExecutable, 0.1));
        }

        if (await RequirementsAreInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(new("requirements-skipped", _requirementsFile, 0.35));
        }
        else
        {
            progress?.Report(new("requirements", _requirementsFile, null));
            await RunProcessAsync(
                _pythonExecutable,
                ["-m", "pip", "install", "--disable-pip-version-check", "-r", _requirementsFile],
                line => progress?.Report(new("requirements", line, null)),
                cancellationToken).ConfigureAwait(false);
            await WriteRequirementsStampAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new("requirements-complete", _requirementsFile, 0.35));
        }

        progress?.Report(new("models", _modelsDirectory, 0.35));
        await RunProcessAsync(
            _pythonExecutable,
            ["-u", _modelInstallerScript],
            line => ReportModelProgress(line, progress),
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new("complete", _modelsDirectory, 1.0));
    }

    private void ValidateWorkerFiles()
    {
        if (!File.Exists(_requirementsFile)) throw new FileNotFoundException("ASR requirements.txt was not found.", _requirementsFile);
        if (!File.Exists(_modelInstallerScript)) throw new FileNotFoundException("ASR model installer was not found.", _modelInstallerScript);
    }

    private async Task CreateVirtualEnvironmentAsync(IProgress<AsrInstallationProgress>? progress, CancellationToken cancellationToken)
    {
        var candidates = new (string FileName, string[] Prefix)[]
        {
            ("py", ["-3.12"]),
            ("py", ["-3.11"]),
            ("python", [])
        };
        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                var arguments = candidate.Prefix.Concat(new[] { "-m", "venv", Path.Combine(_workerDirectory, ".venv") }).ToArray();
                progress?.Report(new("environment", $"{candidate.FileName} {string.Join(' ', candidate.Prefix)}".Trim(), 0.05));
                await RunProcessAsync(candidate.FileName, arguments, line => progress?.Report(new("environment", line, 0.05)), cancellationToken).ConfigureAwait(false);
                if (File.Exists(_pythonExecutable)) return;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                failures.Add($"{candidate.FileName} {string.Join(' ', candidate.Prefix)}: {exception.Message}");
            }
        }
        throw new InvalidOperationException("Python 3.11 or 3.12 is required to create the ASR environment. " + string.Join(" | ", failures));
    }

    private async Task<bool> RequirementsAreInstalledAsync(CancellationToken cancellationToken)
    {
        var stampPath = GetRequirementsStampPath();
        var expectedHash = await GetRequirementsHashAsync(cancellationToken).ConfigureAwait(false);
        var stampExists = File.Exists(stampPath);
        if (stampExists)
        {
            var installedHash = (await File.ReadAllTextAsync(stampPath, cancellationToken).ConfigureAwait(false)).Trim();
            if (!string.Equals(installedHash, expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
        }

        var moduleList = string.Join(',', RequiredModules.Select(module => $"'{module}'"));
        var check = $"import importlib.util,sys;sys.exit(0 if all(importlib.util.find_spec(x) for x in [{moduleList}]) else 1)";
        if (await TryRunProcessAsync(_pythonExecutable, ["-c", check], cancellationToken).ConfigureAwait(false))
        {
            if (!stampExists) await WriteRequirementsStampAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private async Task WriteRequirementsStampAsync(CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(GetRequirementsStampPath(), await GetRequirementsHashAsync(cancellationToken).ConfigureAwait(false), Encoding.ASCII, cancellationToken).ConfigureAwait(false);

    private string GetRequirementsStampPath() => Path.Combine(_workerDirectory, ".venv", ".aimediaworker-requirements.sha256");

    private async Task<string> GetRequirementsHashAsync(CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(_requirementsFile, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void ReportModelProgress(string line, IProgress<AsrInstallationProgress>? progress)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var kind = root.GetProperty("kind").GetString() ?? "models";
            if (kind == "error") throw new InvalidOperationException(root.GetProperty("message").GetString());
            var modelProgress = root.TryGetProperty("progress", out var value) ? value.GetDouble() : 0;
            var overall = kind switch
            {
                "asr" => 0.35 + modelProgress * 0.45,
                "aligner" => 0.8 + modelProgress * 0.2,
                "complete" => 1.0,
                _ => 0.35
            };
            var message = root.TryGetProperty("message", out var messageValue) ? messageValue.GetString() ?? string.Empty : string.Empty;
            var downloaded = root.TryGetProperty("downloaded_bytes", out var downloadedValue) ? downloadedValue.GetInt64() : 0;
            var total = root.TryGetProperty("total_bytes", out var totalValue) ? totalValue.GetInt64() : 0;
            progress?.Report(new(kind, message, overall, downloaded, total));
        }
        catch (JsonException)
        {
            progress?.Report(new("models", line, null));
        }
    }

    private static async Task<bool> TryRunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            await RunProcessAsync(fileName, arguments, null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static async Task RunProcessAsync(string fileName, IReadOnlyList<string> arguments, Action<string>? output, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Failed to start {fileName}.");
        using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var stdout = ReadLinesAsync(process.StandardOutput, output, cancellationToken);
        var stderrLines = new List<string>();
        var stderr = ReadLinesAsync(process.StandardError, line => { stderrLines.Add(line); output?.Invoke(line); }, cancellationToken);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException(stderrLines.LastOrDefault() ?? $"{fileName} exited with code {process.ExitCode}.");
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string>? output, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            if (!string.IsNullOrWhiteSpace(line)) output?.Invoke(line);
    }
}
