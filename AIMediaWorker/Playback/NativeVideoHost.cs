using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AIMediaWorker.Playback;

public sealed class NativeVideoHost : IDisposable
{
    private const uint WmDropFiles = 0x0233;
    private const uint WmNonClientHitTest = 0x0084;
    private const uint WmSetCursor = 0x0020;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const int HitTestClient = 1;
    private const int IdcArrow = 32512;
    private const int GwlpWndProc = -4;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int StaticBlackRectangle = 0x00000004;
    private const int StaticNotify = 0x00000100;
    private const int SwpNoActivate = 0x0010;
    private readonly Window _window;
    private readonly FrameworkElement _placeholder;
    private nint _handle;
    private nint _originalWindowProcedure;
    private WindowProcedure? _windowProcedure;
    private bool _disposed;
    private bool _visible = true;
    private bool _mediaVisible = true;
    private bool _dialogVisible = true;
    private bool _cursorHidden;
    private (int X, int Y, int Width, int Height)? _lastBounds;

    public NativeVideoHost(Window window, FrameworkElement placeholder)
    {
        _window = window;
        _placeholder = placeholder;
        _placeholder.SizeChanged += OnLayoutChanged;
        _placeholder.LayoutUpdated += OnLayoutUpdated;
    }

    public nint Handle => _handle;
    public bool IsVisible => _visible;
    public event EventHandler<FilesDroppedEventArgs>? FilesDropped;
    public event EventHandler? Clicked;
    public event EventHandler? DoubleClicked;

    public nint Create()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle != 0) return _handle;
        var parent = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        // A plain STATIC control paints itself with COLOR_WINDOW (normally white)
        // until libmpv creates its swap chain. Start with a black rectangle so the
        // native host matches the XAML video surface during that initialization gap.
        _handle = CreateWindowEx(0, "STATIC", string.Empty, WsChild | WsVisible | WsClipChildren | StaticBlackRectangle | StaticNotify, 0, 0, 1, 1, parent, 0, GetModuleHandle(null), 0);
        if (_handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create the libmpv video window.");
        _windowProcedure = OnWindowMessage;
        _originalWindowProcedure = SetWindowLongPtr(_handle, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_windowProcedure));
        if (_originalWindowProcedure == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            DestroyWindow(_handle);
            _handle = 0;
            throw new System.ComponentModel.Win32Exception(error, "Could not enable file drop on the video window.");
        }
        DragAcceptFiles(_handle, true);
        UpdateBounds();
        return _handle;
    }

    public void SetVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dialogVisible = visible;
        ApplyVisibility();
    }

    public void SetMediaVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mediaVisible = visible;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        _visible = _mediaVisible && _dialogVisible;
        if (_handle == 0) return;
        ShowWindow(_handle, _visible ? 4 : 0); // SW_SHOWNOACTIVATE / SW_HIDE
        if (_visible) UpdateBounds();
    }

    public void SetCursorHidden(bool hidden)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cursorHidden = hidden;
        ApplyCursor();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _placeholder.SizeChanged -= OnLayoutChanged;
        _placeholder.LayoutUpdated -= OnLayoutUpdated;
        if (_handle != 0)
        {
            DragAcceptFiles(_handle, false);
            if (_originalWindowProcedure != 0) SetWindowLongPtr(_handle, GwlpWndProc, _originalWindowProcedure);
            DestroyWindow(_handle);
            _handle = 0;
        }
        _windowProcedure = null;
        _originalWindowProcedure = 0;
        GC.SuppressFinalize(this);
    }

    private nint OnWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        // STATIC controls can report HTTRANSPARENT, which bypasses both cursor and click handling.
        if (message == WmNonClientHitTest) return HitTestClient;
        if (message == WmSetCursor)
        {
            ApplyCursor();
            return 1;
        }
        if (message == WmMouseMove) ApplyCursor();
        if (message == WmLeftButtonDown)
        {
            SetCapture(window);
            Clicked?.Invoke(this, EventArgs.Empty);
            return 0;
        }
        if (message == WmLeftButtonDoubleClick)
        {
            SetCapture(window);
            DoubleClicked?.Invoke(this, EventArgs.Empty);
            return 0;
        }
        if (message == WmLeftButtonUp)
        {
            if (GetCapture() == window) ReleaseCapture();
            return 0;
        }
        if (message == WmDropFiles)
        {
            var paths = ReadDroppedPaths(wParam);
            if (paths.Count > 0) FilesDropped?.Invoke(this, new FilesDroppedEventArgs(paths));
            return 0;
        }
        return _originalWindowProcedure == 0 ? DefWindowProc(window, message, wParam, lParam) : CallWindowProc(_originalWindowProcedure, window, message, wParam, lParam);
    }

    private void ApplyCursor()
    {
        if (_cursorHidden)
        {
            SetCursor(0);
            return;
        }
        var cursor = LoadCursor(0, (nint)IdcArrow);
        if (cursor != 0) SetCursor(cursor);
    }

    private static IReadOnlyList<string> ReadDroppedPaths(nint dropHandle)
    {
        try
        {
            var count = DragQueryFile(dropHandle, uint.MaxValue, null, 0);
            var paths = new List<string>((int)Math.Min(count, int.MaxValue));
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFile(dropHandle, index, null, 0);
                if (length == 0) continue;
                var buffer = new StringBuilder(checked((int)length + 1));
                if (DragQueryFile(dropHandle, index, buffer, (uint)buffer.Capacity) > 0) paths.Add(buffer.ToString());
            }
            return paths;
        }
        finally { DragFinish(dropHandle); }
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] private static extern nint CallWindowProc(nint previousProcedure, nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern nint LoadCursor(nint instance, nint cursorName);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(nint window, [MarshalAs(UnmanagedType.Bool)] bool accept);
    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(nint dropHandle, uint fileIndex, StringBuilder? path, uint pathLength);
    [DllImport("shell32.dll")] private static extern void DragFinish(nint dropHandle);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
}

public sealed class FilesDroppedEventArgs(IReadOnlyList<string> paths) : EventArgs
{
    public IReadOnlyList<string> Paths { get; } = paths;
}
