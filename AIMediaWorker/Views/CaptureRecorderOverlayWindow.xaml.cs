using System.Globalization;
using System.Runtime.InteropServices;
using AIMediaWorker.Capture;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using AIMediaWorker.Settings;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace AIMediaWorker.Views;

internal enum CaptureRecorderMode
{
    Capture,
    Record
}

/// <summary>
/// Always-on-top toolbar shown while the main window is hidden. Offers a capture/record mode
/// selector plus grouped fullscreen/window/region/OCR actions, and switches to a compact
/// recording state with elapsed time, pause, and stop controls. Closing it restores the main window.
/// </summary>
public sealed partial class CaptureRecorderOverlayWindow : Window
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
    private readonly DispatcherQueueTimer _elapsedTimer;
    private readonly DispatcherQueueTimer _statusHideTimer;
    private AppSettings _settings = new();
    private CaptureRecorderMode _mode = CaptureRecorderMode.Capture;
    private MediaFoundationH264Recorder? _recorder;
    private CaptureRegionSelectorWindow? _selector;
    private RECT? _targetMonitor;
    private Task? _shutdownTask;
    private bool _allowClose;
    private bool _hasUserPosition;
    private bool _isDraggingOverlay;
    private ScreenCaptureInterop.POINT _dragStartCursor;
    private PointInt32 _dragStartWindowPosition;

    public CaptureRecorderOverlayWindow(Window owner)
    {
        InitializeComponent();
        Title = L("CaptureRecorderTitle");
        WindowOwner.Attach(this, owner);
        _selfHandle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_selfHandle));
        if (_appWindow is not null)
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            RemoveNativeWindowFrame();
            _appWindow.ResizeClient(new SizeInt32(640, 50));
            _appWindow.Closing += OnAppWindowClosing;
        }

        _elapsedTimer = DispatcherQueue.CreateTimer();
        _elapsedTimer.Interval = TimeSpan.FromMilliseconds(250);
        _elapsedTimer.Tick += OnElapsedTimerTick;

        _statusHideTimer = DispatcherQueue.CreateTimer();
        _statusHideTimer.Interval = TimeSpan.FromSeconds(4);
        _statusHideTimer.Tick += (_, _) =>
        {
            _statusHideTimer.Stop();
            StatusText.Visibility = Visibility.Collapsed;
        };

        Closed += (_, _) =>
        {
            _elapsedTimer.Stop();
            _shutdownTask ??= ShutdownAsync();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await SettingsService.CreateDefault().LoadAsync();
        }
        catch
        {
            _settings = new AppSettings();
        }

        UiFontService.Apply(_settings.General.UiFontFamily, Root);
        Root.RequestedTheme = _settings.General.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        RemoveNativeWindowFrame();
        ApplyLocalizedTexts();
        OcrButton.IsEnabled = true;
        AdjustWindowSize();
        ShowOverlay();
    }

    private void ApplyLocalizedTexts()
    {
        FullscreenButton.Content = L("AreaFullscreen.Content");
        WindowAreaButton.Content = L("AreaWindow.Content");
        RegionButton.Content = L("AreaRegion.Content");
        OcrButton.Content = L("OcrAction.Content");
        ToolTipService.SetToolTip(OcrButton, L("OcrTooltip"));
        PauseResumeButton.Content = L("RecordPause.Content");
        StopRecordButton.Content = L("RecordStop.Content");
        ToolTipService.SetToolTip(CloseOverlayButton, L("CloseTooltip"));
        CaptureModeButton.Content = L("CaptureModeToggle.Content");
        RecordModeButton.Content = L("RecordModeToggle.Content");
        UpdateModeButtons();
    }

    private double Scale => Root.XamlRoot?.RasterizationScale ?? 1.0;

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        _ = CloseAsync();
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e) => AdjustWindowSize();

    private void AdjustWindowSize()
    {
        if (_appWindow is null || Root.ActualWidth <= 0) return;
        var scale = Scale;
        var widthPixels = (int)Math.Ceiling(Root.ActualWidth * scale);
        var heightPixels = (int)Math.Ceiling(Root.ActualHeight * scale);
        if (_appWindow.ClientSize.Width != widthPixels || _appWindow.ClientSize.Height != heightPixels)
        {
            _appWindow.ResizeClient(new SizeInt32(widthPixels, heightPixels));
        }

        RepositionToTarget();
    }

    private void RepositionToTarget()
    {
        if (_appWindow is null || Root.ActualWidth <= 0 || _hasUserPosition) return;
        if (_targetMonitor is not { } monitor)
        {
            var cursor = ScreenCaptureInterop.GetCursorPosition();
            monitor = ScreenCaptureInterop.GetMonitorBounds(cursor.X, cursor.Y);
            _targetMonitor = monitor;
        }

        var scale = Scale;
        var widthPixels = (int)Math.Ceiling(Root.ActualWidth * scale);
        var heightPixels = (int)Math.Ceiling(Root.ActualHeight * scale);
        var x = monitor.Left + Math.Max(0, (monitor.Width - widthPixels) / 2);
        var y = monitor.Top + (int)Math.Round(18 * scale);
        _appWindow.Move(new PointInt32(x, y));
    }

    private void OnDragHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement dragSurface || !e.GetCurrentPoint(dragSurface).Properties.IsLeftButtonPressed) return;
        _hasUserPosition = true;
        _isDraggingOverlay = true;
        _dragStartCursor = ScreenCaptureInterop.GetCursorPosition();
        _dragStartWindowPosition = _appWindow?.Position ?? default;
        dragSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDragHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingOverlay || _appWindow is null) return;
        if (!e.GetCurrentPoint(sender as UIElement).Properties.IsLeftButtonPressed)
        {
            EndOverlayDrag(sender as UIElement, e);
            return;
        }

        var cursor = ScreenCaptureInterop.GetCursorPosition();
        _appWindow.Move(new PointInt32(
            _dragStartWindowPosition.X + cursor.X - _dragStartCursor.X,
            _dragStartWindowPosition.Y + cursor.Y - _dragStartCursor.Y));
        e.Handled = true;
    }

    private void OnDragHandlePointerReleased(object sender, PointerRoutedEventArgs e)
        => EndOverlayDrag(sender as UIElement, e);

    private void OnDragHandlePointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _isDraggingOverlay = false;

    private void EndOverlayDrag(UIElement? dragSurface, PointerRoutedEventArgs e)
    {
        if (!_isDraggingOverlay) return;
        _isDraggingOverlay = false;
        dragSurface?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
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

    private void ShowOverlay()
    {
        _appWindow?.Show();
        RemoveNativeWindowFrame();
        Activate();
        FocusSink.Focus(FocusState.Programmatic);
    }

    private void HideOverlay() => _appWindow?.Hide();

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        _statusHideTimer.Stop();
        _statusHideTimer.Start();
    }

    private void OnCaptureModeClick(object sender, RoutedEventArgs e)
    {
        SetMode(CaptureRecorderMode.Capture);
    }

    private void OnRecordModeClick(object sender, RoutedEventArgs e)
    {
        SetMode(CaptureRecorderMode.Record);
    }

    private void SetMode(CaptureRecorderMode mode)
    {
        _mode = mode;
        OcrButton.IsEnabled = mode == CaptureRecorderMode.Capture;
        UpdateModeButtons();
    }

    private void UpdateModeButtons()
    {
        CaptureModeButton.IsChecked = _mode == CaptureRecorderMode.Capture;
        RecordModeButton.IsChecked = _mode == CaptureRecorderMode.Record;
    }

    private async void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        var cursor = ScreenCaptureInterop.GetCursorPosition();
        await ExecuteForBoundsAsync(ScreenCaptureInterop.GetMonitorBounds(cursor.X, cursor.Y));
    }

    private async void OnWindowAreaClick(object sender, RoutedEventArgs e) => await SelectThenExecuteAsync(CaptureSelectorMode.Window);

    private async void OnRegionClick(object sender, RoutedEventArgs e) => await SelectThenExecuteAsync(CaptureSelectorMode.Region);

    private async void OnOcrClick(object sender, RoutedEventArgs e) => await SelectAndRecognizeAsync();

    private async Task SelectThenExecuteAsync(CaptureSelectorMode selectorMode)
    {
        var cursor = ScreenCaptureInterop.GetCursorPosition();
        var monitor = ScreenCaptureInterop.GetMonitorBounds(cursor.X, cursor.Y);
        HideOverlay();
        RECT? bounds;
        try
        {
            _selector ??= new CaptureRegionSelectorWindow();
            bounds = await _selector.SelectAsync(selectorMode, monitor);
        }
        catch (Exception exception)
        {
            await HandleActionErrorAsync(L("CaptureErrorTitle"), "CAPTURE_SELECTOR_ERROR", exception);
            ShowOverlay();
            return;
        }

        if (bounds is null)
        {
            ShowOverlay();
            return;
        }

        await ExecuteForBoundsAsync(bounds.Value);
    }

    private async Task ExecuteForBoundsAsync(RECT bounds)
    {
        HideOverlay();
        await Task.Delay(150);
        if (_mode == CaptureRecorderMode.Record)
        {
            await StartRecordingAsync(bounds);
            return;
        }

        try
        {
            await CapturePngAsync(bounds);
        }
        catch (Exception exception)
        {
            await HandleActionErrorAsync(L("CaptureErrorTitle"), "CAPTURE_PNG_ERROR", exception);
        }
        finally
        {
            ShowOverlay();
        }
    }

    private async Task CapturePngAsync(RECT bounds)
    {
        var pixels = ScreenCaptureInterop.CaptureRegion(bounds)
            ?? throw new InvalidOperationException(L("CaptureErrorTitle"));
        var directory = ScreenCaptureService.ResolveHomeDirectory(_settings.General.DefaultFolder);
        var path = ScreenCaptureService.BuildUniqueFilePath(directory, $"AIMediaWorker_Capture_{DateTime.Now:yyyyMMdd_HHmmss}", ".png");
        await ScreenCaptureService.SavePngAsync(pixels, bounds.Width, bounds.Height, path);
        ShowStatus(F("StatusSavedFormat", path));
    }

    private async Task StartRecordingAsync(RECT bounds)
    {
        try
        {
            var directory = ScreenCaptureService.ResolveHomeDirectory(_settings.General.DefaultFolder);
            var path = ScreenCaptureService.BuildUniqueFilePath(directory, $"AIMediaWorker_Recording_{DateTime.Now:yyyyMMdd_HHmmss}", ".mp4");
            _recorder = MediaFoundationH264Recorder.Start(path, bounds);
            ScreenCaptureInterop.ExcludeWindowFromCapture(_selfHandle, true);
            _targetMonitor = ScreenCaptureInterop.GetMonitorBounds(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            ToolbarPanel.Visibility = Visibility.Collapsed;
            RecordingPanel.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;
            PauseResumeButton.IsEnabled = true;
            StopRecordButton.IsEnabled = true;
            PauseResumeButton.Content = L("RecordPause.Content");
            RecordingElapsedText.Text = FormatElapsed(TimeSpan.Zero);
            AdjustWindowSize();
            _elapsedTimer.Start();
            ShowOverlay();
        }
        catch (Exception exception)
        {
            _recorder?.Dispose();
            _recorder = null;
            await HandleActionErrorAsync(L("RecordingErrorTitle"), "CAPTURE_RECORD_START_ERROR", exception);
            ShowOverlay();
        }
    }

    private void OnElapsedTimerTick(DispatcherQueueTimer sender, object args)
        => RecordingElapsedText.Text = FormatElapsed(_recorder?.Elapsed ?? TimeSpan.Zero);

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");

    private void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (_recorder is null) return;
        if (_recorder.IsPaused)
        {
            _recorder.Resume();
            PauseResumeButton.Content = L("RecordPause.Content");
        }
        else
        {
            _recorder.Pause();
            PauseResumeButton.Content = L("RecordResume.Content");
        }
    }

    private async void OnStopRecordClick(object sender, RoutedEventArgs e)
    {
        var recorder = _recorder;
        if (recorder is null) return;
        PauseResumeButton.IsEnabled = false;
        StopRecordButton.IsEnabled = false;
        _recorder = null;
        _elapsedTimer.Stop();
        await recorder.StopAsync();
        ScreenCaptureInterop.ExcludeWindowFromCapture(_selfHandle, false);
        recorder.Dispose();
        RecordingPanel.Visibility = Visibility.Collapsed;
        ToolbarPanel.Visibility = Visibility.Visible;
        _targetMonitor = null;
        AdjustWindowSize();
        ShowOverlay();
        if (recorder.Failure is not null)
        {
            await HandleActionErrorAsync(L("RecordingErrorTitle"), "CAPTURE_RECORD_STOP_ERROR", recorder.Failure);
        }
        else
        {
            ShowStatus(F("StatusSavedFormat", recorder.OutputPath));
        }
    }

    private async Task SelectAndRecognizeAsync()
    {
        var cursor = ScreenCaptureInterop.GetCursorPosition();
        var monitor = ScreenCaptureInterop.GetMonitorBounds(cursor.X, cursor.Y);
        HideOverlay();
        try
        {
            RECT? bounds;
            try
            {
                _selector ??= new CaptureRegionSelectorWindow();
                bounds = await _selector.SelectAsync(CaptureSelectorMode.Region, monitor);
            }
            catch (Exception exception)
            {
                await HandleActionErrorAsync(L("CaptureErrorTitle"), "CAPTURE_SELECTOR_ERROR", exception);
                return;
            }

            if (bounds is null) return;
            await Task.Delay(120);
            var pixels = ScreenCaptureInterop.CaptureRegion(bounds.Value)
                ?? throw new InvalidOperationException(L("CaptureErrorTitle"));
            var text = await ScreenCaptureService.RecognizeTextAsync(pixels, bounds.Value.Width, bounds.Value.Height);
            if (text is null)
            {
                ShowStatus(L("ErrorNoOcrEngine"));
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowStatus(L("StatusOcrNoText"));
                return;
            }

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            ShowStatus(F("StatusOcrCopiedFormat", text.Length));
        }
        catch (Exception exception)
        {
            await HandleActionErrorAsync(L("CaptureErrorTitle"), "CAPTURE_OCR_ERROR", exception);
        }
        finally
        {
            ShowOverlay();
        }
    }

    private async Task HandleActionErrorAsync(string title, string eventId, Exception exception)
    {
        await AppLog.WriteAsync("error", "capture", eventId, exception.Message, exception);
        ShowStatus($"{title}: {exception.Message}");
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseOverlayButton.IsEnabled = false;
        await CloseAsync();
    }

    public async Task CloseAsync()
    {
        _shutdownTask ??= ShutdownAsync();
        await _shutdownTask;
        if (_allowClose) return;
        _allowClose = true;
        Close();
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        await CloseAsync();
    }

    private async Task ShutdownAsync()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is not null)
        {
            try
            {
                await recorder.StopAsync();
            }
            catch
            {
                // Best effort during shutdown.
            }

            ScreenCaptureInterop.ExcludeWindowFromCapture(_selfHandle, false);
            recorder.Dispose();
        }

        _selector?.Close();
        _selector = null;
    }

    private static string L(string key) => LocalizationService.Get(key);

    private static string F(string key, params object[] arguments)
        => string.Format(CultureInfo.CurrentCulture, L(key), arguments);

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
