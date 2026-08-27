using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using AIMediaWorker.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AIMediaWorker.Views;

/// <summary>Owns creation, activation, and shutdown of the application's secondary windows.</summary>
internal sealed class AuxiliaryWindowController
{
    private readonly Window _owner;
    private readonly AppWindow? _ownerAppWindow;
    private readonly Func<bool> _isOwnerClosing;
    private readonly Action<AppSettings> _applySettings;
    private readonly Action<bool> _requestOwnerClose;
    private readonly WindowDialogService _dialogs;
    private CameraWindow? _cameraWindow;
    private WindowsCaptionWindow? _captionWindow;
    private SettingsWindow? _settingsWindow;
    private CaptureRecorderOverlayWindow? _captureRecorderWindow;

    public AuxiliaryWindowController(
        Window owner,
        AppWindow? ownerAppWindow,
        Func<bool> isOwnerClosing,
        Action<AppSettings> applySettings,
        Action<bool> requestOwnerClose,
        WindowDialogService dialogs)
    {
        _owner = owner;
        _ownerAppWindow = ownerAppWindow;
        _isOwnerClosing = isOwnerClosing;
        _applySettings = applySettings;
        _requestOwnerClose = requestOwnerClose;
        _dialogs = dialogs;
    }

    public bool RestartRequested { get; private set; }

    public async Task ShowCameraAsync()
    {
        try
        {
            if (_cameraWindow is not null)
            {
                _cameraWindow.Activate();
                return;
            }
            _cameraWindow = new CameraWindow(_owner);
            _cameraWindow.Closed += OnCameraClosed;
            _cameraWindow.Activate();
        }
        catch (Exception exception)
        {
            _cameraWindow = null;
            await AppLog.WriteAsync("error", "camera", "CAMERA_WINDOW_ERROR", exception.Message, exception);
            await _dialogs.ShowMessageAsync(L("CameraErrorTitle"), exception.Message);
        }
    }

    public async Task ShowWindowsCaptionsAsync()
    {
        try
        {
            if (_captionWindow is not null)
            {
                _captionWindow.Activate();
                return;
            }
            _ownerAppWindow?.Hide();
            _captionWindow = new WindowsCaptionWindow(_owner);
            _captionWindow.Closed += OnCaptionClosed;
            _captionWindow.Activate();
        }
        catch (Exception exception)
        {
            _captionWindow = null;
            _ownerAppWindow?.Show();
            await AppLog.WriteAsync("error", "captions", "WINDOWS_CAPTION_WINDOW_ERROR", exception.Message, exception);
            await _dialogs.ShowMessageAsync(L("WindowsCaptionErrorTitle"), exception.Message);
        }
    }

    public async Task ShowCaptureRecorderAsync()
    {
        try
        {
            if (_captureRecorderWindow is not null)
            {
                _captureRecorderWindow.Activate();
                return;
            }
            _ownerAppWindow?.Hide();
            _captureRecorderWindow = new CaptureRecorderOverlayWindow(_owner);
            _captureRecorderWindow.Closed += OnCaptureRecorderClosed;
            _captureRecorderWindow.Activate();
        }
        catch (Exception exception)
        {
            _captureRecorderWindow = null;
            _ownerAppWindow?.Show();
            await AppLog.WriteAsync("error", "capture", "CAPTURE_RECORDER_WINDOW_ERROR", exception.Message, exception);
            await _dialogs.ShowMessageAsync(L("CaptureRecorderErrorTitle"), exception.Message);
        }
    }

    public async Task ShowSettingsAsync()
    {
        try
        {
            if (_settingsWindow is not null)
            {
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(_owner);
            _settingsWindow.SettingsSaved += OnSettingsSaved;
            _settingsWindow.DllUnloadRequested += OnDllUnloadRequested;
            _settingsWindow.RestartRequested += OnRestartRequested;
            _settingsWindow.Closed += OnSettingsClosed;
            _settingsWindow.Activate();
        }
        catch (Exception exception)
        {
            _settingsWindow = null;
            await AppLog.WriteAsync("error", "settings", "SETTINGS_WINDOW_ERROR", exception.Message, exception);
            await _dialogs.ShowMessageAsync(L("SettingsErrorTitle"), exception.Message);
        }
    }

    public async Task CloseAsync()
    {
        _settingsWindow?.Close();
        if (_cameraWindow is { } cameraWindow) await cameraWindow.CloseAsync();
        if (_captionWindow is { } captionWindow) await captionWindow.CloseAsync();
        _captureRecorderWindow?.Close();
    }

    private void OnCameraClosed(object sender, WindowEventArgs args)
    {
        if (_cameraWindow is not null) _cameraWindow.Closed -= OnCameraClosed;
        _cameraWindow = null;
    }

    private void OnCaptionClosed(object sender, WindowEventArgs args)
    {
        if (_captionWindow is not null) _captionWindow.Closed -= OnCaptionClosed;
        _captionWindow = null;
        if (_isOwnerClosing()) return;
        _ownerAppWindow?.Show();
        _owner.Activate();
    }

    private void OnCaptureRecorderClosed(object sender, WindowEventArgs args)
    {
        if (_captureRecorderWindow is not null) _captureRecorderWindow.Closed -= OnCaptureRecorderClosed;
        _captureRecorderWindow = null;
        if (_isOwnerClosing()) return;
        _ownerAppWindow?.Show();
        _owner.Activate();
    }

    private void OnSettingsSaved(object? sender, AppSettings settings) => _applySettings(settings);

    private void OnDllUnloadRequested(object? sender, EventArgs args)
    {
        _settingsWindow?.Close();
        _requestOwnerClose(false);
    }

    private void OnRestartRequested(object? sender, EventArgs args)
    {
        RestartRequested = true;
        _settingsWindow?.Close();
        _requestOwnerClose(true);
    }

    private void OnSettingsClosed(object sender, WindowEventArgs args)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.SettingsSaved -= OnSettingsSaved;
            _settingsWindow.DllUnloadRequested -= OnDllUnloadRequested;
            _settingsWindow.RestartRequested -= OnRestartRequested;
            _settingsWindow.Closed -= OnSettingsClosed;
        }
        _settingsWindow = null;
    }

    private static string L(string key) => LocalizationService.Get(key);
}
