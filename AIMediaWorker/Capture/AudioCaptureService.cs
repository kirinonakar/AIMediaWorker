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
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _device;
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

        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        WasapiCapture? capture = null;
        CancellationTokenSource? processingCancellation = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            device = ResolveDevice(enumerator, deviceId);
            capture = new WasapiCapture(device);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            processingCancellation = new CancellationTokenSource();
            var sourceFormat = capture.WaveFormat;
            var processingTask = Task.Run(() => ProcessAsync(sourceFormat, processingCancellation.Token), CancellationToken.None);

            // Keep the endpoint alive for the lifetime of WasapiCapture. The camera
            // window supplies a Windows.Devices.Enumeration ID, while NAudio needs
            // the Core Audio endpoint ID; ResolveDevice adapts the two formats.
            capture.StartRecording();
            _deviceEnumerator = enumerator;
            _device = device;
            _processingCancellation = processingCancellation;
            _processingTask = processingTask;
            _capture = capture;
            return Task.CompletedTask;
        }
        catch
        {
            if (capture is not null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                try { capture.Dispose(); } catch (Exception) { }
            }
            processingCancellation?.Cancel();
            processingCancellation?.Dispose();
            device?.Dispose();
            enumerator?.Dispose();
            throw;
        }
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
        Interlocked.Exchange(ref _device, null)?.Dispose();
        Interlocked.Exchange(ref _deviceEnumerator, null)?.Dispose();
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

    private static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

        // DeviceInformation.FindAllAsync(DeviceClass.AudioCapture) returns a
        // Windows device path such as
        // \\?\SWD#MMDEVAPI#{0.0.1.00000000}.{...}#{...}. Core Audio's
        // IMMDeviceEnumerator.GetDevice expects only the endpoint portion:
        // {0.0.1.00000000}.{...}. Passing the former directly produces the
        // unhelpful E_INVALIDARG message "Value does not fall within the
        // expected range.".
        var endpointId = ExtractCoreAudioEndpointId(deviceId) ?? deviceId.Trim();
        return enumerator.GetDevice(endpointId);
    }

    internal static string? ExtractCoreAudioEndpointId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        var value = Uri.UnescapeDataString(deviceId.Trim());
        var marker = value.IndexOf("MMDEVAPI", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;

        var start = marker + "MMDEVAPI".Length;
        while (start < value.Length && (value[start] == '#' || value[start] == '\\' || value[start] == '/')) start++;
        if (start >= value.Length) return null;

        var end = value.IndexOf('#', start);
        if (end < 0) end = value.Length;
        var endpointId = value[start..end].Trim();
        return endpointId.Length == 0 ? null : endpointId;
    }

    private async Task ProcessAsync(WaveFormat sourceFormat, CancellationToken cancellationToken)
    {
        try
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { CaptureFailed?.Invoke(this, exception); }
    }
}
