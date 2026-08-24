using System.Drawing;
using AIMediaWorker.Capture;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AIMediaWorker.Views;

public sealed partial class ScreenRecordingWindow : Window
{
    private readonly ScreenRecordingSession _session = new();
    private readonly DispatcherQueueTimer _timer;
    private AppWindow? _appWindow;
    private Rectangle _selectedRegion;
    private string? _recordingFileName;
    private Task? _startOperation;
    private Task? _stopOperation;
    private Task? _shutdownTask;
    private bool _allowClose;

    public ScreenRecordingWindow(Window owner)
    {
        InitializeComponent();
        Title = L("ScreenRecordingWindow.Title");
        WindowOwner.Attach(this, owner);
        CaptureModeCombo.SelectedIndex = 0;
        _selectedRegion = RegionSelectionWindow.GetPrimaryScreenBounds();
        UpdateRegionText();

        var handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        _appWindow?.Resize(new SizeInt32(760, 520));
        if (_appWindow is not null) _appWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTimerTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        SetStatus(L("ReadyText"), L("ScreenRecordingReadyMessage"), InfoBarSeverity.Informational);
    }

    private void OnCaptureModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowTargetPanel is null || RegionTargetPanel is null) return;
        var region = (CaptureModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Region";
        WindowTargetPanel.Visibility = region ? Visibility.Collapsed : Visibility.Visible;
        RegionTargetPanel.Visibility = region ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRefreshWindowsClick(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        var selectedHandle = (WindowCombo.SelectedItem as ScreenWindow)?.Handle;
        var windows = ScreenWindowEnumerator.GetCapturableWindows();
        WindowCombo.ItemsSource = windows;
        WindowCombo.SelectedItem = windows.FirstOrDefault(window => window.Handle == selectedHandle) ?? windows.FirstOrDefault();
    }

    private async void OnSelectRegionClick(object sender, RoutedEventArgs e)
    {
        var selector = new RegionSelectionWindow(this);
        var selected = await selector.SelectAsync();
        if (selected is null) return;
        _selectedRegion = selected.Value;
        UpdateRegionText();
    }

    private void UpdateRegionText() => RegionText.Text = $"{_selectedRegion.Width} × {_selectedRegion.Height} · X {_selectedRegion.X}, Y {_selectedRegion.Y}";

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_startOperation is not null || _stopOperation is { IsCompleted: false }) return;
        _stopOperation = null;
        _startOperation = StartRecordingAsync();
        try { await _startOperation; }
        finally { _startOperation = null; }
    }

    private async Task StartRecordingAsync()
    {
        if (_session.IsRecording) return;
        try
        {
            var target = CreateTarget();
            if (target is null) { SetStatus(L("ScreenRecordingTargetRequiredTitle"), L("ScreenRecordingTargetRequiredMessage"), InfoBarSeverity.Warning); return; }
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                DefaultFileExtension = ".mp4",
                SuggestedFileName = $"screen-{DateTime.Now:yyyyMMdd-HHmmss}"
            };
            picker.FileTypeChoices.Add(L("Mpeg4FileType"), [".mp4"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            _stopOperation = null;
            SetControlsForRecording(true);
            SetStatus(L("ScreenRecordingStartingTitle"), L("ScreenRecordingStartingMessage"), InfoBarSeverity.Informational);
            await _session.StartAsync(target, file.Path, (int)Math.Round(FrameRateBox.Value));
            _recordingFileName = file.Name;
            _timer.Start();
            OnTimerTick(_timer, null!);
            SetStatus(L("RecordingTitle"), F("ScreenRecordingActiveMessage", file.Name), InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            SetControlsForRecording(false);
            await AppLog.WriteAsync("error", "screen-recording", "SCREEN_RECORDING_START_ERROR", exception.Message, exception);
            SetStatus(L("ScreenRecordingErrorTitle"), exception.Message, InfoBarSeverity.Error);
        }
    }

    private ScreenCaptureTarget? CreateTarget()
    {
        var regionMode = (CaptureModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Region";
        if (regionMode) return _selectedRegion.Width >= 2 && _selectedRegion.Height >= 2 ? ScreenCaptureTarget.ForRegion(_selectedRegion) : null;
        return WindowCombo.SelectedItem is ScreenWindow window ? ScreenCaptureTarget.ForWindow(window.Handle, window.Title) : null;
    }

    private async void OnStopClick(object sender, RoutedEventArgs e) => await StopRecordingAsync();

    private Task StopRecordingAsync()
    {
        if (_stopOperation is not null) return _stopOperation;
        _stopOperation = StopRecordingCoreAsync();
        return _stopOperation;
    }

    private async Task StopRecordingCoreAsync()
    {
        try
        {
            if (!_session.IsRecording) return;
            StopButton.IsEnabled = false;
            SetStatus(L("ScreenRecordingFinalizingTitle"), L("ScreenRecordingFinalizingMessage"), InfoBarSeverity.Informational);
            await _session.StopAsync();
            SetStatus(L("CaptureSavedTitle"), F("ScreenRecordingSavedMessage", _recordingFileName ?? string.Empty), InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "screen-recording", "SCREEN_RECORDING_STOP_ERROR", exception.Message, exception);
            SetStatus(L("ScreenRecordingErrorTitle"), exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _timer.Stop();
            SetControlsForRecording(false);
        }
    }

    private void SetControlsForRecording(bool recording)
    {
        StartButton.IsEnabled = !recording;
        StopButton.IsEnabled = recording;
        CaptureModeCombo.IsEnabled = !recording;
        WindowCombo.IsEnabled = !recording;
        FrameRateBox.IsEnabled = !recording;
        WindowTargetPanel.IsHitTestVisible = !recording;
        RegionTargetPanel.IsHitTestVisible = !recording;
        RecordingIcon.Foreground = recording
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 50, 47))
            : null;
        if (!recording) RecordingTimeText.Text = "00:00:00";
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = _session.StartedAt is { } started ? DateTimeOffset.Now - started : TimeSpan.Zero;
        RecordingTimeText.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void SetStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
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

    private async Task ShutdownAsync()
    {
        try
        {
            if (_startOperation is { } starting)
            {
                try { await starting; } catch (Exception) { }
            }
            if (_session.IsRecording || _stopOperation is not null) await StopRecordingAsync();
            await _session.DisposeAsync();
        }
        catch (Exception exception) { await AppLog.WriteAsync("error", "screen-recording", "SCREEN_RECORDING_SHUTDOWN_ERROR", exception.Message, exception); }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        if (_appWindow is not null) _appWindow.Closing -= OnAppWindowClosing;
        Closed -= OnClosed;
    }

    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) => string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);
}
