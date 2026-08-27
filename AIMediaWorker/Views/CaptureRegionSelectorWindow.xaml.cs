using System.Runtime.InteropServices;
using AIMediaWorker.Capture;
using AIMediaWorker.Localization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private const int GwlExStyle = -20;
    private const nint WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x02;

    private readonly AppWindow? _appWindow;
    private TaskCompletionSource<RECT?>? _completion;
    private CaptureSelectorMode _mode;
    private RECT _monitor;
    private bool _dragging;
    private Windows.Foundation.Point _pressPoint;
    private RECT? _hoverBounds;

    public CaptureRegionSelectorWindow()
    {
        InitializeComponent();
        Title = "AIMediaWorker";
        var handle = WindowNative.GetWindowHandle(this);
        EnableDimTranslucency(handle);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
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

        Closed += (_, _) => Complete(null);
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, _) => Complete(null);
        Root.KeyboardAccelerators.Add(escape);
    }

    /// <summary>Shows the selector over the given monitor and completes with the chosen physical-pixel rect, or null when cancelled.</summary>
    internal async Task<RECT?> SelectAsync(CaptureSelectorMode mode, RECT monitorBounds)
    {
        if (_completion is not null) return null;
        var completion = new TaskCompletionSource<RECT?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        _mode = mode;
        _monitor = monitorBounds;
        _dragging = false;
        _hoverBounds = null;
        HintText.Text = L(mode == CaptureSelectorMode.Region ? "RegionHint.Text" : "WindowHint.Text");
        HintBorder.Visibility = mode == CaptureSelectorMode.Region ? Visibility.Collapsed : Visibility.Visible;
        HoverHighlight.Visibility = Visibility.Collapsed;
        SelectionBand.Visibility = Visibility.Collapsed;
        PositionOver(monitorBounds);
        Activate();
        _appWindow?.Show();
        FocusSink.Focus(FocusState.Programmatic);
        return await completion.Task;
    }

    private double Scale => Root.XamlRoot?.RasterizationScale ?? 1.0;

    private void PositionOver(RECT monitorBounds)
    {
        if (_appWindow is null) return;
        _appWindow.Move(new PointInt32(monitorBounds.Left, monitorBounds.Top));
        _appWindow.Resize(new SizeInt32(monitorBounds.Width, monitorBounds.Height));
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
    }

    private void UpdateHoverHighlight(Windows.Foundation.Point position)
    {
        var screenX = _monitor.Left + (int)Math.Round(position.X * Scale);
        var screenY = _monitor.Top + (int)Math.Round(position.Y * Scale);
        var handle = ScreenCaptureInterop.FindTopLevelWindowAtPoint(screenX, screenY);
        var clipped = ScreenCaptureInterop.TryGetWindowBounds(handle, out var bounds) ? ClipToMonitor(bounds) : default(RECT?);
        if (clipped is not { } clip || clip.Width < 8 || clip.Height < 8)
        {
            HoverHighlight.Visibility = Visibility.Collapsed;
            _hoverBounds = null;
            return;
        }

        _hoverBounds = clip;
        HoverHighlight.Visibility = Visibility.Visible;
        Canvas.SetLeft(HoverHighlight, (clip.Left - _monitor.Left) / Scale);
        Canvas.SetTop(HoverHighlight, (clip.Top - _monitor.Top) / Scale);
        HoverHighlight.Width = clip.Width / Scale;
        HoverHighlight.Height = clip.Height / Scale;
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
        completion.TrySetResult(bounds);
    }

    /// <summary>Makes the whole window uniformly translucent so the screen shows through the dim layer.</summary>
    private void EnableDimTranslucency(nint handle)
    {
        var previousStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, previousStyle | WsExLayered);
        SetLayeredWindowAttributes(handle, 0, 112, LwaAlpha);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint windowHandle, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint windowHandle, uint colorKey, byte alpha, uint flags);

    private static nint GetWindowLong(nint windowHandle, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(windowHandle, index) : GetWindowLong32(windowHandle, index);

    private static nint SetWindowLong(nint windowHandle, int index, nint value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(windowHandle, index, value) : SetWindowLong32(windowHandle, index, (int)value);

    private static string L(string key) => LocalizationService.Get(key);
}
