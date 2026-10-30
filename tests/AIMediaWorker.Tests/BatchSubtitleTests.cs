using AIMediaWorker.Asr;
using AIMediaWorker.Llm;
using AIMediaWorker.Settings;
using System.Runtime.CompilerServices;

namespace AIMediaWorker.Tests;

public sealed class BatchSubtitleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AIMediaWorker.BatchTests", Guid.NewGuid().ToString("N"));

    public BatchSubtitleTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ProcessesAudioAndVideoSequentiallyAndPreservesExistingSidecars()
    {
        var paths = new[] { Media("clip.mp4"), Media("clip.mp3") };
        var existing = Path.Combine(_directory, "clip.srt");
        await File.WriteAllTextAsync(existing, "keep me");
        var engine = new FakeEngine();
        var updates = new List<BatchSubtitleUpdate>();

        await new BatchSubtitleService(engine).RunAsync(paths, new AppSettings(), new InlineProgress<BatchSubtitleUpdate>(updates.Add));

        Assert.Equal(paths, engine.Inputs);
        Assert.Equal(1, engine.LoadCount);
        Assert.Equal("keep me", await File.ReadAllTextAsync(existing));
        var results = updates.Where(update => update.Stage == BatchSubtitleStage.Completed).ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal(Path.Combine(_directory, "clip (1).srt"), results[0].OriginalPath);
        Assert.Equal(Path.Combine(_directory, "clip (2).srt"), results[1].OriginalPath);
        Assert.All(results, result => Assert.Null(result.TranslationPath));
        var text = await File.ReadAllTextAsync(results[1].OriginalPath!);
        Assert.Contains("00:00:01,000 --> 00:00:02,000", text);
        Assert.Contains("原文 1", text);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task SavesSeparateTranslatedSrtWithMatchingTimestamps()
    {
        var path = Media("日本語.wav");
        var updates = new List<BatchSubtitleUpdate>();
        var translator = new LlmService(new FakeProvider(_ => Task.FromResult("{\"items\":[{\"index\":0,\"text\":\"번역 0\"},{\"index\":1,\"text\":\"번역 1\"}]}")), "test");

        await new BatchSubtitleService(new FakeEngine(), translator).RunAsync([path], new AppSettings(), new InlineProgress<BatchSubtitleUpdate>(updates.Add));

        var result = Assert.Single(updates, update => update.Stage == BatchSubtitleStage.Completed);
        Assert.Equal(Path.Combine(_directory, "日本語.srt"), result.OriginalPath);
        Assert.Equal(Path.Combine(_directory, "日本語.translated.srt"), result.TranslationPath);
        var original = await File.ReadAllTextAsync(result.OriginalPath!);
        var translated = await File.ReadAllTextAsync(result.TranslationPath!);
        Assert.Contains("原文 0", original);
        Assert.DoesNotContain("번역", original);
        Assert.Contains("번역 1", translated);
        Assert.DoesNotContain("原文", translated);
        Assert.Contains("00:00:01,000 --> 00:00:02,000", original);
        Assert.Contains("00:00:01,000 --> 00:00:02,000", translated);
    }

    [Fact]
    public async Task FileFailureDoesNotStopTheNextFileOrSavePartialOutput()
    {
        var paths = new[] { Media("bad.mp4"), Media("good.flac") };
        var engine = new FakeEngine { FailPath = paths[0] };
        var updates = new List<BatchSubtitleUpdate>();

        await new BatchSubtitleService(engine).RunAsync(paths, new AppSettings(), new InlineProgress<BatchSubtitleUpdate>(updates.Add));

        Assert.Single(updates, update => update.Stage == BatchSubtitleStage.Failed);
        Assert.Single(updates, update => update.Stage == BatchSubtitleStage.Completed);
        Assert.False(File.Exists(Path.ChangeExtension(paths[0], ".srt")));
        Assert.True(File.Exists(Path.ChangeExtension(paths[1], ".srt")));
    }

    [Fact]
    public async Task CancellationDuringRecognitionStopsQueueWithoutPublishingPartialSrt()
    {
        using var cancellation = new CancellationTokenSource();
        var paths = new[] { Media("first.mp4"), Media("second.mp3") };
        var engine = new FakeEngine { AfterSegment = () => cancellation.Cancel() };
        var updates = new List<BatchSubtitleUpdate>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BatchSubtitleService(engine).RunAsync(
            paths, new AppSettings(), new InlineProgress<BatchSubtitleUpdate>(updates.Add), cancellation.Token));

        Assert.Equal([paths[0]], engine.Inputs);
        Assert.Single(updates, update => update.Stage == BatchSubtitleStage.Cancelled);
        Assert.Empty(Directory.GetFiles(_directory, "*.srt"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task TranslationFailureKeepsOriginalAndContinuesQueue()
    {
        var paths = new[] { Media("first.mp4"), Media("second.wav") };
        var translator = new LlmService(new FakeProvider(_ => throw new InvalidOperationException("Provider unavailable")), "test");
        var updates = new List<BatchSubtitleUpdate>();

        await new BatchSubtitleService(new FakeEngine(), translator).RunAsync(paths, new AppSettings(), new InlineProgress<BatchSubtitleUpdate>(updates.Add));

        var failures = updates.Where(update => update.Stage == BatchSubtitleStage.Failed).ToArray();
        Assert.Equal(2, failures.Length);
        foreach (var failure in failures)
        {
            Assert.True(File.Exists(failure.OriginalPath));
            Assert.Null(failure.TranslationPath);
        }
        Assert.Empty(Directory.GetFiles(_directory, "*.translated.srt"));
    }

    [Fact]
    public async Task MissingTranslationsAreRetriedWithoutSavingMixedLanguageOutput()
    {
        var path = Media("missing.mkv");
        var provider = new FakeProvider(_ => Task.FromResult("{\"items\":[{\"index\":0,\"text\":\"번역\"}]}"));
        var updates = new List<BatchSubtitleUpdate>();

        await new BatchSubtitleService(new FakeEngine { SegmentCount = 5 }, new LlmService(provider, "test"))
            .RunAsync([path], new AppSettings(), new InlineProgress<BatchSubtitleUpdate>(updates.Add));

        Assert.Equal(3, provider.Calls);
        var failure = Assert.Single(updates, update => update.Stage == BatchSubtitleStage.Failed);
        Assert.True(File.Exists(failure.OriginalPath));
        Assert.Null(failure.TranslationPath);
        Assert.False(File.Exists(Path.Combine(_directory, "missing.translated.srt")));
    }

    [Fact]
    public async Task CancellationDuringTranslationPreservesOriginalAndStopsQueue()
    {
        using var cancellation = new CancellationTokenSource();
        var paths = new[] { Media("first.mp4"), Media("second.mp4") };
        var provider = new FakeProvider(token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(""); });
        var engine = new FakeEngine();
        var updates = new List<BatchSubtitleUpdate>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BatchSubtitleService(engine, new LlmService(provider, "test"))
            .RunAsync(paths, new AppSettings(), new InlineProgress<BatchSubtitleUpdate>(updates.Add), cancellation.Token));

        var result = Assert.Single(updates, update => update.Stage == BatchSubtitleStage.Cancelled);
        Assert.True(File.Exists(result.OriginalPath));
        Assert.Null(result.TranslationPath);
        Assert.Equal([paths[0]], engine.Inputs);
    }

    private string Media(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "fake media");
        return path;
    }

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(_directory)) File.Delete(path);
        Directory.Delete(_directory);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    private sealed class FakeProvider(Func<CancellationToken, Task<string>> generate) : ILlmProvider
    {
        public int Calls { get; private set; }
        public string Id => "fake";
        public string DisplayName => "Fake";
        public LlmProviderCapabilities Capabilities => new(false, false, false, false, true, true);
        public Task<IReadOnlyList<LlmModel>> GetModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LlmModel>>([]);
        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, LlmGenerationOptions options, CancellationToken cancellationToken = default)
        { Calls++; return generate(cancellationToken); }
    }

    private sealed class FakeEngine : IAsrEngine
    {
        public List<string> Inputs { get; } = [];
        public int LoadCount { get; private set; }
        public string? FailPath { get; init; }
        public Action? AfterSegment { get; init; }
        public int SegmentCount { get; init; } = 2;
        public AsrWorkerState State => AsrWorkerState.Ready;
        public event EventHandler<AsrWorkerState>? StateChanged { add { } remove { } }
        public event EventHandler<string>? WorkerLog { add { } remove { } }
        public event EventHandler<AsrEvent>? LiveResultReceived { add { } remove { } }
        public Task StartAsync(string directory, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadModelAsync(string path, string? aligner, string device, string precision, IProgress<AsrEvent>? progress = null, CancellationToken cancellationToken = default)
        { LoadCount++; return Task.CompletedTask; }
        public async IAsyncEnumerable<AsrEvent> TranscribeFileAsync(string path, string language, double chunkDurationSeconds = 30,
            bool useVad = true, AsrSegmentationOptions? segmentation = null, long startMicroseconds = 0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default, int? audioStreamIndex = null, long? durationMicroseconds = null)
        {
            Inputs.Add(path);
            await Task.Yield();
            for (var index = 0; index < SegmentCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AsrEvent { Event = "segment", Segment = new AsrSegment
                {
                    StartMicroseconds = index * 1_000_000, EndMicroseconds = (index + 1) * 1_000_000, Text = $"原文 {index}"
                } };
                AfterSegment?.Invoke();
                if (path == FailPath) throw new InvalidDataException("Bad audio");
            }
        }
        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelAsync(string requestId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> StartStreamingAsync(string language, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PushAudioAsync(string streamId, ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopStreamingAsync(string streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
