using AIMediaWorker.Asr;
using System.Collections.Concurrent;

namespace AIMediaWorker.Tests;

public sealed class AsrTests
{
    [Fact]
    public void CrispAsrRuntimeUsesNearestProjectWorkerDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMediaWorker.Tests", Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "build", "asr-worker", "crispasr");
        Directory.CreateDirectory(runtime);
        try
        {
            Assert.Equal(Path.Combine(root, "build", "asr-worker"), AsrRuntimePaths.GetWorkerDirectory(runtime));
            Assert.Equal(Path.GetFullPath(runtime), AsrRuntimePaths.GetCrispAsrRuntimeDirectory(runtime));
            Assert.Equal(Path.Combine(root, "build", "asr-worker", "ffmpeg"), AsrRuntimePaths.GetFfmpegDirectory(runtime));
            Assert.Equal(Path.Combine(root, "build", "asr-worker", "ffmpeg", "ffmpeg.exe"), AsrRuntimePaths.GetFfmpegPath(runtime));
            Assert.Equal(Path.Combine(root, "build", "asr-worker", "models"), AsrRuntimePaths.GetModelsDirectory(runtime));
            Assert.Equal(Path.Combine(root, "build", "asr-worker", "crispasr", "crispasr.dll"), AsrRuntimePaths.CrispAsrDllPath.Replace(AsrRuntimePaths.CrispAsrRuntimeDirectory, runtime, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ProtocolUsesMicrosecondWireNames()
    {
        var request = AsrRequest.Create("transcribe_file", new { input = "C:\\media.mp4", timestamps = true }, "job-123");
        var json = AsrJson.Serialize(request);
        Assert.Contains("\"id\":\"job-123\"", json);
        Assert.Contains("\"command\":\"transcribe_file\"", json);
        var result = AsrJson.DeserializeEvent("{\"id\":\"job-123\",\"event\":\"segment\",\"segment\":{\"start_us\":100,\"end_us\":200,\"text\":\"hi\"}}");
        Assert.Equal(100, result.Segment!.StartMicroseconds);
    }

    [Fact]
    public void ProtocolPreservesKoreanSubtitleText()
    {
        var result = AsrJson.DeserializeEvent("{\"id\":\"job-utf8\",\"event\":\"segment\",\"segment\":{\"start_us\":0,\"end_us\":1000000,\"text\":\"안녕하세요, 한글 자막입니다.\"}}");

        Assert.Equal("안녕하세요, 한글 자막입니다.", result.Segment!.Text);
    }

    [Fact]
    public void ProtocolReadsModelDownloadProgress()
    {
        var result = AsrJson.DeserializeEvent("{\"id\":\"job-1\",\"event\":\"progress\",\"stage\":\"download\",\"progress\":0.42,\"model_progress\":0.6,\"message\":\"Qwen3-ASR-1.7B\",\"downloaded_bytes\":420,\"total_bytes\":1000}");

        Assert.Equal("download", result.Stage);
        Assert.Equal(0.42, result.Progress);
        Assert.Equal(0.6, result.ModelProgress);
        Assert.Equal(420, result.DownloadedBytes);
        Assert.Equal(1000, result.TotalBytes);
    }

    [Fact]
    public void ProtocolReadsModelLoadingElapsedTime()
    {
        var result = AsrJson.DeserializeEvent("{\"id\":\"job-1\",\"event\":\"progress\",\"stage\":\"loading\",\"elapsed_seconds\":12,\"message\":\"Qwen3-ASR + ForcedAligner (cuda:0)\"}");

        Assert.Equal("loading", result.Stage);
        Assert.Equal(12, result.ElapsedSeconds);
        Assert.Contains("cuda:0", result.Message);
    }

    [Fact]
    public void ProtocolUsesSnakeCaseInsideNestedArguments()
    {
        var request = AsrRequest.Create("transcribe_file", new
        {
            segmentation = new AsrSegmentationOptions(0.7, 7.0, 2, 42, 0.45, 18.0)
        });

        var json = AsrJson.Serialize(request);

        Assert.Contains("\"minimum_cue_seconds\":0.7", json);
        Assert.DoesNotContain("MinimumCueSeconds", json);
    }

    [Fact]
    public async Task CrispAsrRuntimeLifecycleRoundTripsWhenRuntimeIsAvailable()
    {
        var root = FindRepositoryRoot();
        var runtime = Path.Combine(root, "asr-worker", "crispasr");
        if (!File.Exists(Path.Combine(runtime, "crispasr.dll"))) return;
        await using var client = new AsrWorkerClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.StartAsync(runtime, cancellation.Token);
        Assert.Equal(AsrWorkerState.Ready, client.State);
        await client.ShutdownAsync(cancellation.Token);
        Assert.Equal(AsrWorkerState.NotStarted, client.State);
    }

    [Fact]
    public async Task CrispAsrClientCanRestartWithoutLeavingTheClientFailed()
    {
        var runtime = Path.Combine(FindRepositoryRoot(), "asr-worker", "crispasr");
        if (!File.Exists(Path.Combine(runtime, "crispasr.dll"))) return;
        await using var client = new AsrWorkerClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.StartAsync(runtime, cancellation.Token);

        await client.RestartAsync(cancellation.Token);

        Assert.Equal(AsrWorkerState.Ready, client.State);
        var error = await Assert.ThrowsAsync<AsrWorkerException>(() => client.StartStreamingAsync("auto", cancellation.Token));
        Assert.Equal("MODEL_NOT_LOADED", error.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CrispAsrClientReportsMissingModelWithoutCrashing()
    {
        var runtime = Path.Combine(FindRepositoryRoot(), "asr-worker", "crispasr");
        if (!File.Exists(Path.Combine(runtime, "crispasr.dll"))) return;
        await using var client = new AsrWorkerClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.StartAsync(runtime, cancellation.Token);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), AsrRuntimePaths.AsrModelFileName);
        var progress = new RecordingProgress();
        var error = await Assert.ThrowsAsync<AsrWorkerException>(() => client.LoadModelAsync(missing, null, "cpu", "float32", progress, cancellation.Token));
        Assert.Equal("MODEL_NOT_FOUND", error.Code);
        Assert.Contains(progress.Events, update => update.Event == "progress" && update.Stage == "loading");
        Assert.Equal(AsrWorkerState.Ready, client.State);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AIMediaWorker.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class RecordingProgress : IProgress<AsrEvent>
    {
        public ConcurrentQueue<AsrEvent> Events { get; } = new();
        public void Report(AsrEvent value) => Events.Enqueue(value);
    }
}
