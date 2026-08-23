using System.Threading.Channels;
using AIMediaWorker.Asr;

namespace AIMediaWorker.Capture;

public sealed class LiveAsrController : IAsyncDisposable
{
    private readonly AudioCaptureService _audioCapture;
    private readonly IAsrEngine _asr;
    private readonly Channel<ReadOnlyMemory<byte>> _audioQueue = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(24)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false
    });
    private CancellationTokenSource? _cancellation;
    private Task? _pump;
    private string? _streamId;

    public bool IsRunning => _streamId is not null;
    public event EventHandler<AsrEvent>? CaptionReceived;
    public event EventHandler<Exception>? Failed;

    public LiveAsrController(AudioCaptureService audioCapture, IAsrEngine asr)
    {
        _audioCapture = audioCapture;
        _asr = asr;
        _audioCapture.Pcm16Available += OnPcm16Available;
        _audioCapture.CaptureFailed += OnCaptureFailed;
        _asr.LiveResultReceived += OnLiveResult;
    }

    public async Task StartAsync(string? microphoneDeviceId, string language, CancellationToken cancellationToken = default)
    {
        if (_streamId is not null) return;
        while (_audioQueue.Reader.TryRead(out _)) { }
        _streamId = await _asr.StartStreamingAsync(language, cancellationToken).ConfigureAwait(false);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pump = Task.Run(() => PumpAsync(_streamId, _cancellation.Token), CancellationToken.None);
        try { await _audioCapture.StartAsync(microphoneDeviceId, cancellationToken).ConfigureAwait(false); }
        catch { await StopAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _audioCapture.StopAsync().ConfigureAwait(false);
        _cancellation?.Cancel();
        if (_pump is not null) { try { await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        var stream = Interlocked.Exchange(ref _streamId, null);
        if (stream is not null) await _asr.StopStreamingAsync(stream, cancellationToken).ConfigureAwait(false);
        _cancellation?.Dispose(); _cancellation = null; _pump = null;
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); } catch { }
        _audioCapture.Pcm16Available -= OnPcm16Available;
        _audioCapture.CaptureFailed -= OnCaptureFailed;
        _asr.LiveResultReceived -= OnLiveResult;
        await _audioCapture.DisposeAsync().ConfigureAwait(false);
    }

    private void OnPcm16Available(object? sender, ReadOnlyMemory<byte> data) => _audioQueue.Writer.TryWrite(data);
    private void OnCaptureFailed(object? sender, Exception exception) => Failed?.Invoke(this, exception);
    private void OnLiveResult(object? sender, AsrEvent result) => CaptionReceived?.Invoke(this, result);

    private async Task PumpAsync(string streamId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var audio in _audioQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await _asr.PushAudioAsync(streamId, audio, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Failed?.Invoke(this, exception); }
    }
}
