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
    public void WorkerDirectoryOverrideRedirectsRuntimeAndModelPaths()
    {
        try
        {
            var custom = Path.Combine(Path.GetTempPath(), "AIMediaWorker.Tests", Guid.NewGuid().ToString("N"), "custom-asr");
            AsrRuntimePaths.SetWorkerDirectory(custom);
            Assert.Equal(Path.GetFullPath(custom), AsrRuntimePaths.WorkerDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "crispasr", "crispasr.dll"), AsrRuntimePaths.CrispAsrDllPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "ffmpeg", "ffmpeg.exe"), AsrRuntimePaths.FfmpegPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "models", AsrRuntimePaths.AsrModelFileName), AsrRuntimePaths.AsrModelPath);
            Assert.Equal(Path.GetFullPath(custom), AsrRuntimePaths.GetWorkerDirectory(AsrRuntimePaths.CrispAsrRuntimeDirectory));
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "models"), AsrRuntimePaths.GetModelsDirectory(AsrRuntimePaths.CrispAsrRuntimeDirectory));
        }
        finally
        {
            // Null restores the original default: the asr-worker folder beside the executable.
            AsrRuntimePaths.SetWorkerDirectory(null);
        }

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "asr-worker"), AsrRuntimePaths.WorkerDirectory);
    }

    [Fact]
    public void InstallationServiceTargetsConfiguredWorkerDirectory()
    {
        var custom = Path.Combine(Path.GetTempPath(), "AIMediaWorker.Tests", Guid.NewGuid().ToString("N"), "custom-asr");
        Assert.Equal(Path.GetFullPath(custom), new AsrInstallationService(custom).WorkerDirectory);
        Assert.Equal(AsrRuntimePaths.WorkerDirectory, new AsrInstallationService().WorkerDirectory);
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
    public void LiveCaptionStabilizerCommitsOldWordsAndReplacesRecentTail()
    {
        var stabilizer = new LiveCaptionStabilizer(2_000_000);
        var first = LiveEvent("partial",
            Word("오늘", 500_000), Word("날씨가", 1_500_000), Word("정말", 2_500_000), Word("좋네요.", 3_500_000));
        var second = LiveEvent("partial",
            Word("날씨가", 1_500_000), Word("정말", 2_500_000), Word("좋네요.", 3_500_000), Word("그래서", 4_500_000), Word("나갑니다.", 5_500_000));

        Assert.Equal("오늘 날씨가 정말 좋네요.", stabilizer.Update(first));
        Assert.Equal("오늘 날씨가 정말 좋네요. 그래서 나갑니다.", stabilizer.Update(second));
        Assert.Equal("오늘 날씨가 정말 좋네요.", stabilizer.ConfirmedText);
        Assert.Equal("그래서 나갑니다.", stabilizer.ProvisionalText);
    }

    [Fact]
    public void LiveCaptionStabilizerReturnsOnlyNewlyCommittedDelta()
    {
        var stabilizer = new LiveCaptionStabilizer(2_000_000);

        var first = stabilizer.UpdateState(LiveEvent("partial",
            Word("I", 500_000), Word("think", 1_500_000), Word("this", 2_500_000), Word("works", 3_500_000)));
        var second = stabilizer.UpdateState(LiveEvent("partial",
            Word("think", 1_500_000), Word("this", 2_500_000), Word("works", 3_500_000), Word("very", 4_500_000), Word("well.", 5_500_000)));

        Assert.Equal("I think", first.CommittedDelta);
        Assert.Equal("this works", second.CommittedDelta);
        Assert.Equal("very well.", second.UnstableText);
        Assert.Equal("I think this works very well.", second.DisplayText);
    }

    [Fact]
    public void LiveCaptionStabilizerFinalCommitsUnchangedVisibleTail()
    {
        var stabilizer = new LiveCaptionStabilizer(2_000_000);
        var partial = LiveEvent("partial", Word("hello", 500_000), Word("world", 1_500_000));
        stabilizer.UpdateState(partial);

        var final = stabilizer.UpdateState(LiveEvent("final", Word("hello", 500_000), Word("world", 1_500_000)));

        Assert.Equal("hello world", final.DisplayText);
        Assert.Equal("hello world", final.CommittedDelta);
        Assert.True(final.IsFinal);
        Assert.Empty(final.UnstableText);
    }

    [Fact]
    public void LiveCaptionStabilizerUsesFuzzyOverlapWhenTimestampsAreUnavailable()
    {
        var stabilizer = new LiveCaptionStabilizer();
        stabilizer.Update(new AsrEvent { Event = "partial", Text = "그 사람이 오늘 서울에 왔습니다" });

        var display = stabilizer.Update(new AsrEvent { Event = "partial", Text = "사람은 오늘 서울에 왔습니다 그리고 출발합니다" });

        Assert.Equal(1, CountOccurrences(display, "오늘 서울에 왔습니다"));
        Assert.EndsWith("그리고 출발합니다", display);
    }

    [Fact]
    public void LiveCaptionStabilizerIgnoresSmallAlignerBoundaryJitter()
    {
        var stabilizer = new LiveCaptionStabilizer(1_000_000);
        stabilizer.Update(LiveEvent("partial", Word("오늘", 500_000), Word("좋아요.", 1_500_000), Word("계속", 2_500_000)));

        var display = stabilizer.Update(LiveEvent("partial", Word("좋아요.", 1_650_000), Word("계속", 2_650_000), Word("갑시다.", 3_650_000)));

        Assert.Equal(1, CountOccurrences(display, "좋아요."));
    }

    [Fact]
    public void LiveCaptionStabilizerKeepsJapaneseCharactersTogether()
    {
        var stabilizer = new LiveCaptionStabilizer(language: "ja");
        var source = new AsrSegment
        {
            StartMicroseconds = 0,
            EndMicroseconds = 4_000_000,
            Text = "おはよう。今日は晴れです。",
            Words =
            [
                Word("おはよう", 700_000), Word("。", 800_000),
                Word("今日", 1_500_000), Word("は", 1_700_000),
                Word("晴れ", 2_500_000), Word("です", 3_000_000), Word("。", 3_100_000)
            ]
        };

        var display = stabilizer.Update(new AsrEvent { Event = "partial", Segments = [source] });

        Assert.Equal(source.Text, display);
        Assert.DoesNotContain("お は", display, StringComparison.Ordinal);
        Assert.DoesNotContain("今 日", display, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveCaptionStabilizerPreservesKoreanWordSpacingWhenAlignerReturnsSubwords()
    {
        var stabilizer = new LiveCaptionStabilizer(language: "ko");
        var source = new AsrSegment
        {
            StartMicroseconds = 0,
            EndMicroseconds = 4_000_000,
            Text = "오늘 날씨가 정말 좋네요.",
            Words =
            [
                Word("오", 300_000), Word("늘", 600_000),
                Word("날", 1_000_000), Word("씨", 1_200_000), Word("가", 1_400_000),
                Word("정말", 2_200_000), Word("좋네요", 3_000_000), Word(".", 3_100_000)
            ]
        };

        var display = stabilizer.Update(new AsrEvent { Event = "partial", Segments = [source] });

        Assert.Equal(source.Text, display);
        Assert.DoesNotContain("오 늘", display, StringComparison.Ordinal);
        Assert.Contains("오늘 날씨가 정말 좋네요.", display, StringComparison.Ordinal);
    }

    private static AsrWord Word(string text, long endMicroseconds) => new()
    {
        StartMicroseconds = Math.Max(0, endMicroseconds - 400_000),
        EndMicroseconds = endMicroseconds,
        Text = text
    };

    private static AsrEvent LiveEvent(string eventName, params AsrWord[] words) => new()
    {
        Event = eventName,
        Segments =
        [
            new AsrSegment
            {
                StartMicroseconds = words[0].StartMicroseconds,
                EndMicroseconds = words[^1].EndMicroseconds,
                Text = string.Join(' ', words.Select(word => word.Text)),
                Words = words
            }
        ]
    };

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

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
