using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace AIMediaWorker.Capture;

public sealed class CameraSession : IAsyncDisposable
{
    private MediaCapture? _capture;
    private MediaPlayer? _player;
    private MediaFrameSource? _frameSource;
    private readonly SemaphoreSlim _recordingLock = new(1, 1);
    private LowLagMediaRecording? _recording;
    private StorageFile? _recordingFile;

    public sealed record CameraFormat(uint Width, uint Height, double FrameRate, MediaFrameFormat NativeFormat)
    {
        public string DisplayName => $"{Width} × {Height} @ {FrameRate:0.##} fps";
    }

    public MediaPlayer? Player => _player;
    public bool IsRunning => _capture is not null;
    public bool IsRecording { get; private set; }
    public bool IsPaused { get; private set; }
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
            }).AsTask(cancellationToken);
            var frameSource = capture.FrameSources.Values.FirstOrDefault(source => source.Info.SourceKind == MediaFrameSourceKind.Color)
                ?? throw new InvalidOperationException("The selected camera has no color preview source.");
            AvailableFormats = frameSource.SupportedFormats.Where(format => format.VideoFormat is not null).Select(format => new CameraFormat(format.VideoFormat.Width, format.VideoFormat.Height, format.FrameRate.Denominator == 0 ? 0 : format.FrameRate.Numerator / (double)format.FrameRate.Denominator, format)).OrderByDescending(format => format.Width * format.Height).ThenByDescending(format => format.FrameRate).ToArray();
            var preferred = AvailableFormats.OrderBy(format => Math.Abs((long)format.Width - preferredWidth) + Math.Abs((long)format.Height - preferredHeight) + Math.Abs(format.FrameRate - preferredFrameRate) * 10).FirstOrDefault();
            if (preferred is not null) await frameSource.SetFormatAsync(preferred.NativeFormat).AsTask(cancellationToken);
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
        await _frameSource.SetFormatAsync(format.NativeFormat).AsTask(cancellationToken);
    }

    public async Task StartRecordingAsync(StorageFile file, CancellationToken cancellationToken = default)
    {
        await _recordingLock.WaitAsync(cancellationToken);
        try
        {
            if (_capture is null) throw new InvalidOperationException("Camera preview is not running.");
            if (IsRecording) return;
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            var recording = await _capture.PrepareLowLagRecordToStorageFileAsync(profile, file).AsTask(cancellationToken);
            try
            {
                await recording.StartAsync().AsTask(cancellationToken);
            }
            catch
            {
                try { await recording.FinishAsync().AsTask(); } catch { }
                throw;
            }
            _recording = recording;
            _recordingFile = file;
            IsRecording = true;
            IsPaused = false;
        }
        finally { _recordingLock.Release(); }
    }

    public async Task PauseRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _recordingLock.WaitAsync(cancellationToken);
        try
        {
            if (_recording is null || !IsRecording || IsPaused) return;
            await _recording.PauseAsync(MediaCapturePauseBehavior.RetainHardwareResources).AsTask(cancellationToken);
            IsPaused = true;
        }
        finally { _recordingLock.Release(); }
    }

    public async Task ResumeRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _recordingLock.WaitAsync(cancellationToken);
        try
        {
            if (_recording is null || !IsRecording || !IsPaused) return;
            await _recording.ResumeAsync().AsTask(cancellationToken);
            IsPaused = false;
        }
        finally { _recordingLock.Release(); }
    }

    public async Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _recordingLock.WaitAsync(cancellationToken);
        try
        {
            if (_recording is null || !IsRecording) return;
            var recording = _recording;
            var recordingFile = _recordingFile;
            Exception? finishFailure = null;
            try
            {
                try { await recording.StopAsync().AsTask(cancellationToken); } catch { }
                try { await recording.FinishAsync().AsTask(); }
                catch (Exception exception) { finishFailure = exception; }
            }
            finally
            {
                _recording = null;
                _recordingFile = null;
                IsRecording = false;
                IsPaused = false;
            }

            if (finishFailure is null) return;
            if (recordingFile is not null && await WaitForUsableRecordingFileAsync(recordingFile)) return;
            throw new InvalidOperationException("Camera recording could not be finalized.", finishFailure);
        }
        finally { _recordingLock.Release(); }
    }

    private static bool IsUsableRecordingFile(StorageFile file)
    {
        try { return File.Exists(file.Path) && new FileInfo(file.Path).Length > 0; }
        catch { return false; }
    }

    private static async Task<bool> WaitForUsableRecordingFileAsync(StorageFile file)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (IsUsableRecordingFile(file)) return true;
            await Task.Delay(100);
        }
        return IsUsableRecordingFile(file);
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
