namespace AIMediaWorker.Asr;

public interface IAsrEngine : IAsyncDisposable
{
    AsrWorkerState State { get; }
    event EventHandler<AsrWorkerState>? StateChanged;
    event EventHandler<string>? WorkerLog;
    event EventHandler<AsrEvent>? LiveResultReceived;
    Task StartAsync(string pythonExecutable, string workerScript, CancellationToken cancellationToken = default);
    Task RestartAsync(CancellationToken cancellationToken = default);
    Task LoadModelAsync(string modelPath, string? alignerPath, string device, string precision, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AsrEvent> TranscribeFileAsync(string path, string language, double chunkDurationSeconds = 30, bool useVad = true, AsrSegmentationOptions? segmentation = null, CancellationToken cancellationToken = default);
    Task CancelAsync(string requestId, CancellationToken cancellationToken = default);
    Task<string> StartStreamingAsync(string language, CancellationToken cancellationToken = default);
    Task PushAudioAsync(string streamId, ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default);
    Task StopStreamingAsync(string streamId, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
