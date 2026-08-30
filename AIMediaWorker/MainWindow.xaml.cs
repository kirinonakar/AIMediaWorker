using AIMediaWorker.Playback;
using AIMediaWorker.Controllers;
using AIMediaWorker.Subtitle;
using AIMediaWorker.Subtitle.Editing;
using AIMediaWorker.Views;
using AIMediaWorker.Settings;
using AIMediaWorker.Asr;
using AIMediaWorker.Media;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace AIMediaWorker;

public sealed partial class MainWindow : Window, IAiWorkflowHost
{
    private const uint DwmwaCloak = 13;

    private readonly MpvPlaybackEngine _playback = new();
    private readonly SubtitleSessionController _subtitleSession;
    private readonly SubtitleEditorController _subtitleEditor;
    private readonly SubtitleOverlayController _subtitleOverlay;
    private NativeVideoHost? _videoHost;
    private AppWindow? _appWindow;
    private bool _initialized;
    private AppSettings _settings = new();
    private readonly AiWorkflowController _aiWorkflow;
    private int _generatedSubtitleUiRefreshQueued;
    private MediaSessionController _mediaSession = null!;
    private bool _allowClose;
    private bool _closeInProgress;
    private Task? _shutdownTask;
    private SubtitleTrackController _subtitleTracks = null!;
    private readonly AudioPresentationController _audioPresentation;
    private readonly TaskCompletionSource _firstUiFrameReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _firstUiFrameWaitStarted;
    private readonly Task _playbackInitializationTask;
    private readonly PanelLayoutController _panels;
    private readonly FullscreenPresentationController _fullscreen;
    private readonly RightPanelController _rightPanel;
    private readonly MediaNavigationController _mediaNavigation;
    private readonly WindowChromeController _chrome;
    private readonly WindowDialogService _dialogs;
    private readonly AboutDialogService _aboutDialog;
    private readonly AuxiliaryWindowController _auxiliaryWindows;
    private PlaybackController _playbackController = null!;
    private readonly ShortcutController _shortcuts;
    private readonly nint _windowHandle;
    private bool _startupWindowCloaked;

    public MainWindow() : this(null, new AppSettings()) { }

    public MainWindow(string? initialSource) : this(initialSource, new AppSettings()) { }

    public MainWindow(string? initialSource, AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _windowHandle = WindowNative.GetWindowHandle(this);
        // A WinUI HWND can briefly expose its default light surface after Activate and
        // before the first XAML frame reaches DWM. Keep it composed but invisible until
        // that first frame is ready, so only the correctly themed UI is ever presented.
        _startupWindowCloaked = SetWindowCloak(_windowHandle, cloak: true);
        StartupProfiler.Mark("xaml-start");
        InitializeComponent();
        // Set the saved theme before any potentially expensive window setup. This keeps
        // the first composed frame from using the default light resources and then
        // flashing to dark when WindowChromeController is initialized later.
        RootGrid.RequestedTheme = _settings.General.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        StartupProfiler.Mark("xaml-ready");
        _dialogs = new WindowDialogService(RootGrid, () => _videoHost);
        _aboutDialog = new AboutDialogService(_dialogs);
        _subtitleSession = new SubtitleSessionController(new SubtitleSessionHost(
            _windowHandle,
            () => _settings,
            () => _playback.CurrentSource,
            () => _subtitleTracks.DisplayMode,
            PrepareForSubtitleLoadAsync,
            document => _subtitleTracks.BindDocument(document),
            CompleteSubtitleLoad,
            message => StatusText.Text = message,
            _dialogs));
        _subtitleEditor = new SubtitleEditorController(
            SubtitleList,
            TimelineCanvas,
            () => _subtitleSession.Document,
            () => CurrentPlaybackPositionMicroseconds,
            position => SeekAndRestartAi(position, () => _playback.Seek(position, true)),
            () => ScheduleSubtitleOverlaySync(),
            message => StatusText.Text = message,
            L,
            async (title, content, primaryText) => await ShowDialogAsync(CreateDialog(title, content, primaryText)),
            ShowMessageAsync);
        _subtitleOverlay = new SubtitleOverlayController(
            _playback,
            () => _subtitleSession.Document,
            () => _settings,
            () => _subtitleTracks.DisplayMode,
            () => _subtitleTracks.SelectedNativeTrackId,
            () => CurrentPlaybackPositionMicroseconds,
            visible => SubtitleVisibilityMenuItem.IsChecked = visible,
            message => StatusText.Text = message,
            DispatcherQueue);
        _subtitleTracks = new SubtitleTrackController(
            _playback,
            _subtitleSession,
            _subtitleOverlay,
            _subtitleEditor,
            SubtitleTrackCombo,
            force => ScheduleSubtitleOverlaySync(force),
            TryPlayback);
        _aiWorkflow = new AiWorkflowController(
            this,
            _playback,
            GenerateSubtitlesMenuItem.IsChecked,
            TranslateMenuItem.IsChecked);
        _panels = new PanelLayoutController(
            new PanelLayoutViewElements(
                SubtitlePanel, RightPanelSplitter, RightPanelSplitterColumn, RightPanelColumn,
                VisualizationPanel, BottomPanelSplitter, BottomPanelSplitterRow, BottomPanelRow, StatusPanel,
                ShowRightPanelMenuItem, ShowBottomPanelMenuItem, ShowStatusPanelMenuItem,
                RightPanelToggleButton, BottomPanelToggleButton, StatusPanelToggleButton),
            () => _settings.Window,
            UpdatePanelToggleIcons);
        // Apply the saved font to already-created elements as well as the app resource.
        // The custom title bar is part of this visual tree and must follow the setting
        // on the first launch, not only after the Preferences window is saved.
        UiFontService.Apply(_settings.General.UiFontFamily, RootGrid);
        _playback.FirstFrameReady += OnFirstFrameReady;
        _videoHost = new NativeVideoHost(this, VideoPlaceholder);
        _videoHost.FilesDropped += OnNativeVideoFilesDropped;
        _videoHost.Clicked += OnNativeVideoClicked;
        _videoHost.DoubleClicked += OnNativeVideoDoubleClicked;
        _audioPresentation = new AudioPresentationController(
            new AudioPresentationViewElements(AudioArtworkSurface, AlbumArtImage, AudioArtworkFallback),
            DispatcherQueue,
            () => _videoHost,
            message => StatusText.Text = message);
        _playbackInitializationTask = InitializePlaybackAfterFirstUiFrameAsync(_videoHost.Create());
        ExtendsContentIntoTitleBar = true;
        _rightPanel = new RightPanelController(
            new RightPanelViewElements(
                RightPanelSectionList, ExplorerSection, PlaylistSection, WebDavSection, FavoritesSection,
                SubtitlesSection, PlaylistTitleText, FavoritesTitleText, SubtitlesTitleText),
            () =>
            {
                _panels.IsRightVisible = true;
                ApplyPanelVisibility();
            });
        _mediaNavigation = new MediaNavigationController(
            new MediaNavigationViewElements(
                this, MediaBrowser, WebDavBrowser, PlaylistList, FavoriteList, FavoritesEmptyText,
                RecentMenu, PreviousButton, NextButton),
            new MediaNavigationHost(
                () => _settings,
                () => _mediaSession.CurrentSource,
                () => _playback.CurrentSource,
                () => CurrentPlaybackPositionMicroseconds,
                request => _mediaSession.OpenAsync(request.Source, request.HttpHeaders, request.MediaSource, request.PreservePlaylist),
                LoadSubtitleFromPathAsync,
                PrepareForRemoteSubtitleLoadAsync,
                ApplyDownloadedWebDavSubtitleAsync,
                source => _mediaSession.WaitForFirstFrameAsync(source),
                position => SeekAndRestartAi(position, () => _playback.Seek(position, true)),
                _rightPanel.Show,
                message => StatusText.Text = message,
                ShowMessageAsync,
                dialog =>
                {
                    dialog.XamlRoot ??= RootGrid.XamlRoot;
                    dialog.RequestedTheme = RootGrid.ActualTheme;
                    return ShowDialogAsync(dialog);
                }));
        _rightPanel.SectionChanged += (_, section) =>
        {
            if (_initialized && section == RightPanelSection.Favorites) _ = _mediaNavigation.LoadFavoritesAsync();
        };
        GenerateSubtitlesMenuItem.IsChecked = _settings.Asr.GenerateSubtitles;
        TranslateMenuItem.IsChecked = _settings.Llm.TranslateSubtitles;
        _aiWorkflow.UpdateModes(GenerateSubtitlesMenuItem.IsChecked, TranslateMenuItem.IsChecked);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle));
        _chrome = new WindowChromeController(
            _appWindow,
            new WindowChromeViewElements(
                RootGrid, AppTitleBarArea, BeginningIcon, PreviousIcon, SeekBackIcon, PlayPauseIcon,
                StopIcon, SeekForwardIcon, NextIcon, MuteIcon, RepeatIcon,
                BottomPanelToggleIcon, StatusPanelToggleIcon, RightPanelToggleIcon),
            () => _playback.State,
            () => _playback.IsMuted,
            () => _playbackController.RepeatIconName,
            () => _panels.IsRightVisible,
            () => _panels.IsBottomVisible,
            () => _panels.IsStatusVisible);
        _chrome.ResizeToAvailableWorkArea(1280, 820);
        _auxiliaryWindows = new AuxiliaryWindowController(
            this,
            _appWindow,
            () => _closeInProgress || _allowClose,
            ApplySettings,
            _ => Close(),
            _dialogs);
        if (_appWindow is not null)
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        }
        _fullscreen = new FullscreenPresentationController(
            this,
            _appWindow,
            new FullscreenViewElements(
                MainMenuBarHost, AppTitleBarArea, PlaybackControls, VisualizationPanel, StatusPanel,
                SubtitlePanel, RightPanelSplitter, RightPanelSplitterColumn, RightPanelColumn,
                BottomPanelSplitter, BottomPanelSplitterRow, BottomPanelRow, VideoPlaceholder),
            () => _panels.RightWidth,
            ApplyPanelVisibility,
            FocusPlaybackSurface,
            hidden => _videoHost?.SetCursorHidden(hidden),
            message => StatusText.Text = message);
        _mediaSession = new MediaSessionController(
            _playback,
            _audioPresentation,
            _subtitleSession,
            initialSource,
            new MediaSessionHost(
                _windowHandle,
                _firstUiFrameReady.Task,
                _playbackInitializationTask,
                action => DispatcherQueue.TryEnqueue(() => action()),
                CancelAiPipelineAsync,
                _mediaNavigation.PrepareForMediaOpenAsync,
                paths => _mediaNavigation.OpenFilesAsync(paths),
                _mediaNavigation.MediaOpened,
                _mediaNavigation.NotifyFirstFrameReady,
                () => _playbackController.ApplyRepeatMode(),
                _subtitleEditor.ResetTimeline,
                _aiWorkflow.ResetForMedia,
                UpdateWindowTitle,
                FocusPlaybackSurface,
                () => _aiWorkflow.IsSeekRestartPending,
                () => StartCheckedAiPipeline(waitForMediaReady: true),
                message => StatusText.Text = message,
                ShowMessageAsync));
        _playbackController = new PlaybackController(
            _playback,
            new PlaybackViewElements(
                PlaybackControls, PositionSlider, PositionText, VolumeSlider, RateCombo,
                AudioTrackCombo, ResolutionText, DecoderText, AudioCodecText, RepeatButton,
                [BeginningButton, PreviousButton, SeekBackButton, PlayPauseButton, StopButton,
                    SeekForwardButton, NextButton, ScreenshotButton, MuteButton, RepeatButton,
                    FullscreenButton, CloseButton],
                BeginningIcon, PreviousIcon, SeekBackIcon, PlayPauseIcon, StopIcon, SeekForwardIcon,
                NextIcon, MuteIcon, RepeatIcon, ScreenshotButtonIcon, FullscreenButtonIcon, CloseButtonIcon),
            new PlaybackControllerHost(
                _windowHandle,
                DispatcherQueue,
                () => _settings,
                () => _audioPresentation.StatusText,
                () => _mediaSession.CurrentSource,
                message => StatusText.Text = message,
                SeekAndRestartAi,
                HandlePlaybackStateChanged,
                HandlePlaybackSeeked,
                HandlePlaybackTracksChanged,
                HandlePlaybackPositionChanged,
                _mediaNavigation.AutoAdvanceAsync,
                _chrome.UpdateIcons,
                ShowMessageAsync));
        _playbackController.ApplyToolbarSize();
        _shortcuts = new ShortcutController(
            new ShortcutViewElements(
                RootGrid, VideoFocusTarget, SaveSubtitleMenuItem, SaveSubtitleAsMenuItem, ExitMenuItem,
                PlayPauseMenuItem, DeleteCueMenuItem, PreviousSubtitleMenuItem, NextSubtitleMenuItem,
                UndoMenuItem, RedoMenuItem, SubtitleVisibilityMenuItem, ShowBottomPanelMenuItem,
                ShowRightPanelMenuItem, ShowStatusPanelMenuItem, FullscreenMenuItem,
                BottomPanelToggleButton, RightPanelToggleButton, StatusPanelToggleButton,
                PlayPauseButton, BeginningButton, PreviousButton, NextButton, SeekBackButton,
                SeekForwardButton, StopButton, MuteButton, VolumeSlider, PositionSlider, SubtitleList,
                CloseButton, FullscreenButton, FullscreenButtonIcon),
            new ShortcutControllerHost(
                () => _settings,
                () => _fullscreen.IsFullscreen,
                _fullscreen.Exit,
                _fullscreen.Toggle,
                FocusPlaybackSurface,
                Close,
                _subtitleSession.SaveCurrentAsync,
                _subtitleSession.SaveAsAsync,
                _playbackController.PlayFromBeginning,
                _playbackController.TogglePause,
                _mediaNavigation.OpenPreviousAsync,
                _mediaNavigation.OpenNextAsync,
                _subtitleEditor.SelectRelativeCue,
                _playbackController.SeekBackward,
                _playbackController.SeekForward,
                _subtitleEditor.Undo,
                _subtitleEditor.Redo,
                _subtitleEditor.DeleteSelectedCues,
                ToggleSubtitleVisibility,
                ToggleBottomPanel,
                ToggleRightPanel,
                ToggleStatusPanel,
                _playbackController.ToggleMute,
                _playbackController.AdjustVolume,
                _playbackController.GoToBeginning,
                _playbackController.SeekToEnd,
                _playbackController.RefreshRepeatToolTip));
        if (_appWindow is not null) { _appWindow.Closing += OnAppWindowClosing; _appWindow.Changed += OnAppWindowChanged; }
        Closed += OnWindowClosed;
        RootGrid.ActualThemeChanged += OnRootActualThemeChanged;
        _chrome.ApplyTheme(_settings.General.Theme);
        PositionSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPositionSliderPointerPressed), true);
        PositionSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPositionSliderPointerReleased), true);
        PositionSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPositionSliderPointerReleased), true);
        _subtitleSession.Bind(_subtitleSession.Document);
        // Subscribe before Activate so the reveal cannot be skipped by other Loaded work.
        _ = WaitForFirstUiFrameAsync();
    }

    private async Task InitializePlaybackAsync(nint videoWindowHandle)
    {
        await _playback.InitializeAsync(videoWindowHandle, _settings.Playback.HardwareDecoder, _settings.Playback.Renderer, _settings.Playback.RtxVideoSuperResolution, _settings.Playback.HdrOutput).ConfigureAwait(false);
        if (!_playback.IsAvailable) return;
        _playback.SetLoopFile(_playbackController.RepeatMode == PlaybackRepeatMode.One);
        _playback.SetVolume(_settings.Playback.DefaultVolume);
        _playback.SetRate(_settings.Playback.PlaybackRate);
        _playback.ConfigureNetwork(TimeSpan.FromSeconds(_settings.Network.TimeoutSeconds), _settings.Network.Proxy);
        _playback.ConfigurePreferredLanguages(_settings.Playback.DefaultAudioLanguage, _settings.Playback.DefaultSubtitleLanguage);
        _playback.ConfigureSubtitleStyle(_settings.Subtitle.FontFamily, _settings.Subtitle.FontSize, _settings.Subtitle.Color, _settings.Subtitle.Background, _settings.Subtitle.Outline, _settings.Subtitle.BottomMargin);
        _playback.SetSubtitleVisibility(_settings.Playback.ShowSubtitles);
    }

    private async Task InitializePlaybackAfterFirstUiFrameAsync(nint videoWindowHandle)
    {
        // Warm libmpv even when no file was supplied, but only after the initial WinUI frame
        // has been submitted. File-open paths reuse this single task and therefore enter
        // playback immediately when initialization has already completed.
        await _firstUiFrameReady.Task.ConfigureAwait(false);
        await InitializePlaybackAsync(videoWindowHandle).ConfigureAwait(false);
    }

    public void ApplySavedWindowPlacement(WindowLayoutSettings layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _panels.Load(layout);
        ApplyPanelVisibility();
        _chrome.ApplySavedWindowPlacement(layout);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_initialized || _fullscreen.HandleAppWindowChanged()) return;
        if (sender.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized) return;
        WindowChromeController.CaptureWindowPlacement(sender, presenter, _settings.Window);
        _chrome.UpdateTitleBarDragRegion();
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        _chrome.UpdateTitleBarDragRegion();
        try
        {
            _panels.Load(_settings.Window);
            ClampPanelSizesToAvailable();
            ApplyPanelVisibility();
            var recentLoad = _mediaNavigation.LoadRecentAsync();
            _ = _mediaNavigation.InitializeBrowserAsync();
            SubtitleVisibilityMenuItem.IsChecked = _settings.Playback.ShowSubtitles;
            _playbackController.InitializeView();
            _shortcuts.RefreshHints();
            // Shell activation previously issued loadfile from the constructor, before WinUI
            // had presented its first frame. Let one complete composition pass finish first so
            // decoder/GPU startup cannot delay painting the window chrome and controls.
            await WaitForFirstUiFrameAsync();
            await _playbackInitializationTask;
            StatusText.Text = _playback.IsAvailable ? L("StatusLibmpvReady") : L("StatusPlaybackUnavailable");
            await _mediaSession.OpenPendingAsync();
            await recentLoad;
            _chrome.ApplyTheme(_settings.General.Theme);
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private Task WaitForFirstUiFrameAsync()
    {
        if (_firstUiFrameReady.Task.IsCompleted || _firstUiFrameWaitStarted) return _firstUiFrameReady.Task;
        _firstUiFrameWaitStarted = true;
        EventHandler<object>? rendering = null;
        rendering = (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendering;
            // Rendering is raised while the frame is being prepared. Complete from a low
            // priority dispatcher item so the current frame is submitted before playback work.
            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CompleteFirstUiFrame))
                CompleteFirstUiFrame();
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += rendering;
        return _firstUiFrameReady.Task;
    }

    private void CompleteFirstUiFrame()
    {
        StartupProfiler.Mark("first-ui-frame");
        RevealStartupWindow();
        _firstUiFrameReady.TrySetResult();
    }

    private void RevealStartupWindow()
    {
        if (!_startupWindowCloaked) return;
        _startupWindowCloaked = !SetWindowCloak(_windowHandle, cloak: false);
        if (!_startupWindowCloaked) StartupProfiler.Mark("window-revealed");
    }

    private static bool SetWindowCloak(nint windowHandle, bool cloak)
    {
        var value = cloak ? 1 : 0;
        return DwmSetWindowAttribute(windowHandle, DwmwaCloak, ref value, sizeof(int)) == 0;
    }

    private async void OnOpenMediaClick(object sender, RoutedEventArgs e) => await _mediaSession.PickAndOpenMediaAsync();

    private async void OnOpenUrlClick(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "https://example.com/video.m3u8", MinWidth = 460 };
        var dialog = CreateDialog(L("OpenUrlTitle"), input, L("OpenButton"));
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if (!Uri.TryCreate(input.Text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            await ShowMessageAsync(L("InvalidUrlTitle"), L("InvalidUrlMessage"));
            return;
        }
        await _mediaSession.OpenAsync(uri.AbsoluteUri);
    }

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = L("OpenMedia.Text");
            e.DragUIOverride.IsCaptionVisible = true;
        }
        else if (e.DataView.Contains(StandardDataFormats.Text)) e.AcceptedOperation = DataPackageOperation.Link;
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                await _mediaSession.OpenDroppedFilesAsync(items.OfType<StorageFile>().Select(file => file.Path));
                return;
            }
            if (e.DataView.Contains(StandardDataFormats.Text))
            {
                var value = (await e.DataView.GetTextAsync()).Trim();
                if (Path.IsPathFullyQualified(value)) await _mediaSession.OpenDroppedFilesAsync([value]);
                else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") await _mediaSession.OpenAsync(uri.AbsoluteUri);
            }
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "drag-drop", "DROP_OPEN_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("PlaybackErrorTitle"), exception.Message);
        }
    }

    private void OnNativeVideoFilesDropped(object? sender, FilesDroppedEventArgs e)
    {
        var paths = e.Paths.ToArray();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try { await _mediaSession.OpenDroppedFilesAsync(paths); }
            catch (Exception exception)
            {
                await AppLog.WriteAsync("error", "drag-drop", "NATIVE_DROP_OPEN_ERROR", exception.Message, exception);
                await ShowMessageAsync(L("PlaybackErrorTitle"), exception.Message);
            }
        });
    }

    private void OnNativeVideoDoubleClicked(object? sender, EventArgs e)
    {
        if (_playback.State is PlaybackState.Playing or PlaybackState.Paused) TryPlayback(_playback.TogglePause);
    }

    private void OnNativeVideoClicked(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            _fullscreen.RevealPanelAtCurrentPointer();
            DismissOpenMenus();
            FocusPlaybackSurface();
        });

    private void DismissOpenMenus()
    {
        if (RootGrid.XamlRoot is not { } xamlRoot) return;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).ToArray())
        {
            if (popup.Child is MenuFlyoutPresenter) popup.IsOpen = false;
        }
    }

    /// <summary>
    /// Handles a launch redirected from a secondary app instance: brings this window
    /// to the foreground and opens the forwarded files.
    /// </summary>
    public void ActivateFromExternalLaunch(IReadOnlyList<string>? filePaths)
    {
        BringToFront();
        if (filePaths is not { Count: > 0 }) return;
        _ = _mediaSession.OpenForwardedFilesAsync(filePaths);
    }

    private void BringToFront()
    {
        var handle = WindowNative.GetWindowHandle(this);
        if (_appWindow?.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        ShowWindow(handle, 9); // SW_RESTORE
        SetForegroundWindow(handle);
    }

    private void UpdateWindowTitle(string displayName)
    {
        var title = string.IsNullOrWhiteSpace(displayName) ? "AIMediaWorker" : $"{displayName} - AIMediaWorker";
        Title = title;
        if (_appWindow is not null) _appWindow.Title = title;
        AppTitleText.Text = title;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _playbackController.TogglePause();
    private void PlayFromBeginning() => _playbackController.PlayFromBeginning();
    private void OnGoToBeginningClick(object sender, RoutedEventArgs e) => _playbackController.GoToBeginning();
    private void OnStopClick(object sender, RoutedEventArgs e) => _playbackController.Stop();
    private async void OnPreviousMediaClick(object sender, RoutedEventArgs e) => await _mediaNavigation.OpenPreviousAsync();
    private async void OnNextMediaClick(object sender, RoutedEventArgs e) => await _mediaNavigation.OpenNextAsync();
    private void OnFrameStepClick(object sender, RoutedEventArgs e) => _playbackController.FrameStep();
    private async void OnSaveScreenshotClick(object sender, RoutedEventArgs e) => await _playbackController.SaveScreenshotAsync();
    private void OnSeekBackClick(object sender, RoutedEventArgs e) => _playbackController.SeekBackward();
    private void OnSeekForwardClick(object sender, RoutedEventArgs e) => _playbackController.SeekForward();
    private void OnMuteClick(object sender, RoutedEventArgs e) => _playbackController.ToggleMute();
    private void OnToggleSubtitleVisibilityClick(object sender, RoutedEventArgs e) => ApplySubtitleVisibilitySelection();

    private void ToggleSubtitleVisibility()
    {
        SubtitleVisibilityMenuItem.IsChecked = !_playback.AreSubtitlesVisible;
        ApplySubtitleVisibilitySelection();
    }

    private void ApplySubtitleVisibilitySelection()
    {
        var visible = SubtitleVisibilityMenuItem.IsChecked;
        _settings.Playback.ShowSubtitles = visible;
        ApplySubtitleVisibilityPreference();
        ShowSubtitleVisibilityOverlay();
    }
    private void OnRateChanged(object sender, SelectionChangedEventArgs e) => _playbackController?.ChangeRate(RateCombo.SelectedItem);
    private void OnRepeatClick(object sender, RoutedEventArgs e) => _playbackController.CycleRepeatMode();
    private void OnSetAbStartClick(object sender, RoutedEventArgs e) => _playbackController.SetAbStart();
    private void OnSetAbEndClick(object sender, RoutedEventArgs e) => _playbackController.SetAbEnd();
    private void OnClearAbClick(object sender, RoutedEventArgs e) => _playbackController.ClearAb();
    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e) => _playbackController?.ChangeVolume(e.NewValue);

    private void ShowSubtitleVisibilityOverlay()
    {
        if (!_playback.IsAvailable) return;
        TryPlayback(() => _playback.ShowOsdText(L(_playback.AreSubtitlesVisible ? "OsdSubtitlesOn" : "OsdSubtitlesOff"), 1.5));
    }

    private void OnPositionSliderChanged(object sender, RangeBaseValueChangedEventArgs e) => _playbackController?.PositionSliderChanged(e.NewValue);
    private void OnPositionSliderPointerPressed(object sender, PointerRoutedEventArgs e) => _playbackController?.PositionSliderPressed();
    private void OnPositionSliderPointerReleased(object sender, PointerRoutedEventArgs e) => _playbackController?.PositionSliderReleased();

    private async void OnLoadSubtitleClick(object sender, RoutedEventArgs e) => await _subtitleSession.PickAndLoadAsync();
    private async Task LoadSubtitleFromPathAsync(string path) => await _subtitleSession.LoadAsync(path);
    private async void OnSaveSubtitleClick(object sender, RoutedEventArgs e) => await _subtitleSession.SaveCurrentAsync();
    private async void OnSaveSubtitleAsClick(object sender, RoutedEventArgs e) => await _subtitleSession.SaveAsAsync();

    private void OnAddCueClick(object sender, RoutedEventArgs e)
        => _subtitleEditor.AddCue();

    private void OnDeleteCueClick(object sender, RoutedEventArgs e)
        => _subtitleEditor.DeleteSelectedCues();

    private void OnSplitCueClick(object sender, RoutedEventArgs e)
        => _subtitleEditor.SplitSelectedCue();

    private void OnMergeCueClick(object sender, RoutedEventArgs e)
        => _subtitleEditor.MergeSelectedCueWithNext();

    private void OnUndoClick(object sender, RoutedEventArgs e) => _subtitleEditor.Undo();
    private void OnRedoClick(object sender, RoutedEventArgs e) => _subtitleEditor.Redo();
    private void OnCueTextGotFocus(object sender, RoutedEventArgs e) => _subtitleEditor.CueTextGotFocus(sender);
    private void OnCueTextLostFocus(object sender, RoutedEventArgs e) => _subtitleEditor.CueTextLostFocus(sender);

    private void OnCueTimeGotFocus(object sender, RoutedEventArgs e) => _subtitleEditor.CueTimeGotFocus(sender);

    private void OnCueTimeLostFocus(object sender, RoutedEventArgs e) => _subtitleEditor.CueTimeLostFocus(sender);

    private void OnDuplicateCueClick(object sender, RoutedEventArgs e)
        => _subtitleEditor.DuplicateSelectedCues();

    private async void OnShiftCueClick(object sender, RoutedEventArgs e)
        => await _subtitleEditor.ShiftCuesAsync();

    private async void OnAdjustSubtitleSyncClick(object sender, RoutedEventArgs e)
        => await _subtitleEditor.AdjustSynchronizationAsync();

    private void OnSelectAllCuesClick(object sender, RoutedEventArgs e) => _subtitleEditor.SelectAll();

    private void OnCopyCuesClick(object sender, RoutedEventArgs e)
        => _subtitleEditor.CopySelectedCues();

    private async void OnPasteCuesClick(object sender, RoutedEventArgs e)
        => await _subtitleEditor.PasteCuesAsync();
    private void OnSubtitleItemClick(object sender, ItemClickEventArgs e) => _subtitleEditor.SubtitleItemClicked(e);

    private void OnTimelinePointerPressed(object sender, PointerRoutedEventArgs e)
        => _subtitleEditor.TimelinePointerPressed(e);

    private void OnTimelinePointerMoved(object sender, PointerRoutedEventArgs e)
        => _subtitleEditor.TimelinePointerMoved(e);

    private void OnTimelinePointerReleased(object sender, PointerRoutedEventArgs e)
        => _subtitleEditor.TimelinePointerReleased(e);

    private void OnTimelinePointerWheelChanged(object sender, PointerRoutedEventArgs e)
        => _subtitleEditor.TimelinePointerWheelChanged(e);
    private void OnVisualizationSizeChanged(object sender, SizeChangedEventArgs e) => _subtitleEditor.DrawTimeline();

    private void DrawTimeline(long? positionMicroseconds = null)
        => _subtitleEditor.DrawTimeline(positionMicroseconds);

    private async Task<bool> PrepareForSubtitleLoadAsync()
    {
        await CancelAiPipelineAsync();
        return await _subtitleSession.ConfirmDiscardChangesAsync(L("ActionLoadSubtitle"));
    }

    private void CompleteSubtitleLoad(SubtitleDocument document)
    {
        _aiWorkflow.ResetTranslation();
        // Keep the native player and editable document on the same rendered track.
        ScheduleSubtitleOverlaySync();
        StatusText.Text = F("StatusSubtitlesLoaded", document.ActiveTrack?.Cues.Count ?? 0);
    }

    private void HandlePlaybackStateChanged(PlaybackState state)
    {
        if (state == PlaybackState.Playing)
        {
            // show-text has a finite lifetime. Re-arm the current cue after a
            // pause/resume so a long pause cannot make it disappear permanently.
            _subtitleOverlay.InvalidateGeneratedCue();
        }
        RefreshGeneratedSubtitleOsd(CurrentPlaybackPositionMicroseconds);
    }

    private void OnFirstFrameReady(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        ApplySubtitleVisibilityPreference();
        _mediaSession.FirstFrameReady();
    });
    private void HandlePlaybackSeeked()
    {
        if (_subtitleOverlay.IsGeneratedOverlayActive)
        {
            ApplySubtitleVisibilityPreference();
            ClearGeneratedSubtitleOsd(force: true);
            RefreshGeneratedSubtitleOsd(CurrentPlaybackPositionMicroseconds);
            return;
        }

        // Seeking can temporarily clear the selected subtitle track in libmpv. Restore the
        // visibility preference and reselect the editor overlay without rebuilding its file.
        ApplySubtitleVisibilityPreference();
        if (_subtitleTracks.DisplayMode is not null && _subtitleSession.Document.ActiveTrack is { Cues.Count: > 0 } &&
            !_playback.RestoreEditorSubtitleAfterSeek())
            ScheduleSubtitleOverlaySync(force: true);
    }

    private void HandlePlaybackPositionChanged(long positionMicroseconds)
    {
        _subtitleEditor.UpdatePlaybackPosition(positionMicroseconds);
        RefreshGeneratedSubtitleOsd(positionMicroseconds);
    }

    private void HandlePlaybackTracksChanged()
    {
        ApplySubtitleVisibilityPreference();
        _subtitleTracks.Refresh();
    }

    private void OnAudioTrackChanged(object sender, SelectionChangedEventArgs e) =>
        _playbackController?.SelectAudioTrack(AudioTrackCombo.SelectedItem);
    private void FocusPlaybackSurface()
    {
        VideoFocusTarget.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => VideoFocusTarget.Focus(FocusState.Programmatic));
    }

    private void SelectRelativeCue(int delta)
        => _subtitleEditor.SelectRelativeCue(delta);

    private void OnPreviousSubtitleClick(object sender, RoutedEventArgs e) => SelectRelativeCue(-1);
    private void OnNextSubtitleClick(object sender, RoutedEventArgs e) => SelectRelativeCue(1);
    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => _shortcuts.ToggleFullscreen();
    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_playback.State is PlaybackState.Playing or PlaybackState.Paused) TryPlayback(_playback.TogglePause);
        e.Handled = true;
    }
    private void OnToggleRightPanelClick(object sender, RoutedEventArgs e) { _panels.IsRightVisible = ShowRightPanelMenuItem.IsChecked; ApplyPanelVisibility(); }
    private void OnToggleBottomPanelClick(object sender, RoutedEventArgs e) { _panels.IsBottomVisible = ShowBottomPanelMenuItem.IsChecked; ApplyPanelVisibility(); }
    private void OnToggleStatusPanelClick(object sender, RoutedEventArgs e) { _panels.IsStatusVisible = ShowStatusPanelMenuItem.IsChecked; ApplyPanelVisibility(); }
    private void OnRightPanelToggleButtonClick(object sender, RoutedEventArgs e) { _panels.IsRightVisible = RightPanelToggleButton.IsChecked == true; ApplyPanelVisibility(); }
    private void OnBottomPanelToggleButtonClick(object sender, RoutedEventArgs e) { _panels.IsBottomVisible = BottomPanelToggleButton.IsChecked == true; ApplyPanelVisibility(); }
    private void OnStatusPanelToggleButtonClick(object sender, RoutedEventArgs e) { _panels.IsStatusVisible = StatusPanelToggleButton.IsChecked == true; ApplyPanelVisibility(); }

    private void ToggleRightPanel()
    {
        _panels.IsRightVisible = !_panels.IsRightVisible;
        ApplyPanelVisibility();
    }

    private void ToggleBottomPanel()
    {
        _panels.IsBottomVisible = !_panels.IsBottomVisible;
        ApplyPanelVisibility();
    }

    private void ToggleStatusPanel()
    {
        _panels.IsStatusVisible = !_panels.IsStatusVisible;
        ApplyPanelVisibility();
    }

    private void ApplyPanelVisibility()
    {
        if (_fullscreen.IsFullscreen) return;
        _panels.Apply(_initialized);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fullscreen.IsFullscreen) return;
        _chrome.UpdateTitleBarDragRegion();
        if (_panels.Clamp(MainContentGrid.ActualWidth, RootGrid.ActualHeight)) ApplyPanelVisibility();
    }

    private void ClampPanelSizesToAvailable() => _panels.Clamp(MainContentGrid.ActualWidth, RootGrid.ActualHeight);

    private void OnRightPanelSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_fullscreen.IsFullscreen || !_panels.IsRightVisible) return;
        _panels.ResizeRight(e.HorizontalChange, MainContentGrid.ActualWidth, RightPanelSplitterColumn.ActualWidth, _initialized);
    }

    private void OnBottomPanelSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_fullscreen.IsFullscreen || !_panels.IsBottomVisible) return;
        _panels.ResizeBottom(e.VerticalChange, RootGrid.ActualHeight, _initialized);
    }

    private async void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = L("StatusCollectingDiagnostics");
        var snapshot = await new DiagnosticsService().CollectAsync(_playback, _aiWorkflow.AsrState, AsrRuntimePaths.GetCrispAsrRuntimeDirectory(_settings.Asr.CrispAsrRuntimeDirectory), _settings.Asr.ModelPath, _settings.Asr.AlignerPath);
        var output = new TextBox { Text = snapshot.ToString(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 650, MinHeight = 420, FontFamily = new FontFamily("Consolas") };
        await ShowDialogAsync(new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = L("DiagnosticsTitle"), Content = output, CloseButtonText = L("CloseButton") });
        StatusText.Text = L("ReadyText");
    }
    private void OnGenerateSubtitleClick(object sender, RoutedEventArgs e)
        => _aiWorkflow.RequestSubtitleGeneration(GenerateSubtitlesMenuItem.IsChecked);

    private void StartCheckedAiPipeline(long? requestedStartMicroseconds = null, bool waitForMediaReady = false, bool continueExistingResults = false)
        => _aiWorkflow.StartPipeline(requestedStartMicroseconds, waitForMediaReady, continueExistingResults);

    private async Task CancelAiPipelineAsync()
        => await _aiWorkflow.CancelAsync();

    private void ScheduleAiRestartAfterSeek(TimeSpan requestedPosition)
        => _aiWorkflow.ScheduleRestartAfterSeek(requestedPosition);

    private void OnTranslateClick(object sender, RoutedEventArgs e)
        => _aiWorkflow.RequestTranslation(TranslateMenuItem.IsChecked);

    private async void OnSummarizeClick(object sender, RoutedEventArgs e)
        => await _aiWorkflow.SummarizeAsync();

    private void OnCancelAiClick(object sender, RoutedEventArgs e)
        => _aiWorkflow.CancelWithRetry();

    private async void OnRetryAiClick(object sender, RoutedEventArgs e)
        => await _aiWorkflow.RetryAsync();
    private async void OnCameraClick(object sender, RoutedEventArgs e) =>
        await _auxiliaryWindows.ShowCameraAsync();

    private async void OnWindowsCaptionsClick(object sender, RoutedEventArgs e) =>
        await _auxiliaryWindows.ShowWindowsCaptionsAsync();

    private async void OnCaptureRecorderClick(object sender, RoutedEventArgs e) =>
        await _auxiliaryWindows.ShowCaptureRecorderAsync();
    private async void OnSettingsClick(object sender, RoutedEventArgs e) =>
        await _auxiliaryWindows.ShowSettingsAsync();

    private void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        AsrRuntimePaths.SetWorkerDirectory(settings.Asr.WorkerDirectory);
        _mediaNavigation.ApplySettings();
        LocalizationService.Apply(settings.General.Language);
        UiFontService.Apply(settings.General.UiFontFamily, RootGrid);
        _playbackController.ApplyToolbarSize();
        _chrome.ApplyTheme(settings.General.Theme);
        _rightPanel.RefreshLabels();
        _shortcuts.RefreshHints();
        if (!_playback.IsAvailable) return;
        TryPlayback(() =>
        {
            _playback.SetVolume(settings.Playback.DefaultVolume);
            _playback.SetRate(settings.Playback.PlaybackRate);
            _playback.ConfigureNetwork(TimeSpan.FromSeconds(settings.Network.TimeoutSeconds), settings.Network.Proxy);
            _playback.ConfigurePreferredLanguages(settings.Playback.DefaultAudioLanguage, settings.Playback.DefaultSubtitleLanguage);
            _playback.ConfigureSubtitleStyle(
                settings.Subtitle.FontFamily,
                settings.Subtitle.FontSize,
                settings.Subtitle.Color,
                settings.Subtitle.Background,
                settings.Subtitle.Outline,
                settings.Subtitle.BottomMargin);
            _playback.ConfigureHdrOutput(settings.Playback.HdrOutput);
            _playback.ConfigureRtxVideoSuperResolution(settings.Playback.RtxVideoSuperResolution);
        });
        ScheduleSubtitleOverlaySync();
    }

    private async void OnAboutClick(object sender, RoutedEventArgs e) =>
        await _aboutDialog.ShowAsync();

    private void UpdatePanelToggleIcons() => _chrome.UpdatePanelToggleIcons();

    private void OnRootActualThemeChanged(FrameworkElement sender, object args) =>
        _chrome.ActualThemeChanged(sender.ActualTheme);

    private async void OnPlaylistItemClick(object sender, ItemClickEventArgs e) =>
        await _mediaNavigation.OpenPlaylistItemAsync(e.ClickedItem);

    private void OnClearPlaylistClick(object sender, RoutedEventArgs e) =>
        _mediaNavigation.ClearPlaylist();

    private async void OnFavoriteDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        await _mediaNavigation.ReorderFavoritesAsync();

    private async void OnFavoriteItemClick(object sender, ItemClickEventArgs e) =>
        await _mediaNavigation.OpenFavoriteItemAsync(e.ClickedItem);

    private async void OnRemoveFavoriteItemClick(object sender, RoutedEventArgs e) =>
        await _mediaNavigation.RemoveFavoriteAsync(sender);

    private async Task<bool> PrepareForRemoteSubtitleLoadAsync()
        => await PrepareForSubtitleLoadAsync();

    private Task ApplyDownloadedWebDavSubtitleAsync(DownloadedWebDavSubtitle subtitle)
    {
        _subtitleSession.DecodeAndBind(subtitle.Path, subtitle.Bytes);
        if (subtitle.ShowSubtitlePanel) _rightPanel.Show(RightPanelSection.Subtitles);
        return Task.CompletedTask;
    }

    private void ScheduleSubtitleOverlaySync(bool force = false)
        => _subtitleOverlay.ScheduleSync(force);

    private void ScheduleGeneratedSubtitleUiRefresh()
    {
        if (Interlocked.Exchange(ref _generatedSubtitleUiRefreshQueued, 1) != 0) return;
        _ = DispatchGeneratedSubtitleUiRefreshAsync();
    }

    private async Task DispatchGeneratedSubtitleUiRefreshAsync()
    {
        await Task.Delay(200);
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            Interlocked.Exchange(ref _generatedSubtitleUiRefreshQueued, 0);
            DrawTimeline();
            // The native ASS track is refreshed once when generation/translation
            // completes. Keeping this UI refresh local prevents every incoming cue
            // from asking mpv to rebuild its subtitle renderer.
        })) Interlocked.Exchange(ref _generatedSubtitleUiRefreshQueued, 0);
    }

    private void EnableGeneratedSubtitleOverlay()
        => _subtitleOverlay.EnableGeneratedOverlay();

    private void ApplySubtitleVisibilityPreference()
        => _subtitleOverlay.ApplyVisibilityPreference();

    private void RefreshGeneratedSubtitleOsd(long positionMicroseconds)
        => _subtitleOverlay.RefreshGeneratedOsd(positionMicroseconds);

    private void ClearGeneratedSubtitleOsd(bool force = false)
        => _subtitleOverlay.ClearGeneratedOsd(force);

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_fullscreen.IsFullscreen && sender.Presenter is OverlappedPresenter presenter && presenter.State != OverlappedPresenterState.Minimized)
            WindowChromeController.CaptureWindowPlacement(sender, presenter, _settings.Window);
        if (_allowClose) return;

        // Closed is too late for asynchronous cleanup: WinUI can stop its dispatcher before
        // child processes and native playback have finished shutting down. Keep the window
        // alive until all owned resources have been released, then issue the final Close.
        args.Cancel = true;
        if (_closeInProgress) return;
        _closeInProgress = true;
        try
        {
            if (!await _subtitleSession.ConfirmCloseAsync()) return;

            _shutdownTask ??= ShutdownAsync();
            await _shutdownTask;
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "shutdown", "APPLICATION_SHUTDOWN_ERROR", exception.Message, exception);
            _allowClose = true;
            Close();
        }
        finally
        {
            if (!_allowClose) _closeInProgress = false;
        }
    }

    private void TryPlayback(Action action) { try { action(); } catch (Exception exception) { StatusText.Text = exception.Message; } }
    private void SeekAndRestartAi(TimeSpan requestedPosition, Action seek)
    {
        TryPlayback(seek);
        ScheduleAiRestartAfterSeek(requestedPosition);
    }
    private ContentDialog CreateDialog(string title, object content, string primaryText) =>
        _dialogs.Create(title, content, primaryText);
    private Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog) => _dialogs.ShowAsync(dialog);
    private Task ShowMessageAsync(string title, string message) => _dialogs.ShowMessageAsync(title, message);
    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) => string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);
    private long CurrentPlaybackPositionMicroseconds => Math.Max(0, _playback.Position.Ticks / 10);
    private async Task ShutdownAsync()
    {
        _audioPresentation.Dispose();
        _shortcuts.Dispose();
        _playbackController.Dispose();
        _fullscreen.Dispose();
        if (_appWindow is not null)
        {
            _appWindow.Changed -= OnAppWindowChanged;
        }
        _mediaNavigation.CancelPendingWork();
        _subtitleOverlay.CancelPendingSync();

        await _auxiliaryWindows.CloseAsync();

        try { await _aiWorkflow.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "ASR_DISPOSE_ERROR", exception.Message, exception); }

        try { await _mediaNavigation.SaveHistoryAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "HISTORY_SAVE_ERROR", exception.Message, exception); }
        try { await SettingsService.CreateDefault().SaveAsync(_settings); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "SETTINGS_SAVE_ERROR", exception.Message, exception); }
        _rightPanel.Dispose();
        _subtitleTracks.Dispose();
        _mediaNavigation.Dispose();
        try { await _playback.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "PLAYBACK_DISPOSE_ERROR", exception.Message, exception); }
        _playback.FirstFrameReady -= OnFirstFrameReady;
        if (_videoHost is not null)
        {
            _videoHost.FilesDropped -= OnNativeVideoFilesDropped;
            _videoHost.Clicked -= OnNativeVideoClicked;
            _videoHost.DoubleClicked -= OnNativeVideoDoubleClicked;
            _videoHost.Dispose();
        }
        _subtitleOverlay.Dispose();

        if (_auxiliaryWindows.RestartRequested)
        {
            try { Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().UnregisterKey(); } catch { }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? "AIMediaWorker.exe") { UseShellExecute = true });
            }
            catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "APPLICATION_RESTART_ERROR", exception.Message, exception); }
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _shortcuts.Dispose();
        _playbackController.Dispose();
        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
        }
        Closed -= OnWindowClosed;
        Application.Current.Exit();
    }

    AppSettings IAiWorkflowHost.Settings => _settings;
    SubtitleDocument IAiWorkflowHost.Document => _subtitleSession.Document;
    SubtitleDisplayMode? IAiWorkflowHost.CurrentSubtitleDisplayMode => _subtitleTracks.DisplayMode;
    IReadOnlyDictionary<string, string>? IAiWorkflowHost.CurrentHttpHeaders => _mediaSession.CurrentHttpHeaders;
    long IAiWorkflowHost.CurrentPlaybackPositionMicroseconds => CurrentPlaybackPositionMicroseconds;
    double IAiWorkflowHost.ViewWidth => RootGrid.ActualWidth;
    double IAiWorkflowHost.ViewHeight => RootGrid.ActualHeight;
    DispatcherQueue IAiWorkflowHost.DispatcherQueue => DispatcherQueue;
    void IAiWorkflowHost.BindDocument(SubtitleDocument document) => _subtitleSession.Bind(document);
    void IAiWorkflowHost.SetSubtitleDisplayMode(SubtitleDisplayMode displayMode, bool refreshOverlay) =>
        _subtitleTracks.SetDisplayMode(displayMode, refreshOverlay);
    void IAiWorkflowHost.ShowSubtitlePanel()
    {
        _rightPanel.Show(RightPanelSection.Subtitles);
    }
    void IAiWorkflowHost.DrawTimeline() => DrawTimeline();
    void IAiWorkflowHost.ScheduleSubtitleOverlaySync(bool force) => ScheduleSubtitleOverlaySync(force);
    void IAiWorkflowHost.ScheduleGeneratedSubtitleUiRefresh() => ScheduleGeneratedSubtitleUiRefresh();
    void IAiWorkflowHost.EnableGeneratedSubtitleOverlay() => EnableGeneratedSubtitleOverlay();
    void IAiWorkflowHost.ExecuteSubtitleCommand(IUndoableSubtitleCommand command) => _subtitleEditor.Execute(command);
    void IAiWorkflowHost.SetStatus(string message) => StatusText.Text = message;
    void IAiWorkflowHost.SetDownloadProgress(bool visible, bool indeterminate, double value)
    {
        AsrDownloadProgressBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AsrDownloadProgressBar.IsIndeterminate = indeterminate;
        if (!indeterminate) AsrDownloadProgressBar.Value = value;
    }
    void IAiWorkflowHost.SetRetryAvailable(bool available) => RetryAiMenuItem.IsEnabled = available;
    Task<ContentDialogResult> IAiWorkflowHost.ShowDialogAsync(string title, object content, string primaryText) =>
        ShowDialogAsync(CreateDialog(title, content, primaryText));
    Task<ContentDialogResult> IAiWorkflowHost.ShowDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot ??= RootGrid.XamlRoot;
        dialog.RequestedTheme = RootGrid.ActualTheme;
        return ShowDialogAsync(dialog);
    }
    Task IAiWorkflowHost.ShowMessageAsync(string title, string message) => ShowMessageAsync(title, message);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, uint valueSize);

}
