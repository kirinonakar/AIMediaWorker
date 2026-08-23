using System.Runtime.InteropServices;

namespace AIMediaWorker.Playback;

internal static class MpvInterop
{
    private const string Library = "mpv-2.dll";

    internal enum MpvEventId
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 2,
        GetPropertyReply = 3,
        SetPropertyReply = 4,
        CommandReply = 5,
        StartFile = 6,
        EndFile = 7,
        FileLoaded = 8,
        ClientMessage = 16,
        VideoReconfig = 17,
        AudioReconfig = 18,
        Seek = 20,
        PlaybackRestart = 21,
        PropertyChange = 22,
        QueueOverflow = 24,
        Hook = 25
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEvent
    {
        public MpvEventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventEndFile
    {
        public int Reason;
        public int Error;
        public long PlaylistEntryId;
        public long PlaylistInsertId;
        public int PlaylistInsertNumEntries;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_initialize(nint context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_terminate_destroy(nint context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_option_string(nint context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_property_string(nint context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_get_property_string(nint context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_free(nint data);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_wait_event(nint context, double timeout);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_error_string(int error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(nint context, nint args);

    internal static string ErrorString(int error)
    {
        var ptr = mpv_error_string(error);
        return ptr == 0 ? $"mpv error {error}" : Marshal.PtrToStringUTF8(ptr) ?? $"mpv error {error}";
    }

    internal static void EnsureSuccess(int result, string operation)
    {
        if (result < 0) throw new MpvException(operation, result, ErrorString(result));
    }

    internal static string? GetString(nint context, string property)
    {
        var ptr = mpv_get_property_string(context, property);
        if (ptr == 0) return null;
        try { return Marshal.PtrToStringUTF8(ptr); }
        finally { mpv_free(ptr); }
    }

    internal static void Command(nint context, params string[] arguments)
    {
        if (arguments.Length == 0) throw new ArgumentException("At least one mpv command argument is required.", nameof(arguments));
        var strings = new nint[arguments.Length];
        var array = nint.Zero;
        try
        {
            for (var i = 0; i < arguments.Length; i++) strings[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            array = Marshal.AllocHGlobal((arguments.Length + 1) * IntPtr.Size);
            for (var i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(array, i * IntPtr.Size, strings[i]);
            Marshal.WriteIntPtr(array, arguments.Length * IntPtr.Size, nint.Zero);
            EnsureSuccess(mpv_command(context, array), string.Join(' ', arguments));
        }
        finally
        {
            if (array != 0) Marshal.FreeHGlobal(array);
            foreach (var value in strings) if (value != 0) Marshal.FreeCoTaskMem(value);
        }
    }
}

public sealed class MpvException(string operation, int errorCode, string message) : Exception($"{operation}: {message}")
{
    public int ErrorCode { get; } = errorCode;
}
