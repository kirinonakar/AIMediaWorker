using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using static AIMediaWorker.Capture.MediaFoundationInterop;
using MfMediaBuffer = NAudio.MediaFoundation.IMFMediaBuffer;
using MfMediaType = NAudio.MediaFoundation.IMFMediaType;
using MfSample = NAudio.MediaFoundation.IMFSample;
using MfSinkWriter = NAudio.MediaFoundation.IMFSinkWriter;

namespace AIMediaWorker.Capture;

/// <summary>
/// Records a desktop region into an MP4 file (H.264) by grabbing GDI frames and encoding them
/// through a Media Foundation sink writer on a dedicated worker thread. Pausing skips frames
/// while shifting timestamps so the saved video plays back continuously.
/// </summary>
internal sealed class MediaFoundationH264Recorder : IDisposable
{
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");   // 'vids'
    private static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");  // 'H264'
    private static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71"); // BI_RGB 32bpp
    private static readonly Guid MajorTypeAttribute = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubtypeAttribute = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AvgBitrateAttribute = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid FrameRateAttribute = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid FrameSizeAttribute = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid InterlaceModeAttribute = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");

    private const uint MfVideoInterlaceProgressive = 2;
    private const long HundredNanosecondsPerSecond = 10_000_000;
    private const int AudioSampleRate = 48_000;
    private const int AudioBitsPerSample = 16;
    private const int AudioChannels = 2;
    private const int AudioBitrate = 192_000;
    private const uint CoinitMultithreaded = 0x0;
    private const int SFalse = 1;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private static readonly object StartupGate = new();
    private static int _startupCount;

    private readonly string _outputPath;
    private readonly RECT _bounds;
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameRate;
    private readonly long _frameDuration;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private CancellationTokenSource? _cancellation;
    private Task _runTask = Task.CompletedTask;
    private volatile bool _paused;
    private long _pauseStartMilliseconds;
    private long _pausedTotalMilliseconds;
    private bool _disposed;

    public string OutputPath => _outputPath;
    public Exception? Failure { get; private set; }
    public bool IsPaused => _paused;

    public TimeSpan Elapsed
    {
        get
        {
            var activePause = _paused ? _clock.ElapsedMilliseconds - _pauseStartMilliseconds : 0;
            return TimeSpan.FromMilliseconds(Math.Max(0, _clock.ElapsedMilliseconds - _pausedTotalMilliseconds - activePause));
        }
    }

    private MediaFoundationH264Recorder(string outputPath, RECT bounds, int frameRate)
    {
        _outputPath = outputPath;
        _frameRate = Math.Clamp(frameRate, 10, 60);
        _bounds = MakeEven(bounds);
        _width = _bounds.Width;
        _height = _bounds.Height;
        _frameDuration = HundredNanosecondsPerSecond / _frameRate;
    }

    public static MediaFoundationH264Recorder Start(string outputPath, RECT bounds, int frameRate = 30)
    {
        var recorder = new MediaFoundationH264Recorder(outputPath, bounds, frameRate);
        var cancellation = new CancellationTokenSource();
        recorder._cancellation = cancellation;
        recorder._runTask = Task.Run(() => recorder.Run(cancellation.Token));
        return recorder;
    }

    public void Pause()
    {
        if (_paused) return;
        _pauseStartMilliseconds = _clock.ElapsedMilliseconds;
        _paused = true;
    }

    public void Resume()
    {
        if (!_paused) return;
        _pausedTotalMilliseconds += _clock.ElapsedMilliseconds - _pauseStartMilliseconds;
        _paused = false;
    }

    /// <summary>Stops recording and waits until the sink writer finalized the output file.</summary>
    public async Task StopAsync()
    {
        _cancellation?.Cancel();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Failure ??= exception;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private void Run(CancellationToken cancellation)
    {
        var comInitialized = false;
        MfSinkWriter? writer = null;
        try
        {
            var coInitializeResult = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            comInitialized = coInitializeResult is 0 or SFalse || coInitializeResult == RpcEChangedMode;

            lock (StartupGate)
            {
                if (_startupCount == 0) MediaFoundationApi.Startup();
                _startupCount++;
            }

            NAudio.MediaFoundation.MediaFoundationInterop.MFCreateSinkWriterFromURL(
                _outputPath, null!, null!, out writer);

            using var audio = new LoopbackAudioSource(CreateAudioFormat());
            var videoTargetType = CreateVideoType(VideoFormatH264, ComputeBitrate(_width, _height, _frameRate), target: true);
            try
            {
                writer!.AddStream(videoTargetType, out var videoStreamIndex);
                var audioTargetSelection = MediaFoundationEncoder.SelectMediaType(
                    AudioSubtypes.MFAudioFormat_AAC, audio.OutputFormat, AudioBitrate);
                var audioTargetType = audioTargetSelection.MediaFoundationObject;
                try
                {
                    writer.AddStream(audioTargetType, out var audioStreamIndex);
                    var videoInputType = CreateVideoType(VideoFormatRgb32, 0, target: false);
                    try
                    {
                        var audioInputType = MediaFoundationApi.CreateMediaTypeFromWaveFormat(audio.OutputFormat);
                        try
                        {
                            writer.SetInputMediaType(videoStreamIndex, videoInputType, null);
                            writer.SetInputMediaType(audioStreamIndex, audioInputType, null);
                            writer.BeginWriting();
                            audio.Start();
                            try
                            {
                                WriteFrames(writer, videoStreamIndex, audioStreamIndex, audio, cancellation);
                            }
                            finally
                            {
                                audio.Stop();
                            }

                            if (audio.Failure is not null) throw new InvalidOperationException("System audio capture failed.", audio.Failure);
                            writer.Flush(videoStreamIndex);
                            writer.Flush(audioStreamIndex);
                            writer.DoFinalize();
                        }
                        finally
                        {
                            ReleaseComObject(audioInputType);
                        }
                    }
                    finally
                    {
                        ReleaseComObject(videoInputType);
                    }
                }
                finally
                {
                    ReleaseComObject(audioTargetType);
                }
            }
            finally
            {
                ReleaseComObject(videoTargetType);
            }
        }
        catch (Exception exception)
        {
            Failure = exception;
        }
        finally
        {
            ReleaseComObject(writer);
            lock (StartupGate)
            {
                if (_startupCount > 0 && --_startupCount == 0) MediaFoundationApi.Shutdown();
            }
            if (comInitialized) CoUninitialize();
        }
    }

    private void WriteFrames(
        MfSinkWriter writer,
        int videoStreamIndex,
        int audioStreamIndex,
        LoopbackAudioSource audio,
        CancellationToken cancellation)
    {
        var stride = _width * 4;
        var frameBytes = stride * _height;
        var captureBuffer = new byte[frameBytes];
        var flippedBuffer = new byte[frameBytes];
        var audioBlockAlignment = audio.OutputFormat.BlockAlign;
        var maximumAudioFramesPerVideoFrame = (AudioSampleRate + _frameRate - 1) / _frameRate;
        var audioBuffer = new byte[maximumAudioFramesPerVideoFrame * audioBlockAlignment];
        var intervalMilliseconds = 1000.0 / _frameRate;
        long frameIndex = 0;
        long audioFramesWritten = 0;

        while (!cancellation.IsCancellationRequested)
        {
            var tickStart = _clock.ElapsedMilliseconds;

            if (!_paused)
            {
                var captured = ScreenCaptureInterop.CaptureRegion(_bounds, captureBuffer);
                if (captured is not null)
                {
                    WriteFrame(writer, videoStreamIndex, flippedBuffer, captured, stride, frameIndex);
                    frameIndex++;

                    var targetAudioFrames = frameIndex * AudioSampleRate / _frameRate;
                    var audioFramesToWrite = (int)(targetAudioFrames - audioFramesWritten);
                    if (audioFramesToWrite > 0)
                    {
                        var audioBytesToWrite = audioFramesToWrite * audioBlockAlignment;
                        audio.ReadPcm(audioBuffer, audioBytesToWrite);
                        WriteAudioSample(writer, audioStreamIndex, audioBuffer, audioBytesToWrite, audioFramesWritten, audioFramesToWrite);
                        audioFramesWritten += audioFramesToWrite;
                    }
                }
            }
            else
            {
                audio.DiscardPending();
            }

            if (audio.Failure is not null) throw new InvalidOperationException("System audio capture failed.", audio.Failure);

            var spent = _clock.ElapsedMilliseconds - tickStart;
            var remaining = intervalMilliseconds - spent;
            Thread.Sleep(remaining > 1 ? (int)Math.Ceiling(remaining) : 1);
        }
    }

    private void WriteFrame(MfSinkWriter writer, int streamIndex, byte[] destination, byte[] source, int stride, long frameIndex)
    {
        // Media Foundation expects RGB32 input bottom-up while GDI hands us top-down rows.
        for (var row = 0; row < _height; row++)
        {
            Buffer.BlockCopy(source, row * stride, destination, (_height - 1 - row) * stride, stride);
        }

        MfMediaBuffer buffer = MediaFoundationApi.CreateMemoryBuffer(destination.Length);
        try
        {
            buffer.Lock(out var pointer, out _, out _);
            try
            {
                Marshal.Copy(destination, 0, pointer, destination.Length);
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.SetCurrentLength(destination.Length);

            MfSample sample = MediaFoundationApi.CreateSample();
            try
            {
                sample.AddBuffer(buffer);
                sample.SetSampleTime(frameIndex * _frameDuration);
                sample.SetSampleDuration(_frameDuration);
                writer.WriteSample(streamIndex, sample);
            }
            finally
            {
                ReleaseComObject(sample);
            }
        }
        finally
        {
            ReleaseComObject(buffer);
        }
    }

    private static void WriteAudioSample(
        MfSinkWriter writer,
        int streamIndex,
        byte[] pcm,
        int byteCount,
        long startFrame,
        int frameCount)
    {
        MfMediaBuffer buffer = MediaFoundationApi.CreateMemoryBuffer(byteCount);
        try
        {
            buffer.Lock(out var pointer, out _, out _);
            try
            {
                Marshal.Copy(pcm, 0, pointer, byteCount);
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.SetCurrentLength(byteCount);

            MfSample sample = MediaFoundationApi.CreateSample();
            try
            {
                sample.AddBuffer(buffer);
                sample.SetSampleTime(startFrame * HundredNanosecondsPerSecond / AudioSampleRate);
                sample.SetSampleDuration(frameCount * HundredNanosecondsPerSecond / AudioSampleRate);
                writer.WriteSample(streamIndex, sample);
            }
            finally
            {
                ReleaseComObject(sample);
            }
        }
        finally
        {
            ReleaseComObject(buffer);
        }
    }

    private MfMediaType CreateVideoType(Guid subtype, uint bitrate, bool target)
    {
        var type = MediaFoundationApi.CreateMediaType();
        type.SetGUID(MajorTypeAttribute, MediaTypeVideo);
        type.SetGUID(SubtypeAttribute, subtype);
        type.SetUINT64(FrameSizeAttribute, PackPair(_width, _height));
        type.SetUINT64(FrameRateAttribute, PackPair(_frameRate, 1));
        type.SetUINT32(InterlaceModeAttribute, (int)MfVideoInterlaceProgressive);
        if (target) type.SetUINT32(AvgBitrateAttribute, (int)bitrate);
        return type;
    }

    private static long PackPair(int high, int low)
        => unchecked((long)(((ulong)(uint)high << 32) | (uint)low));

    private static uint ComputeBitrate(int width, int height, int frameRate)
        => (uint)Math.Clamp((long)(width * height * frameRate * 0.12), 2_000_000, 20_000_000);

    private static WaveFormat CreateAudioFormat() => new(AudioSampleRate, AudioBitsPerSample, AudioChannels);

    private static RECT MakeEven(RECT bounds)
    {
        var width = Math.Max(2, bounds.Width - (bounds.Width % 2));
        var height = Math.Max(2, bounds.Height - (bounds.Height % 2));
        return RECT.FromSize(bounds.Left, bounds.Top, width, height);
    }

    private sealed class LoopbackAudioSource : IDisposable
    {
        private readonly ConcurrentQueue<byte[]> _capturedChunks = new();
        private readonly MMDeviceEnumerator _deviceEnumerator;
        private readonly MMDevice _device;
        private readonly WasapiLoopbackCapture _capture;
        private readonly BufferedWaveProvider _sourceBuffer;
        private readonly MediaFoundationResampler _resampler;
        private bool _started;
        private bool _disposed;
        private Exception? _failure;

        public WaveFormat OutputFormat { get; }
        public Exception? Failure => Volatile.Read(ref _failure);

        public LoopbackAudioSource(WaveFormat outputFormat)
        {
            OutputFormat = outputFormat;
            _deviceEnumerator = new MMDeviceEnumerator();
            _device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _capture = new WasapiLoopbackCapture(_device);
            _sourceBuffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };
            _resampler = new MediaFoundationResampler(_sourceBuffer, outputFormat) { ResamplerQuality = 60 };
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
        }

        public void Start()
        {
            if (_started) return;
            _capture.StartRecording();
            _started = true;
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            try
            {
                _capture.StopRecording();
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                Interlocked.CompareExchange(ref _failure, exception, null);
            }
        }

        public void ReadPcm(byte[] destination, int byteCount)
        {
            Array.Clear(destination, 0, byteCount);
            DrainCapturedChunks();
            var written = 0;
            while (written < byteCount)
            {
                var read = _resampler.Read(destination, written, byteCount - written);
                if (read <= 0) break;
                written += read;
            }
        }

        public void DiscardPending()
        {
            while (_capturedChunks.TryDequeue(out _)) { }
            _sourceBuffer.ClearBuffer();
        }

        private void DrainCapturedChunks()
        {
            while (_capturedChunks.TryDequeue(out var chunk))
            {
                _sourceBuffer.AddSamples(chunk, 0, chunk.Length);
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs args)
        {
            var copy = GC.AllocateUninitializedArray<byte>(args.BytesRecorded);
            Buffer.BlockCopy(args.Buffer, 0, copy, 0, args.BytesRecorded);
            _capturedChunks.Enqueue(copy);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is not null) Interlocked.CompareExchange(ref _failure, args.Exception, null);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _resampler.Dispose();
            _capture.Dispose();
            _device.Dispose();
            _deviceEnumerator.Dispose();
        }
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is null) return;
        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // Double releases are harmless here.
        }
    }
}

internal static class MediaFoundationInterop
{
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();
}
