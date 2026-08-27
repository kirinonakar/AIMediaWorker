using System.Diagnostics;
using System.Runtime.InteropServices;
using static AIMediaWorker.Capture.MediaFoundationInterop;

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

    private const int MfVersion = 0x00020070;
    private const uint MfVideoInterlaceProgressive = 2;
    private const long HundredNanosecondsPerSecond = 10_000_000;
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
        IMFSinkWriter? writer = null;
        try
        {
            var coInitializeResult = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            comInitialized = coInitializeResult is 0 or SFalse || coInitializeResult == RpcEChangedMode;

            lock (StartupGate)
            {
                if (_startupCount++ == 0 && MFStartup(MfVersion, 0) < 0)
                {
                    _startupCount--;
                    throw new InvalidOperationException("Media Foundation could not be initialized.");
                }
            }

            MFCreateSinkWriterFromURL(_outputPath, IntPtr.Zero, null, out writer).ThrowOnError();

            var targetType = CreateVideoType(VideoFormatH264, ComputeBitrate(_width, _height, _frameRate), target: true);
            try
            {
                writer!.AddStream(targetType, out var streamIndex);
                var inputType = CreateVideoType(VideoFormatRgb32, 0, target: false);
                try
                {
                    writer.SetInputMediaType(streamIndex, inputType, null);
                    writer.BeginWriting();
                    WriteFrames(writer, streamIndex, cancellation);
                    writer.Flush(streamIndex);
                    writer.Finalize();
                }
                finally
                {
                    ReleaseComObject(inputType);
                }
            }
            finally
            {
                ReleaseComObject(targetType);
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
                if (_startupCount > 0 && --_startupCount == 0) MFShutdown();
            }
            if (comInitialized) CoUninitialize();
        }
    }

    private void WriteFrames(IMFSinkWriter writer, uint streamIndex, CancellationToken cancellation)
    {
        var stride = _width * 4;
        var frameBytes = stride * _height;
        var captureBuffer = new byte[frameBytes];
        var flippedBuffer = new byte[frameBytes];
        var intervalMilliseconds = 1000.0 / _frameRate;
        long frameIndex = 0;

        while (!cancellation.IsCancellationRequested)
        {
            var tickStart = _clock.ElapsedMilliseconds;

            if (!_paused)
            {
                var captured = ScreenCaptureInterop.CaptureRegion(_bounds, captureBuffer);
                if (captured is not null)
                {
                    WriteFrame(writer, streamIndex, flippedBuffer, captured, stride, frameIndex);
                    frameIndex++;
                }
            }

            var spent = _clock.ElapsedMilliseconds - tickStart;
            var remaining = intervalMilliseconds - spent;
            Thread.Sleep(remaining > 1 ? (int)Math.Ceiling(remaining) : 1);
        }
    }

    private void WriteFrame(IMFSinkWriter writer, uint streamIndex, byte[] destination, byte[] source, int stride, long frameIndex)
    {
        // Media Foundation expects RGB32 input bottom-up while GDI hands us top-down rows.
        for (var row = 0; row < _height; row++)
        {
            Buffer.BlockCopy(source, row * stride, destination, (_height - 1 - row) * stride, stride);
        }

        MFCreateMemoryBuffer(destination.Length, out var buffer);
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
            buffer.SetCurrentLength((uint)destination.Length);

            MFCreateSample(out var sample);
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

    private IMFMediaType CreateVideoType(Guid subtype, uint bitrate, bool target)
    {
        MFCreateMediaType(out var type);
        type.SetGUID(MajorTypeAttribute, MediaTypeVideo);
        type.SetGUID(SubtypeAttribute, subtype);
        type.SetUINT64(FrameSizeAttribute, PackPair(_width, _height));
        type.SetUINT64(FrameRateAttribute, PackPair(_frameRate, 1));
        type.SetUINT32(InterlaceModeAttribute, MfVideoInterlaceProgressive);
        if (target) type.SetUINT32(AvgBitrateAttribute, bitrate);
        return type;
    }

    private static ulong PackPair(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    private static uint ComputeBitrate(int width, int height, int frameRate)
        => (uint)Math.Clamp((long)(width * height * frameRate * 0.12), 2_000_000, 20_000_000);

    private static RECT MakeEven(RECT bounds)
    {
        var width = Math.Max(2, bounds.Width - (bounds.Width % 2));
        var height = Math.Max(2, bounds.Height - (bounds.Height % 2));
        return RECT.FromSize(bounds.Left, bounds.Top, width, height);
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
    [DllImport("mfplat.dll")]
    internal static extern int MFStartup(int version, uint flags);

    [DllImport("mfplat.dll")]
    internal static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    internal static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfplat.dll")]
    internal static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll")]
    internal static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfreadwrite.dll")]
    internal static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url,
        IntPtr byteStream,
        IMFAttributes? attributes,
        out IMFSinkWriter writer);

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    internal static void ThrowOnError(this int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}

[ComImport]
[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    void GetItem(ref Guid key, IntPtr value);
    void GetItemType(ref Guid key, IntPtr type);
    void CompareItem(ref Guid key, IntPtr value, IntPtr result);
    void Compare(IMFAttributes other, uint matchType, IntPtr result);
    void GetUINT32(ref Guid key, out uint value);
    void GetUINT64(ref Guid key, out ulong value);
    void GetString(ref Guid key, IntPtr buffer, uint bufferSize, IntPtr length);
    void GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
    void GetBlobSize(ref Guid key, out uint size);
    void GetBlob(ref Guid key, IntPtr buffer, uint bufferSize, IntPtr copied);
    void GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
    void GetUnknown(ref Guid key, ref Guid interfaceId, out IntPtr unknown);
    void SetItem(ref Guid key, IntPtr value);
    void DeleteItem(ref Guid key);
    void DeleteAllItems();
    void SetUINT32(ref Guid key, uint value);
    void SetUINT64(ref Guid key, ulong value);
    void SetDouble(ref Guid key, double value);
    void SetGUID(ref Guid key, ref Guid value);
    void SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void SetBlob(ref Guid key, IntPtr buffer, uint size);
    void SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    void LockStore();
    void UnlockStore();
    void GetCount(out uint count);
    void GetItemByIndex(uint index, out Guid key, IntPtr value);
    void CopyAllItems(IMFAttributes destination);
}

[ComImport]
[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    void GetMajorType(out Guid majorType);
    void IsCompressedFormat(out int compressed);
    void IsEqual(IMFMediaType mediaType, out uint comparisonFlags);
    void CopyFrom(IMFMediaType mediaType);
    void GetRepresentation(Guid representation, out IntPtr data);
    void FreeRepresentation(Guid representation, IntPtr data);
}

[ComImport]
[Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    void Lock(out IntPtr buffer, out uint maximumLength, out uint currentLength);
    void Unlock();
    void GetCurrentLength(out uint currentLength);
    void SetCurrentLength(uint currentLength);
    void GetMaxLength(out uint maxLength);
}

[ComImport]
[Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    void GetBufferCount(out uint bufferCount);
    void GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    void AddBuffer(IMFMediaBuffer buffer);
    void RemoveBufferByIndex(uint index);
    void RemoveAllBuffers();
    void GetTotalLength(out uint totalLength);
    void CopyToBuffer(IMFMediaBuffer buffer);
    void SetSampleTime(long sampleTime);
    void GetSampleTime(out long sampleTime);
    void SetSampleDuration(long sampleDuration);
    void GetSampleDuration(out long sampleDuration);
}

#pragma warning disable CS0465
[ComImport]
[Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    void AddStream(IMFMediaType targetType, out uint streamIndex);
    void SetInputMediaType(uint streamIndex, IMFMediaType inputMediaType, IMFAttributes? encodingParameters);
    void BeginWriting();
    void WriteSample(uint streamIndex, IMFSample sample);
    void SendStreamTick(uint streamIndex, long timestamp);
    void PlaceMarker(uint streamIndex, IntPtr context);
    void NotifyEndOfSegment(uint streamIndex);
    void Flush(uint streamIndex);
    void Finalize();
    void GetServiceForStream(uint streamIndex, ref Guid service, ref Guid interfaceId, out IntPtr serviceObject);
    void GetStatistics(uint streamIndex, IntPtr statistics);
}
#pragma warning restore CS0465
