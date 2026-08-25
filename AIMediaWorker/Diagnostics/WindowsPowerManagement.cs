using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AIMediaWorker.Diagnostics;

internal sealed class WindowsPowerManagement : IDisposable
{
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x00000001;
    private const int PowerRequestDisplayRequired = 0;
    private const int PowerRequestSystemRequired = 1;

    private PowerRequestHandle? _request;
    private bool _active;
    private bool _disposed;

    /// <summary>
    /// Registers display and system power requests while playback is active. The system
    /// request prevents automatic low-power transitions, including the idle path that can
    /// lead to hibernation. Explicit user power actions remain under Windows' control.
    /// </summary>
    public bool TrySetPlaybackActive(bool active)
    {
        if (_disposed) return false;
        if (_active == active) return true;

        if (active)
        {
            var request = CreateRequest();
            if (request is null) return false;

            var displaySet = PowerSetRequest(request.DangerousGetHandle(), PowerRequestDisplayRequired);
            var systemSet = displaySet && PowerSetRequest(request.DangerousGetHandle(), PowerRequestSystemRequired);
            if (!systemSet)
            {
                if (displaySet) _ = PowerClearRequest(request.DangerousGetHandle(), PowerRequestDisplayRequired);
                request.Dispose();
                return false;
            }

            _request = request;
            _active = true;
            return true;
        }

        if (_request is null)
        {
            _active = false;
            return true;
        }

        var handle = _request.DangerousGetHandle();
        var displayCleared = PowerClearRequest(handle, PowerRequestDisplayRequired);
        var systemCleared = PowerClearRequest(handle, PowerRequestSystemRequired);
        _request.Dispose();
        _request = null;
        _active = false;
        return displayCleared && systemCleared;
    }

    public void Dispose()
    {
        if (_disposed) return;
        TrySetPlaybackActive(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static PowerRequestHandle? CreateRequest()
    {
        var reason = Marshal.StringToHGlobalUni("AIMediaWorker video playback");
        try
        {
            var context = new ReasonContext
            {
                Version = PowerRequestContextVersion,
                Flags = PowerRequestContextSimpleString,
                Reason = new ReasonUnion { SimpleReasonString = reason }
            };
            var handle = PowerCreateRequest(ref context);
            return handle == 0 ? null : new PowerRequestHandle(handle);
        }
        finally { Marshal.FreeHGlobal(reason); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public ReasonUnion Reason;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ReasonUnion
    {
        [FieldOffset(0)] public nint SimpleReasonString;
    }

    private sealed class PowerRequestHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public PowerRequestHandle(nint handle) : base(ownsHandle: true) => SetHandle(handle);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(nint powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(nint powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
