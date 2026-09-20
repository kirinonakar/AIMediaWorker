using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using static AIMediaWorker.Capture.MediaFoundationInterop;
// NAudio 3.x keeps its Media Foundation interop internal, so the recorder uses the interfaces
// declared in Capture/MediaFoundationInterop.cs.
using MfMediaBuffer = AIMediaWorker.Capture.IMFMediaBuffer;
using MfMediaType = AIMediaWorker.Capture.IMFMediaType;
using MfSample = AIMediaWorker.Capture.IMFSample;
using MfSinkWriter = AIMediaWorker.Capture.IMFSinkWriter;

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
    private static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");   // 'auds'
    private static readonly Guid AudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");  // PCM
    private static readonly Guid AudioChannelCountAttribute = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid AudioSampleRateAttribute = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid AudioBlockAlignmentAttribute = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid AudioAverageBytesPerSecondAttribute = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid AudioBitsPerSampleAttribute = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid AacPayloadTypeAttribute = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    private static readonly Guid AacProfileLevelAttribute = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
    private static readonly Guid AllSamplesIndependentAttribute = new("c9173739-5e56-461c-b713-46fb995cb95f");

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

            writer = CreateSinkWriterFromUrl(_outputPath);

            using var audio = new LoopbackAudioSource(CreateAudioFormat());
            var videoTargetType = CreateVideoType(VideoFormatH264, ComputeBitrate(_width, _height, _frameRate), target: true);
            try
            {
                writer!.AddStream(videoTargetType, out var videoStreamIndex);
                var audioTargetType = CreateAudioTargetType(audio.OutputFormat, AudioBitrate);
                try
                {
                    writer.AddStream(audioTargetType, out var audioStreamIndex);
                    var videoInputType = CreateVideoType(VideoFormatRgb32, 0, target: false);
                    try
                    {
                        var audioInputType = CreateAudioInputType(audio.OutputFormat);
                        try
                        {
                            writer.SetInputMediaType(videoStreamIndex, videoInputType, IntPtr.Zero);
                            writer.SetInputMediaType(audioStreamIndex, audioInputType, IntPtr.Zero);
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

        MfMediaBuffer buffer = CreateMemoryBuffer(destination.Length);
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

            MfSample sample = CreateSample();
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
        MfMediaBuffer buffer = CreateMemoryBuffer(byteCount);
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

            MfSample sample = CreateSample();
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
        var type = CreateMediaType();
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

    /// <summary>Builds the PCM input type consumed by the Media Foundation AAC encoder.</summary>
    private static MfMediaType CreateAudioInputType(WaveFormat format)
    {
        var type = CreateMediaType();
        type.SetGUID(MajorTypeAttribute, MediaTypeAudio);
        type.SetGUID(SubtypeAttribute, AudioFormatPcm);
        type.SetUINT32(AudioChannelCountAttribute, format.Channels);
        type.SetUINT32(AudioSampleRateAttribute, format.SampleRate);
        type.SetUINT32(AudioBlockAlignmentAttribute, format.BlockAlign);
        type.SetUINT32(AudioAverageBytesPerSecondAttribute, format.AverageBytesPerSecond);
        type.SetUINT32(AudioBitsPerSampleAttribute, format.BitsPerSample);
        type.SetUINT32(AllSamplesIndependentAttribute, 1);
        return type;
    }

    /// <summary>
    /// Chooses the AAC encoder output type that matches the captured audio. Media Foundation only
    /// accepts the output types it advertises for the codec, so one of those templates is reused and
    /// only the average bytes per second is overridden.
    /// </summary>
    private static MfMediaType CreateAudioTargetType(WaveFormat outputFormat, int bitrate)
    {
        var desiredBytesPerSecond = bitrate / 8;
        var availableTypes = GetAudioOutputAvailableTypes(AudioSubtypes.MFAudioFormat_AAC);
        try
        {
            availableTypes.GetElementCount(out var count);
            MfMediaType? selected = null;
            var selectedDistance = long.MaxValue;
            for (var index = 0; index < count; index++)
            {
                var candidate = TakeAudioOutputType(availableTypes, index);
                var keep = false;
                try
                {
                    if (MatchesAudioFormat(candidate, outputFormat))
                    {
                        if (candidate.GetUINT32(AudioAverageBytesPerSecondAttribute, out var averageBytesPerSecond) < 0)
                        {
                            averageBytesPerSecond = desiredBytesPerSecond;
                        }

                        var distance = Math.Abs((long)averageBytesPerSecond - desiredBytesPerSecond);
                        if (distance < selectedDistance)
                        {
                            selectedDistance = distance;
                            if (selected is not null) Marshal.ReleaseComObject(selected);
                            selected = candidate;
                            keep = true;
                        }
                    }
                }
                finally
                {
                    if (!keep) Marshal.ReleaseComObject(candidate);
                }
            }

            if (selected is null) return CreateManualAudioTargetType(outputFormat, desiredBytesPerSecond);
            selected.SetUINT32(AudioAverageBytesPerSecondAttribute, desiredBytesPerSecond);
            return selected;
        }
        finally
        {
            Marshal.ReleaseComObject(availableTypes);
        }
    }

    private static MfMediaType TakeAudioOutputType(IMFCollection collection, int index)
    {
        collection.GetElement(index, out var element);
        try
        {
            return (MfMediaType)Marshal.GetObjectForIUnknown(element);
        }
        finally
        {
            Marshal.Release(element);
        }
    }

    private static bool MatchesAudioFormat(MfMediaType type, WaveFormat format)
    {
        if (type.GetUINT32(AudioSampleRateAttribute, out var sampleRate) < 0 || sampleRate != format.SampleRate) return false;
        if (type.GetUINT32(AudioChannelCountAttribute, out var channels) < 0 || channels != format.Channels) return false;
        if (type.GetUINT32(AudioBitsPerSampleAttribute, out var bitsPerSample) < 0 || bitsPerSample != format.BitsPerSample) return false;

        // Prefer AAC-LC output so the encoded track plays everywhere.
        return type.GetUINT32(AacPayloadTypeAttribute, out var payloadType) < 0 || payloadType == 0;
    }

    /// <summary>Fallback for when Media Foundation exposes no matching AAC output template.</summary>
    private static MfMediaType CreateManualAudioTargetType(WaveFormat outputFormat, int bytesPerSecond)
    {
        var type = CreateAudioInputType(outputFormat);
        type.SetGUID(SubtypeAttribute, AudioSubtypes.MFAudioFormat_AAC);
        type.SetUINT32(AudioAverageBytesPerSecondAttribute, bytesPerSecond);
        type.SetUINT32(AacPayloadTypeAttribute, 0);
        type.SetUINT32(AacProfileLevelAttribute, 0x29); // AAC-LC
        return type;
    }

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
        private readonly WasapiRecorder _capture;
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
            _capture = new WasapiRecorderBuilder().WithDevice(_device).WithSharedMode().WithLoopbackCapture().Build();
            _sourceBuffer = new BufferedWaveProvider(_capture.WaveFormat, TimeSpan.FromSeconds(2))
            {
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
                var read = _resampler.Read(destination.AsSpan(written, byteCount - written));
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

        private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            if (buffer.Length == 0) return;
            var copy = GC.AllocateUninitializedArray<byte>(buffer.Length);
            buffer.CopyTo(copy);
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
