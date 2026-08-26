using AIMediaWorker.Playback;
using AIMediaWorker.Subtitle;
using AIMediaWorker.Subtitle.Editing;
using AIMediaWorker.Subtitle.Parsing;
using AIMediaWorker.Views;
using AIMediaWorker.Settings;
using AIMediaWorker.Asr;
using AIMediaWorker.Network;
using AIMediaWorker.History;
using AIMediaWorker.Media;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.Storage;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using System.Collections.ObjectModel;

namespace AIMediaWorker;

public sealed partial class MainWindow : Window, IAiWorkflowHost
{
    private readonly MpvPlaybackEngine _playback = new();
    private readonly WindowsPowerManagement _windowsPowerManagement = new();
    private readonly SubtitleFileService _subtitleFiles = new();
    private readonly SubtitleEditorController _subtitleEditor;
    private readonly SubtitleOverlayController _subtitleOverlay;
    private SubtitleDocument _document = new();
    private NativeVideoHost? _videoHost;
    private AppWindow? _appWindow;
    private bool _updatingPosition;
    private bool _positionSliderDragging;
    private bool _initialized;
    private CameraWindow? _cameraWindow;
    private WindowsCaptionWindow? _windowsCaptionWindow;
    private SettingsWindow? _settingsWindow;
    private AppSettings _settings = new();
    private readonly WindowsCredentialService _windowsCredentials = new();
    private readonly WebDavCredentialStore _webDavCredentials;
    private readonly WebDavClient _webDavClient;
    private readonly AiWorkflowController _aiWorkflow;
    private int _generatedSubtitleUiRefreshQueued;
    private int _playbackPositionUiRefreshQueued;
    private readonly SemaphoreSlim _dialogLock = new(1, 1);
    private readonly MediaHistoryService _historyService = MediaHistoryService.CreateDefault();
    private readonly ObservableCollection<FavoriteListEntry> _favoriteEntries = [];
    private IMediaSource? _currentMediaSource;
    private IReadOnlyDictionary<string, string>? _currentHttpHeaders;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _restartRequested;
    private bool _playbackPowerRequirementActive;
    private bool _playbackPowerManagementDisabled;
    private Task? _shutdownTask;
    private TimeSpan? _abStart;
    private SubtitleDisplayMode? _subtitleDisplayMode;
    private int? _selectedNativeSubtitleTrackId;
    private bool _updatingSubtitleTrackSelector;
    private readonly List<PlaylistEntry> _playlist = [];
    private int _playlistIndex = -1;
    private RepeatMode _repeatMode;
    private bool _mediaOpenReady;
    private bool _firstFrameUiReadyForMedia;
    private string? _pendingMediaOpenSource;
    private string? _firstFrameWaitSource;
    private TaskCompletionSource? _firstFrameWaiter;
    private string? _pendingLaunchSource;
    private string[]? _pendingDroppedFiles;
    private string? _audioTagStatusText;
    private PendingPostOpenWork? _pendingPostOpenWork;
    private CancellationTokenSource? _postOpenCancellation;
    private readonly TaskCompletionSource _firstUiFrameReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _playbackInitializationTask;
    private readonly PanelLayoutController _panels;
    private readonly FullscreenPresentationController _fullscreen;

    private sealed record SubtitleSelectionOption(string DisplayName, SubtitleDisplayMode? DisplayMode, int? TrackId);

    public MainWindow() : this(null, new AppSettings()) { }

    public MainWindow(string? initialSource) : this(initialSource, new AppSettings()) { }

    public MainWindow(string? initialSource, AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _webDavCredentials = new WebDavCredentialStore(_windowsCredentials);
        _webDavClient = new WebDavClient(_windowsCredentials, timeout: TimeSpan.FromSeconds(_settings.Network.TimeoutSeconds));
        StartupProfiler.Mark("xaml-start");
        InitializeComponent();
        StartupProfiler.Mark("xaml-ready");
        _subtitleEditor = new SubtitleEditorController(
            SubtitleList,
            TimelineCanvas,
            () => _document,
            () => CurrentPlaybackPositionMicroseconds,
            position => SeekAndRestartAi(position, () => _playback.Seek(position, true)),
            () => ScheduleSubtitleOverlaySync(),
            message => StatusText.Text = message,
            L,
            async (title, content, primaryText) => await ShowDialogAsync(CreateDialog(title, content, primaryText)),
            ShowMessageAsync);
        _subtitleOverlay = new SubtitleOverlayController(
            _playback,
            () => _document,
            () => _settings,
            () => _subtitleDisplayMode,
            () => _selectedNativeSubtitleTrackId,
            () => CurrentPlaybackPositionMicroseconds,
            visible => SubtitleVisibilityMenuItem.IsChecked = visible,
            message => StatusText.Text = message,
            DispatcherQueue);
        _aiWorkflow = new AiWorkflowController(
            this,
            _playback,
            GenerateSubtitlesMenuItem.IsChecked,
            TranslateMenuItem.IsChecked);
        _panels = new PanelLayoutController(
            new PanelLayoutViewElements(
                SubtitlePanel, RightPanelSplitter, RightPanelSplitterColumn, RightPanelColumn,
                VisualizationPanel, BottomPanelSplitter, BottomPanelSplitterRow, BottomPanelRow, StatusPanel,
                ShowRightPanelMenuItem, ShowBottomPanelMenuItem, RightPanelToggleButton, BottomPanelToggleButton),
            () => _settings.Window,
            UpdatePanelToggleIcons);
        // Apply the saved font to already-created elements as well as the app resource.
        // The custom title bar is part of this visual tree and must follow the setting
        // on the first launch, not only after the Preferences window is saved.
        UiFontService.Apply(_settings.General.UiFontFamily, RootGrid);
        PositionSlider.ThumbToolTipValueConverter = new PositionSliderThumbToolTipValueConverter();
        _playback.StateChanged += OnPlaybackStateChanged;
        _playback.FirstFrameReady += OnFirstFrameReady;
        _playback.PositionChanged += OnPlaybackPositionChanged;
        _playback.Seeked += OnPlaybackSeeked;
        _playback.TracksChanged += OnTracksChanged;
        _playback.ErrorOccurred += OnPlaybackError;
        _playback.MediaEnded += OnMediaEnded;
        _videoHost = new NativeVideoHost(this, VideoPlaceholder);
        _videoHost.FilesDropped += OnNativeVideoFilesDropped;
        _videoHost.Clicked += OnNativeVideoClicked;
        _videoHost.DoubleClicked += OnNativeVideoDoubleClicked;
        _playbackInitializationTask = InitializePlaybackAfterFirstUiFrameAsync(_videoHost.Create());
        _pendingLaunchSource = initialSource;
        ExtendsContentIntoTitleBar = true;
        RightPanelSectionList.SelectionChanged += OnRightPanelSectionChanged;
        MediaBrowser.DefaultDirectory = _settings.General.DefaultFolder;
        MediaBrowser.ChooseFolderRequested += OnBrowserChooseFolderRequested;
        MediaBrowser.MediaRequested += OnBrowserMediaRequested;
        MediaBrowser.FavoriteRequested += OnBrowserFavoriteRequested;
        MediaBrowser.ErrorOccurred += OnBrowserErrorOccurred;
        WebDavBrowser.Configure(_webDavClient, _webDavCredentials);
        WebDavBrowser.AddServerRequested += OnAddWebDavServerRequested;
        WebDavBrowser.DeleteServerRequested += OnDeleteWebDavServerRequested;
        WebDavBrowser.EntryRequested += OnWebDavEntryRequested;
        WebDavBrowser.FavoriteRequested += OnWebDavFavoriteRequested;
        RefreshRightPanelSections();
        GenerateSubtitlesMenuItem.IsChecked = _settings.Asr.GenerateSubtitles;
        TranslateMenuItem.IsChecked = _settings.Llm.TranslateSubtitles;
        _aiWorkflow.UpdateModes(GenerateSubtitlesMenuItem.IsChecked, TranslateMenuItem.IsChecked);
        var handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        ResizeToAvailableWorkArea(1280, 820);
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
        if (_appWindow is not null) { _appWindow.Closing += OnAppWindowClosing; _appWindow.Changed += OnAppWindowChanged; }
        Closed += OnWindowClosed;
        RootGrid.ActualThemeChanged += OnRootActualThemeChanged;
        ApplyTheme(_settings.General.Theme);
        RootGrid.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnRootPreviewKeyDown), true);
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
        PositionSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPositionSliderPointerPressed), true);
        PositionSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPositionSliderPointerReleased), true);
        PositionSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPositionSliderPointerReleased), true);
        BindDocument(new SubtitleDocument());
    }

    private async Task OpenInitialLaunchSourceAsync()
    {
        try
        {
            await _firstUiFrameReady.Task.ConfigureAwait(false);
            await _playbackInitializationTask.ConfigureAwait(false);
            if (!_playback.IsAvailable || _pendingDroppedFiles is { Length: > 0 } ||
                _pendingLaunchSource is not { Length: > 0 } launchSource) return;
            _pendingLaunchSource = null;
            BeginMediaOpen(launchSource);
            await _playback.OpenAsync(launchSource).ConfigureAwait(false);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(() =>
                {
                    try { CompleteMediaOpen(launchSource, null, null, preservePlaylist: false, showInExplorer: true); completion.SetResult(); }
                    catch (Exception exception) { completion.SetException(exception); }
                }))
                throw new InvalidOperationException("Could not complete the initial media open on the UI thread.");
            await completion.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "startup", "INITIAL_MEDIA_OPEN_ERROR", exception.Message, exception);
        }
    }

    private async Task InitializePlaybackAsync(nint videoWindowHandle)
    {
        await _playback.InitializeAsync(videoWindowHandle, _settings.Playback.HardwareDecoder, _settings.Playback.Renderer, _settings.Playback.RtxVideoSuperResolution).ConfigureAwait(false);
        if (!_playback.IsAvailable) return;
        _playback.SetLoopFile(_repeatMode == RepeatMode.One);
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
        if (_appWindow is null || !layout.HasPlacement) return;
        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var maxWidth = Math.Max(1, workArea.Width - 32);
        var maxHeight = Math.Max(1, workArea.Height - 32);
        var minWidth = Math.Min(640, maxWidth);
        var minHeight = Math.Min(420, maxHeight);
        var width = Math.Clamp(layout.Width, minWidth, maxWidth);
        var height = Math.Clamp(layout.Height, minHeight, maxHeight);
        var x = Math.Clamp(layout.X, workArea.X, workArea.X + workArea.Width - width);
        var y = Math.Clamp(layout.Y, workArea.Y, workArea.Y + workArea.Height - height);
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        if (layout.IsMaximized && _appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }

    private void ResizeToAvailableWorkArea(int preferredWidth, int preferredHeight)
    {
        if (_appWindow is null) return;
        var workArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(preferredWidth, Math.Max(1, workArea.Width - 32));
        var height = Math.Min(preferredHeight, Math.Max(1, workArea.Height - 32));
        _appWindow.Resize(new SizeInt32(width, height));
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_initialized || _fullscreen.HandleAppWindowChanged()) return;
        if (sender.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized) return;
        CaptureWindowPlacement(sender, presenter);
        UpdateTitleBarDragRegion();
    }

    private void CaptureWindowPlacement(AppWindow window, OverlappedPresenter presenter)
    {
        _settings.Window.IsMaximized = presenter.State == OverlappedPresenterState.Maximized;
        if (presenter.State != OverlappedPresenterState.Restored) return;
        _settings.Window.HasPlacement = true;
        _settings.Window.X = window.Position.X;
        _settings.Window.Y = window.Position.Y;
        _settings.Window.Width = window.Size.Width;
        _settings.Window.Height = window.Size.Height;
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        UpdateTitleBarDragRegion();
        try
        {
            _panels.Load(_settings.Window);
            ClampPanelSizesToAvailable();
            ApplyPanelVisibility();
            var recentLoad = _historyService.LoadRecentAsync();
            _ = MediaBrowser.InitializeAsync();
            SubtitleVisibilityMenuItem.IsChecked = _settings.Playback.ShowSubtitles;
            RateCombo.ItemsSource = new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 };
            RateCombo.SelectedItem = RateCombo.Items.Cast<double>().OrderBy(value => Math.Abs(value - _settings.Playback.PlaybackRate)).First();
            UpdateShortcutHints();
            UpdatePlaylistButtons();
            // Shell activation previously issued loadfile from the constructor, before WinUI
            // had presented its first frame. Let one complete composition pass finish first so
            // decoder/GPU startup cannot delay painting the window chrome and controls.
            await WaitForFirstUiFrameAsync();
            await _playbackInitializationTask;
            StatusText.Text = _playback.IsAvailable ? L("StatusLibmpvReady") : L("StatusPlaybackUnavailable");
            if (_playback.IsAvailable && _pendingDroppedFiles is { Length: > 0 } droppedFiles)
            {
                _pendingDroppedFiles = null;
                _pendingLaunchSource = null;
                await OpenFilesAsPlaylistAsync(droppedFiles);
            }
            if (_pendingLaunchSource is { Length: > 0 }) await OpenInitialLaunchSourceAsync();
            await recentLoad;
            RebuildRecentMenu();
            RefreshWebDavServerList();
            ApplyTheme(_settings.General.Theme);
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private Task WaitForFirstUiFrameAsync()
    {
        if (_firstUiFrameReady.Task.IsCompleted) return _firstUiFrameReady.Task;
        EventHandler<object>? rendering = null;
        rendering = (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendering;
            // Rendering is raised while the frame is being prepared. Complete from a low
            // priority dispatcher item so the current frame is submitted before playback work.
            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    StartupProfiler.Mark("first-ui-frame");
                    _firstUiFrameReady.TrySetResult();
                }))
                _firstUiFrameReady.TrySetResult();
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += rendering;
        return _firstUiFrameReady.Task;
    }

    private async void OnOpenMediaClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0) await HandleDroppedFilesAsync(files.Select(file => file.Path));
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "file-picker", "OPEN_MEDIA_PICKER_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("PlaybackErrorTitle"), exception.Message);
        }
    }

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
        await OpenMediaAsync(uri.AbsoluteUri);
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
                await HandleDroppedFilesAsync(items.OfType<StorageFile>().Select(file => file.Path));
                return;
            }
            if (e.DataView.Contains(StandardDataFormats.Text))
            {
                var value = (await e.DataView.GetTextAsync()).Trim();
                if (Path.IsPathFullyQualified(value)) await HandleDroppedFilesAsync([value]);
                else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") await OpenMediaAsync(uri.AbsoluteUri);
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
            try { await HandleDroppedFilesAsync(paths); }
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

    private async Task HandleDroppedFilesAsync(IEnumerable<string> paths)
    {
        var pathSnapshot = paths.ToArray();
        var files = await Task.Run(() => pathSnapshot
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
        if (files.Length == 0) return;
        if (!_playback.IsAvailable)
        {
            _pendingDroppedFiles = files;
            StatusText.Text = L("StatusPreparingDroppedMedia");
            return;
        }
        await OpenFilesAsPlaylistAsync(files);
    }

    /// <summary>
    /// Handles a launch redirected from a secondary app instance: brings this window
    /// to the foreground and opens the forwarded files.
    /// </summary>
    public void ActivateFromExternalLaunch(IReadOnlyList<string>? filePaths)
    {
        BringToFront();
        if (filePaths is not { Count: > 0 }) return;
        _pendingLaunchSource = null;
        _ = OpenForwardedFilesAsync(filePaths);
    }

    private async Task OpenForwardedFilesAsync(IReadOnlyList<string> filePaths)
    {
        try { await HandleDroppedFilesAsync(filePaths); }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "activation", "REDIRECTED_OPEN_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("PlaybackErrorTitle"), exception.Message);
        }
    }

    private void BringToFront()
    {
        var handle = WindowNative.GetWindowHandle(this);
        if (_appWindow?.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        ShowWindow(handle, 9); // SW_RESTORE
        SetForegroundWindow(handle);
    }

    private async Task OpenMediaAsync(string source, IReadOnlyDictionary<string, string>? httpHeaders = null, IMediaSource? mediaSource = null, bool preservePlaylist = false)
    {
        // Cancelling an active ASR/translation request can take a second or two while its
        // provider returns. Signal it now, but do not make the new media wait to start.
        var aiPipelineCancellation = CancelAiPipelineAsync();
        if (!await ConfirmDiscardChangesAsync(L("ActionOpenMedia")))
        {
            await aiPipelineCancellation;
            return;
        }
        try
        {
            await _firstUiFrameReady.Task;
            await _playbackInitializationTask;
            if (!_playback.IsAvailable) throw new InvalidOperationException(L("StatusPlaybackUnavailable"));
            await _historyService.LoadRecentAsync();
            RememberCurrentPosition();
            BeginMediaOpen(source);
            await _playback.OpenAsync(source, httpHeaders);
            await aiPipelineCancellation;
            CompleteMediaOpen(source, httpHeaders, mediaSource ?? MediaSourceFactory.Parse(source), preservePlaylist, showInExplorer: false);
        }
        catch (Exception exception)
        {
            if (string.Equals(_pendingMediaOpenSource, source, StringComparison.OrdinalIgnoreCase))
            {
                _mediaOpenReady = false;
                _pendingMediaOpenSource = null;
            }
            await aiPipelineCancellation;
            await AppLog.WriteAsync("error", "playback", "OPEN_MEDIA_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("PlaybackErrorTitle"), exception.Message);
        }
    }

    private void CompleteMediaOpen(string source, IReadOnlyDictionary<string, string>? httpHeaders, IMediaSource? mediaSource, bool preservePlaylist, bool showInExplorer)
    {
        _currentMediaSource = mediaSource ?? MediaSourceFactory.Parse(source);
        if (!preservePlaylist)
        {
            _playlist.Clear();
            if (_currentMediaSource is LocalMediaSource localSource) { _playlist.Add(PlaylistEntry.FromLocal(localSource.Path)); _playlistIndex = 0; }
            else _playlistIndex = -1;
        }
        _currentHttpHeaders = httpHeaders is null ? null : new Dictionary<string, string>(httpHeaders, StringComparer.OrdinalIgnoreCase);
        ApplyRepeatModeToPlayback();
        UpdateWindowTitle(_currentMediaSource.DisplayName);
        if (_currentMediaSource is WebDavMediaSource webDavSource) WebDavBrowser.SelectEntry(webDavSource.ServerId, webDavSource.Uri);
        _ = SaveHistoryAfterOpenAsync(_currentMediaSource);
        _subtitleEditor.ResetTimeline();
        var blank = new SubtitleDocument(); blank.EnsureTrack(); blank.MarkSaved(); BindDocument(blank);
        _aiWorkflow.ResetForMedia();
        _audioTagStatusText = null;
        StatusText.Text = source;
        UpdateAudioTagStatus();
        QueuePostOpenWork(source, _currentMediaSource as LocalMediaSource, !preservePlaylist, showInExplorer);
        UpdatePlaylistButtons();
        FocusPlaybackSurface();
        _mediaOpenReady = true;
        _pendingMediaOpenSource = null;
        if (_firstFrameUiReadyForMedia) StartAutomaticSubtitleGenerationIfReady();
    }

    private void UpdateAudioTagStatus()
    {
        if (_currentMediaSource is not LocalMediaSource localSource || !MediaFileClassifier.IsAudio(localSource.Path)) return;
        var path = localSource.Path;
        _ = Task.Run(() =>
        {
            var tagText = AudioTagReader.ReadDisplayText(path);
            if (tagText is null) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_currentMediaSource is LocalMediaSource current &&
                    string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    _audioTagStatusText = tagText;
                    StatusText.Text = tagText;
                }
            });
        });
    }
    private void BeginMediaOpen(string source)
    {
        _mediaOpenReady = false;
        _firstFrameUiReadyForMedia = false;
        _audioTagStatusText = null;
        _pendingMediaOpenSource = source;
        _firstFrameWaiter?.TrySetCanceled();
        _firstFrameWaitSource = source;
        _firstFrameWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task SaveHistoryAfterOpenAsync(IMediaSource source)
    {
        try
        {
            await _historyService.LoadRecentAsync();
            _historyService.AddRecent(source, 0, _settings.General.RecentMediaCount);
            await _historyService.SaveRecentAsync();
            RebuildRecentMenu();
        }
        catch (Exception exception) { await AppLog.WriteAsync("error", "history", "HISTORY_SAVE_AFTER_OPEN_ERROR", exception.Message, exception); }
    }

    private void UpdateWindowTitle(string displayName)
    {
        var title = string.IsNullOrWhiteSpace(displayName) ? "AIMediaWorker" : $"{displayName} - AIMediaWorker";
        Title = title;
        if (_appWindow is not null) _appWindow.Title = title;
        AppTitleText.Text = title;
    }

    private void QueuePostOpenWork(string source, LocalMediaSource? localSource, bool populateSiblingPlaylist, bool showInExplorer)
    {
        _postOpenCancellation?.Cancel();
        _postOpenCancellation?.Dispose();
        _postOpenCancellation = new CancellationTokenSource();
        var localPath = localSource is null ? null : Path.GetFullPath(localSource.Path);
        if (localPath is not null) PrepareBrowserForOpenedFile(localPath, showInExplorer);
        _pendingPostOpenWork = new PendingPostOpenWork(source, localPath, populateSiblingPlaylist, _postOpenCancellation.Token);
        if (_playback.IsFirstFrameReady) StartPostOpenWorkIfReady();
    }

    private void StartPostOpenWorkIfReady()
    {
        if (_pendingPostOpenWork is not { } work ||
            !string.Equals(_playback.CurrentSource, work.Source, StringComparison.OrdinalIgnoreCase)) return;
        _pendingPostOpenWork = null;
        _ = RunPostOpenWorkAsync(work);
    }

    private async Task RunPostOpenWorkAsync(PendingPostOpenWork work)
    {
        try
        {
            // The browser location is already visible. Defer only directory enumeration so
            // media decoding gets the first slice of disk and CPU time.
            await Task.Delay(250, work.CancellationToken);
            if (!string.Equals(_playback.CurrentSource, work.Source, StringComparison.OrdinalIgnoreCase)) return;
            if (work.LocalPath is { } fullPath)
            {
                await RefreshBrowserForOpenedFileAsync(fullPath);
                if (work.PopulateSiblingPlaylist) await PopulateSiblingPlaylistAsync(fullPath);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "post-open", "POST_OPEN_WORK_ERROR", exception.Message, exception);
        }
    }

    private void PrepareBrowserForOpenedFile(string fullPath, bool showInExplorer)
    {
        if (showInExplorer) ShowRightPanelSection(RightPanelSection.Explorer);
        MediaBrowser.PrepareForOpenedFile(fullPath);
    }

    private async Task RefreshBrowserForOpenedFileAsync(string fullPath)
    {
        try
        {
            await MediaBrowser.SynchronizeOpenedFileAsync(fullPath);
        }
        catch (Exception exception) { await AppLog.WriteAsync("error", "browser", "BROWSER_SYNC_AFTER_OPEN_ERROR", exception.Message, exception); }
    }

    private async Task OpenFilesAsPlaylistAsync(IEnumerable<string> paths)
    {
        // Entry points normalize and validate paths on a worker thread before reaching here.
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        if (files.Length == 1 && MediaFileClassifier.IsSubtitle(files[0])) { await LoadSubtitleFromPathAsync(files[0]); return; }
        _playlist.Clear();
        _playlist.AddRange(files.Where(path => !MediaFileClassifier.IsSubtitle(path)).Select(PlaylistEntry.FromLocal));
        if (_playlist.Count == 0) return;
        _playlistIndex = 0;
        await OpenPlaylistEntryAsync(_playlist[0]);
    }

    private async Task PopulateSiblingPlaylistAsync(string currentPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(currentPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null) return;
            var siblings = MediaBrowser.GetLoadedMediaPaths(directory)?.ToArray()
                ?? await Task.Run(() => Directory.EnumerateFiles(directory).Where(MediaFileClassifier.IsPlayable).OrderBy(Path.GetFileName, WindowsFileNameComparer.Instance).Take(5000).Select(Path.GetFullPath).ToArray());
            if (!string.Equals(_playback.CurrentSource, fullPath, StringComparison.OrdinalIgnoreCase)) return;
            var index = Array.FindIndex(siblings, path => path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            _playlist.Clear(); _playlist.AddRange(siblings.Select(PlaylistEntry.FromLocal)); _playlistIndex = index; UpdatePlaylistButtons();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TryPlayback(_playback.TogglePause);
    private void PlayFromBeginning() => SeekAndRestartAi(TimeSpan.Zero, () => { _playback.Seek(TimeSpan.Zero, true); _playback.Play(); });
    private void OnGoToBeginningClick(object sender, RoutedEventArgs e) => SeekAndRestartAi(TimeSpan.Zero, () => _playback.Seek(TimeSpan.Zero, true));
    private void OnStopClick(object sender, RoutedEventArgs e) => SeekAndRestartAi(TimeSpan.Zero, () =>
    {
        _playback.Seek(TimeSpan.Zero, true);
        _playback.Pause();
    });
    private async void OnPreviousMediaClick(object sender, RoutedEventArgs e) => await OpenAdjacentMediaAsync(-1);
    private async void OnNextMediaClick(object sender, RoutedEventArgs e) => await OpenAdjacentMediaAsync(1);
    private void OnFrameStepClick(object sender, RoutedEventArgs e) => TryPlayback(() => _playback.FrameStep());
    private async void OnSaveScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (!_playback.IsAvailable || _playback.State is not (PlaybackState.Playing or PlaybackState.Paused) || _playback.VideoWidth is null)
        {
            await ShowMessageAsync(L("ScreenshotUnavailableTitle"), L("ScreenshotUnavailableMessage"));
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
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await Task.Run(() => _playback.SaveScreenshot(file.Path));
            StatusText.Text = F("StatusScreenshotSaved", file.Name);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "screenshot", "SCREENSHOT_SAVE_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("ScreenshotErrorTitle"), exception.Message);
        }
    }

    private string CreateScreenshotFileName()
    {
        var displayName = _currentMediaSource?.DisplayName;
        var stem = string.IsNullOrWhiteSpace(displayName) ? "AIMediaWorker" : Path.GetFileNameWithoutExtension(displayName);
        foreach (var character in Path.GetInvalidFileNameChars()) stem = stem.Replace(character, '_');
        if (string.IsNullOrWhiteSpace(stem)) stem = "AIMediaWorker";
        var position = _playback.Position;
        return $"{stem}_{(int)position.TotalHours:00}-{position.Minutes:00}-{position.Seconds:00}.{position.Milliseconds:000}";
    }
    private void OnSeekBackClick(object sender, RoutedEventArgs e) => SeekAndRestartAi(_playback.Position - TimeSpan.FromSeconds(_settings.Playback.SeekIntervalSeconds), () => _playback.SeekRelative(TimeSpan.FromSeconds(-_settings.Playback.SeekIntervalSeconds)));
    private void OnSeekForwardClick(object sender, RoutedEventArgs e) => SeekAndRestartAi(_playback.Position + TimeSpan.FromSeconds(_settings.Playback.SeekIntervalSeconds), () => _playback.SeekRelative(TimeSpan.FromSeconds(_settings.Playback.SeekIntervalSeconds)));
    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        TryPlayback(() => _playback.SetMute(!_playback.IsMuted));
        MuteIcon.Source = PlaybackIconSource(_playback.IsMuted ? "mute" : "volume");
        ShowMuteOverlay();
    }
    private void OnToggleSubtitleVisibilityClick(object sender, RoutedEventArgs e)
    {
        var visible = SubtitleVisibilityMenuItem.IsChecked;
        _settings.Playback.ShowSubtitles = visible;
        ApplySubtitleVisibilityPreference();
        ShowSubtitleVisibilityOverlay();
    }
    private void OnRateChanged(object sender, SelectionChangedEventArgs e) { if (RateCombo.SelectedItem is double rate && _playback.IsAvailable) TryPlayback(() => _playback.SetRate(rate)); }
    private void OnRepeatClick(object sender, RoutedEventArgs e)
    {
        _repeatMode = _repeatMode switch { RepeatMode.Off => RepeatMode.One, RepeatMode.One => RepeatMode.AutoAdvance, _ => RepeatMode.Off };
        ApplyRepeatModeToPlayback();
        RepeatIcon.Source = PlaybackIconSource(_repeatMode switch { RepeatMode.One => "repeat-one", RepeatMode.AutoAdvance => "repeat-auto", _ => "repeat" });
        ToolTipService.SetToolTip(RepeatButton, L(_repeatMode switch { RepeatMode.One => "TooltipRepeatCurrent", RepeatMode.AutoAdvance => "TooltipAutoAdvance", _ => "TooltipRepeatOff" }));
        UpdatePlaylistButtons();
    }

    private void ApplyRepeatModeToPlayback()
    {
        if (_playback.IsAvailable) TryPlayback(() => _playback.SetLoopFile(_repeatMode == RepeatMode.One));
    }

    private async Task OpenAdjacentMediaAsync(int direction)
    {
        if (_playlist.Count == 0) return;
        var next = _playlistIndex + Math.Sign(direction);
        if (next < 0 || next >= _playlist.Count) return;
        _playlistIndex = next;
        await OpenPlaylistEntryAsync(_playlist[_playlistIndex]);
    }

    private async Task OpenPlaylistEntryAsync(PlaylistEntry entry)
    {
        await OpenMediaAsync(entry.Path, entry.HttpHeaders, entry.MediaSource, preservePlaylist: true);
        if (entry.MediaSource is WebDavMediaSource webDavSource &&
            _currentMediaSource is WebDavMediaSource currentSource &&
            currentSource.ServerId == webDavSource.ServerId && WebDavUri.Equals(currentSource.Uri, webDavSource.Uri))
        {
            await TryLoadMatchingWebDavSmiAsync(webDavSource);
        }
    }

    private void UpdatePlaylistButtons()
    {
        PreviousButton.IsEnabled = _playlist.Count > 1 && _playlistIndex > 0;
        NextButton.IsEnabled = _playlist.Count > 1 && _playlistIndex >= 0 && _playlistIndex < _playlist.Count - 1;
        PlaylistList.ItemsSource = _playlist.ToArray();
        PlaylistList.SelectedIndex = _playlistIndex;
    }
    private void OnSetAbStartClick(object sender, RoutedEventArgs e)
    {
        _abStart = _playback.Position;
        TryPlayback(() => _playback.SetAbLoop(_abStart, null));
        StatusText.Text = F("StatusAPoint", FormatTime(_abStart.Value));
    }
    private void OnSetAbEndClick(object sender, RoutedEventArgs e)
    {
        if (_abStart is null) _abStart = TimeSpan.Zero;
        if (_playback.Position <= _abStart) { StatusText.Text = L("StatusBMustFollowA"); return; }
        TryPlayback(() => _playback.SetAbLoop(_abStart, _playback.Position)); StatusText.Text = F("StatusAbRepeat", FormatTime(_abStart.Value), FormatTime(_playback.Position));
    }
    private void OnClearAbClick(object sender, RoutedEventArgs e) { _abStart = null; if (_playback.IsAvailable) _playback.SetAbLoop(null, null); StatusText.Text = L("StatusAbCleared"); }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_initialized && _playback.IsAvailable) { TryPlayback(() => _playback.SetVolume(e.NewValue)); ShowVolumeOverlay(); }
    }

    private void ShowVolumeOverlay()
    {
        if (!_playback.IsAvailable) return;
        var percent = double.IsFinite(_playback.Volume) ? Math.Clamp(_playback.Volume, 0, 130) : 0;
        var roundedPercent = Math.Round(percent, MidpointRounding.AwayFromZero);
        TryPlayback(() => _playback.ShowOsdText($"Volume:{roundedPercent:0}", 1.5));
    }

    private void ShowMuteOverlay()
    {
        if (!_playback.IsAvailable) return;
        TryPlayback(() => _playback.ShowOsdText(L(_playback.IsMuted ? "OsdMuteOn" : "OsdMuteOff"), 1.5));
    }

    private void ShowSubtitleVisibilityOverlay()
    {
        if (!_playback.IsAvailable) return;
        TryPlayback(() => _playback.ShowOsdText(L(_playback.AreSubtitlesVisible ? "OsdSubtitlesOn" : "OsdSubtitlesOff"), 1.5));
    }

    private void OnPositionSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_positionSliderDragging)
        {
            PositionText.Text = $"{FormatTime(TimeSpan.FromSeconds(e.NewValue))} / {FormatTime(_playback.Duration)}";
            return;
        }

        if (!_updatingPosition && !_positionSliderDragging && _playback.IsAvailable && PositionSlider.Maximum > 0) SeekAndRestartAi(TimeSpan.FromSeconds(e.NewValue), () => _playback.Seek(TimeSpan.FromSeconds(e.NewValue)));
    }

    private void OnPositionSliderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _positionSliderDragging = true;
        PositionText.Text = $"{FormatTime(TimeSpan.FromSeconds(PositionSlider.Value))} / {FormatTime(_playback.Duration)}";
    }
    private void OnPositionSliderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_positionSliderDragging) return;
        _positionSliderDragging = false;
        var position = TimeSpan.FromSeconds(PositionSlider.Value);
        PositionText.Text = $"{FormatTime(position)} / {FormatTime(_playback.Duration)}";
        if (_playback.IsAvailable) SeekAndRestartAi(position, () => _playback.Seek(position, true));
    }

    private async void OnLoadSubtitleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var extension in new[] { ".srt", ".vtt", ".ass", ".ssa", ".smi" }) picker.FileTypeFilter.Add(extension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await LoadSubtitleFromPathAsync(file.Path);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "file-picker", "OPEN_SUBTITLE_PICKER_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("SubtitleErrorTitle"), exception.Message);
        }
    }

    private async Task LoadSubtitleFromPathAsync(string path)
    {
        await CancelAiPipelineAsync();
        if (!await ConfirmDiscardChangesAsync(L("ActionLoadSubtitle"))) return;
        try
        {
            var document = await _subtitleFiles.LoadAsync(path, _settings.Subtitle.Encoding);
            BindDocument(document);
            _aiWorkflow.ResetTranslation();
            // Keep the native player and the editable document on one subtitle track.
            // Loading the source file directly left the old track alive when the editor
            // later switched to its temporary ASS overlay.
            ScheduleSubtitleOverlaySync();
            StatusText.Text = F("StatusSubtitlesLoaded", document.ActiveTrack?.Cues.Count ?? 0);
        }
        catch (Exception exception) { await ShowMessageAsync(L("SubtitleErrorTitle"), exception.Message); }
    }

    private async void OnSaveSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (_document.FilePath is null) await SaveSubtitleAsAsync(); else await SaveSubtitleAsync(_document.FilePath);
    }
    private async void OnSaveSubtitleAsClick(object sender, RoutedEventArgs e) => await SaveSubtitleAsAsync();

    private async Task SaveSubtitleAsAsync()
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_playback.CurrentSource ?? "subtitles") };
            picker.FileTypeChoices.Add(L("SubRipFileType"), [".srt"]);
            picker.FileTypeChoices.Add(L("WebVttFileType"), [".vtt"]);
            picker.FileTypeChoices.Add(L("AssFileType"), [".ass"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is not null) await SaveSubtitleAsync(file.Path);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "file-picker", "SAVE_SUBTITLE_PICKER_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("SubtitleErrorTitle"), exception.Message);
        }
    }

    private async Task SaveSubtitleAsync(string path)
    {
        var track = _document.ActiveTrack;
        if (track is null) return;
        try
        {
            var displayMode = _subtitleDisplayMode ?? SubtitleDisplayMode.Original;
            var result = await _subtitleFiles.SaveAsync(track, path, displayMode, _settings.Subtitle.FontFamily, _settings.Subtitle.Encoding);
            track.Format = result.TargetFormat;
            _document.MarkSaved(path);
            StatusText.Text = result.HasStyleLoss ? F("StatusSavedStyleLoss", path) : F("StatusSaved", path);
        }
        catch (Exception exception) { await ShowMessageAsync(L("SaveErrorTitle"), exception.Message); }
    }

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

    private void BindDocument(SubtitleDocument document)
    {
        _subtitleOverlay.ResetForDocument();
        _document = document;
        _subtitleDisplayMode = null;
        _selectedNativeSubtitleTrackId = null;
        var track = _document.EnsureTrack();
        if (track.Cues.Count > 0) _subtitleDisplayMode = SubtitleDisplayMode.Original;
        if (_document.FilePath is null && track.Cues.Count == 0) _document.MarkSaved();
        _subtitleEditor.BindDocument(document);
        RefreshSubtitleTrackSelector();
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        var state = _playback.State;
        UpdatePlaybackPowerRequirement(state);
        PlayPauseIcon.Source = PlaybackIconSource(state == PlaybackState.Playing ? "pause" : "play");
        StatusText.Text = _audioTagStatusText is { } audioTag && state != PlaybackState.Failed ? audioTag : L(state switch
        {
            PlaybackState.Playing => "PlaybackStatePlaying",
            PlaybackState.Paused => "PlaybackStatePaused",
            PlaybackState.Loading => "PlaybackStateLoading",
            PlaybackState.Idle => "PlaybackStateIdle",
            PlaybackState.Failed => "PlaybackStateFailed",
            _ => "PlaybackStateUninitialized"
        });
        if (state == PlaybackState.Playing)
        {
            // show-text has a finite lifetime. Re-arm the current cue after a
            // pause/resume so a long pause cannot make it disappear permanently.
            _subtitleOverlay.InvalidateGeneratedCue();
        }
        RefreshGeneratedSubtitleOsd(CurrentPlaybackPositionMicroseconds);
    });

    private void UpdatePlaybackPowerRequirement(PlaybackState state)
    {
        if (_playbackPowerManagementDisabled) return;
        var shouldKeepAwake = state == PlaybackState.Playing;
        if (shouldKeepAwake == _playbackPowerRequirementActive) return;
        if (!_windowsPowerManagement.TrySetPlaybackActive(shouldKeepAwake))
        {
            _ = AppLog.WriteAsync("warning", "playback", "PLAYBACK_POWER_REQUEST_ERROR",
                shouldKeepAwake
                    ? "Windows could not keep the display and system awake during playback."
                    : "Windows could not release the playback power request.");
            return;
        }

        _playbackPowerRequirementActive = shouldKeepAwake;
    }

    private void ReleasePlaybackPowerRequirement()
    {
        _playbackPowerManagementDisabled = true;
        if (!_playbackPowerRequirementActive) return;
        if (!_windowsPowerManagement.TrySetPlaybackActive(false))
            _ = AppLog.WriteAsync("warning", "playback", "PLAYBACK_POWER_REQUEST_ERROR", "Windows could not release the playback power request.");
        _playbackPowerRequirementActive = false;
    }

    private void OnFirstFrameReady(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (string.Equals(_firstFrameWaitSource, _playback.CurrentSource, StringComparison.OrdinalIgnoreCase))
        {
            _firstFrameUiReadyForMedia = true;
            _firstFrameWaiter?.TrySetResult();
        }
        ApplySubtitleVisibilityPreference();
        StartPostOpenWorkIfReady();
        StartAutomaticSubtitleGenerationIfReady();
    });

    private void StartAutomaticSubtitleGenerationIfReady()
    {
        if (!_mediaOpenReady || !_firstFrameUiReadyForMedia ||
            !string.Equals(_currentMediaSource?.Location, _playback.CurrentSource, StringComparison.OrdinalIgnoreCase)) return;
        if (_playback.State == PlaybackState.Playing && !_aiWorkflow.IsSeekRestartPending)
            StartCheckedAiPipeline(waitForMediaReady: true);
    }

    private async Task<bool> WaitForFirstFrameAsync(string source)
    {
        if (!_mediaOpenReady || !string.Equals(_playback.CurrentSource, source, StringComparison.OrdinalIgnoreCase)) return false;
        if (!_firstFrameUiReadyForMedia)
        {
            var waiter = _firstFrameWaiter;
            if (waiter is null || !string.Equals(_firstFrameWaitSource, source, StringComparison.OrdinalIgnoreCase)) return false;
            try { await waiter.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (TimeoutException) { return false; }
            catch (OperationCanceledException) { return false; }
        }
        return _mediaOpenReady && _firstFrameUiReadyForMedia &&
               _playback.IsFirstFrameReady &&
               string.Equals(_playback.CurrentSource, source, StringComparison.OrdinalIgnoreCase);
    }
    private void OnPlaybackPositionChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _playbackPositionUiRefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RefreshPlaybackPositionUi))
            Interlocked.Exchange(ref _playbackPositionUiRefreshQueued, 0);
    }

    private void OnPlaybackSeeked(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
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
        if (_subtitleDisplayMode is not null && _document.ActiveTrack is { Cues.Count: > 0 } &&
            !_playback.RestoreEditorSubtitleAfterSeek())
            ScheduleSubtitleOverlaySync(force: true);
    });

    private void RefreshPlaybackPositionUi()
    {
        try
        {
            var position = _playback.Position;
            var duration = _playback.Duration;
            _updatingPosition = true;
            PositionSlider.Maximum = Math.Max(1, duration.TotalSeconds);
            if (!_positionSliderDragging) PositionSlider.Value = Math.Clamp(position.TotalSeconds, 0, PositionSlider.Maximum);
            _updatingPosition = false;
            PositionText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
            DecoderText.Text = _playback.DecoderDescription ?? string.Empty;
            ResolutionText.Text = _playback.VideoWidth is { } width && _playback.VideoHeight is { } height ? $"{width}×{height}" : string.Empty;
            var positionMicroseconds = Math.Max(0, position.Ticks / 10);
            _subtitleEditor.UpdatePlaybackPosition(positionMicroseconds);
            RefreshGeneratedSubtitleOsd(positionMicroseconds);
        }
        finally { Interlocked.Exchange(ref _playbackPositionUiRefreshQueued, 0); }
    }
    private void OnMediaEnded(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_repeatMode == RepeatMode.AutoAdvance && _playlistIndex >= 0 && _playlistIndex < _playlist.Count - 1)
            await OpenAdjacentMediaAsync(1);
    });
    private void OnTracksChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        ApplySubtitleVisibilityPreference();
        AudioTrackCombo.ItemsSource = _playback.Tracks.Where(t => t.Type == MediaTrackType.Audio).ToArray(); AudioTrackCombo.SelectedItem = _playback.Tracks.FirstOrDefault(t => t.Type == MediaTrackType.Audio && t.IsSelected);
        RefreshSubtitleTrackSelector();
    });
    private void OnAudioTrackChanged(object sender, SelectionChangedEventArgs e) { if (AudioTrackCombo.SelectedItem is MediaTrack track) TryPlayback(() => _playback.SelectTrack(MediaTrackType.Audio, track.Id)); }
    private void OnSubtitleTrackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSubtitleTrackSelector || SubtitleTrackCombo.SelectedItem is not SubtitleSelectionOption option) return;
        if (option.DisplayMode is { } displayMode)
        {
            SetSubtitleDisplayMode(displayMode, refreshOverlay: true);
            return;
        }

        if (option.TrackId is not { } trackId) return;
        if (_subtitleOverlay.IsGeneratedOverlayActive)
        {
            _subtitleOverlay.DisableGeneratedOverlay();
        }
        _subtitleDisplayMode = null;
        _selectedNativeSubtitleTrackId = trackId;
        TryPlayback(() => _playback.SelectTrack(MediaTrackType.Subtitle, trackId));
    }

    private void RefreshSubtitleTrackSelector()
    {
        var options = new List<SubtitleSelectionOption>();
        if (_document.ActiveTrack is { Cues.Count: > 0 } || _subtitleDisplayMode is not null)
        {
            options.Add(new SubtitleSelectionOption(L("SubtitleOptionOriginal"), SubtitleDisplayMode.Original, null));
            options.Add(new SubtitleSelectionOption(L("SubtitleOptionTranslation"), SubtitleDisplayMode.Translation, null));
            options.Add(new SubtitleSelectionOption(L("SubtitleOptionBoth"), SubtitleDisplayMode.OriginalAndTranslation, null));
        }
        options.AddRange(_playback.Tracks
            .Where(track => track.Type == MediaTrackType.Subtitle)
            .Select(track => new SubtitleSelectionOption(track.DisplayName, null, track.Id)));

        _updatingSubtitleTrackSelector = true;
        try
        {
            SubtitleTrackCombo.ItemsSource = options;
            SubtitleTrackCombo.SelectedItem = _subtitleDisplayMode is { } displayMode
                ? options.FirstOrDefault(option => option.DisplayMode == displayMode)
                : _selectedNativeSubtitleTrackId is { } trackId
                    ? options.FirstOrDefault(option => option.TrackId == trackId)
                    : options.FirstOrDefault(option => option.TrackId is not null && _playback.Tracks.Any(track => track.Id == option.TrackId && track.IsSelected));
        }
        finally { _updatingSubtitleTrackSelector = false; }
    }

    private void SetSubtitleDisplayMode(SubtitleDisplayMode displayMode, bool refreshOverlay)
    {
        _subtitleDisplayMode = displayMode;
        _selectedNativeSubtitleTrackId = null;
        RefreshSubtitleTrackSelector();
        if (refreshOverlay && _document.ActiveTrack is { Cues.Count: > 0 }) ScheduleSubtitleOverlaySync(force: true);
    }

    private void OnPlaybackError(object? sender, PlaybackError e)
    {
        _ = AppLog.WriteAsync("error", "playback", e.Code, e.Message, e.Exception);
        DispatcherQueue.TryEnqueue(() => StatusText.Text = $"{e.Code}: {e.Message}");
    }

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Space || e.OriginalSource is TextBox or PasswordBox) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!_settings.General.Shortcuts.TryGetValue(ShortcutActions.PlayPause, out var gesture) || !ShortcutGesture.Matches(gesture, e.Key.ToString(), ctrl, shift, alt)) return;
        e.Handled = true;
        FocusPlaybackSurface();
        OnPlayPauseClick(this, new RoutedEventArgs());
    }

    private void FocusPlaybackSurface()
    {
        VideoFocusTarget.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => VideoFocusTarget.Focus(FocusState.Programmatic));
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space && e.Handled) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var key = e.Key.ToString();
        var playbackHasFocus = VideoFocusTarget.FocusState != FocusState.Unfocused;
        if (e.Key == Windows.System.VirtualKey.Escape && _fullscreen.IsFullscreen) { _fullscreen.Exit(); UpdateFullscreenButton(); e.Handled = true; return; }
        var isTextInput = e.OriginalSource is TextBox or PasswordBox;
        if (ctrl && shift && !alt && e.Key == Windows.System.VirtualKey.N) { PlayFromBeginning(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Enter) { ToggleFullscreen(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.F) { ToggleFullscreen(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.F11) { ToggleFullscreen(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.M) { OnMuteClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (playbackHasFocus && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Up) { TryPlayback(() => _playback.SetVolume(_playback.Volume + 5)); VolumeSlider.Value = _playback.Volume; e.Handled = true; return; }
        if (playbackHasFocus && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Down) { TryPlayback(() => _playback.SetVolume(_playback.Volume - 5)); VolumeSlider.Value = _playback.Volume; e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Back) { PlayFromBeginning(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Home) { SeekAndRestartAi(TimeSpan.Zero, () => _playback.Seek(TimeSpan.Zero, true)); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.End) { SeekAndRestartAi(_playback.Duration, () => _playback.Seek(_playback.Duration, true)); e.Handled = true; return; }
        bool Is(string action) => _settings.General.Shortcuts.TryGetValue(action, out var gesture) && ShortcutGesture.Matches(gesture, key, ctrl, shift, alt);
        var save = Is(ShortcutActions.SaveSubtitle);
        var saveAs = Is(ShortcutActions.SaveSubtitleAs);
        var close = Is(ShortcutActions.CloseWindow);
        var playPauseAlternate = Is(ShortcutActions.PlayPauseAlternate);
        var playFromBeginning = Is(ShortcutActions.PlayFromBeginning);
        var previousMedia = Is(ShortcutActions.PreviousMedia);
        var nextMedia = Is(ShortcutActions.NextMedia);
        var toggleTimelinePanel = Is(ShortcutActions.ToggleTimelinePanel);
        var toggleSidePanel = Is(ShortcutActions.ToggleSidePanel);
        if (isTextInput && !save && !saveAs && !close && !playPauseAlternate && !playFromBeginning && !previousMedia && !nextMedia && !toggleTimelinePanel && !toggleSidePanel) return;
        if (close) OnExitClick(this, new RoutedEventArgs());
        else if (saveAs) OnSaveSubtitleAsClick(this, new RoutedEventArgs());
        else if (save) OnSaveSubtitleClick(this, new RoutedEventArgs());
        else if (playFromBeginning) PlayFromBeginning();
        else if (Is(ShortcutActions.PlayPause) || playPauseAlternate) OnPlayPauseClick(this, new RoutedEventArgs());
        else if (previousMedia) OnPreviousMediaClick(this, new RoutedEventArgs());
        else if (nextMedia) OnNextMediaClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.PreviousSubtitle)) SelectRelativeCue(-1);
        else if (Is(ShortcutActions.NextSubtitle)) SelectRelativeCue(1);
        else if (Is(ShortcutActions.SeekBackward)) OnSeekBackClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.SeekForward)) OnSeekForwardClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.Undo)) OnUndoClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.Redo)) OnRedoClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.DeleteCue)) OnDeleteCueClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.Fullscreen)) ToggleFullscreen();
        else if (Is(ShortcutActions.ToggleSubtitles)) { SubtitleVisibilityMenuItem.IsChecked = !_playback.AreSubtitlesVisible; OnToggleSubtitleVisibilityClick(this, new RoutedEventArgs()); }
        else if (toggleTimelinePanel) { ShowBottomPanelMenuItem.IsChecked = !_panels.IsBottomVisible; OnToggleBottomPanelClick(this, new RoutedEventArgs()); }
        else if (toggleSidePanel) { ShowRightPanelMenuItem.IsChecked = !_panels.IsRightVisible; OnToggleRightPanelClick(this, new RoutedEventArgs()); }
        else return;
        e.Handled = true;
    }
    private void SelectRelativeCue(int delta)
        => _subtitleEditor.SelectRelativeCue(delta);

    private void OnPreviousSubtitleClick(object sender, RoutedEventArgs e) => SelectRelativeCue(-1);
    private void OnNextSubtitleClick(object sender, RoutedEventArgs e) => SelectRelativeCue(1);
    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void UpdateShortcutHints()
    {
        string Shortcut(string action) => _settings.General.Shortcuts.TryGetValue(action, out var gesture) ? gesture : string.Empty;
        static string Combine(params string[] gestures) => string.Join(" / ", gestures.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));

        SaveSubtitleMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.SaveSubtitle);
        SaveSubtitleAsMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.SaveSubtitleAs);
        ExitMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.CloseWindow);
        PlayPauseMenuItem.KeyboardAcceleratorTextOverride = Combine(Shortcut(ShortcutActions.PlayPause), Shortcut(ShortcutActions.PlayPauseAlternate));
        DeleteCueMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.DeleteCue);
        PreviousSubtitleMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.PreviousSubtitle);
        NextSubtitleMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.NextSubtitle);
        UndoMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.Undo);
        RedoMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.Redo);
        SubtitleVisibilityMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.ToggleSubtitles);
        ShowBottomPanelMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.ToggleTimelinePanel);
        ShowRightPanelMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.ToggleSidePanel);
        FullscreenMenuItem.KeyboardAcceleratorTextOverride = $"{Combine(Shortcut(ShortcutActions.Fullscreen), "Enter", "F", "F11")} · Esc";

        ToolTipService.SetToolTip(BottomPanelToggleButton, F("TooltipToggleBottomPanel", Shortcut(ShortcutActions.ToggleTimelinePanel)));
        ToolTipService.SetToolTip(RightPanelToggleButton, F("TooltipToggleRightPanel", Shortcut(ShortcutActions.ToggleSidePanel)));
        AutomationProperties.SetName(BottomPanelToggleButton, L("ShowBottomPanel.Text"));
        AutomationProperties.SetName(RightPanelToggleButton, L("ShowRightPanel.Text"));

        ToolTipService.SetToolTip(PlayPauseButton, $"{L("PlayPause.Text")} ({Combine(Shortcut(ShortcutActions.PlayPause), Shortcut(ShortcutActions.PlayPauseAlternate))})");
        ToolTipService.SetToolTip(BeginningButton, L("TooltipBeginning"));
        ToolTipService.SetToolTip(PreviousButton, F("TooltipPreviousMedia", Shortcut(ShortcutActions.PreviousMedia)));
        ToolTipService.SetToolTip(NextButton, F("TooltipNextMedia", Shortcut(ShortcutActions.NextMedia)));
        ToolTipService.SetToolTip(SeekBackButton, F("TooltipSeekBackward", _settings.Playback.SeekIntervalSeconds, Shortcut(ShortcutActions.SeekBackward)));
        ToolTipService.SetToolTip(SeekForwardButton, F("TooltipSeekForward", _settings.Playback.SeekIntervalSeconds, Shortcut(ShortcutActions.SeekForward)));
        ToolTipService.SetToolTip(StopButton, L("Stop.Text"));
        ToolTipService.SetToolTip(MuteButton, L("TooltipMute"));
        ToolTipService.SetToolTip(VolumeSlider, L("TooltipVolume"));
        ToolTipService.SetToolTip(PositionSlider, F("TooltipPosition", Shortcut(ShortcutActions.PlayFromBeginning)));
        ToolTipService.SetToolTip(SubtitleList, F("TooltipSubtitleNavigation", Shortcut(ShortcutActions.PreviousSubtitle), Shortcut(ShortcutActions.NextSubtitle)));
        ToolTipService.SetToolTip(RepeatButton, L(_repeatMode switch { RepeatMode.One => "TooltipRepeatCurrent", RepeatMode.AutoAdvance => "TooltipAutoAdvance", _ => "TooltipRepeatOff" }));
        ToolTipService.SetToolTip(CloseButton, F("TooltipClose", Shortcut(ShortcutActions.CloseWindow)));
        UpdateFullscreenButton();
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_playback.State is PlaybackState.Playing or PlaybackState.Paused) TryPlayback(_playback.TogglePause);
        e.Handled = true;
    }
    private void OnToggleRightPanelClick(object sender, RoutedEventArgs e) { _panels.IsRightVisible = ShowRightPanelMenuItem.IsChecked; ApplyPanelVisibility(); }
    private void OnToggleBottomPanelClick(object sender, RoutedEventArgs e) { _panels.IsBottomVisible = ShowBottomPanelMenuItem.IsChecked; ApplyPanelVisibility(); }
    private void OnRightPanelToggleButtonClick(object sender, RoutedEventArgs e) { _panels.IsRightVisible = RightPanelToggleButton.IsChecked == true; ApplyPanelVisibility(); }
    private void OnBottomPanelToggleButtonClick(object sender, RoutedEventArgs e) { _panels.IsBottomVisible = BottomPanelToggleButton.IsChecked == true; ApplyPanelVisibility(); }

    private void ToggleFullscreen()
    {
        try { _fullscreen.Toggle(); }
        finally { UpdateFullscreenButton(); }
    }

    private void UpdateFullscreenButton()
    {
        FullscreenButton.IsChecked = _fullscreen.IsFullscreen;
        FullscreenButtonIcon.Glyph = _fullscreen.IsFullscreen ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(FullscreenButton, L(_fullscreen.IsFullscreen ? "TooltipExitFullscreen" : "TooltipEnterFullscreen"));
    }

    private void ApplyPanelVisibility()
    {
        if (_fullscreen.IsFullscreen) return;
        _panels.Apply(_initialized);
    }

    private void RefreshRightPanelSections()
    {
        var selectedIndex = Math.Max(0, RightPanelSectionList.SelectedIndex);
        RightPanelSectionList.ItemsSource = new[]
        {
            new RightPanelSectionEntry("\uE8B7", L("RightPanelExplorer")),
            new RightPanelSectionEntry("\uE142", L("RightPanelPlaylist")),
            new RightPanelSectionEntry("\uE774", L("RightPanelWebDav")),
            new RightPanelSectionEntry("\uE734", L("RightPanelFavorites")),
            new RightPanelSectionEntry("\uE8C1", L("RightPanelSubtitles"))
        };
        PlaylistTitleText.Text = L("RightPanelPlaylist");
        FavoritesTitleText.Text = L("RightPanelFavorites");
        SubtitlesTitleText.Text = L("RightPanelSubtitles");
        RightPanelSectionList.SelectedIndex = Math.Clamp(selectedIndex, 0, 4);
        ApplyRightPanelSection((RightPanelSection)RightPanelSectionList.SelectedIndex);
    }

    private void OnRightPanelSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RightPanelSectionList.SelectedIndex >= 0) ApplyRightPanelSection((RightPanelSection)RightPanelSectionList.SelectedIndex);
    }

    private void ShowRightPanelSection(RightPanelSection section)
    {
        _panels.IsRightVisible = true;
        ApplyPanelVisibility();
        RightPanelSectionList.SelectedIndex = (int)section;
        ApplyRightPanelSection(section);
    }

    private void ApplyRightPanelSection(RightPanelSection section)
    {
        ExplorerSection.Visibility = section == RightPanelSection.Explorer ? Visibility.Visible : Visibility.Collapsed;
        PlaylistSection.Visibility = section == RightPanelSection.Playlist ? Visibility.Visible : Visibility.Collapsed;
        WebDavSection.Visibility = section == RightPanelSection.WebDav ? Visibility.Visible : Visibility.Collapsed;
        FavoritesSection.Visibility = section == RightPanelSection.Favorites ? Visibility.Visible : Visibility.Collapsed;
        SubtitlesSection.Visibility = section == RightPanelSection.Subtitles ? Visibility.Visible : Visibility.Collapsed;
        if (_initialized && section == RightPanelSection.Favorites) _ = LoadFavoritesForDisplayAsync();
    }

    private async Task LoadFavoritesForDisplayAsync()
    {
        try
        {
            await _historyService.LoadFavoritesAsync();
            RefreshFavoritesList();
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "favorites", "FAVORITES_LOAD_ERROR", exception.Message, exception);
        }
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fullscreen.IsFullscreen) return;
        UpdateTitleBarDragRegion();
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
    private async void OnAddWebDavServerRequested(object? sender, EventArgs e)
    {
        try
        {
            var name = CreateWebDavTextBox(L("NameHeader"), string.Empty);
            var address = CreateWebDavTextBox(L("AddressHeader"), string.Empty, "https://server.example/dav/");
            var port = new NumberBox
            {
                Header = L("PortHeader"),
                Value = 443,
                Minimum = 1,
                Maximum = 65535,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden
            };
            var username = CreateWebDavTextBox(L("UsernameHeader"), string.Empty);
            var password = new PasswordBox { Header = L("PasswordHeader") };
            var validation = new TextBlock
            {
                Foreground = ThemeBrush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(255, 196, 43, 28)),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            var panel = new StackPanel { Spacing = 12, Width = 440, Children = { name, address, port, username, password, validation } };
            var dialog = CreateDialog(L("AddWebDavServerTitle"), panel, L("SaveButtonText"));
            Uri? parsedAddress = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (Uri.TryCreate(address.Text.Trim(), UriKind.Absolute, out parsedAddress) &&
                    parsedAddress.Scheme is "http" or "https" &&
                    !double.IsNaN(port.Value) && port.Value % 1 == 0 && port.Value is >= 1 and <= 65535) return;
                args.Cancel = true;
                validation.Text = L("InvalidWebDavAddressMessage");
                validation.Visibility = Visibility.Visible;
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || parsedAddress is null) return;

            var server = new WebDavServerSettings { Name = string.IsNullOrWhiteSpace(name.Text) ? parsedAddress.Host : name.Text.Trim() };
            _webDavCredentials.Save(server.Id, new WebDavConnectionCredential(WebDavConnectionCredential.NormalizeAddress(parsedAddress), (int)port.Value, username.Text.Trim(), password.Password));
            _settings.Network.WebDavServers.Add(server);
            await SettingsService.CreateDefault().SaveAsync(_settings);
            _panels.IsRightVisible = true;
            ApplyPanelVisibility();
            ShowRightPanelSection(RightPanelSection.WebDav);
            RefreshWebDavServerList(server);
            await WebDavBrowser.ConnectAsync(server);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "webdav", "WEBDAV_ADD_ERROR", exception.Message, exception);
            WebDavBrowser.SetStatus(exception.Message);
        }
    }

    private static TextBox CreateWebDavTextBox(string header, string text, string? placeholder = null) => new()
    {
        Header = header,
        Text = text,
        PlaceholderText = placeholder ?? string.Empty,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false
    };

    private void RefreshWebDavServerList(WebDavServerSettings? selected = null)
    {
        WebDavBrowser.SetServers(_settings.Network.WebDavServers, selected);
    }

    private async void OnDeleteWebDavServerRequested(object? sender, WebDavServerEventArgs e)
    {
        var server = e.Server;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = L("DeleteWebDavServerTitle"),
            Content = new TextBlock { Text = F("DeleteWebDavServerMessage", server.Name), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = L("DeleteButtonText"),
            CloseButtonText = L("CancelButtonText"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        try
        {
            WebDavBrowser.Disconnect(server.Id);
            _settings.Network.WebDavServers.RemoveAll(candidate => candidate.Id == server.Id);
            _webDavCredentials.Delete(server.Id);
            await SettingsService.CreateDefault().SaveAsync(_settings);
            RefreshWebDavServerList();
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "webdav", "WEBDAV_DELETE_ERROR", exception.Message, exception);
            WebDavBrowser.SetStatus(exception.Message);
        }
    }

    private Task ConnectWebDavServerAsync(WebDavServerSettings server, Uri? directory = null) =>
        WebDavBrowser.ConnectAsync(server, directory);

    private async void OnCameraClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_cameraWindow is not null) { _cameraWindow.Activate(); return; }
            _cameraWindow = new CameraWindow(this);
            _cameraWindow.Closed += (_, _) => _cameraWindow = null;
            _cameraWindow.Activate();
        }
        catch (Exception exception)
        {
            _cameraWindow = null;
            await AppLog.WriteAsync("error", "camera", "CAMERA_WINDOW_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("CameraErrorTitle"), exception.Message);
        }
    }

    private async void OnWindowsCaptionsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_windowsCaptionWindow is not null) { _windowsCaptionWindow.Activate(); return; }
            _appWindow?.Hide();
            _windowsCaptionWindow = new WindowsCaptionWindow(this);
            _windowsCaptionWindow.Closed += (_, _) =>
            {
                _windowsCaptionWindow = null;
                if (_closeInProgress || _allowClose) return;
                _appWindow?.Show();
                Activate();
            };
            _windowsCaptionWindow.Activate();
        }
        catch (Exception exception)
        {
            _windowsCaptionWindow = null;
            _appWindow?.Show();
            await AppLog.WriteAsync("error", "captions", "WINDOWS_CAPTION_WINDOW_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("WindowsCaptionErrorTitle"), exception.Message);
        }
    }
    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.SettingsSaved += (_, settings) =>
            {
                _settings = settings;
                MediaBrowser.DefaultDirectory = settings.General.DefaultFolder;
                RefreshWebDavServerList();
                LocalizationService.Apply(settings.General.Language);
                UiFontService.Apply(settings.General.UiFontFamily, RootGrid);
                ApplyTheme(settings.General.Theme);
                RefreshFavoritesList();
                RefreshRightPanelSections();
                UpdateShortcutHints();
                if (!_playback.IsAvailable) return;
                TryPlayback(() =>
                {
                    _playback.SetVolume(settings.Playback.DefaultVolume);
                    _playback.SetRate(settings.Playback.PlaybackRate);
                    _playback.ConfigureNetwork(TimeSpan.FromSeconds(settings.Network.TimeoutSeconds), settings.Network.Proxy);
                    _playback.ConfigurePreferredLanguages(settings.Playback.DefaultAudioLanguage, settings.Playback.DefaultSubtitleLanguage);
                    _playback.ConfigureSubtitleStyle(settings.Subtitle.FontFamily, settings.Subtitle.FontSize, settings.Subtitle.Color, settings.Subtitle.Background, settings.Subtitle.Outline, settings.Subtitle.BottomMargin);
                    _playback.ConfigureRtxVideoSuperResolution(settings.Playback.RtxVideoSuperResolution);
                });
                ScheduleSubtitleOverlaySync();
            };
            _settingsWindow.DllUnloadRequested += (_, _) =>
            {
                _settingsWindow?.Close();
                Close();
            };
            _settingsWindow.RestartRequested += (_, _) =>
            {
                _restartRequested = true;
                _settingsWindow?.Close();
                Close();
            };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Activate();
        }
        catch (Exception exception)
        {
            _settingsWindow = null;
            await AppLog.WriteAsync("error", "settings", "SETTINGS_WINDOW_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("SettingsErrorTitle"), exception.Message);
        }
    }

    private async void OnAboutClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            header.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/app.png")),
                Width = 64,
                Height = 64,
                VerticalAlignment = VerticalAlignment.Center
            });
            var nameVersion = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            nameVersion.Children.Add(new TextBlock { Text = "AIMediaWorker", FontSize = 20, FontWeight = FontWeights.SemiBold });
            nameVersion.Children.Add(new TextBlock { Text = F("AboutVersion", GetAppVersion()), Opacity = 0.7 });
            header.Children.Add(nameVersion);

            var github = new HyperlinkButton { Content = "https://github.com/kirinonakar/AIMediaWorker", HorizontalAlignment = HorizontalAlignment.Left };
            github.Click += async (_, _) =>
            {
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/kirinonakar/AIMediaWorker")); }
                catch (Exception exception) { await AppLog.WriteAsync("error", "about", "OPEN_GITHUB_ERROR", exception.Message, exception); }
            };

            var licenses = new Expander
            {
                Header = L("AboutThirdPartyLicenses"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer
                {
                    MaxHeight = 220,
                    Content = new TextBlock { Text = ThirdPartyLicensesText, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12, Opacity = 0.85 }
                }
            };

            var content = new StackPanel { Spacing = 12, Width = 440 };
            content.Children.Add(header);
            content.Children.Add(github);
            content.Children.Add(licenses);

            await ShowDialogAsync(new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = L("AboutTitle"), Content = content, CloseButtonText = L("CloseButton") });
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "about", "ABOUT_DIALOG_ERROR", exception.Message, exception);
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
        }
    }

    private const string ThirdPartyLicensesText =
        "NAudio 2.2.1 — MIT License\nhttps://github.com/naudio/NAudio\n\n" +
        "Windows App SDK 2.4.0 — MIT License\nhttps://github.com/microsoft/WindowsAppSDK\n\n" +
        "System.Security.Cryptography.ProtectedData 10.0.0 — MIT License\nhttps://www.nuget.org/packages/System.Security.Cryptography.ProtectedData\n\n" +
        "libmpv / mpv — GPLv2+ (build-dependent)\nhttps://github.com/mpv-player/mpv\n\n" +
        "FFmpeg — LGPLv2.1+ (build-dependent)\nhttps://ffmpeg.org\n\n" +
        "Silero VAD — MIT License\nhttps://github.com/snakers4/silero-vad\n\n" +
        "Qwen3-ASR — Apache License 2.0\nhttps://github.com/QwenLM/Qwen3-ASR";

    private void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch { AppTheme.Light => ElementTheme.Light, AppTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyTitleBarTheme(RootGrid.ActualTheme);
        UpdatePlaybackIcons();
    }

    private SvgImageSource PlaybackIconSource(string name) => new()
    {
        UriSource = new Uri($"ms-appx:///Assets/Playback/{name}{(RootGrid.ActualTheme == ElementTheme.Dark ? "-dark" : string.Empty)}.svg")
    };

    private void UpdatePlaybackIcons()
    {
        BeginningIcon.Source = PlaybackIconSource("beginning");
        PreviousIcon.Source = PlaybackIconSource("previous");
        SeekBackIcon.Source = PlaybackIconSource("seek-back");
        PlayPauseIcon.Source = PlaybackIconSource(_playback.State == PlaybackState.Playing ? "pause" : "play");
        StopIcon.Source = PlaybackIconSource("stop");
        SeekForwardIcon.Source = PlaybackIconSource("seek-forward");
        NextIcon.Source = PlaybackIconSource("next");
        MuteIcon.Source = PlaybackIconSource(_playback.IsMuted ? "mute" : "volume");
        RepeatIcon.Source = PlaybackIconSource(_repeatMode switch { RepeatMode.One => "repeat-one", RepeatMode.AutoAdvance => "repeat-auto", _ => "repeat" });
        UpdatePanelToggleIcons();
    }

    private void UpdatePanelToggleIcons()
    {
        BottomPanelToggleIcon.Source = PanelToggleIconSource("bottom-panel", _panels.IsBottomVisible);
        RightPanelToggleIcon.Source = PanelToggleIconSource("right-panel", _panels.IsRightVisible);
    }

    private SvgImageSource PanelToggleIconSource(string name, bool isOpen) => new()
    {
        UriSource = new Uri($"ms-appx:///Assets/Panels/{name}{(isOpen ? string.Empty : "-closed")}{(RootGrid.ActualTheme == ElementTheme.Dark ? "-dark" : string.Empty)}.svg")
    };

    private void OnRootActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTitleBarTheme(sender.ActualTheme);
        UpdatePlaybackIcons();
    }

    private void ApplyTitleBarTheme(ElementTheme theme)
    {
        if (_appWindow?.TitleBar is not { } titleBar) return;
        var dark = theme == ElementTheme.Dark;
        var background = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 24, 24, 24);
        var inactiveForeground = dark ? Windows.UI.Color.FromArgb(255, 160, 160, 160) : Windows.UI.Color.FromArgb(255, 110, 110, 110);
        var hover = dark ? Windows.UI.Color.FromArgb(255, 58, 58, 58) : Windows.UI.Color.FromArgb(255, 224, 224, 224);
        var pressed = dark ? Windows.UI.Color.FromArgb(255, 72, 72, 72) : Windows.UI.Color.FromArgb(255, 208, 208, 208);
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

    private void UpdateTitleBarDragRegion()
    {
        if (_appWindow?.TitleBar is not { } titleBar) return;
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var left = 0.0;
        var top = 0.0;
        var right = 0.0;
        var bottom = 0.0;
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
        {
            // When maximized, the client area is inset by the invisible resize border.
            var border = 8 * scale;
            left = border;
            top = border;
            right = border;
        }
        var width = AppTitleBarArea.ActualWidth * scale;
        var height = AppTitleBarArea.ActualHeight * scale;
        var dragWidth = Math.Max(0, width - left - right - titleBar.RightInset);
        var dragHeight = Math.Max(0, height - top - bottom);
        titleBar.SetDragRectangles([new RectInt32((int)left, (int)top, (int)dragWidth, (int)dragHeight)]);
    }

    private async void OnBrowserChooseFolderRequested(object? sender, EventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) await MediaBrowser.NavigateAsync(folder.Path);
        }
        catch (Exception exception) { await ShowMessageAsync(L("FolderUnavailableTitle"), exception.Message); }
    }

    private async void OnBrowserMediaRequested(object? sender, LocalMediaBrowserEntryEventArgs e)
    {
        // Start playback before the browser materializes the sibling playlist.
        await OpenMediaAsync(e.Path);
    }

    private async void OnBrowserFavoriteRequested(object? sender, LocalMediaBrowserEntryEventArgs e)
    {
        await AddFavoriteAsync(new LocalMediaSource(e.Path), e.IsDirectory);
    }

    private void OnBrowserErrorOccurred(object? sender, LocalMediaBrowserErrorEventArgs e)
    {
        StatusText.Text = e.Exception.Message;
    }

    private async void OnPlaylistItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlaylistEntry entry) return;
        var index = _playlist.IndexOf(entry);
        if (index < 0) return;
        _playlistIndex = index;
        await OpenPlaylistEntryAsync(entry);
    }

    private void OnClearPlaylistClick(object sender, RoutedEventArgs e) { _playlist.Clear(); _playlistIndex = -1; UpdatePlaylistButtons(); }

    private async void OnWebDavEntryRequested(object? sender, WebDavEntryEventArgs e)
    {
        var server = e.Server;
        var entry = e.Entry;
        if (MediaFileClassifier.IsSubtitle(Uri.UnescapeDataString(entry.Uri.AbsolutePath)))
        {
            await LoadWebDavSubtitleAsync(server, entry, confirmChanges: true, showSubtitlePanel: true);
            return;
        }
        await OpenWebDavMediaAsync(server, entry, e.Siblings);
    }

    private async Task LoadWebDavSubtitleAsync(WebDavServerSettings server, WebDavEntry entry, bool confirmChanges, bool showSubtitlePanel, Uri? expectedMediaUri = null)
    {
        if (confirmChanges)
        {
            await CancelAiPipelineAsync();
            if (!await ConfirmDiscardChangesAsync(L("ActionLoadSubtitle"))) return;
        }
        try
        {
            var bytes = await _webDavClient.DownloadAsync(server, entry.Uri);
            if (expectedMediaUri is not null &&
                (_currentMediaSource is not WebDavMediaSource currentSource ||
                 currentSource.ServerId != server.Id || !WebDavUri.Equals(currentSource.Uri, expectedMediaUri))) return;
            var path = Uri.UnescapeDataString(entry.Uri.AbsolutePath);
            var document = _subtitleFiles.DecodeAndParse(path, bytes, _settings.Subtitle.Encoding);
            document.MarkSaved();
            BindDocument(document);
            _aiWorkflow.ResetTranslation();
            ScheduleSubtitleOverlaySync();
            if (showSubtitlePanel) ShowRightPanelSection(RightPanelSection.Subtitles);
            StatusText.Text = F("StatusSubtitlesLoaded", document.ActiveTrack?.Cues.Count ?? 0);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "webdav", exception is WebDavException webDavException ? webDavException.Code : "WEBDAV_SUBTITLE_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("SubtitleErrorTitle"), exception.Message);
        }
    }

    private async Task TryLoadMatchingWebDavSmiAsync(WebDavMediaSource mediaSource)
    {
        try
        {
            var server = _settings.Network.WebDavServers.FirstOrDefault(candidate => candidate.Id == mediaSource.ServerId);
            if (server is null) return;

            var directory = WebDavUri.AsDirectory(new Uri(mediaSource.Uri, "."));
            IReadOnlyList<WebDavEntry> entries;
            if (WebDavBrowser.TryGetEntries(mediaSource.ServerId, directory, out var displayedEntries)) entries = displayedEntries;
            else
            {
                entries = await _webDavClient.ListAsync(server, directory);
            }

            if (_currentMediaSource is not WebDavMediaSource currentSource ||
                currentSource.ServerId != mediaSource.ServerId || !WebDavUri.Equals(currentSource.Uri, mediaSource.Uri)) return;

            var sidecar = entries.FirstOrDefault(candidate =>
                !candidate.IsCollection &&
                SmiParser.IsSidecarFor(mediaSource.DisplayName, candidate.Name));
            if (sidecar is not null)
            {
                await LoadWebDavSubtitleAsync(server, sidecar, confirmChanges: false, showSubtitlePanel: false, expectedMediaUri: mediaSource.Uri);
            }
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("warning", "webdav", exception is WebDavException webDavException ? webDavException.Code : "WEBDAV_SIDECAR_SUBTITLE_ERROR", exception.Message, exception);
        }
    }

    private async Task OpenWebDavMediaAsync(WebDavServerSettings server, WebDavEntry entry, IReadOnlyList<WebDavEntry>? siblings = null)
    {
        using var request = _webDavClient.CreateMediaRequest(server, entry.Uri);
        IReadOnlyDictionary<string, string>? headers = request.Headers.Authorization is { } authorization
            ? new Dictionary<string, string> { ["Authorization"] = authorization.ToString() }
            : null;

        if (siblings is null)
        {
            try
            {
                siblings = await _webDavClient.ListAsync(server, new Uri(entry.Uri, "."));
                WebDavBrowser.Synchronize(server, entry.Uri, siblings);
            }
            catch (Exception exception)
            {
                await AppLog.WriteAsync("warning", "webdav", "WEBDAV_SIBLING_LIST_ERROR", exception.Message, exception);
            }
        }

        var mediaEntries = (siblings ?? [])
            .Where(IsPlayableWebDavEntry)
            .ToList();
        if (!mediaEntries.Any(candidate => WebDavUri.Equals(candidate.Uri, entry.Uri))) mediaEntries.Add(entry);

        _playlist.Clear();
        _playlist.AddRange(mediaEntries.Select(candidate => PlaylistEntry.FromWebDav(server.Id, candidate, headers)));
        _playlistIndex = _playlist.FindIndex(item => WebDavUri.Equals(new Uri(item.Path), entry.Uri));
        if (_playlistIndex < 0) _playlistIndex = 0;
        await OpenPlaylistEntryAsync(_playlist[_playlistIndex]);
    }

    private static bool IsPlayableWebDavEntry(WebDavEntry entry) =>
        !entry.IsCollection &&
        (MediaFileClassifier.IsPlayable(Uri.UnescapeDataString(entry.Uri.AbsolutePath)) ||
         entry.ContentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
         entry.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true);

    private async void OnWebDavFavoriteRequested(object? sender, WebDavEntryEventArgs e)
    {
        await AddFavoriteAsync(new WebDavMediaSource(e.Server.Id, e.Entry.Uri, e.Entry.Name), e.Entry.IsCollection);
    }

    private async Task AddFavoriteAsync(IMediaSource source, bool isFolder)
    {
        await _historyService.LoadFavoritesAsync();
        if (!_historyService.AddFavorite(source, isFolder)) return;
        await _historyService.SaveFavoritesAsync();
        RefreshFavoritesList();
        StatusText.Text = isFolder ? F("StatusAddedFavoriteFolder", source.DisplayName) : L("StatusAddedFavorite");
    }

    private void RefreshFavoritesList()
    {
        if (!ReferenceEquals(FavoriteList.ItemsSource, _favoriteEntries)) FavoriteList.ItemsSource = _favoriteEntries;
        _favoriteEntries.Clear();
        foreach (var item in _historyService.Favorites)
        {
            _favoriteEntries.Add(new FavoriteListEntry(item, L("RemoveFavoriteButton")));
        }
        FavoritesEmptyText.Visibility = _favoriteEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnFavoriteDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        await _historyService.LoadFavoritesAsync();
        if (!_historyService.ReorderFavorites(_favoriteEntries.Select(entry => entry.Item.Location))) return;
        RefreshFavoritesList();
        await _historyService.SaveFavoritesAsync();
    }

    private async void OnFavoriteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FavoriteListEntry entry) await OpenFavoriteAsync(entry.Item);
    }

    private async void OnRemoveFavoriteItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FavoriteListEntry entry }) return;
        await _historyService.LoadFavoritesAsync();
        if (!_historyService.RemoveFavorite(entry.Item.Location)) return;
        await _historyService.SaveFavoritesAsync();
        RefreshFavoritesList();
    }

    private async Task OpenFavoriteAsync(FavoriteItem favorite)
    {
        if (favorite.IsFolder)
        {
            if (favorite.SourceType == MediaSourceKind.WebDav)
            {
                var server = FindWebDavServerForLocation(favorite.Location);
                if (server is null) { await ShowMessageAsync(L("WebDavServerMissingTitle"), L("FavoriteServerMissingMessage")); return; }
                _panels.IsRightVisible = true;
                ApplyPanelVisibility();
                ShowRightPanelSection(RightPanelSection.WebDav);
                await ConnectWebDavServerAsync(server, new Uri(favorite.Location));
                return;
            }
            if (!Directory.Exists(favorite.Location)) { await ShowMessageAsync(L("FolderUnavailableTitle"), favorite.Location); return; }
            _panels.IsRightVisible = true;
            ApplyPanelVisibility();
            ShowRightPanelSection(RightPanelSection.Explorer);
            await MediaBrowser.NavigateAsync(favorite.Location);
            return;
        }
        await OpenRecentAsync(new RecentMediaItem(favorite.SourceType, favorite.DisplayName, favorite.Location, favorite.Added, 0));
    }

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var recent in _historyService.Recent.Take(20))
        {
            var item = new MenuFlyoutItem { Text = recent.DisplayName, Tag = recent };
            item.Click += async (_, _) => await OpenRecentAsync(recent);
            RecentMenu.Items.Add(item);
        }
        if (RecentMenu.Items.Count == 0) RecentMenu.Items.Add(new MenuFlyoutItem { Text = L("NoRecentMediaText"), IsEnabled = false });
    }

    private async Task OpenRecentAsync(RecentMediaItem recent)
    {
        if (recent.SourceType == MediaSourceKind.WebDav)
        {
            var server = FindWebDavServerForLocation(recent.Location);
            if (server is null) { await ShowMessageAsync(L("WebDavServerMissingTitle"), L("RecentServerMissingMessage")); return; }
            var uri = new Uri(recent.Location);
            await OpenWebDavMediaAsync(server, new WebDavEntry(recent.DisplayName, uri, false, null, null, null));
        }
        else
        {
            await OpenMediaAsync(recent.Location, mediaSource: MediaSourceFactory.Parse(recent.Location));
        }
        if (_settings.General.ResumePlayback && recent.LastPlaybackPositionMicroseconds > 0)
        {
            var resumePosition = TimeSpan.FromTicks(recent.LastPlaybackPositionMicroseconds * 10);
            // OpenAsync queues loadfile and returns before libmpv has produced a
            // frame. Waiting for the UI first-frame signal prevents a recent-file
            // resume seek from racing AV1/WebM decoder initialization.
            if (await WaitForFirstFrameAsync(recent.Location))
                SeekAndRestartAi(resumePosition, () => _playback.Seek(resumePosition, true));
        }
    }

    private WebDavServerSettings? FindWebDavServerForLocation(string location)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var target)) return null;
        var credentials = new WebDavCredentialStore(new WindowsCredentialService());
        WebDavServerSettings? bestMatch = null;
        var bestPathLength = -1;
        foreach (var server in _settings.Network.WebDavServers)
        {
            WebDavConnectionCredential? credential;
            try { credential = credentials.Read(server.Id); }
            catch (Exception) { continue; }
            if (credential is null) continue;
            var root = credential.RootUri;
            if (!root.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase) || !root.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) || root.Port != target.Port || !target.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (root.AbsolutePath.Length <= bestPathLength) continue;
            bestMatch = server;
            bestPathLength = root.AbsolutePath.Length;
        }
        return bestMatch;
    }

    private void RememberCurrentPosition()
    {
        if (_currentMediaSource is null) return;
        _historyService.AddRecent(_currentMediaSource, CurrentPlaybackPositionMicroseconds, _settings.General.RecentMediaCount);
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
        if (!_fullscreen.IsFullscreen && sender.Presenter is OverlappedPresenter presenter && presenter.State != OverlappedPresenterState.Minimized) CaptureWindowPlacement(sender, presenter);
        if (_allowClose) return;

        // Closed is too late for asynchronous cleanup: WinUI can stop its dispatcher before
        // child processes and native playback have finished shutting down. Keep the window
        // alive until all owned resources have been released, then issue the final Close.
        args.Cancel = true;
        if (_closeInProgress) return;
        _closeInProgress = true;
        try
        {
            if (_document.IsDirty)
            {
                var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = L("UnsavedChangesTitle"), Content = L("UnsavedChangesCloseMessage"), PrimaryButtonText = L("SaveButtonText"), SecondaryButtonText = L("DiscardButton"), CloseButtonText = L("CancelButtonText"), DefaultButton = ContentDialogButton.Primary };
                var result = await ShowDialogAsync(dialog);
                if (result == ContentDialogResult.None) return;
                if (result == ContentDialogResult.Primary)
                {
                    if (_document.FilePath is null) await SaveSubtitleAsAsync(); else await SaveSubtitleAsync(_document.FilePath);
                    if (_document.IsDirty) return;
                }
            }

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

    private async Task<bool> ConfirmDiscardChangesAsync(string action)
    {
        if (!_document.IsDirty) return true;
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = L("UnsavedChangesTitle"), Content = F("UnsavedChangesActionMessage", action), PrimaryButtonText = L("SaveButtonText"), SecondaryButtonText = L("DiscardButton"), CloseButtonText = L("CancelButtonText"), DefaultButton = ContentDialogButton.Primary };
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.None) return false;
        if (result == ContentDialogResult.Primary)
        {
            if (_document.FilePath is null) await SaveSubtitleAsAsync(); else await SaveSubtitleAsync(_document.FilePath);
            return !_document.IsDirty;
        }
        return true;
    }

    private void TryPlayback(Action action) { try { action(); } catch (Exception exception) { StatusText.Text = exception.Message; } }
    private void SeekAndRestartAi(TimeSpan requestedPosition, Action seek)
    {
        TryPlayback(seek);
        ScheduleAiRestartAfterSeek(requestedPosition);
    }
    private ContentDialog CreateDialog(string title, object content, string primaryText) => new() { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = title, Content = content, PrimaryButtonText = primaryText, CloseButtonText = L("CancelButtonText"), DefaultButton = ContentDialogButton.Primary };
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogLock.WaitAsync();
        var restoreVideo = _videoHost?.IsVisible == true;
        try
        {
            if (restoreVideo) _videoHost!.SetVisible(false);
            return await dialog.ShowAsync();
        }
        finally
        {
            if (restoreVideo && _videoHost is not null) _videoHost.SetVisible(true);
            _dialogLock.Release();
        }
    }
    private async Task ShowMessageAsync(string title, string message) => await ShowDialogAsync(new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = L("OkButton") });
    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) => string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);
    private static Brush ThemeBrush(string resourceKey, Windows.UI.Color fallback) => Application.Current.Resources.TryGetValue(resourceKey, out var value) && value is Brush brush ? brush : new SolidColorBrush(fallback);
    private long CurrentPlaybackPositionMicroseconds => Math.Max(0, _playback.Position.Ticks / 10);
    private static string FormatTime(TimeSpan value)
    {
        var totalSeconds = Math.Max(0, (long)value.TotalSeconds);
        return $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}";
    }
    private sealed class PositionSliderThumbToolTipValueConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double seconds && double.IsFinite(seconds)) return FormatTime(TimeSpan.FromSeconds(Math.Max(0, seconds)));
            return FormatTime(TimeSpan.Zero);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }
    private async Task ShutdownAsync()
    {
        _playback.StateChanged -= OnPlaybackStateChanged;
        ReleasePlaybackPowerRequirement();
        _windowsPowerManagement.Dispose();
        _fullscreen.Dispose();
        if (_appWindow is not null)
        {
            _appWindow.Changed -= OnAppWindowChanged;
        }
        await _historyService.LoadRecentAsync();
        RememberCurrentPosition();

        _postOpenCancellation?.Cancel();
        _subtitleOverlay.CancelPendingSync();
        WebDavBrowser.Cancel();

        _settingsWindow?.Close();
        if (_cameraWindow is { } cameraWindow) await cameraWindow.CloseAsync();
        if (_windowsCaptionWindow is { } captionWindow) await captionWindow.CloseAsync();

        try { await _aiWorkflow.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "ASR_DISPOSE_ERROR", exception.Message, exception); }

        try { await _historyService.SaveRecentAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "HISTORY_SAVE_ERROR", exception.Message, exception); }
        try { await SettingsService.CreateDefault().SaveAsync(_settings); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "SETTINGS_SAVE_ERROR", exception.Message, exception); }
        _postOpenCancellation?.Dispose(); _postOpenCancellation = null;
        _webDavClient.Dispose();
        try { await _playback.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "PLAYBACK_DISPOSE_ERROR", exception.Message, exception); }
        _playback.FirstFrameReady -= OnFirstFrameReady;
        _playback.PositionChanged -= OnPlaybackPositionChanged;
        _playback.Seeked -= OnPlaybackSeeked;
        if (_videoHost is not null)
        {
            _videoHost.FilesDropped -= OnNativeVideoFilesDropped;
            _videoHost.Clicked -= OnNativeVideoClicked;
            _videoHost.DoubleClicked -= OnNativeVideoDoubleClicked;
            _videoHost.Dispose();
        }
        _subtitleOverlay.Dispose();

        if (_restartRequested)
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
        ReleasePlaybackPowerRequirement();
        _windowsPowerManagement.Dispose();
        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
        }
        Closed -= OnWindowClosed;
        Application.Current.Exit();
    }

    AppSettings IAiWorkflowHost.Settings => _settings;
    SubtitleDocument IAiWorkflowHost.Document => _document;
    SubtitleDisplayMode? IAiWorkflowHost.CurrentSubtitleDisplayMode => _subtitleDisplayMode;
    IReadOnlyDictionary<string, string>? IAiWorkflowHost.CurrentHttpHeaders => _currentHttpHeaders;
    long IAiWorkflowHost.CurrentPlaybackPositionMicroseconds => CurrentPlaybackPositionMicroseconds;
    double IAiWorkflowHost.ViewWidth => RootGrid.ActualWidth;
    double IAiWorkflowHost.ViewHeight => RootGrid.ActualHeight;
    DispatcherQueue IAiWorkflowHost.DispatcherQueue => DispatcherQueue;
    void IAiWorkflowHost.BindDocument(SubtitleDocument document) => BindDocument(document);
    void IAiWorkflowHost.SetSubtitleDisplayMode(SubtitleDisplayMode displayMode, bool refreshOverlay) =>
        SetSubtitleDisplayMode(displayMode, refreshOverlay);
    void IAiWorkflowHost.ShowSubtitlePanel()
    {
        _panels.IsRightVisible = true;
        ApplyPanelVisibility();
        ShowRightPanelSection(RightPanelSection.Subtitles);
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

    private sealed record PlaylistEntry(string Path, string DisplayName, IReadOnlyDictionary<string, string>? HttpHeaders = null, IMediaSource? MediaSource = null)
    {
        public string SourceIconGlyph => MediaSource?.Kind == MediaSourceKind.WebDav ? "\uE774" : string.Empty;

        public static PlaylistEntry FromLocal(string path)
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            return new PlaylistEntry(fullPath, System.IO.Path.GetFileName(fullPath));
        }

        public static PlaylistEntry FromWebDav(Guid serverId, WebDavEntry entry, IReadOnlyDictionary<string, string>? headers) =>
            new(entry.Uri.AbsoluteUri, entry.Name, headers, new WebDavMediaSource(serverId, entry.Uri, entry.Name));
    }

    private sealed record FavoriteListEntry(FavoriteItem Item, string RemoveLabel)
    {
        public string DisplayName => Item.DisplayName;
        public string Location => Item.Location;
        public string SourceIconGlyph => Item.SourceType == MediaSourceKind.WebDav ? "\uE774" : string.Empty;
        public string IconGlyph => Item.IsFolder ? "\uE8B7" : "\uE8A5";
    }

    private sealed record RightPanelSectionEntry(string IconGlyph, string Label);
    private enum RightPanelSection { Explorer, Playlist, WebDav, Favorites, Subtitles }

    private sealed record PendingPostOpenWork(string Source, string? LocalPath, bool PopulateSiblingPlaylist, CancellationToken CancellationToken);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);

    private enum RepeatMode { Off, One, AutoAdvance }
}
