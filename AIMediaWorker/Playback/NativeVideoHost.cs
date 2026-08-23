using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AIMediaWorker.Playback;

public sealed class NativeVideoHost : IDisposable
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int SwpNoActivate = 0x0010;
    private readonly Window _window;
    private readonly FrameworkElement _placeholder;
    private nint _handle;
    private bool _disposed;
    private (int X, int Y, int Width, int Height)? _lastBounds;

    public NativeVideoHost(Window window, FrameworkElement placeholder)
    {
        _window = window;
        _placeholder = placeholder;
        _placeholder.SizeChanged += OnLayoutChanged;
        _placeholder.LayoutUpdated += OnLayoutUpdated;
    }

    public nint Handle => _handle;

    public nint Create()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle != 0) return _handle;
        var parent = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _handle = CreateWindowEx(0, "STATIC", string.Empty, WsChild | WsVisible | WsClipChildren, 0, 0, 1, 1, parent, 0, GetModuleHandle(null), 0);
        if (_handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create the libmpv video window.");
        UpdateBounds();
        return _handle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _placeholder.SizeChanged -= OnLayoutChanged;
        _placeholder.LayoutUpdated -= OnLayoutUpdated;
        if (_handle != 0) { DestroyWindow(_handle); _handle = 0; }
        GC.SuppressFinalize(this);
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs e) => UpdateBounds();
    private void OnLayoutUpdated(object? sender, object e) => UpdateBounds();

    private void UpdateBounds()
    {
        if (_handle == 0 || _placeholder.XamlRoot is null || _placeholder.ActualWidth <= 0 || _placeholder.ActualHeight <= 0) return;
        var point = _placeholder.TransformToVisual(null).TransformPoint(new Point());
        var scale = _placeholder.XamlRoot.RasterizationScale;
        var bounds = ((int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale), (int)Math.Round(_placeholder.ActualWidth * scale), (int)Math.Round(_placeholder.ActualHeight * scale));
        if (_lastBounds == bounds) return;
        if (!SetWindowPos(_handle, 0, bounds.Item1, bounds.Item2, bounds.Item3, bounds.Item4, SwpNoActivate)) return;
        _lastBounds = bounds;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, int flags);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
}
