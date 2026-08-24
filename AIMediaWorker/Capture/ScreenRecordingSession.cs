using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AIMediaWorker.Capture;

public enum ScreenCaptureTargetKind { Window, Region }

public sealed record ScreenCaptureTarget(ScreenCaptureTargetKind Kind, nint WindowHandle, Rectangle Region, string DisplayName)
{
    public static ScreenCaptureTarget ForWindow(nint handle, string displayName) => new(ScreenCaptureTargetKind.Window, handle, Rectangle.Empty, displayName);
    public static ScreenCaptureTarget ForRegion(Rectangle region) => new(ScreenCaptureTargetKind.Region, nint.Zero, region, $"{region.Width} × {region.Height} ({region.X}, {region.Y})");
}

public sealed class ScreenRecordingSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private Process? _videoProcess;
    private Task<string>? _videoErrorTask;
    private MMDeviceEnumerator? _audioDeviceEnumerator;
    private MMDevice? _audioDevice;
    private WasapiLoopbackCapture? _audioCapture;
    private WaveFileWriter? _audioWriter;
    private TaskCompletionSource _audioStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _temporaryDirectory;
    private string? _videoPath;
    private string? _audioPath;
    private string? _outputPath;
    private bool _disposed;

    public bool IsRecording { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }

    public async Task StartAsync(ScreenCaptureTarget target, string outputPath, int frameRate = 30, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRecording) throw new InvalidOperationException("Screen recording is already running.");
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("An output path is required.", nameof(outputPath));
        if (target.Kind == ScreenCaptureTargetKind.Window && target.WindowHandle == nint.Zero) throw new ArgumentException("A window must be selected.", nameof(target));
        if (target.Kind == ScreenCaptureTargetKind.Region && (target.Region.Width < 2 || target.Region.Height < 2)) throw new ArgumentException("The capture region is too small.", nameof(target));

        var sessionId = Guid.NewGuid().ToString("N");
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), "AIMediaWorker", "ScreenRecording", sessionId);
        Directory.CreateDirectory(_temporaryDirectory);
        _videoPath = Path.Combine(_temporaryDirectory, "video.mp4");
        _audioPath = Path.Combine(_temporaryDirectory, "system-audio.wav");
        _outputPath = Path.GetFullPath(outputPath);
        _audioStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _videoProcess = CreateVideoProcess(target, Math.Clamp(frameRate, 10, 60), _videoPath);
            if (!_videoProcess.Start()) throw new InvalidOperationException("FFmpeg did not start.");
            _videoErrorTask = _videoProcess.StandardError.ReadToEndAsync(cancellationToken);

            _audioDeviceEnumerator = new MMDeviceEnumerator();
            _audioDevice = _audioDeviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _audioCapture = new WasapiLoopbackCapture(_audioDevice);
            _audioWriter = new WaveFileWriter(_audioPath, _audioCapture.WaveFormat);
            _audioCapture.DataAvailable += OnAudioDataAvailable;
            _audioCapture.RecordingStopped += OnAudioRecordingStopped;
            _audioCapture.StartRecording();

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            if (_videoProcess.HasExited)
            {
                var error = _videoErrorTask is null ? string.Empty : await _videoErrorTask.ConfigureAwait(false);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "FFmpeg stopped before recording began." : error.Trim());
            }

            StartedAt = DateTimeOffset.Now;
            IsRecording = true;
        }
        catch
        {
            await StopCaptureProcessesAsync(CancellationToken.None).ConfigureAwait(false);
            CleanupTemporaryFiles();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _stopLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRecording) return;
            IsRecording = false;
            await StopCaptureProcessesAsync(cancellationToken).ConfigureAwait(false);
            if (_videoPath is null || _audioPath is null || _outputPath is null) throw new InvalidOperationException("The recording paths are unavailable.");
            if (!File.Exists(_videoPath) || new FileInfo(_videoPath).Length == 0) throw new InvalidOperationException("The screen recording did not produce video data.");
            var hasAudio = File.Exists(_audioPath) && new FileInfo(_audioPath).Length > 44;
            await MuxAsync(_videoPath, hasAudio ? _audioPath : null, _outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StartedAt = null;
            CleanupTemporaryFiles();
            _stopLock.Release();
        }
    }

    private static Process CreateVideoProcess(ScreenCaptureTarget target, int frameRate, string videoPath)
    {
        var startInfo = CreateFfmpegStartInfo();
        AddArguments(startInfo, "-hide_banner", "-loglevel", "error", "-f", "gdigrab", "-framerate", frameRate.ToString(CultureInfo.InvariantCulture), "-draw_mouse", "1");
        if (target.Kind == ScreenCaptureTargetKind.Region)
        {
            var width = target.Region.Width - target.Region.Width % 2;
            var height = target.Region.Height - target.Region.Height % 2;
            AddArguments(startInfo,
                "-offset_x", target.Region.X.ToString(CultureInfo.InvariantCulture),
                "-offset_y", target.Region.Y.ToString(CultureInfo.InvariantCulture),
                "-video_size", $"{width}x{height}", "-i", "desktop");
        }
        else
        {
            var handle = unchecked((ulong)target.WindowHandle.ToInt64());
            AddArguments(startInfo, "-i", $"hwnd=0x{handle:X}");
        }
        AddArguments(startInfo, "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-an", "-y", videoPath);
        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    private static async Task MuxAsync(string videoPath, string? audioPath, string outputPath, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateFfmpegStartInfo() };
        AddArguments(process.StartInfo, "-hide_banner", "-loglevel", "error", "-i", videoPath);
        if (audioPath is not null) AddArguments(process.StartInfo, "-i", audioPath);
        else AddArguments(process.StartInfo, "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000");
        AddArguments(process.StartInfo, "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", "-y", outputPath);
        if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start while finalizing the recording.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"FFmpeg exited with code {process.ExitCode}." : error.Trim());
    }

    private async Task StopCaptureProcessesAsync(CancellationToken cancellationToken)
    {
        var capture = Interlocked.Exchange(ref _audioCapture, null);
        if (capture is not null)
        {
            try
            {
                capture.StopRecording();
                await _audioStopped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
            finally
            {
                capture.DataAvailable -= OnAudioDataAvailable;
                capture.RecordingStopped -= OnAudioRecordingStopped;
                capture.Dispose();
            }
        }
        Interlocked.Exchange(ref _audioWriter, null)?.Dispose();
        Interlocked.Exchange(ref _audioDevice, null)?.Dispose();
        Interlocked.Exchange(ref _audioDeviceEnumerator, null)?.Dispose();

        var process = Interlocked.Exchange(ref _videoProcess, null);
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
                    catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                }
                if (process.ExitCode != 0)
                {
                    var error = _videoErrorTask is null ? string.Empty : await _videoErrorTask.ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error.Trim());
                }
            }
            finally { process.Dispose(); _videoErrorTask = null; }
        }
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs args)
    {
        var writer = _audioWriter;
        if (writer is null || args.BytesRecorded <= 0) return;
        writer.Write(args.Buffer, 0, args.BytesRecorded);
        writer.Flush();
    }

    private void OnAudioRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is null) _audioStopped.TrySetResult();
        else _audioStopped.TrySetException(args.Exception);
    }

    private static ProcessStartInfo CreateFfmpegStartInfo() => new()
    {
        FileName = "ffmpeg",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    }

    private void CleanupTemporaryFiles()
    {
        var directory = Interlocked.Exchange(ref _temporaryDirectory, null);
        _videoPath = null;
        _audioPath = null;
        _outputPath = null;
        if (directory is null) return;
        try
        {
            var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AIMediaWorker", "ScreenRecording")) + Path.DirectorySeparatorChar;
            var fullDirectory = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            if (!fullDirectory.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (IsRecording) await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _stopLock.Dispose();
        CleanupTemporaryFiles();
        GC.SuppressFinalize(this);
    }
}
