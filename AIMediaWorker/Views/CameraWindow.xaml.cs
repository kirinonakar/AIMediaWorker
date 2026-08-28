using AIMediaWorker.Asr;
using AIMediaWorker.Capture;
using AIMediaWorker.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;
using Windows.Storage;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using AIMediaWorker.Localization;
using AIMediaWorker.Diagnostics;
using Microsoft.UI.Dispatching;
using System.Diagnostics;

namespace AIMediaWorker.Views;

public sealed partial class CameraWindow : Window
{
    private readonly CameraManager _devices = new();
    private readonly CameraSession _camera = new();
    private readonly AsrWorkerClient _asr = new();
    private readonly AudioCaptureService _audio = new();
    private readonly LiveAsrController _liveAsr;
    private readonly DispatcherQueueTimer _recordingTimer;
    private readonly Stopwatch _recordingStopwatch = new();
    private TimeSpan _recordingPausedDuration;
    private DateTime? _recordingPausedAt;
    private AppSettings _settings = new();
    private AppWindow? _appWindow;
    private Task? _shutdownTask;
    private bool _allowClose;

    public CameraWindow(Window owner)
    {
        InitializeComponent();
        Title = L("CameraWindow.Title");
        AppTitleText.Text = Title;
        ExtendsContentIntoTitleBar = true;
        WindowOwner.Attach(this, owner);
        _liveAsr = new LiveAsrController(_audio, _asr);
        _liveAsr.CaptionReceived += OnCaptionReceived;
        _liveAsr.Failed += OnLiveFailed;
        _recordingTimer = DispatcherQueue.CreateTimer();
        _recordingTimer.Interval = TimeSpan.FromMilliseconds(500);
        _recordingTimer.Tick += OnRecordingTimerTick;
        Closed += OnClosed;
        var handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        _appWindow?.Resize(new SizeInt32(1100, 720));
        if (_appWindow is not null)
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
            _appWindow.Closing += OnAppWindowClosing;
            _appWindow.Changed += OnAppWindowChanged;
        }
        Root.ActualThemeChanged += OnRootActualThemeChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = await SettingsService.CreateDefault().LoadAsync();
        UiFontService.Apply(_settings.General.UiFontFamily, Root);
        ApplyTheme(_settings.General.Theme);
        ApplyCaptionAppearance();
        UpdateTitleBarDragRegion();
        await RefreshDevicesAsync();
    }

    private void ApplyTheme(AppTheme theme)
    {
        Root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyTitleBarTheme(Root.ActualTheme);
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args) => ApplyTitleBarTheme(sender.ActualTheme);

    private void ApplyTitleBarTheme(ElementTheme theme)
    {
        if (_appWindow?.TitleBar is not { } titleBar) return;
        var dark = theme == ElementTheme.Dark;
        var background = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 24, 24, 24);
        var inactiveForeground = dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 110, 110, 110);
        var hover = dark ? Color.FromArgb(255, 58, 58, 58) : Color.FromArgb(255, 224, 224, 224);
        var pressed = dark ? Color.FromArgb(255, 72, 72, 72) : Color.FromArgb(255, 208, 208, 208);
        AppTitleBarArea.Background = new SolidColorBrush(background);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
    }

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarDragRegion();

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => UpdateTitleBarDragRegion();

    private void UpdateTitleBarDragRegion()
    {
        if (_appWindow?.TitleBar is not { } titleBar) return;
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var width = AppTitleBarArea.ActualWidth * scale;
        var height = AppTitleBarArea.ActualHeight * scale;
        var dragWidth = Math.Max(0, width - titleBar.RightInset);
        var dragHeight = Math.Max(0, height);
        titleBar.SetDragRectangles([new RectInt32(0, 0, (int)dragWidth, (int)dragHeight)]);
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var cameras = await _devices.GetCamerasAsync();
            CameraCombo.ItemsSource = cameras;
            CameraCombo.SelectedItem = cameras.FirstOrDefault(device => device.Id == _settings.Capture.CameraDeviceId) ?? cameras.FirstOrDefault();
            var microphones = await _devices.GetMicrophonesAsync();
            MicrophoneCombo.ItemsSource = microphones;
            MicrophoneCombo.SelectedItem = microphones.FirstOrDefault(device => device.Id == _settings.Capture.MicrophoneDeviceId) ?? microphones.FirstOrDefault();
            SetStatus(L("ReadyText"), F("CameraDevicesFound", cameras.Count, microphones.Count), InfoBarSeverity.Informational);
        }
        catch (Exception exception) { SetStatus(L("DeviceErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_camera.IsRunning)
            {
                if (_camera.IsRecording) { await _camera.StopRecordingAsync(); StopRecordingIndicator(); UpdateRecordingControls(); }
                Preview.SetMediaPlayer(null); await _camera.StopAsync(); PreviewButton.Content = L("StartPreviewText"); return;
            }
            var device = CameraCombo.SelectedItem as CaptureDevice;
            await _camera.StartAsync(device?.Id, (MicrophoneCombo.SelectedItem as CaptureDevice)?.Id, _settings.Capture.Width, _settings.Capture.Height, _settings.Capture.FrameRate);
            Preview.SetMediaPlayer(_camera.Player);
            FormatCombo.ItemsSource = _camera.AvailableFormats;
            FormatCombo.SelectedItem = _camera.AvailableFormats.OrderBy(format => Math.Abs((long)format.Width - _settings.Capture.Width) + Math.Abs((long)format.Height - _settings.Capture.Height) + Math.Abs(format.FrameRate - _settings.Capture.FrameRate) * 10).FirstOrDefault();
            PreviewButton.Content = L("StopPreviewText");
            SetStatus(L("CameraPreviewTitle"), device?.Name ?? L("DefaultCameraText"), InfoBarSeverity.Success);
        }
        catch (UnauthorizedAccessException) { SetStatus(L("CameraPermissionTitle"), L("CameraPermissionMessage"), InfoBarSeverity.Error); }
        catch (Exception exception) { SetStatus(L("CameraErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }

    private async void OnRecordClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_camera.IsRecording)
            {
                try
                {
                    await _camera.StopRecordingAsync();
                    SetStatus(L("CaptureSavedTitle"), L("CaptureSavedMessage"), InfoBarSeverity.Success);
                }
                finally
                {
                    StopRecordingIndicator();
                    UpdateRecordingControls();
                }
                return;
            }
            if (!_camera.IsRunning)
            {
                var device = CameraCombo.SelectedItem as CaptureDevice;
                await _camera.StartAsync(device?.Id, (MicrophoneCombo.SelectedItem as CaptureDevice)?.Id, _settings.Capture.Width, _settings.Capture.Height, _settings.Capture.FrameRate);
                Preview.SetMediaPlayer(_camera.Player); PreviewButton.Content = L("StopPreviewText");
                FormatCombo.ItemsSource = _camera.AvailableFormats;
            }
            var directory = ScreenCaptureService.ResolveHomeDirectory(_settings.Capture.CaptureFolder);
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync(
                $"AIMediaWorker_Camera_{DateTime.Now:yyyyMMdd_HHmmss}.mp4",
                CreationCollisionOption.GenerateUniqueName);
            await _camera.StartRecordingAsync(file);
            StartRecordingIndicator();
            UpdateRecordingControls(); SetStatus(L("RecordingTitle"), file.Path, InfoBarSeverity.Warning);
        }
        catch (Exception exception) { SetStatus(L("CaptureErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }

    private async void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_camera.IsRunning || FormatCombo.SelectedItem is not CameraSession.CameraFormat format) return;
        try { await _camera.ApplyFormatAsync(format); SetStatus(L("CameraFormatTitle"), format.DisplayName, InfoBarSeverity.Success); }
        catch (Exception exception) { SetStatus(L("CameraFormatErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }

    private async void OnPauseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_camera.IsRecording) return;
            if (_camera.IsPaused)
            {
                await _camera.ResumeRecordingAsync();
                if (_recordingPausedAt is { } pausedAt)
                {
                    _recordingPausedDuration += DateTime.UtcNow - pausedAt;
                    _recordingPausedAt = null;
                }
                _recordingTimer.Start();
                UpdateRecordingControls();
                SetStatus(L("RecordingResumedTitle"), L("RecordingResumedMessage"), InfoBarSeverity.Warning);
            }
            else
            {
                await _camera.PauseRecordingAsync();
                _recordingPausedAt = DateTime.UtcNow;
                _recordingTimer.Stop();
                OnRecordingTimerTick(_recordingTimer, EventArgs.Empty);
                UpdateRecordingControls();
                SetStatus(L("RecordingPausedTitle"), L("RecordingPausedMessage"), InfoBarSeverity.Warning);
            }
        }
        catch (Exception exception) { SetStatus(L("CaptureErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }

    private void UpdateRecordingControls()
    {
        var recording = _camera.IsRecording;
        RecordButton.Content = recording ? L("StopRecordingText") : L("StartRecordingText");
        PauseButton.IsEnabled = recording;
        PauseButton.Content = _camera.IsPaused ? L("ResumeRecordingText") : L("PauseRecordingText");
    }

    private async void OnCaptionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_liveAsr.IsRunning)
            {
                CaptionButton.IsEnabled = false; await _liveAsr.StopAsync(); CaptionButton.Content = L("StartCaptionsText"); CaptionButton.IsEnabled = true; return;
            }
            if (!File.Exists(AsrRuntimePaths.CrispAsrDllPath))
            {
                SetStatus(L("AsrInstallRequiredTitle"), L("AsrInstallRequiredMessage"), InfoBarSeverity.Warning); return;
            }
            CaptionButton.IsEnabled = false;
            var runtimeDirectory = AsrRuntimePaths.GetCrispAsrRuntimeDirectory(_settings.Asr.CrispAsrRuntimeDirectory);
            await _asr.StartAsync(runtimeDirectory);
            var acceptingLoadProgress = true;
            var loadProgress = new Progress<AsrEvent>(update => { if (acceptingLoadProgress) UpdateAsrModelProgress(update); });
            try { await _asr.LoadModelAsync(_settings.Asr.ModelPath ?? AsrSettings.DefaultModelId, _settings.Asr.AlignerPath, _settings.Asr.Device.ToString(), _settings.Asr.Precision.ToString(), loadProgress); }
            finally { acceptingLoadProgress = false; }
            var language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
            await _liveAsr.StartAsync((MicrophoneCombo.SelectedItem as CaptureDevice)?.Id, language);
            CaptionButton.Content = L("StopCaptionsText");
            CaptionButton.IsEnabled = true;
            SetStatus(L("LiveCaptionsTitle"), L("ListeningMessage"), InfoBarSeverity.Success);
        }
        catch (UnauthorizedAccessException) { CaptionButton.IsEnabled = true; SetStatus(L("MicrophonePermissionTitle"), L("MicrophonePermissionMessage"), InfoBarSeverity.Error); }
        catch (Exception exception) { CaptionButton.IsEnabled = true; SetStatus(L("LiveAsrErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }

    private void UpdateAsrModelProgress(AsrEvent update)
    {
        if (update.Stage == "download" && update.Progress is { } progress)
        {
            var model = update.Message ?? "Qwen3-ASR";
            var modelProgress = update.ModelProgress ?? progress;
            var message = update.TotalBytes is > 0 && update.DownloadedBytes is { } downloaded
                ? F("StatusDownloadingAsrModel", model, modelProgress, FormatDownloadSize(downloaded), FormatDownloadSize(update.TotalBytes.Value))
                : F("StatusPreparingAsrDownload", model);
            SetStatus(L("StatusLoadingAsr"), message, InfoBarSeverity.Informational);
        }
        else
        {
            var message = update.ElapsedSeconds is > 0 ? $"{L("StatusLoadingAsr")} ({update.ElapsedSeconds}s)" : L("StatusLoadingAsr");
            SetStatus(L("StatusLoadingAsr"), message, InfoBarSeverity.Informational);
        }
    }

    private static string FormatDownloadSize(long bytes) => bytes >= 1_073_741_824
        ? $"{bytes / 1_073_741_824d:0.00} GB"
        : $"{bytes / 1_048_576d:0.0} MB";

    private void OnCaptionReceived(object? sender, AsrEvent result) => DispatcherQueue.TryEnqueue(() =>
    {
        CaptionBackground.Visibility = Visibility.Visible;
        CaptionText.Text = result.Text ?? result.Segment?.Text ?? string.Empty;
        CaptionText.Opacity = result.Event == "partial" ? 0.72 : 1;
    });

    private void ApplyCaptionAppearance()
    {
        CaptionText.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(_settings.Subtitle.FontFamily) ? SubtitleSettings.DefaultFontFamily : _settings.Subtitle.FontFamily);
        CaptionText.FontSize = Math.Clamp(_settings.Capture.CaptionFontSize, 8, 144);
        CaptionText.Foreground = new SolidColorBrush(ParseColor(_settings.Capture.CaptionTextColor, Color.FromArgb(255, 255, 255, 255)));
        CaptionText.MaxLines = Math.Clamp(_settings.Capture.CaptionMaximumLines, 1, 6);
        CaptionBackground.Background = new SolidColorBrush(ParseColor(_settings.Capture.CaptionBackgroundColor, Color.FromArgb(160, 0, 0, 0)));
        CaptionBackground.VerticalAlignment = _settings.Capture.CaptionPosition.ToLowerInvariant() switch
        {
            "top" => VerticalAlignment.Top,
            "center" => VerticalAlignment.Center,
            _ => VerticalAlignment.Bottom
        };
    }

    private static Color ParseColor(string value, Color fallback)
    {
        var text = value.Trim().TrimStart('#');
        if (text.Length == 6) text = "FF" + text;
        return text.Length == 8 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var argb)
            ? Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)
            : fallback;
    }

    private void OnLiveFailed(object? sender, Exception exception) => DispatcherQueue.TryEnqueue(() => SetStatus(L("LiveAsrErrorTitle"), exception.Message, InfoBarSeverity.Error));
    private void OnRecordingTimerTick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = GetRecordingElapsed();
        RecordingTimeText.Text = $"{L("RecordingIndicatorText")} {elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        RecordingDot.Opacity = RecordingDot.Opacity > 0.5 ? 0.25 : 1;
    }

    private TimeSpan GetRecordingElapsed()
    {
        var pausedDuration = _recordingPausedDuration;
        if (_recordingPausedAt is { } pausedAt) pausedDuration += DateTime.UtcNow - pausedAt;
        return (_recordingStopwatch.Elapsed - pausedDuration).Duration();
    }

    private void StartRecordingIndicator()
    {
        _recordingStopwatch.Restart();
        _recordingPausedDuration = TimeSpan.Zero;
        _recordingPausedAt = null;
        RecordingDot.Opacity = 1;
        RecordingTimeText.Text = $"{L("RecordingIndicatorText")} 00:00:00";
        RecordingIndicator.Visibility = Visibility.Visible;
        _recordingTimer.Start();
    }

    private void StopRecordingIndicator()
    {
        _recordingTimer.Stop();
        RecordingIndicator.Visibility = Visibility.Collapsed;
    }

    private void SetStatus(string title, string message, InfoBarSeverity severity) { StatusBar.Title = title; StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true; }
    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) => string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);

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
        Preview.SetMediaPlayer(null);
        try
        {
            if (_camera.IsRecording) { await _camera.StopRecordingAsync(); StopRecordingIndicator(); }
        }
        catch (Exception exception) { await AppLog.WriteAsync("error", "camera", "CAMERA_SHUTDOWN_ERROR", exception.Message, exception); }
        try { await _liveAsr.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "camera", "LIVE_ASR_SHUTDOWN_ERROR", exception.Message, exception); }
        try { await _asr.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "camera", "CAMERA_ASR_SHUTDOWN_ERROR", exception.Message, exception); }
        try { await _camera.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "camera", "CAMERA_CAPTURE_SHUTDOWN_ERROR", exception.Message, exception); }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
        }
        Root.ActualThemeChanged -= OnRootActualThemeChanged;
        _recordingTimer.Stop();
        Closed -= OnClosed;
    }
}
