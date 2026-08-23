using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AIMediaWorker.Capture;

public sealed class AudioCaptureService : IAsyncDisposable
{
    private readonly Channel<byte[]> _input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false
    });
    private WasapiCapture? _capture;
    private CancellationTokenSource? _processingCancellation;
    private Task? _processingTask;

    public bool IsCapturing => _capture is not null;
    public event EventHandler<ReadOnlyMemory<byte>>? Pcm16Available;
    public event EventHandler<Exception>? CaptureFailed;

    public Task StartAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        if (_capture is not null) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        while (_input.Reader.TryRead(out _)) { }
        using var enumerator = new MMDeviceEnumerator();
        using var device = string.IsNullOrWhiteSpace(deviceId) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia) : enumerator.GetDevice(deviceId);
        var capture = new WasapiCapture(device);
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
        _processingCancellation = new CancellationTokenSource();
        _processingTask = Task.Run(() => ProcessAsync(capture.WaveFormat, _processingCancellation.Token), CancellationToken.None);
        _capture = capture;
        capture.StartRecording();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch (InvalidOperationException) { }
            capture.Dispose();
        }
        _processingCancellation?.Cancel();
        if (_processingTask is not null) { try { await _processingTask.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        _processingCancellation?.Dispose(); _processingCancellation = null; _processingTask = null;
    }

    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var copy = GC.AllocateUninitializedArray<byte>(e.BytesRecorded);
        Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
        _input.Writer.TryWrite(copy);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) { if (e.Exception is not null) CaptureFailed?.Invoke(this, e.Exception); }

    private async Task ProcessAsync(WaveFormat sourceFormat, CancellationToken cancellationToken)
    {
        var provider = new BufferedWaveProvider(sourceFormat) { BufferDuration = TimeSpan.FromSeconds(2), DiscardOnBufferOverflow = true, ReadFully = false };
        using var resampler = new MediaFoundationResampler(provider, new WaveFormat(16_000, 16, 1)) { ResamplerQuality = 60 };
        var output = new byte[16_000];
        await foreach (var chunk in _input.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            provider.AddSamples(chunk, 0, chunk.Length);
            while (provider.BufferedBytes > sourceFormat.AverageBytesPerSecond / 20)
            {
                var count = resampler.Read(output, 0, output.Length);
                if (count <= 0) break;
                var result = GC.AllocateUninitializedArray<byte>(count);
                Buffer.BlockCopy(output, 0, result, 0, count);
                Pcm16Available?.Invoke(this, result);
            }
        }
    }
}
