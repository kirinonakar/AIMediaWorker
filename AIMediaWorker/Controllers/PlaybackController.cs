using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using AIMediaWorker.Media;
using AIMediaWorker.Playback;
using AIMediaWorker.Settings;
using AIMediaWorker.WindowsIntegration;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AIMediaWorker.Controllers;

/// <summary>
/// Owns playback commands and projects playback state onto the transport/status controls.
/// Cross-feature work (subtitle overlay and media navigation) is exposed as semantic callbacks.
/// </summary>
internal sealed class PlaybackController : IDisposable
{
    private readonly MpvPlaybackEngine _playback;
    private readonly PlaybackViewElements _view;
    private readonly PlaybackControllerHost _host;
    private readonly WindowsPowerManagement _powerManagement = new();
    private readonly TaskbarProgressController _taskbarProgress;
    private int _positionRefreshQueued;
    private bool _updatingPosition;
    private bool _positionSliderDragging;
    private bool _loaded;
    private bool _powerRequirementActive;
    private bool _powerManagementDisabled;
    private bool _disposed;
    private TimeSpan? _abStart;

    public PlaybackController(MpvPlaybackEngine playback, PlaybackViewElements view, PlaybackControllerHost host)
    {
        _playback = playback;
        _view = view;
        _host = host;
        _taskbarProgress = new TaskbarProgressController(host.WindowHandle);

        _view.PositionSlider.ThumbToolTipValueConverter = new PositionSliderThumbToolTipValueConverter();
        _playback.StateChanged += OnPlaybackStateChanged;
        _playback.PositionChanged += OnPlaybackPositionChanged;
        _playback.Seeked += OnPlaybackSeeked;
        _playback.TracksChanged += OnTracksChanged;
        _playback.ErrorOccurred += OnPlaybackError;
        _playback.MediaEnded += OnMediaEnded;
    }

    public PlaybackRepeatMode RepeatMode { get; private set; }

    public string RepeatIconName => RepeatMode switch
    {
        PlaybackRepeatMode.One => "repeat-one",
        PlaybackRepeatMode.AutoAdvance => "repeat-auto",
        _ => "repeat"
    };

    public void InitializeView()
    {
        var settings = _host.GetSettings();
        _view.RateCombo.ItemsSource = new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 };
        _view.RateCombo.SelectedItem = _view.RateCombo.Items.Cast<double>()
            .OrderBy(value => Math.Abs(value - settings.Playback.PlaybackRate))
            .First();
        _view.VolumeSlider.Value = settings.Playback.DefaultVolume;
        _loaded = true;
    }

    public void TogglePause() => Try(_playback.TogglePause);

    public void PlayFromBeginning() => Seek(TimeSpan.Zero, () =>
    {
        _playback.Seek(TimeSpan.Zero, true);
        _playback.Play();
    });

    public void GoToBeginning() => Seek(TimeSpan.Zero, () => _playback.Seek(TimeSpan.Zero, true));

    public void Stop() => Seek(TimeSpan.Zero, () =>
    {
        _playback.Seek(TimeSpan.Zero, true);
        _playback.Pause();
    });

    public void FrameStep() => Try(() => _playback.FrameStep());

    public void SeekBackward()
    {
        var interval = TimeSpan.FromSeconds(_host.GetSettings().Playback.SeekIntervalSeconds);
        Seek(_playback.Position - interval, () => _playback.SeekRelative(-interval));
    }

    public void SeekForward()
    {
        var interval = TimeSpan.FromSeconds(_host.GetSettings().Playback.SeekIntervalSeconds);
        Seek(_playback.Position + interval, () => _playback.SeekRelative(interval));
    }

    public void SeekToEnd() => Seek(_playback.Duration, () => _playback.Seek(_playback.Duration, true));

    public void ToggleMute()
    {
        Try(() => _playback.SetMute(!_playback.IsMuted));
        _host.IconsChanged();
        ShowOsd(_playback.IsMuted ? "OsdMuteOn" : "OsdMuteOff");
    }

    public void AdjustVolume(double delta)
    {
        Try(() => _playback.SetVolume(_playback.Volume + delta));
        _view.VolumeSlider.Value = _playback.Volume;
    }

    public void ChangeVolume(double value)
    {
        if (!_loaded || !_playback.IsAvailable) return;
        Try(() => _playback.SetVolume(value));
        var percent = double.IsFinite(_playback.Volume) ? Math.Clamp(_playback.Volume, 0, 130) : 0;
        Try(() => _playback.ShowOsdText($"Volume: {Math.Round(percent, MidpointRounding.AwayFromZero):0}", 1.5));
    }

    public void ChangeRate(object? selectedItem)
    {
        if (selectedItem is double rate && _playback.IsAvailable) Try(() => _playback.SetRate(rate));
    }

    public void CycleRepeatMode()
    {
        RepeatMode = RepeatMode switch
        {
            PlaybackRepeatMode.Off => PlaybackRepeatMode.One,
            PlaybackRepeatMode.One => PlaybackRepeatMode.AutoAdvance,
            _ => PlaybackRepeatMode.Off
        };
        ApplyRepeatMode();
        _host.IconsChanged();
        RefreshRepeatToolTip();
    }

    public void ApplyRepeatMode()
    {
        if (_playback.IsAvailable) Try(() => _playback.SetLoopFile(RepeatMode == PlaybackRepeatMode.One));
    }

    public void SetAbStart()
    {
        _abStart = _playback.Position;
        Try(() => _playback.SetAbLoop(_abStart, null));
        _host.SetStatus(F("StatusAPoint", FormatTime(_abStart.Value)));
    }

    public void SetAbEnd()
    {
        _abStart ??= TimeSpan.Zero;
        if (_playback.Position <= _abStart)
        {
            _host.SetStatus(L("StatusBMustFollowA"));
            return;
        }

        Try(() => _playback.SetAbLoop(_abStart, _playback.Position));
        _host.SetStatus(F("StatusAbRepeat", FormatTime(_abStart.Value), FormatTime(_playback.Position)));
    }

    public void ClearAb()
    {
        _abStart = null;
        if (_playback.IsAvailable) Try(() => _playback.SetAbLoop(null, null));
        _host.SetStatus(L("StatusAbCleared"));
    }

    public void PositionSliderChanged(double value)
    {
        if (_positionSliderDragging)
        {
            _view.PositionText.Text = $"{FormatTime(TimeSpan.FromSeconds(value))} / {FormatTime(_playback.Duration)}";
            return;
        }

        if (!_updatingPosition && _playback.IsAvailable && _view.PositionSlider.Maximum > 0)
            Seek(TimeSpan.FromSeconds(value), () => _playback.Seek(TimeSpan.FromSeconds(value)));
    }

    public void PositionSliderPressed()
    {
        _positionSliderDragging = true;
        _view.PositionText.Text = $"{FormatTime(TimeSpan.FromSeconds(_view.PositionSlider.Value))} / {FormatTime(_playback.Duration)}";
    }

    public void PositionSliderReleased()
    {
        if (!_positionSliderDragging) return;
        _positionSliderDragging = false;
        var position = TimeSpan.FromSeconds(_view.PositionSlider.Value);
        _view.PositionText.Text = $"{FormatTime(position)} / {FormatTime(_playback.Duration)}";
        if (_playback.IsAvailable) Seek(position, () => _playback.Seek(position, true));
    }

    public void SelectAudioTrack(object? selectedItem)
    {
        if (selectedItem is MediaTrack track) Try(() => _playback.SelectTrack(MediaTrackType.Audio, track.Id));
    }

    public async Task SaveScreenshotAsync()
    {
        if (!_playback.IsAvailable || _playback.State is not (PlaybackState.Playing or PlaybackState.Paused) || _playback.VideoWidth is null)
        {
            await _host.ShowMessageAsync(L("ScreenshotUnavailableTitle"), L("ScreenshotUnavailableMessage"));
            return;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                DefaultFileExtension = ".png",
                SuggestedFileName = CreateScreenshotFileName()
            };
            picker.FileTypeChoices.Add(L("PngImageFileType"), [".png"]);
            InitializeWithWindow.Initialize(picker, _host.WindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await Task.Run(() => _playback.SaveScreenshot(file.Path));
            _host.SetStatus(F("StatusScreenshotSaved", file.Name));
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "screenshot", "SCREENSHOT_SAVE_ERROR", exception.Message, exception);
            await _host.ShowMessageAsync(L("ScreenshotErrorTitle"), exception.Message);
        }
    }

    public void ApplyToolbarSize()
    {
        const double defaultButtonWidth = 44;
        const double defaultButtonHeight = 38;
        const double defaultVerticalPadding = 4;
        var scale = _host.GetSettings().Playback.UseLargeToolbarIcons ? 1.35 : 1.0;

        foreach (var button in _view.ToolbarButtons)
        {
            button.Width = defaultButtonWidth * scale;
            button.MinWidth = defaultButtonWidth * scale;
            button.Height = defaultButtonHeight * scale;
        }

        var verticalPadding = _host.GetSettings().Playback.UseLargeToolbarIcons ? 6 : defaultVerticalPadding;
        _view.Container.Padding = new Thickness(8, verticalPadding, 8, verticalPadding);
        _view.Container.MinHeight = (defaultButtonHeight * scale) + (verticalPadding * 2);

        SetImageSize(_view.BeginningIcon, 19 * scale);
        SetImageSize(_view.PreviousIcon, 19 * scale);
        SetImageSize(_view.SeekBackIcon, 20 * scale);
        SetImageSize(_view.PlayPauseIcon, 21 * scale);
        SetImageSize(_view.StopIcon, 18 * scale);
        SetImageSize(_view.SeekForwardIcon, 20 * scale);
        SetImageSize(_view.NextIcon, 19 * scale);
        SetImageSize(_view.MuteIcon, 20 * scale);
        SetImageSize(_view.RepeatIcon, 20 * scale);
        _view.ScreenshotIcon.FontSize = 19 * scale;
        _view.FullscreenIcon.FontSize = 19 * scale;
        _view.CloseIcon.FontSize = 19 * scale;
    }

    public void RefreshRepeatToolTip() => ToolTipService.SetToolTip(_view.RepeatButton, L(RepeatMode switch
    {
        PlaybackRepeatMode.One => "TooltipRepeatCurrent",
        PlaybackRepeatMode.AutoAdvance => "TooltipAutoAdvance",
        _ => "TooltipRepeatOff"
    }));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _playback.StateChanged -= OnPlaybackStateChanged;
        _playback.PositionChanged -= OnPlaybackPositionChanged;
        _playback.Seeked -= OnPlaybackSeeked;
        _playback.TracksChanged -= OnTracksChanged;
        _playback.ErrorOccurred -= OnPlaybackError;
        _playback.MediaEnded -= OnMediaEnded;
        ReleasePowerRequirement();
        _taskbarProgress.Dispose();
        _powerManagement.Dispose();
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs e) => _host.DispatcherQueue.TryEnqueue(() =>
    {
        var state = _playback.State;
        UpdatePowerRequirement(state);
        _host.IconsChanged();
        _host.SetStatus(_host.GetAudioStatus() is { } audioTag && state != PlaybackState.Failed
            ? audioTag
            : L(state switch
            {
                PlaybackState.Playing => "PlaybackStatePlaying",
                PlaybackState.Paused => "PlaybackStatePaused",
                PlaybackState.Loading => "PlaybackStateLoading",
                PlaybackState.Idle => "PlaybackStateIdle",
                PlaybackState.Failed => "PlaybackStateFailed",
                _ => "PlaybackStateUninitialized"
            }));
        _host.StateChanged(state);
        UpdateTaskbarProgress();
    });

    private void OnPlaybackPositionChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _positionRefreshQueued, 1) != 0) return;
        if (!_host.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RefreshPositionUi))
            Interlocked.Exchange(ref _positionRefreshQueued, 0);
    }

    private void RefreshPositionUi()
    {
        try
        {
            var position = _playback.Position;
            var duration = _playback.Duration;
            _updatingPosition = true;
            _view.PositionSlider.Maximum = Math.Max(1, duration.TotalSeconds);
            if (!_positionSliderDragging)
                _view.PositionSlider.Value = Math.Clamp(position.TotalSeconds, 0, _view.PositionSlider.Maximum);
            _updatingPosition = false;
            _view.PositionText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
            _view.DecoderText.Text = _playback.DecoderDescription ?? string.Empty;
            _view.ResolutionText.Text = _playback.VideoWidth is { } width && _playback.VideoHeight is { } height
                ? $"{width}×{height}"
                : string.Empty;
            _host.PositionChanged(Math.Max(0, position.Ticks / 10));
            UpdateTaskbarProgress();
        }
        finally
        {
            _updatingPosition = false;
            Interlocked.Exchange(ref _positionRefreshQueued, 0);
        }
    }

    private void OnPlaybackSeeked(object? sender, EventArgs e) =>
        _host.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _host.Seeked());

    private void OnTracksChanged(object? sender, EventArgs e) => _host.DispatcherQueue.TryEnqueue(() =>
    {
        _view.AudioTrackCombo.ItemsSource = _playback.Tracks.Where(track => track.Type == MediaTrackType.Audio).ToArray();
        _view.AudioTrackCombo.SelectedItem = _playback.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Audio && track.IsSelected);
        _host.TracksChanged();
    });

    private void OnMediaEnded(object? sender, EventArgs e) => _host.DispatcherQueue.TryEnqueue(async () =>
    {
        _taskbarProgress.Clear();
        if (RepeatMode == PlaybackRepeatMode.AutoAdvance) await _host.AutoAdvanceAsync();
    });

    private void OnPlaybackError(object? sender, PlaybackError error)
    {
        _ = AppLog.WriteAsync("error", "playback", error.Code, error.Message, error.Exception);
        _host.DispatcherQueue.TryEnqueue(() => _host.SetStatus($"{error.Code}: {error.Message}"));
    }

    private void UpdatePowerRequirement(PlaybackState state)
    {
        if (_powerManagementDisabled) return;
        var shouldKeepAwake = state == PlaybackState.Playing;
        if (shouldKeepAwake == _powerRequirementActive) return;
        if (!_powerManagement.TrySetPlaybackActive(shouldKeepAwake))
        {
            _ = AppLog.WriteAsync("warning", "playback", "PLAYBACK_POWER_REQUEST_ERROR",
                shouldKeepAwake
                    ? "Windows could not keep the display and system awake during playback."
                    : "Windows could not release the playback power request.");
            return;
        }

        _powerRequirementActive = shouldKeepAwake;
    }

    private void ReleasePowerRequirement()
    {
        _powerManagementDisabled = true;
        if (!_powerRequirementActive) return;
        if (!_powerManagement.TrySetPlaybackActive(false))
            _ = AppLog.WriteAsync("warning", "playback", "PLAYBACK_POWER_REQUEST_ERROR", "Windows could not release the playback power request.");
        _powerRequirementActive = false;
    }

    private void UpdateTaskbarProgress()
    {
        if (_playback.State is not (PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused) ||
            string.IsNullOrEmpty(_playback.CurrentSource))
        {
            _taskbarProgress.Clear();
            return;
        }

        _taskbarProgress.Update(_playback.State, _playback.Position, _playback.Duration);
    }

    private string CreateScreenshotFileName()
    {
        var displayName = _host.GetCurrentMediaSource()?.DisplayName;
        var stem = string.IsNullOrWhiteSpace(displayName) ? "AIMediaWorker" : Path.GetFileNameWithoutExtension(displayName);
        foreach (var character in Path.GetInvalidFileNameChars()) stem = stem.Replace(character, '_');
        if (string.IsNullOrWhiteSpace(stem)) stem = "AIMediaWorker";
        var position = _playback.Position;
        return $"{stem}_{(int)position.TotalHours:00}-{position.Minutes:00}-{position.Seconds:00}.{position.Milliseconds:000}";
    }

    private void Seek(TimeSpan position, Action action) => _host.SeekAndRestartAi(position, action);

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception exception) { _host.SetStatus(exception.Message); }
    }

    private void ShowOsd(string localizationKey)
    {
        if (_playback.IsAvailable) Try(() => _playback.ShowOsdText(L(localizationKey), 1.5));
    }

    private static void SetImageSize(Image image, double size)
    {
        image.Width = size;
        image.Height = size;
    }

    private static string FormatTime(TimeSpan value)
    {
        var totalSeconds = Math.Max(0, (long)value.TotalSeconds);
        return $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}";
    }

    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);

    private sealed class PositionSliderThumbToolTipValueConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is double seconds && double.IsFinite(seconds)
                ? FormatTime(TimeSpan.FromSeconds(Math.Max(0, seconds)))
                : FormatTime(TimeSpan.Zero);

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }
}

internal enum PlaybackRepeatMode
{
    Off,
    One,
    AutoAdvance
}

internal sealed record PlaybackControllerHost(
    nint WindowHandle,
    DispatcherQueue DispatcherQueue,
    Func<AppSettings> GetSettings,
    Func<string?> GetAudioStatus,
    Func<IMediaSource?> GetCurrentMediaSource,
    Action<string> SetStatus,
    Action<TimeSpan, Action> SeekAndRestartAi,
    Action<PlaybackState> StateChanged,
    Action Seeked,
    Action TracksChanged,
    Action<long> PositionChanged,
    Func<Task> AutoAdvanceAsync,
    Action IconsChanged,
    Func<string, string, Task> ShowMessageAsync);

internal sealed record PlaybackViewElements(
    Grid Container,
    Slider PositionSlider,
    TextBlock PositionText,
    Slider VolumeSlider,
    ComboBox RateCombo,
    ComboBox AudioTrackCombo,
    TextBlock ResolutionText,
    TextBlock DecoderText,
    Button RepeatButton,
    IReadOnlyList<ButtonBase> ToolbarButtons,
    Image BeginningIcon,
    Image PreviousIcon,
    Image SeekBackIcon,
    Image PlayPauseIcon,
    Image StopIcon,
    Image SeekForwardIcon,
    Image NextIcon,
    Image MuteIcon,
    Image RepeatIcon,
    FontIcon ScreenshotIcon,
    FontIcon FullscreenIcon,
    FontIcon CloseIcon);
