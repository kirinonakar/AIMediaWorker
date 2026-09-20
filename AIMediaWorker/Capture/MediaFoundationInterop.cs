using System.Runtime.InteropServices;

namespace AIMediaWorker.Capture;

/// <summary>
/// Media Foundation entry points and the COM interfaces used by <see cref="MediaFoundationH264Recorder"/>.
/// NAudio 3.x moved its Media Foundation interop to internal types and dropped the media type, sample,
/// and memory buffer helpers, so the sink writer pipeline declares the small surface it needs here.
/// </summary>
/// <remarks>
/// Each interface repeats the <c>IMFAttributes</c> methods inline instead of deriving from a shared
/// interface: when a ComImport interface inherits from another ComImport interface its own methods end
/// up in the wrong vtable slots, so calls such as <c>IMFMediaType.GetMajorType</c> jump to slot 0.
/// Stream methods keep the declaring order of the native interfaces.
/// </remarks>
internal static class MediaFoundationInterop
{
    /// <summary>MFT_ENUM_FLAG_ALL.</summary>
    internal const int MftEnumFlagAll = 0x3F;

    /// <summary>MFT_ENUM_FLAG_SORTANDFILTER.</summary>
    internal const int MftEnumFlagSortAndFilter = 0x40;

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    internal static IMFMediaType CreateMediaType()
    {
        ThrowIfFailed(MFCreateMediaType(out var mediaType));
        return mediaType;
    }

    internal static IMFMediaBuffer CreateMemoryBuffer(int maxLength)
    {
        ThrowIfFailed(MFCreateMemoryBuffer(maxLength, out var buffer));
        return buffer;
    }

    internal static IMFSample CreateSample()
    {
        ThrowIfFailed(MFCreateSample(out var sample));
        return sample;
    }

    internal static IMFSinkWriter CreateSinkWriterFromUrl(string outputUrl)
    {
        ThrowIfFailed(MFCreateSinkWriterFromURL(outputUrl, IntPtr.Zero, IntPtr.Zero, out var sinkWriter));
        return sinkWriter;
    }

    internal static IMFCollection GetAudioOutputAvailableTypes(Guid audioSubtype)
    {
        ThrowIfFailed(MFTranscodeGetAudioOutputAvailableTypes(
            audioSubtype, MftEnumFlagAll | MftEnumFlagSortAndFilter, IntPtr.Zero, out var availableTypes));
        return availableTypes;
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    private static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string outputUrl,
        IntPtr byteStream,
        IntPtr attributes,
        out IMFSinkWriter sinkWriter);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mf.dll", ExactSpelling = true)]
    private static extern int MFTranscodeGetAudioOutputAvailableTypes(
        Guid audioSubtype,
        int mftEnumFlags,
        IntPtr codecConfig,
        out IMFCollection availableTypes);
}

[ComImport]
[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    void GetItem(Guid guidKey, IntPtr value);
    void GetItemType(Guid guidKey, out int type);
    void CompareItem(Guid guidKey, IntPtr value, out int result);
    void Compare(IntPtr theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(Guid guidKey, out int value);
    void GetUINT64(Guid guidKey, out long value);
    void GetDouble(Guid guidKey, out double value);
    void GetGUID(Guid guidKey, out Guid value);
    void GetStringLength(Guid guidKey, out int length);
    void GetString(Guid guidKey, IntPtr value, int bufferSize, out int length);
    void GetAllocatedString(Guid guidKey, out IntPtr value, out int length);
    void GetBlobSize(Guid guidKey, out int size);
    void GetBlob(Guid guidKey, IntPtr buffer, int bufferSize, out int size);
    void GetAllocatedBlob(Guid guidKey, out IntPtr buffer, out int size);
    void GetUnknown(Guid guidKey, ref Guid interfaceId, out IntPtr value);
    void SetItem(Guid guidKey, IntPtr value);
    void DeleteItem(Guid guidKey);
    void DeleteAllItems();
    void SetUINT32(Guid guidKey, int value);
    void SetUINT64(Guid guidKey, long value);
    void SetDouble(Guid guidKey, double value);
    void SetGUID(Guid guidKey, Guid value);
    void SetString(Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void SetBlob(Guid guidKey, IntPtr buffer, int bufferSize);
    void SetUnknown(Guid guidKey, IntPtr value);
    void LockStore();
    void UnlockStore();
    void GetCount(out int count);
    void GetItemByIndex(int index, out Guid guidKey, IntPtr value);
    void CopyAllItems(IntPtr destination);
    // IMFMediaType continues with GetMajorType/IsCompressedFormat/IsEqual/GetRepresentation and
    // FreeRepresentation; the recorder only reads and writes attributes.
}

[ComImport]
[Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
    void Unlock();
    void GetCurrentLength(out int currentLength);
    void SetCurrentLength(int currentLength);
    void GetMaxLength(out int maxLength);
}

[ComImport]
[Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    void GetItem(Guid guidKey, IntPtr value);
    void GetItemType(Guid guidKey, out int type);
    void CompareItem(Guid guidKey, IntPtr value, out int result);
    void Compare(IntPtr theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(Guid guidKey, out int value);
    void GetUINT64(Guid guidKey, out long value);
    void GetDouble(Guid guidKey, out double value);
    void GetGUID(Guid guidKey, out Guid value);
    void GetStringLength(Guid guidKey, out int length);
    void GetString(Guid guidKey, IntPtr value, int bufferSize, out int length);
    void GetAllocatedString(Guid guidKey, out IntPtr value, out int length);
    void GetBlobSize(Guid guidKey, out int size);
    void GetBlob(Guid guidKey, IntPtr buffer, int bufferSize, out int size);
    void GetAllocatedBlob(Guid guidKey, out IntPtr buffer, out int size);
    void GetUnknown(Guid guidKey, ref Guid interfaceId, out IntPtr value);
    void SetItem(Guid guidKey, IntPtr value);
    void DeleteItem(Guid guidKey);
    void DeleteAllItems();
    void SetUINT32(Guid guidKey, int value);
    void SetUINT64(Guid guidKey, long value);
    void SetDouble(Guid guidKey, double value);
    void SetGUID(Guid guidKey, Guid value);
    void SetString(Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void SetBlob(Guid guidKey, IntPtr buffer, int bufferSize);
    void SetUnknown(Guid guidKey, IntPtr value);
    void LockStore();
    void UnlockStore();
    void GetCount(out int count);
    void GetItemByIndex(int index, out Guid guidKey, IntPtr value);
    void CopyAllItems(IntPtr destination);
    void GetSampleFlags(out int sampleFlags);
    void SetSampleFlags(int sampleFlags);
    void GetSampleTime(out long sampleTime);
    void SetSampleTime(long sampleTime);
    void GetSampleDuration(out long sampleDuration);
    void SetSampleDuration(long sampleDuration);
    void GetBufferCount(out int bufferCount);
    void GetBufferByIndex(int index, out IMFMediaBuffer buffer);
    void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    void AddBuffer(IMFMediaBuffer buffer);
    // IMFSample continues with RemoveBufferByIndex/RemoveAllBuffers/GetTotalLength/CopyToBuffer.
}

[ComImport]
[Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    void AddStream(IMFMediaType targetMediaType, out int streamIndex);
    void SetInputMediaType(int streamIndex, IMFMediaType inputMediaType, IntPtr encodingParameters);
    void BeginWriting();
    void WriteSample(int streamIndex, IMFSample sample);
    void SendStreamTick(int streamIndex, long timestamp);
    void PlaceMarker(int streamIndex, IntPtr context);
    void NotifyEndOfSegment(int streamIndex);
    void Flush(int streamIndex);
    void DoFinalize();
    // IMFSinkWriter continues with GetServiceForStream and GetStatistics.
}

[ComImport]
[Guid("5BC8A76B-869A-46A3-9B03-FA218A66AEBE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFCollection
{
    void GetElementCount(out int count);
    void GetElement(int index, out IntPtr element);
    // IMFCollection continues with AddElement/RemoveElement/InsertElementAt/RemoveAllElements.
}
