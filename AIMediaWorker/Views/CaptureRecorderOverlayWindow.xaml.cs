using System.Globalization;
using System.Runtime.InteropServices;
using AIMediaWorker.Asr;
using AIMediaWorker.Capture;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Llm;
using AIMediaWorker.Localization;
using AIMediaWorker.Network;
using AIMediaWorker.Settings;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

namespace AIMediaWorker.Views;

internal enum CaptureRecorderMode
{
    Capture,
    Record
}

internal enum OcrCaptureAction
{
    Recognize,
    Translate,
    Vlm
}

/// <summary>
/// Always-on-top toolbar shown while the main window is hidden. Offers a capture/record mode
/// selector plus grouped fullscreen/window/region/OCR actions, and switches to a compact
/// recording state with elapsed time, pause, and stop controls. Closing it restores the main window.
/// </summary>
public sealed partial class CaptureRecorderOverlayWindow : Window
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const nint WsBorder = 0x00800000;
    private const nint WsDlgFrame = 0x00400000;
    private const nint WsThickFrame = 0x00040000;
    private const nint WsExTopmost = 0x00000008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly nint HwndTopmost = new(-1);
    private const uint DwmwaCloak = 13;
    private const uint DwmwaBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VkBack = 0x08;

    private readonly AppWindow? _appWindow;
    private readonly nint _selfHandle;
    private readonly DispatcherQueueTimer _elapsedTimer;
    private readonly DispatcherQueueTimer _statusHideTimer;
    private readonly AsrWorkerClient _dictationAsr = new();
    private readonly AudioCaptureService _dictationAudio = new();
    private readonly LiveCaptionStabilizer _dictationStabilizer = new();
    private readonly LiveAsrController _dictation;
    private readonly bool _settingsProvided;
    private AppSettings _settings;
    private bool _startupWindowCloaked;
    private bool _startupRevealScheduled;
    private CaptureRecorderMode _mode = CaptureRecorderMode.Capture;
    private MediaFoundationH264Recorder? _recorder;
    private LlmService? _ocrTranslator;
    private CaptureRegionSelectorWindow? _selector;
    private RECT? _targetMonitor;
    private Task? _shutdownTask;
    private bool _allowClose;
    private bool _hasUserPosition;
    private bool _isDraggingOverlay;
    private bool _topmostRepairPending;
    private bool _isClosed;
    private bool _acceptDictationResults;
    private bool _dictationArmed;
    private bool _startingDictationCapture;
    private nint _dictationTarget;
    private string _injectedDictationText = string.Empty;
    private ScreenCaptureInterop.POINT _dragStartCursor;
    private PointInt32 _dragStartWindowPosition;

    public CaptureRecorderOverlayWindow(Window? owner = null, AppSettings? initialSettings = null)
    {
        _settings = initialSettings ?? new AppSettings();
        _settingsProvided = initialSettings is not null;
        _selfHandle = WindowNative.GetWindowHandle(this);
        _startupWindowCloaked = SetWindowCloak(_selfHandle, cloak: true);
        InitializeComponent();
        _dictation = new LiveAsrController(_dictationAudio, _dictationAsr);
        _dictation.CaptionReceived += OnDictationReceived;
        _dictation.Failed += OnDictationFailed;
        Title = L("CaptureRecorderTitle");
        if (owner is not null) WindowOwner.Attach(this, owner);
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
            _appWindow.ResizeClient(new SizeInt32(510, 42));
            _appWindow.Closing += OnAppWindowClosing;
            _appWindow.Changed += OnAppWindowChanged;
        }

        Activated += OnWindowActivated;
        EnsureAlwaysOnTop(forceZOrder: true);

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
            _isClosed = true;
            Activated -= OnWindowActivated;
            if (_appWindow is not null) _appWindow.Changed -= OnAppWindowChanged;
            _elapsedTimer.Stop();
            _shutdownTask ??= ShutdownAsync();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_settingsProvided)
        {
            try
            {
                _settings = await SettingsService.CreateDefault().LoadAsync();
            }
            catch
            {
                _settings = new AppSettings();
            }
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
        RevealAfterNextFrame();
    }

    private void RevealAfterNextFrame()
    {
        if (!_startupWindowCloaked || _startupRevealScheduled) return;
        _startupRevealScheduled = true;
        EventHandler<object>? rendering = null;
        rendering = (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendering;
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RevealStartupWindow))
                RevealStartupWindow();
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += rendering;
    }

    private void RevealStartupWindow()
    {
        if (!_startupWindowCloaked) return;
        _startupWindowCloaked = !SetWindowCloak(_selfHandle, cloak: false);
        if (!_startupWindowCloaked)
        {
            EnsureAlwaysOnTop(forceZOrder: true);
            StartupProfiler.Mark("capture-window-revealed");
        }
    }

    private static bool SetWindowCloak(nint windowHandle, bool cloak)
    {
        var value = cloak ? 1u : 0u;
        return DwmSetWindowAttribute(windowHandle, DwmwaCloak, ref value, sizeof(uint)) == 0;
    }

    private void ApplyLocalizedTexts()
    {
        SetButtonDescription(CaptureModeButton, L("CaptureModeToggle.Content"));
        SetButtonDescription(RecordModeButton, L("RecordModeToggle.Content"));
        SetButtonDescription(FullscreenButton, L("AreaFullscreen.Content"));
        SetButtonDescription(WindowAreaButton, L("AreaWindow.Content"));
        SetButtonDescription(ScrollCaptureButton, L("ScrollCaptureTooltip"));
        SetButtonDescription(RegionButton, L("AreaRegion.Content"));
        SetButtonDescription(OcrButton, L("OcrTooltip"));
        SetButtonDescription(TranslateOcrButton, L("OcrTranslateTooltip"));
        SetButtonDescription(VlmOcrButton, L("OcrVlmTooltip"));
        SetButtonDescription(DictationButton, L("DictationTooltip"));
        SetButtonDescription(StopRecordButton, L("RecordStop.Content"));
        SetButtonDescription(CloseOverlayButton, L("CloseTooltip"));
        UpdatePauseResumeButton(false);
        UpdateModeButtons();
    }

    private static void SetButtonDescription(FrameworkElement button, string description)
    {
        ToolTipService.SetToolTip(button, description);
        AutomationProperties.SetName(button, description);
    }

    private void UpdatePauseResumeButton(bool isPaused)
    {
        PauseResumeIcon.Glyph = isPaused ? "\uE768" : "\uE769";
        SetButtonDescription(PauseResumeButton, L(isPaused ? "RecordResume.Content" : "RecordPause.Content"));
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
        EnsureAlwaysOnTop(forceZOrder: true);
        Activate();
        EnsureAlwaysOnTop(forceZOrder: true);
        FocusSink.Focus(FocusState.Programmatic);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        EnsureAlwaysOnTop(forceZOrder: true);
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        // Foreground ownership changes just after this callback. Sampling on the
        // dispatcher lets the user's next input-field click choose the target;
        // the overlay never forces focus back to another app.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CaptureExternalForegroundWindow);
    }

    private void CaptureExternalForegroundWindow()
    {
        var window = GetForegroundWindow();
        if (!IsExternalWindow(window)) return;
        if (_dictationArmed && !_dictation.IsRunning && !_startingDictationCapture)
            _ = StartDictationAtTargetAsync(window);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isClosed || _topmostRepairPending) return;
        _topmostRepairPending = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RepairAlwaysOnTop))
            _topmostRepairPending = false;
    }

    private void RepairAlwaysOnTop()
    {
        _topmostRepairPending = false;
        if (_isClosed) return;
        EnsureAlwaysOnTop();
    }

    private void EnsureAlwaysOnTop(bool forceZOrder = false)
    {
        var presenter = _appWindow?.Presenter as OverlappedPresenter;
        var presenterNeedsRepair = presenter is not null && !presenter.IsAlwaysOnTop;
        if (presenterNeedsRepair) presenter!.IsAlwaysOnTop = true;

        var nativeNeedsRepair = (GetWindowLong(_selfHandle, GwlExStyle) & WsExTopmost) == 0;
        if (forceZOrder || presenterNeedsRepair || nativeNeedsRepair)
        {
            SetWindowPos(_selfHandle, HwndTopmost, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate);
        }
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
        TranslateOcrButton.IsEnabled = mode == CaptureRecorderMode.Capture;
        VlmOcrButton.IsEnabled = mode == CaptureRecorderMode.Capture;
        ScrollCaptureButton.IsEnabled = mode == CaptureRecorderMode.Capture;
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

    private async void OnScrollCaptureClick(object sender, RoutedEventArgs e)
        => await SelectThenExecuteAsync(CaptureSelectorMode.Window, scrollingCapture: true);

    private async void OnRegionClick(object sender, RoutedEventArgs e) => await SelectThenExecuteAsync(CaptureSelectorMode.Region);

    private async void OnOcrClick(object sender, RoutedEventArgs e)
        => await SelectAndRecognizeAsync(OcrCaptureAction.Recognize);

    private async void OnTranslateOcrClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = GetOcrTranslator();
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message);
            return;
        }

        await SelectAndRecognizeAsync(OcrCaptureAction.Translate);
    }

    private async void OnVlmOcrClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = GetOcrTranslator();
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message);
            return;
        }

        await SelectAndRecognizeAsync(OcrCaptureAction.Vlm);
    }

    private async void OnDictationClick(object sender, RoutedEventArgs e)
    {
        if (_dictationArmed || _dictation.IsRunning || _startingDictationCapture)
        {
            await StopDictationAsync();
            return;
        }

        DictationButton.IsEnabled = false;
        DictationButton.IsChecked = true;
        _acceptDictationResults = false;
        _dictationTarget = 0;
        try
        {
            if (!File.Exists(AsrRuntimePaths.CrispAsrDllPath))
            {
                ShowStatus(L("AsrInstallRequiredMessage"));
                return;
            }

            ShowStatus(L("StatusLoadingAsr"));
            var runtimeDirectory = AsrRuntimePaths.GetCrispAsrRuntimeDirectory(_settings.Asr.CrispAsrRuntimeDirectory);
            await _dictationAsr.StartAsync(runtimeDirectory);
            var acceptingLoadProgress = true;
            var progress = new Progress<AsrEvent>(_ => { if (acceptingLoadProgress) ShowStatus(L("StatusLoadingAsr")); });
            try
            {
                await _dictationAsr.LoadModelAsync(
                    _settings.Asr.ModelPath ?? AsrSettings.DefaultModelId,
                    _settings.Asr.AlignerPath,
                    _settings.Asr.Device.ToString(),
                    _settings.Asr.Precision.ToString(),
                    progress);
            }
            finally
            {
                acceptingLoadProgress = false;
            }

            _dictationArmed = true;
            ShowStatus(L("DictationReady"));
            _statusHideTimer.Stop();
        }
        catch (UnauthorizedAccessException)
        {
            _acceptDictationResults = false;
            ShowStatus(L("MicrophonePermissionMessage"));
        }
        catch (Exception exception)
        {
            _acceptDictationResults = false;
            await HandleActionErrorAsync(L("LiveAsrErrorTitle"), "CAPTURE_DICTATION_START_ERROR", exception);
        }
        finally
        {
            DictationButton.IsEnabled = true;
            DictationButton.IsChecked = _dictationArmed || _dictation.IsRunning;
        }
    }

    private async Task StartDictationAtTargetAsync(nint target)
    {
        if (!_dictationArmed || _dictation.IsRunning || _startingDictationCapture || !IsExternalWindow(target)) return;
        _startingDictationCapture = true;
        _dictationTarget = target;
        _injectedDictationText = string.Empty;
        _dictationStabilizer.Reset();
        _dictationStabilizer.Language = _settings.Asr.Language;
        try
        {
            await _dictation.StartAsync(_settings.Capture.MicrophoneDeviceId, _settings.Asr.Language);
            if (!_dictationArmed)
            {
                await _dictation.StopAsync();
                return;
            }
            _acceptDictationResults = true;
            ShowStatus(L("DictationListening"));
        }
        catch (UnauthorizedAccessException)
        {
            _dictationArmed = false;
            _acceptDictationResults = false;
            ShowStatus(L("MicrophonePermissionMessage"));
        }
        catch (Exception exception)
        {
            _dictationArmed = false;
            _acceptDictationResults = false;
            await HandleActionErrorAsync(L("LiveAsrErrorTitle"), "CAPTURE_DICTATION_START_ERROR", exception);
        }
        finally
        {
            _startingDictationCapture = false;
            DictationButton.IsEnabled = true;
            DictationButton.IsChecked = _dictationArmed || _dictation.IsRunning;
        }
    }

    private async Task StopDictationAsync()
    {
        DictationButton.IsEnabled = false;
        _dictationArmed = false;
        _acceptDictationResults = false;
        try
        {
            if (_dictation.IsRunning) await _dictation.StopAsync();
            ShowStatus(L("DictationStopped"));
        }
        catch (Exception exception)
        {
            await HandleActionErrorAsync(L("LiveAsrErrorTitle"), "CAPTURE_DICTATION_STOP_ERROR", exception);
        }
        finally
        {
            _dictationTarget = 0;
            _injectedDictationText = string.Empty;
            _dictationStabilizer.Reset();
            DictationButton.IsChecked = false;
            DictationButton.IsEnabled = true;
        }
    }

    private void OnDictationReceived(object? sender, AsrEvent result) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_acceptDictationResults) return;
        var text = _dictationStabilizer.UpdateState(result).DisplayText;
        if (string.IsNullOrWhiteSpace(text)) return;
        InjectDictationText(text);
    });

    private void OnDictationFailed(object? sender, Exception exception) => DispatcherQueue.TryEnqueue(async () =>
    {
        _dictationArmed = false;
        _acceptDictationResults = false;
        DictationButton.IsChecked = _dictation.IsRunning;
        await HandleActionErrorAsync(L("LiveAsrErrorTitle"), "CAPTURE_DICTATION_ERROR", exception);
    });

    private static bool IsExternalWindow(nint window)
    {
        if (window == 0 || !IsWindow(window) || !IsWindowVisible(window)) return false;
        GetWindowThreadProcessId(window, out var processId);
        return processId != (uint)Environment.ProcessId;
    }

    private void InjectDictationText(string text)
    {
        if (!IsExternalWindow(_dictationTarget) || GetForegroundWindow() != _dictationTarget) return;

        var commonLength = 0;
        var maximum = Math.Min(_injectedDictationText.Length, text.Length);
        while (commonLength < maximum && _injectedDictationText[commonLength] == text[commonLength]) commonLength++;
        if (commonLength > 0 && commonLength < maximum && char.IsLowSurrogate(text[commonLength])) commonLength--;

        var inputs = new List<INPUT>();
        for (var index = commonLength; index < _injectedDictationText.Length; index++)
        {
            if (char.IsLowSurrogate(_injectedDictationText[index])) continue;
            AddKeyStroke(inputs, VkBack, '\0', 0);
        }
        foreach (var character in text.AsSpan(commonLength))
            AddKeyStroke(inputs, 0, character, KeyEventUnicode);

        if (inputs.Count == 0 || SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) == inputs.Count)
            _injectedDictationText = text;
        else
            ShowStatus(L("DictationInputFailed"));
    }

    private static void AddKeyStroke(List<INPUT> inputs, ushort virtualKey, char scanCode, uint flags)
    {
        inputs.Add(new INPUT { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey, ScanCode = scanCode, Flags = flags } } });
        inputs.Add(new INPUT { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey, ScanCode = scanCode, Flags = flags | KeyEventKeyUp } } });
    }

    private async Task SelectThenExecuteAsync(CaptureSelectorMode selectorMode, bool scrollingCapture = false)
    {
        var cursor = ScreenCaptureInterop.GetCursorPosition();
        var monitor = ScreenCaptureInterop.GetMonitorBounds(cursor.X, cursor.Y);
        HideOverlay();
        CaptureSelection? selection;
        try
        {
            _selector ??= new CaptureRegionSelectorWindow();
            selection = await _selector.SelectAsync(selectorMode, monitor);
        }
        catch (Exception exception)
        {
            await HandleActionErrorAsync(L("CaptureErrorTitle"), "CAPTURE_SELECTOR_ERROR", exception);
            ShowOverlay();
            return;
        }

        if (selection is null)
        {
            ShowOverlay();
            return;
        }

        if (scrollingCapture)
        {
            await ExecuteScrollingCaptureAsync(selection.Value);
            return;
        }

        await ExecuteForBoundsAsync(selection.Value.Bounds);
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
        await SaveCaptureAsync(pixels, bounds.Width, bounds.Height);
    }

    private async Task ExecuteScrollingCaptureAsync(CaptureSelection selection)
    {
        HideOverlay();
        await Task.Delay(150);
        try
        {
            var capture = await ScrollingCaptureService.CaptureAsync(selection.WindowHandle, selection.Bounds);
            await SaveCaptureAsync(capture.Pixels, capture.Width, capture.Height);
        }
        catch (Exception exception)
        {
            await HandleActionErrorAsync(L("CaptureErrorTitle"), "CAPTURE_SCROLL_ERROR", exception);
        }
        finally
        {
            ShowOverlay();
        }
    }

    private async Task SaveCaptureAsync(byte[] pixels, int width, int height)
    {
        var directory = ScreenCaptureService.ResolveHomeDirectory(_settings.Capture.CaptureFolder);
        var path = ScreenCaptureService.BuildUniqueFilePath(directory, $"AIMediaWorker_Capture_{DateTime.Now:yyyyMMdd_HHmmss}", ".png");
        await ScreenCaptureService.SavePngAsync(pixels, width, height, path);
        await CopyImageToClipboardAsync(path);
        ShowStatus(F("StatusCaptureSavedAndCopiedFormat", path));
    }

    private async Task StartRecordingAsync(RECT bounds)
    {
        try
        {
            var directory = ScreenCaptureService.ResolveHomeDirectory(_settings.Capture.CaptureFolder);
            var path = ScreenCaptureService.BuildUniqueFilePath(directory, $"AIMediaWorker_Recording_{DateTime.Now:yyyyMMdd_HHmmss}", ".mp4");
            _recorder = MediaFoundationH264Recorder.Start(path, bounds);
            ScreenCaptureInterop.ExcludeWindowFromCapture(_selfHandle, true);
            _targetMonitor = ScreenCaptureInterop.GetMonitorBounds(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            ToolbarPanel.Visibility = Visibility.Collapsed;
            RecordingPanel.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;
            PauseResumeButton.IsEnabled = true;
            StopRecordButton.IsEnabled = true;
            UpdatePauseResumeButton(false);
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
            UpdatePauseResumeButton(false);
        }
        else
        {
            _recorder.Pause();
            UpdatePauseResumeButton(true);
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

    private async Task SelectAndRecognizeAsync(OcrCaptureAction action)
    {
        var cursor = ScreenCaptureInterop.GetCursorPosition();
        var monitor = ScreenCaptureInterop.GetMonitorBounds(cursor.X, cursor.Y);
        HideOverlay();
        try
        {
            CaptureSelection? selection;
            try
            {
                _selector ??= new CaptureRegionSelectorWindow();
                selection = await _selector.SelectAsync(CaptureSelectorMode.Region, monitor);
            }
            catch (Exception exception)
            {
                await HandleActionErrorAsync(L("CaptureErrorTitle"), "CAPTURE_SELECTOR_ERROR", exception);
                return;
            }

            if (selection is null) return;
            await Task.Delay(120);
            var bounds = selection.Value.Bounds;
            var pixels = ScreenCaptureInterop.CaptureRegion(bounds)
                ?? throw new InvalidOperationException(L("CaptureErrorTitle"));
            if (action == OcrCaptureAction.Vlm)
            {
                OcrButton.IsEnabled = false;
                TranslateOcrButton.IsEnabled = false;
                VlmOcrButton.IsEnabled = false;
                ShowOverlay();
                ShowStatus(L("StatusOcrVlmProcessing"));
                var pngBytes = await ScreenCaptureService.EncodePngAsync(pixels, bounds.Width, bounds.Height);
                var result = await GetOcrTranslator().RecognizeAndTranslateImageAsync(
                    pngBytes,
                    _settings.Llm.TranslationLanguage);
                CopyTextToClipboard(OcrClipboardFormatter.Compose(result.SourceText, result.Translation));
                ShowStatus(L("StatusOcrVlmCopied"));
                return;
            }

            var text = await ScreenCaptureService.RecognizeTextAsync(pixels, bounds.Width, bounds.Height);
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

            string? translation = null;
            if (action == OcrCaptureAction.Translate)
            {
                OcrButton.IsEnabled = false;
                TranslateOcrButton.IsEnabled = false;
                ShowOverlay();
                ShowStatus(L("StatusOcrTranslating"));
                try
                {
                    translation = await GetOcrTranslator().TranslateTextAsync(
                        text,
                        _settings.Llm.TranslationLanguage);
                    if (string.IsNullOrWhiteSpace(translation))
                        throw new InvalidOperationException(L("ErrorOcrTranslationEmpty"));
                }
                catch (Exception exception)
                {
                    await HandleActionErrorAsync(L("TranslationTitle"), "CAPTURE_OCR_TRANSLATION_ERROR", exception);
                    return;
                }
            }

            CopyTextToClipboard(OcrClipboardFormatter.Compose(text, translation));
            ShowStatus(translation is null
                ? F("StatusOcrCopiedFormat", text.Length)
                : L("StatusOcrTranslationCopied"));
        }
        catch (Exception exception)
        {
            await HandleActionErrorAsync(L("CaptureErrorTitle"), "CAPTURE_OCR_ERROR", exception);
        }
        finally
        {
            OcrButton.IsEnabled = _mode == CaptureRecorderMode.Capture;
            TranslateOcrButton.IsEnabled = _mode == CaptureRecorderMode.Capture;
            VlmOcrButton.IsEnabled = _mode == CaptureRecorderMode.Capture;
            ShowOverlay();
        }
    }

    private static void CopyTextToClipboard(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private LlmService GetOcrTranslator()
    {
        if (string.IsNullOrWhiteSpace(_settings.Llm.Model))
            throw new InvalidOperationException(L("LlmModelMissingMessage"));

        return _ocrTranslator ??= new LlmService(
            new LlmProviderFactory(new WindowsCredentialService()).Create(_settings.Llm.Provider),
            _settings.Llm.Model,
            _settings.Llm.ThinkingLevel);
    }

    private static async Task CopyImageToClipboardAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(package);
        Clipboard.Flush();
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
        _dictationArmed = false;
        _acceptDictationResults = false;
        try { await _dictation.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "capture", "CAPTURE_DICTATION_SHUTDOWN_ERROR", exception.Message, exception); }
        try { await _dictationAsr.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "capture", "CAPTURE_DICTATION_ASR_SHUTDOWN_ERROR", exception.Message, exception); }

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

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // SendInput validates sizeof(INPUT), whose native union is sized by
        // MOUSEINPUT (32 bytes on x64), even when every item is a key event.
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private static nint GetWindowLong(nint windowHandle, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(windowHandle, index) : GetWindowLong32(windowHandle, index);

    private static nint SetWindowLong(nint windowHandle, int index, nint value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(windowHandle, index, value) : SetWindowLong32(windowHandle, index, (int)value);
}
