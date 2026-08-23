using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace AIMediaWorker.Capture;

public sealed class CameraSession : IAsyncDisposable
{
    private MediaCapture? _capture;
    private MediaPlayer? _player;
    private MediaFrameSource? _frameSource;

    public sealed record CameraFormat(uint Width, uint Height, double FrameRate, MediaFrameFormat NativeFormat)
    {
        public string DisplayName => $"{Width} × {Height} @ {FrameRate:0.##} fps";
    }

    public MediaPlayer? Player => _player;
    public bool IsRunning => _capture is not null;
    public bool IsRecording { get; private set; }
    public IReadOnlyList<CameraFormat> AvailableFormats { get; private set; } = [];

    public async Task StartAsync(string? cameraDeviceId, string? microphoneDeviceId = null, int preferredWidth = 1920, int preferredHeight = 1080, int preferredFrameRate = 30, CancellationToken cancellationToken = default)
    {
        if (_capture is not null) return;
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = cameraDeviceId,
                AudioDeviceId = microphoneDeviceId,
                StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Auto
            }).AsTask(cancellationToken).ConfigureAwait(false);
            var frameSource = capture.FrameSources.Values.FirstOrDefault(source => source.Info.SourceKind == MediaFrameSourceKind.Color)
                ?? throw new InvalidOperationException("The selected camera has no color preview source.");
            AvailableFormats = frameSource.SupportedFormats.Where(format => format.VideoFormat is not null).Select(format => new CameraFormat(format.VideoFormat.Width, format.VideoFormat.Height, format.FrameRate.Denominator == 0 ? 0 : format.FrameRate.Numerator / (double)format.FrameRate.Denominator, format)).OrderByDescending(format => format.Width * format.Height).ThenByDescending(format => format.FrameRate).ToArray();
            var preferred = AvailableFormats.OrderBy(format => Math.Abs((long)format.Width - preferredWidth) + Math.Abs((long)format.Height - preferredHeight) + Math.Abs(format.FrameRate - preferredFrameRate) * 10).FirstOrDefault();
            if (preferred is not null) await frameSource.SetFormatAsync(preferred.NativeFormat).AsTask(cancellationToken).ConfigureAwait(false);
            var player = new MediaPlayer { AutoPlay = true, RealTimePlayback = true, IsLoopingEnabled = false };
            player.Source = MediaSource.CreateFromMediaFrameSource(frameSource);
            _capture = capture;
            _frameSource = frameSource;
            _player = player;
            player.Play();
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    public async Task ApplyFormatAsync(CameraFormat format, CancellationToken cancellationToken = default)
    {
        if (_frameSource is null) throw new InvalidOperationException("Camera preview is not running.");
        if (IsRecording) throw new InvalidOperationException("Camera format cannot change while recording.");
        await _frameSource.SetFormatAsync(format.NativeFormat).AsTask(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartRecordingAsync(StorageFile file, CancellationToken cancellationToken = default)
    {
        if (_capture is null) throw new InvalidOperationException("Camera preview is not running.");
        if (IsRecording) return;
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        await _capture.StartRecordToStorageFileAsync(profile, file).AsTask(cancellationToken).ConfigureAwait(false);
        IsRecording = true;
    }

    public async Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (_capture is null || !IsRecording) return;
        await _capture.StopRecordAsync().AsTask(cancellationToken).ConfigureAwait(false);
        IsRecording = false;
    }

    public ValueTask StopAsync()
    {
        if (IsRecording) throw new InvalidOperationException("Stop recording before stopping the camera session.");
        var player = Interlocked.Exchange(ref _player, null);
        player?.Pause(); player?.Dispose();
        var capture = Interlocked.Exchange(ref _capture, null);
        _frameSource = null;
        AvailableFormats = [];
        capture?.Dispose();
        IsRecording = false;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() { await StopAsync(); GC.SuppressFinalize(this); }
}
