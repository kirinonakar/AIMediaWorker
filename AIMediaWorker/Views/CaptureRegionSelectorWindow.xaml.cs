using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using AIMediaWorker.Capture;
using AIMediaWorker.Localization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace AIMediaWorker.Views;

internal enum CaptureSelectorMode
{
    Region,
    Window
}

/// <summary>
/// Dimmed full-monitor overlay used to pick a capture target: free-form drag selection or
/// click-to-select the window under the cursor. Esc cancels and completes with null.
/// The window is made layered so the screen stays visible underneath the dim layer.
/// </summary>
internal sealed partial class CaptureRegionSelectorWindow : Window
{
    private const int GwlStyle = -16;
    private const nint WsBorder = 0x00800000;
    private const nint WsDlgFrame = 0x00400000;
    private const nint WsThickFrame = 0x00040000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint DwmwaBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;

    private readonly AppWindow? _appWindow;
    private readonly nint _selfHandle;
    private TaskCompletionSource<RECT?>? _completion;
    private CaptureSelectorMode _mode;
    private RECT _monitor;
    private bool _dragging;
    private Windows.Foundation.Point _pressPoint;
    private RECT? _hoverBounds;
    private Windows.Foundation.Rect? _dimHole;

    public CaptureRegionSelectorWindow()
    {
        InitializeComponent();
        Title = "AIMediaWorker";
        _selfHandle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_selfHandle));
        if (_appWindow is not null)
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            _appWindow.Closing += OnAppWindowClosing;
        }
        RemoveNativeWindowFrame();

        Closed += (_, _) => Complete(null);
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape, ScopeOwner = Root };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            Complete(null);
        };
        FocusSink.KeyboardAccelerators.Add(escape);
        ToolTipService.SetToolTip(FocusSink, null);
    }

    /// <summary>Shows the selector over the given monitor and completes with the chosen physical-pixel rect, or null when cancelled.</summary>
    internal async Task<RECT?> SelectAsync(CaptureSelectorMode mode, RECT monitorBounds)
    {
        if (_completion is not null) return null;
        var completion = new TaskCompletionSource<RECT?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        _mode = mode;
        _monitor = monitorBounds;
        FrozenScreenImage.Source = CaptureFrozenScreen(monitorBounds);
        _dragging = false;
        _hoverBounds = null;
        HintText.Text = mode == CaptureSelectorMode.Window ? L("WindowHint.Text") : string.Empty;
        HintBorder.Visibility = mode == CaptureSelectorMode.Region ? Visibility.Collapsed : Visibility.Visible;
        HoverHighlight.Visibility = Visibility.Collapsed;
        SelectionBand.Visibility = Visibility.Collapsed;
        UpdateDimMask(null);
        PositionOver(monitorBounds);
        _appWindow?.Show();
        RemoveNativeWindowFrame();
        Activate();
        FocusSink.Focus(FocusState.Programmatic);
        return await completion.Task;
    }

    private double Scale => Root.XamlRoot?.RasterizationScale ?? 1.0;

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => UpdateDimMask(_dimHole);

    private void PositionOver(RECT monitorBounds)
    {
        if (_appWindow is null) return;
        _appWindow.Move(new PointInt32(monitorBounds.Left, monitorBounds.Top));
        _appWindow.Resize(new SizeInt32(monitorBounds.Width, monitorBounds.Height));
    }

    private void RemoveNativeWindowFrame()
    {
        var style = GetWindowLong(_selfHandle, GwlStyle);
        var framelessStyle = style & ~(WsBorder | WsDlgFrame | WsThickFrame);
        if (framelessStyle != style) SetWindowLong(_selfHandle, GwlStyle, framelessStyle);
        SetWindowPos(_selfHandle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        var color = DwmColorNone;
        DwmSetWindowAttribute(_selfHandle, DwmwaBorderColor, ref color, sizeof(uint));
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        Complete(null);
    }

    private void OnFocusSinkKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) Complete(null);
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_mode == CaptureSelectorMode.Window)
        {
            UpdateHoverHighlight(e.GetCurrentPoint(Root).Position);
            if (_hoverBounds is { } bounds) Complete(bounds);
            return;
        }

        _dragging = true;
        _pressPoint = e.GetCurrentPoint(Root).Position;
        SelectionBand.Visibility = Visibility.Visible;
        UpdateSelectionBand(_pressPoint, _pressPoint);
        Root.CapturePointer(e.Pointer);
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(Root).Position;
        if (_mode == CaptureSelectorMode.Window)
        {
            UpdateHoverHighlight(position);
            return;
        }

        if (_dragging) UpdateSelectionBand(_pressPoint, position);
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_mode != CaptureSelectorMode.Region || !_dragging) return;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);
        var bounds = ToPhysicalRect(_pressPoint, e.GetCurrentPoint(Root).Position);
        if (bounds.Width < 8 || bounds.Height < 8)
        {
            SelectionBand.Visibility = Visibility.Collapsed;
            UpdateDimMask(null);
            return;
        }

        Complete(bounds);
    }

    private void UpdateSelectionBand(Windows.Foundation.Point start, Windows.Foundation.Point end)
    {
        SelectionBand.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionBand, Math.Min(start.X, end.X));
        Canvas.SetTop(SelectionBand, Math.Min(start.Y, end.Y));
        SelectionBand.Width = Math.Abs(end.X - start.X);
        SelectionBand.Height = Math.Abs(end.Y - start.Y);
        UpdateDimMask(new Windows.Foundation.Rect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y)));
    }

    private void UpdateHoverHighlight(Windows.Foundation.Point position)
    {
        var screenX = _monitor.Left + (int)Math.Round(position.X * Scale);
        var screenY = _monitor.Top + (int)Math.Round(position.Y * Scale);
        var handle = ScreenCaptureInterop.FindTopLevelWindowAtPoint(screenX, screenY);
        var clipped = ScreenCaptureInterop.TryGetWindowCaptureBounds(handle, out var bounds) ? ClipToMonitor(bounds) : default(RECT?);
        if (clipped is not { } clip || clip.Width < 8 || clip.Height < 8)
        {
            HoverHighlight.Visibility = Visibility.Collapsed;
            _hoverBounds = null;
            UpdateDimMask(null);
            return;
        }

        _hoverBounds = clip;
        var left = (clip.Left - _monitor.Left) / Scale;
        var top = (clip.Top - _monitor.Top) / Scale;
        var width = clip.Width / Scale;
        var height = clip.Height / Scale;
        HoverHighlight.Visibility = Visibility.Visible;
        Canvas.SetLeft(HoverHighlight, left);
        Canvas.SetTop(HoverHighlight, top);
        HoverHighlight.Width = width;
        HoverHighlight.Height = height;
        UpdateDimMask(new Windows.Foundation.Rect(left, top, width, height));
    }

    private void UpdateDimMask(Windows.Foundation.Rect? hole)
    {
        _dimHole = hole;
        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (hole is not { } target || target.Width <= 0 || target.Height <= 0)
        {
            SetDimRectangle(DimTop, 0, 0, width, height);
            SetDimRectangle(DimLeft, 0, 0, 0, 0);
            SetDimRectangle(DimRight, 0, 0, 0, 0);
            SetDimRectangle(DimBottom, 0, 0, 0, 0);
            return;
        }

        var left = Math.Clamp(target.X, 0, width);
        var top = Math.Clamp(target.Y, 0, height);
        var right = Math.Clamp(target.Right, left, width);
        var bottom = Math.Clamp(target.Bottom, top, height);
        SetDimRectangle(DimTop, 0, 0, width, top);
        SetDimRectangle(DimBottom, 0, bottom, width, height - bottom);
        SetDimRectangle(DimLeft, 0, top, left, bottom - top);
        SetDimRectangle(DimRight, right, top, width - right, bottom - top);
    }

    private static void SetDimRectangle(Rectangle rectangle, double left, double top, double width, double height)
    {
        rectangle.Visibility = width > 0 && height > 0 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        rectangle.Width = Math.Max(0, width);
        rectangle.Height = Math.Max(0, height);
    }

    private RECT ToPhysicalRect(Windows.Foundation.Point start, Windows.Foundation.Point end)
    {
        var scale = Scale;
        var left = _monitor.Left + (int)Math.Round(Math.Min(start.X, end.X) * scale);
        var top = _monitor.Top + (int)Math.Round(Math.Min(start.Y, end.Y) * scale);
        var right = _monitor.Left + (int)Math.Round(Math.Max(start.X, end.X) * scale);
        var bottom = _monitor.Top + (int)Math.Round(Math.Max(start.Y, end.Y) * scale);
        return new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
    }

    private RECT ClipToMonitor(RECT bounds) => new()
    {
        Left = Math.Max(bounds.Left, _monitor.Left),
        Top = Math.Max(bounds.Top, _monitor.Top),
        Right = Math.Min(bounds.Right, _monitor.Right),
        Bottom = Math.Min(bounds.Bottom, _monitor.Bottom)
    };

    private void Complete(RECT? bounds)
    {
        if (_completion is null) return;
        var completion = _completion;
        _completion = null;
        _dragging = false;
        _appWindow?.Hide();
        FrozenScreenImage.Source = null;
        completion.TrySetResult(bounds);
    }

    private static WriteableBitmap? CaptureFrozenScreen(RECT bounds)
    {
        var pixels = ScreenCaptureInterop.CaptureRegion(bounds);
        if (pixels is null) return null;
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;

        var bitmap = new WriteableBitmap(bounds.Width, bounds.Height);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    private static string L(string key) => LocalizationService.Get(key);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint windowHandle, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint windowHandle, uint attribute, ref uint value, uint valueSize);

    private static nint GetWindowLong(nint windowHandle, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(windowHandle, index) : GetWindowLong32(windowHandle, index);

    private static nint SetWindowLong(nint windowHandle, int index, nint value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(windowHandle, index, value) : SetWindowLong32(windowHandle, index, (int)value);
}
