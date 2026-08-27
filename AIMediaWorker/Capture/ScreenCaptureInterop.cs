using System.Runtime.InteropServices;

namespace AIMediaWorker.Capture;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;

    public static RECT FromSize(int left, int top, int width, int height) => new()
    {
        Left = left,
        Top = top,
        Right = left + width,
        Bottom = top + height
    };
}

/// <summary>
/// Win32 helpers for grabbing desktop regions: monitor/window geometry and GDI bit-block captures.
/// All coordinates are physical pixels in virtual-desktop space (the app runs PerMonitorV2 aware).
/// </summary>
internal static class ScreenCaptureInterop
{
    private const uint SourceCopy = 0x00CC0020;
    private const uint MonitorDefaultToNearest = 2;
    private const uint GaRoot = 2;
    private const uint WindowDisplayAffinityNone = 0x0;
    private const uint WindowDisplayAffinityExcludeFromCapture = 0x11;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public uint DwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr windowHandle, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint affinity);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destinationContext, int xDestination, int yDestination, int width, int height, IntPtr sourceContext, int xSource, int ySource, uint rasterOperation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr deviceContext, IntPtr bitmap, uint startScan, uint scanLines, byte[]? bits, ref BITMAPINFOHEADER bitmapInfo, uint usage);

    /// <summary>Returns the bounds of the entire virtual desktop in physical pixels.</summary>
    public static RECT GetVirtualScreenBounds() => RECT.FromSize(
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        GetSystemMetrics(SmCxVirtualScreen),
        GetSystemMetrics(SmCyVirtualScreen));

    /// <summary>Returns the physical-pixel bounds of the monitor containing the given point.</summary>
    public static RECT GetMonitorBounds(int x, int y)
    {
        var monitor = MonitorFromPoint(new POINT(x, y), MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return GetVirtualScreenBounds();
        var info = new MONITORINFO { CbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info) ? info.RcMonitor : GetVirtualScreenBounds();
    }

    public static POINT GetCursorPosition() => GetCursorPos(out var point) ? point : default;

    public static bool TryGetWindowBounds(IntPtr windowHandle, out RECT bounds)
    {
        if (windowHandle == IntPtr.Zero || !GetWindowRect(windowHandle, out bounds))
        {
            bounds = default;
            return false;
        }
        return bounds.Width > 1 && bounds.Height > 1;
    }

    /// <summary>
    /// Finds the visible top-level window at a screen point, excluding windows owned by the
    /// current process so the capture overlay and selector never highlight themselves.
    /// </summary>
    public static IntPtr FindTopLevelWindowAtPoint(int x, int y)
    {
        var handle = WindowFromPoint(new POINT(x, y));
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(handle, GaRoot);
        if (root == IntPtr.Zero) root = handle;
        if (!IsWindowVisible(root)) return IntPtr.Zero;
        GetWindowThreadProcessId(root, out var processId);
        if (processId == Environment.ProcessId) return IntPtr.Zero;
        return TryGetWindowBounds(root, out _) ? root : IntPtr.Zero;
    }

    /// <summary>Makes a window invisible to screen captures (GDI BitBlt, PrintScreen, and similar).</summary>
    public static bool ExcludeWindowFromCapture(IntPtr windowHandle, bool exclude)
        => SetWindowDisplayAffinity(windowHandle, exclude ? WindowDisplayAffinityExcludeFromCapture : WindowDisplayAffinityNone);

    /// <summary>Captures a virtual-desktop region into top-down BGRA pixel data, or null when the blit fails.</summary>
    public static byte[]? CaptureRegion(RECT bounds, byte[]? destination = null)
    {
        var width = bounds.Width;
        var height = bounds.Height;
        if (width <= 0 || height <= 0) return null;

        var screenContext = GetDC(IntPtr.Zero);
        if (screenContext == IntPtr.Zero) return null;
        try
        {
            var memoryContext = CreateCompatibleDC(screenContext);
            if (memoryContext == IntPtr.Zero) return null;
            try
            {
                var bitmap = CreateCompatibleBitmap(screenContext, width, height);
                if (bitmap == IntPtr.Zero) return null;
                try
                {
                    var previous = SelectObject(memoryContext, bitmap);
                    try
                    {
                        if (!BitBlt(memoryContext, 0, 0, width, height, screenContext, bounds.Left, bounds.Top, SourceCopy)) return null;
                        var header = new BITMAPINFOHEADER
                        {
                            BiSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                            BiWidth = width,
                            BiHeight = -height,
                            BiPlanes = 1,
                            BiBitCount = 32,
                            BiCompression = 0
                        };
                        var requiredBytes = checked(width * height * 4);
                        var pixels = destination is not null && destination.Length >= requiredBytes
                            ? destination
                            : new byte[requiredBytes];
                        return GetDIBits(memoryContext, bitmap, 0, (uint)height, pixels, ref header, 0) != 0 ? pixels : null;
                    }
                    finally
                    {
                        SelectObject(memoryContext, previous);
                    }
                }
                finally
                {
                    DeleteObject(bitmap);
                }
            }
            finally
            {
                DeleteDC(memoryContext);
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenContext);
        }
    }
}
