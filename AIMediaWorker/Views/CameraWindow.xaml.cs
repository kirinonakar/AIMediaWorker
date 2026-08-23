using AIMediaWorker.Asr;
using AIMediaWorker.Capture;
using AIMediaWorker.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using AIMediaWorker.Localization;
using AIMediaWorker.Diagnostics;

namespace AIMediaWorker.Views;

public sealed partial class CameraWindow : Window
{
    private readonly CameraManager _devices = new();
    private readonly CameraSession _camera = new();
    private readonly AsrWorkerClient _asr = new();
    private readonly AudioCaptureService _audio = new();
    private readonly LiveAsrController _liveAsr;
    private AppSettings _settings = new();
    private bool _closing;

    public CameraWindow(Window owner)
    {
        InitializeComponent();
        Title = L("CameraWindow.Title");
        WindowOwner.Attach(this, owner);
        _liveAsr = new LiveAsrController(_audio, _asr);
        _liveAsr.CaptionReceived += OnCaptionReceived;
        _liveAsr.Failed += OnLiveFailed;
        Closed += OnClosed;
        var handle = WindowNative.GetWindowHandle(this);
        AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle))?.Resize(new SizeInt32(1100, 720));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = await SettingsService.CreateDefault().LoadAsync();
        ApplyCaptionAppearance();
        await RefreshDevicesAsync();
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
                if (_camera.IsRecording) await _camera.StopRecordingAsync();
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
                await _camera.StopRecordingAsync(); RecordButton.Content = L("StartRecordingText"); SetStatus(L("CaptureSavedTitle"), L("CaptureSavedMessage"), InfoBarSeverity.Success); return;
            }
            if (!_camera.IsRunning)
            {
                var device = CameraCombo.SelectedItem as CaptureDevice;
                await _camera.StartAsync(device?.Id, (MicrophoneCombo.SelectedItem as CaptureDevice)?.Id, _settings.Capture.Width, _settings.Capture.Height, _settings.Capture.FrameRate);
                Preview.SetMediaPlayer(_camera.Player); PreviewButton.Content = L("StopPreviewText");
                FormatCombo.ItemsSource = _camera.AvailableFormats;
            }
            var picker = new FileSavePicker { SuggestedFileName = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}" };
            picker.FileTypeChoices.Add(L("Mpeg4FileType"), [".mp4"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await _camera.StartRecordingAsync(file);
            RecordButton.Content = L("StopRecordingText"); SetStatus(L("RecordingTitle"), file.Name, InfoBarSeverity.Warning);
        }
        catch (Exception exception) { SetStatus(L("CaptureErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }

    private async void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_camera.IsRunning || FormatCombo.SelectedItem is not CameraSession.CameraFormat format) return;
        try { await _camera.ApplyFormatAsync(format); SetStatus(L("CameraFormatTitle"), format.DisplayName, InfoBarSeverity.Success); }
        catch (Exception exception) { SetStatus(L("CameraFormatErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }

    private async void OnCaptionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_liveAsr.IsRunning)
            {
                CaptionButton.IsEnabled = false; await _liveAsr.StopAsync(); CaptionButton.Content = L("StartCaptionsText"); CaptionButton.IsEnabled = true; return;
            }
            if (string.IsNullOrWhiteSpace(_settings.Asr.ModelPath))
            {
                SetStatus(L("AsrModelMissingTitle"), L("LiveAsrModelMissingMessage"), InfoBarSeverity.Warning); return;
            }
            CaptionButton.IsEnabled = false;
            var worker = Path.Combine(AppContext.BaseDirectory, "asr-worker", "main.py");
            await _asr.StartAsync(_settings.Asr.PythonExecutable, worker);
            await _asr.LoadModelAsync(_settings.Asr.ModelPath, _settings.Asr.AlignerPath, _settings.Asr.Device.ToString(), _settings.Asr.Precision.ToString());
            var language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
            await _liveAsr.StartAsync((MicrophoneCombo.SelectedItem as CaptureDevice)?.Id, language);
            CaptionButton.Content = L("StopCaptionsText");
            CaptionButton.IsEnabled = true;
            SetStatus(L("LiveCaptionsTitle"), L("ListeningMessage"), InfoBarSeverity.Success);
        }
        catch (UnauthorizedAccessException) { CaptionButton.IsEnabled = true; SetStatus(L("MicrophonePermissionTitle"), L("MicrophonePermissionMessage"), InfoBarSeverity.Error); }
        catch (Exception exception) { CaptionButton.IsEnabled = true; SetStatus(L("LiveAsrErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }

    private void OnCaptionReceived(object? sender, AsrEvent result) => DispatcherQueue.TryEnqueue(() =>
    {
        CaptionBackground.Visibility = Visibility.Visible;
        CaptionText.Text = result.Text ?? result.Segment?.Text ?? string.Empty;
        CaptionText.Opacity = result.Event == "partial" ? 0.72 : 1;
    });

    private void ApplyCaptionAppearance()
    {
        CaptionText.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(_settings.Capture.CaptionFontFamily) ? "Segoe UI" : _settings.Capture.CaptionFontFamily);
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
    private void SetStatus(string title, string message, InfoBarSeverity severity) { StatusBar.Title = title; StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true; }
    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) => string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closing) return; _closing = true;
        try
        {
            Preview.SetMediaPlayer(null);
            if (_camera.IsRecording) await _camera.StopRecordingAsync();
            await _liveAsr.DisposeAsync();
            await _asr.DisposeAsync();
            await _camera.DisposeAsync();
        }
        catch (Exception exception) { await AppLog.WriteAsync("error", "camera", "CAMERA_SHUTDOWN_ERROR", exception.Message, exception); }
    }
}
