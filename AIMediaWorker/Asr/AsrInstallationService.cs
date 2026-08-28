using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;

namespace AIMediaWorker.Asr;

public sealed record AsrInstallationProgress(string Stage, string Message, double? Progress = null, long DownloadedBytes = 0, long TotalBytes = 0);

public sealed class AsrInstallationService
{
    // CrispASR's qwen3 backend consumes its own single-file qwen3asr GGUF.
    // The similarly named ggml-org file is a llama.cpp qwen3vl + mmproj pair
    // and cannot be opened through the CrispASR C ABI.
    private const string AsrModelUrl = "https://huggingface.co/cstr/qwen3-asr-1.7b-GGUF/resolve/main/qwen3-asr-1.7b-q8_0.gguf?download=true";
    private const string AlignerModelUrl = "https://huggingface.co/cstr/qwen3-forced-aligner-0.6b-GGUF/resolve/main/qwen3-forced-aligner-0.6b-q8_0.gguf?download=true";

    // Keep the native runtime version aligned with the prebuilt runtime already
    // shipped by the application. The archive contains the complete bin folder,
    // including the CUDA and ggml DLLs required by crispasr.dll.
    private const string CrispAsrArchiveUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.29/libcrispasr-windows-x86_64-cuda.tar.gz";

    // Gyan's Windows build is linked from ffmpeg.org's Windows download page.
    // The ZIP is used so extraction does not require a third-party 7-Zip install.
    private const string FfmpegArchiveUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.1-essentials_build.zip";

    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly string _modelsDirectory;
    private readonly HttpClient _http;

    /// <summary>The asr-worker folder the runtime, FFmpeg, and models are installed into.</summary>
    public string WorkerDirectory { get; }

    public AsrInstallationService(string? workerDirectory = null, HttpClient? httpClient = null)
    {
        WorkerDirectory = Path.GetFullPath(workerDirectory ?? AsrRuntimePaths.WorkerDirectory);
        _modelsDirectory = Path.Combine(WorkerDirectory, "models");
        _http = httpClient ?? SharedHttp;
    }

    public async Task InstallAsync(IProgress<AsrInstallationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(WorkerDirectory);

        await EnsureCrispAsrAsync(0.0, 0.20, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new("runtime-complete", "CrispASR", 0.20));

        await EnsureFfmpegAsync(0.20, 0.30, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new("requirements-complete", "FFmpeg", 0.30));

        Directory.CreateDirectory(_modelsDirectory);
        await DownloadModelAsync("asr", AsrModelUrl, AsrRuntimePaths.AsrModelFileName, 0.30, 0.65, progress, cancellationToken,
            CrispAsrModelFormat.IsCrispAsrQwen3Model).ConfigureAwait(false);
        await DownloadModelAsync("aligner", AlignerModelUrl, AsrRuntimePaths.AlignerModelFileName, 0.65, 1.0, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new("complete", _modelsDirectory, 1.0));
    }

    private async Task EnsureCrispAsrAsync(double rangeStart, double rangeEnd,
                                           IProgress<AsrInstallationProgress>? progress,
                                           CancellationToken cancellationToken)
    {
        var runtimeDirectory = Path.Combine(WorkerDirectory, "crispasr");
        var library = Path.Combine(runtimeDirectory, "crispasr.dll");
        if (IsNonEmptyFile(library))
        {
            progress?.Report(new("runtime-skipped", "CrispASR", rangeEnd));
            return;
        }

        var archivePath = Path.Combine(WorkerDirectory, $".crispasr-{Guid.NewGuid():N}.tar.gz");
        var extractionDirectory = Path.Combine(WorkerDirectory, $".crispasr-extract-{Guid.NewGuid():N}");
        try
        {
            await DownloadArchiveAsync("runtime", "CrispASR", CrispAsrArchiveUrl, archivePath,
                rangeStart, rangeEnd, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(extractionDirectory);
            ExtractTarGZip(archivePath, extractionDirectory);
            InstallCrispAsrFiles(extractionDirectory, runtimeDirectory);

            if (!IsNonEmptyFile(library))
                throw new InvalidOperationException("The downloaded CrispASR archive did not contain crispasr.dll.");
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(extractionDirectory);
        }
    }

    private async Task EnsureFfmpegAsync(double rangeStart, double rangeEnd,
                                         IProgress<AsrInstallationProgress>? progress,
                                         CancellationToken cancellationToken)
    {
        var existingPath = FindInstalledFfmpegPath(WorkerDirectory) ?? FindFfmpegOnPath();
        if (existingPath is not null)
        {
            progress?.Report(new("requirements-skipped", "FFmpeg", rangeEnd));
            return;
        }

        var archivePath = Path.Combine(WorkerDirectory, $".ffmpeg-{Guid.NewGuid():N}.zip");
        var extractionDirectory = Path.Combine(WorkerDirectory, $".ffmpeg-extract-{Guid.NewGuid():N}");
        var ffmpegDirectory = Path.Combine(WorkerDirectory, "ffmpeg");
        try
        {
            await DownloadArchiveAsync("requirements", "FFmpeg", FfmpegArchiveUrl, archivePath,
                rangeStart, rangeEnd, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(extractionDirectory);
            ZipFile.ExtractToDirectory(archivePath, extractionDirectory, overwriteFiles: true);
            InstallFfmpegFiles(extractionDirectory, ffmpegDirectory);

            if (!IsNonEmptyFile(Path.Combine(ffmpegDirectory, "ffmpeg.exe")))
                throw new InvalidOperationException("The downloaded FFmpeg archive did not contain ffmpeg.exe.");
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(extractionDirectory);
        }
    }

    private async Task DownloadArchiveAsync(string stage, string displayName, string url, string destination,
                                             double rangeStart, double rangeEnd,
                                             IProgress<AsrInstallationProgress>? progress,
                                             CancellationToken cancellationToken)
    {
        await DownloadFileAsync(stage, displayName, url, destination, rangeStart, rangeEnd, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadFileAsync(string stage, string displayName, string url, string destination,
                                         double rangeStart, double rangeEnd,
                                         IProgress<AsrInstallationProgress>? progress,
                                         CancellationToken cancellationToken)
    {
        var temporary = destination + ".download";
        try
        {
            progress?.Report(new(stage, displayName, rangeStart));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                var fileProgress = total > 0 ? Math.Clamp((double)downloaded / total, 0, 1) : 0;
                progress?.Report(new(stage, displayName,
                    rangeStart + (rangeEnd - rangeStart) * fileProgress, downloaded, total));
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (downloaded == 0) throw new InvalidOperationException($"The {displayName} download was empty.");
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(new(stage, displayName, rangeEnd, downloaded, total));
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void ExtractTarGZip(string archivePath, string destinationDirectory)
    {
        using var archive = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destinationDirectory, overwriteFiles: true);
    }

    private static void InstallCrispAsrFiles(string extractionDirectory, string runtimeDirectory)
    {
        var library = FindFile(extractionDirectory, "crispasr.dll")
            ?? throw new InvalidOperationException("The downloaded CrispASR archive did not contain crispasr.dll.");
        var sourceDirectory = Directory.GetParent(library)?.FullName ?? extractionDirectory;
        CopyDirectory(sourceDirectory, runtimeDirectory);
        CopyRuntimeNotices(Directory.GetParent(sourceDirectory)?.FullName ?? sourceDirectory, runtimeDirectory);
    }

    private static void InstallFfmpegFiles(string extractionDirectory, string ffmpegDirectory)
    {
        var executable = FindFile(extractionDirectory, "ffmpeg.exe")
            ?? throw new InvalidOperationException("The downloaded FFmpeg archive did not contain ffmpeg.exe.");
        var sourceDirectory = Directory.GetParent(executable)?.FullName ?? extractionDirectory;
        CopyDirectory(sourceDirectory, ffmpegDirectory);
        CopyRuntimeNotices(Directory.GetParent(sourceDirectory)?.FullName ?? sourceDirectory, ffmpegDirectory);
    }

    private static string? FindFile(string rootDirectory, string fileName) =>
        Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var destinationFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
    }

    private static void CopyRuntimeNotices(string packageDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(packageDirectory)) return;

        foreach (var sourceFile in Directory.EnumerateFiles(packageDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(sourceFile);
            if (!name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("THIRD_PARTY", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("README", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(sourceFile, Path.Combine(destinationDirectory, name), overwrite: true);
        }
    }

    private async Task DownloadModelAsync(string stage, string url, string fileName, double rangeStart, double rangeEnd,
                                          IProgress<AsrInstallationProgress>? progress, CancellationToken cancellationToken,
                                          Func<string, bool>? existingFileValidator = null)
    {
        var destination = Path.Combine(_modelsDirectory, fileName);
        if (IsNonEmptyFile(destination) &&
            (existingFileValidator is null || existingFileValidator(destination)))
        {
            progress?.Report(new($"{stage}-skipped", fileName, rangeEnd));
            return;
        }

        var temporary = destination + ".download";
        try
        {
            progress?.Report(new(stage, fileName, rangeStart));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                var fileProgress = total > 0 ? Math.Clamp((double)downloaded / total, 0, 1) : 0;
                progress?.Report(new(stage, fileName,
                    rangeStart + (rangeEnd - rangeStart) * fileProgress, downloaded, total));
            }
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (downloaded == 0) throw new InvalidOperationException($"The model download for {fileName} was empty.");
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(new(stage, fileName, rangeEnd, downloaded, total));
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static bool IsNonEmptyFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private static string? FindInstalledFfmpegPath(string workerDirectory)
    {
        var localPath = Path.Combine(workerDirectory, "ffmpeg", "ffmpeg.exe");
        if (IsNonEmptyFile(localPath)) return localPath;

        var legacyPath = Path.Combine(workerDirectory, "ffmpeg.exe");
        return IsNonEmptyFile(legacyPath) ? legacyPath : null;
    }

    private static string? FindFfmpegOnPath()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable)) return null;

        foreach (var pathEntry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = pathEntry.Trim().Trim('"');
            if (directory.Length == 0) continue;

            var executable = Path.Combine(directory, "ffmpeg.exe");
            if (IsNonEmptyFile(executable)) return executable;
        }

        return null;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
