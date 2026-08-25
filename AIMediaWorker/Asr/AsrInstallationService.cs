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
    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly string _workerDirectory;
    private readonly string _modelsDirectory;

    public AsrInstallationService(string? workerDirectory = null)
    {
        _workerDirectory = Path.GetFullPath(workerDirectory ?? AsrRuntimePaths.WorkerDirectory);
        _modelsDirectory = Path.Combine(_workerDirectory, "models");
    }

    public async Task InstallAsync(IProgress<AsrInstallationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateNativeRuntime();
        Directory.CreateDirectory(_modelsDirectory);
        progress?.Report(new("runtime", "CrispASR native runtime is ready.", 0.05));

        await DownloadModelAsync("asr", AsrModelUrl, AsrRuntimePaths.AsrModelFileName, 0.05, 0.55, progress, cancellationToken,
            CrispAsrModelFormat.IsCrispAsrQwen3Model).ConfigureAwait(false);
        await DownloadModelAsync("aligner", AlignerModelUrl, AsrRuntimePaths.AlignerModelFileName, 0.55, 1.0, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new("complete", _modelsDirectory, 1.0));
    }

    private void ValidateNativeRuntime()
    {
        var crispasr = AsrRuntimePaths.GetCrispAsrRuntimeDirectory(_workerDirectory);
        var library = Path.Combine(crispasr, "crispasr.dll");
        if (!File.Exists(library)) throw new FileNotFoundException("The prebuilt CrispASR runtime was not found.", library);
    }

    private async Task DownloadModelAsync(string stage, string url, string fileName, double rangeStart, double rangeEnd,
                                          IProgress<AsrInstallationProgress>? progress, CancellationToken cancellationToken,
                                          Func<string, bool>? existingFileValidator = null)
    {
        var destination = Path.Combine(_modelsDirectory, fileName);
        if (File.Exists(destination) && new FileInfo(destination).Length > 0 &&
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
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                var fileProgress = total > 0 ? Math.Clamp((double)downloaded / total, 0, 1) : 0;
                progress?.Report(new(stage, fileName, rangeStart + (rangeEnd - rangeStart) * fileProgress, downloaded, total));
            }
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (downloaded == 0) throw new InvalidOperationException($"The model download for {fileName} was empty.");
            File.Move(temporary, destination, true);
            progress?.Report(new(stage, fileName, rangeEnd, downloaded, total));
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
