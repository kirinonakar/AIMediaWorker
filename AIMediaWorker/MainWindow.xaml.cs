using AIMediaWorker.Playback;
using AIMediaWorker.Subtitle;
using AIMediaWorker.Subtitle.Editing;
using AIMediaWorker.Subtitle.Parsing;
using AIMediaWorker.Subtitle.Writing;
using AIMediaWorker.Timeline;
using AIMediaWorker.Views;
using AIMediaWorker.Settings;
using AIMediaWorker.Asr;
using AIMediaWorker.Llm;
using AIMediaWorker.Llm.Providers;
using AIMediaWorker.Network;
using AIMediaWorker.History;
using AIMediaWorker.Media;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.Storage;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using System.Collections.ObjectModel;

namespace AIMediaWorker;

public sealed partial class MainWindow : Window
{
    private readonly MpvPlaybackEngine _playback = new();
    private readonly SubtitleCommandHistory _history = new();
    private readonly TimelineTransform _timelineTransform = new();
    private readonly Dictionary<Guid, string> _textBeforeEdit = [];
    private readonly Dictionary<Guid, (long Start, long End)> _timesBeforeEdit = [];
    private SubtitleDocument _document = new();
    private NativeVideoHost? _videoHost;
    private AppWindow? _appWindow;
    private bool _updatingPosition;
    private bool _positionSliderDragging;
    private bool _isFullscreen;
    private bool _changingFullscreen;
    private bool _fullscreenRepairQueued;
    private bool _fullscreenStyleCaptured;
    private int _windowedStyle;
    private RectInt32? _windowBoundsBeforeFullscreen;
    private RectInt32? _workAreaBeforeFullscreen;
    private bool _wasMaximizedBeforeFullscreen;
    private bool _rightPanelVisible = true;
    private bool _bottomPanelVisible = true;
    private double _rightPanelWidth = 360;
    private double _bottomPanelHeight = 160;
    private bool _initialized;
    private CameraWindow? _cameraWindow;
    private ScreenRecordingWindow? _screenRecordingWindow;
    private SettingsWindow? _settingsWindow;
    private AppSettings _settings = new();
    private readonly WindowsCredentialService _windowsCredentials = new();
    private readonly WebDavCredentialStore _webDavCredentials;
    private readonly WebDavClient _webDavClient;
    private CancellationTokenSource? _webDavListingCancellation;
    private Uri? _webDavPanelDirectory;
    private readonly AsrWorkerClient _asrEngine = new();
    private CancellationTokenSource? _aiOperationCancellation;
    private Task? _aiPipelineTask;
    private int _generatedSubtitleUiRefreshQueued;
    private int _playbackPositionUiRefreshQueued;
    private CancellationTokenSource? _seekAiRestartCancellation;
    private bool _subtitleGenerationCompletedForCurrentMedia;
    private bool _translationCompletedForCurrentMedia;
    private readonly SemaphoreSlim _dialogLock = new(1, 1);
    private readonly MediaHistoryService _historyService = MediaHistoryService.CreateDefault();
    private readonly ObservableCollection<FavoriteListEntry> _favoriteEntries = [];
    private IMediaSource? _currentMediaSource;
    private IReadOnlyDictionary<string, string>? _currentHttpHeaders;
    private SubtitleCue? _dragCue;
    private Guid? _playbackLinkedCueId;
    private TimelineDragMode _dragMode;
    private double _dragStartX;
    private long _dragOldStart;
    private long _dragOldEnd;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _restartRequested;
    private Task? _shutdownTask;
    private TimeSpan? _abStart;
    private CancellationTokenSource? _overlaySyncCancellation;
    private readonly SemaphoreSlim _overlayWriteLock = new(1, 1);
    private string? _renderedOverlayContent;
    private string? _renderedOverlayFontFamily;
    private AssCueSnapshot[] _renderedOverlayCues = [];
    private Rectangle? _timelinePlayhead;
    private bool _subtitleEditorHasFocus;
    private readonly List<PlaylistEntry> _playlist = [];
    private int _playlistIndex = -1;
    private RepeatMode _repeatMode;
    private string _browserDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    private string? _loadedBrowserDirectory;
    private BrowserEntry[] _browserEntries = [];
    private WebDavEntry[] _webDavEntries = [];
    private EntrySortMode _browserSortMode;
    private EntrySortMode _webDavSortMode;
    private Guid? _webDavPanelServerId;
    private DispatcherQueueTimer? _fullscreenHoverTimer;
    private DateTimeOffset _showFullscreenMenuUntil;
    private DateTimeOffset _showFullscreenControlsUntil;
    private DateTimeOffset _showFullscreenRightPanelUntil;
    private DateTimeOffset _fullscreenCursorLastMovedAt;
    private NativePoint? _lastFullscreenCursorPosition;
    private bool _fullscreenCursorHidden;
    private string? _pendingLaunchSource;
    private string[]? _pendingDroppedFiles;
    private PendingPostOpenWork? _pendingPostOpenWork;
    private CancellationTokenSource? _postOpenCancellation;
    private readonly Task _playbackInitializationTask;
    private readonly Task? _initialLaunchOpenTask;
    private readonly string _editorOverlayPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AIMediaWorker-{Environment.ProcessId}-{Guid.NewGuid():N}.ass");

    public MainWindow() : this(null, new AppSettings()) { }

    public MainWindow(string? initialSource) : this(initialSource, new AppSettings()) { }

    public MainWindow(string? initialSource, AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        // mpv_create and option setup do not require the video HWND. Start them before
        // parsing the MainWindow XAML so native cold initialization overlaps WinUI work.
        _ = _playback.PrepareAsync(_settings.Playback.HardwareDecoder, _settings.Playback.Renderer);
        _browserDirectory = ResolveDefaultBrowserDirectory(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        _webDavCredentials = new WebDavCredentialStore(_windowsCredentials);
        _webDavClient = new WebDavClient(_windowsCredentials, timeout: TimeSpan.FromSeconds(_settings.Network.TimeoutSeconds));
        StartupProfiler.Mark("xaml-start");
        InitializeComponent();
        StartupProfiler.Mark("xaml-ready");
        _playback.StateChanged += OnPlaybackStateChanged;
        _playback.FirstFrameReady += OnFirstFrameReady;
        _playback.PositionChanged += OnPlaybackPositionChanged;
        _playback.TracksChanged += OnTracksChanged;
        _playback.ErrorOccurred += OnPlaybackError;
        _playback.MediaEnded += OnMediaEnded;
        _videoHost = new NativeVideoHost(this, VideoPlaceholder);
        _videoHost.FilesDropped += OnNativeVideoFilesDropped;
        _videoHost.Clicked += OnNativeVideoClicked;
        _videoHost.DoubleClicked += OnNativeVideoDoubleClicked;
        _playbackInitializationTask = InitializePlaybackAsync(_videoHost.Create());
        _pendingLaunchSource = initialSource;
        // Start waiting immediately. The continuation does not need the UI thread to issue
        // loadfile, so it can run while the rest of this constructor and activation finish.
        _initialLaunchOpenTask = string.IsNullOrWhiteSpace(initialSource) ? null : OpenInitialLaunchSourceAsync();
        ExtendsContentIntoTitleBar = true;
        RightPanelSectionList.SelectionChanged += OnRightPanelSectionChanged;
        RefreshRightPanelSections();
        GenerateSubtitlesMenuItem.IsChecked = _settings.Asr.GenerateSubtitles;
        TranslateMenuItem.IsChecked = _settings.Llm.TranslateSubtitles;
        var handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        _appWindow?.Resize(new SizeInt32(1280, 820));
        if (_appWindow is not null)
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        }
        if (_appWindow is not null) { _appWindow.Closing += OnAppWindowClosing; _appWindow.Changed += OnAppWindowChanged; }
        Closed += OnWindowClosed;
        RootGrid.ActualThemeChanged += OnRootActualThemeChanged;
        ApplyTheme(_settings.General.Theme);
        RootGrid.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnRootPreviewKeyDown), true);
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
        PositionSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPositionSliderPointerPressed), true);
        PositionSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPositionSliderPointerReleased), true);
        PositionSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPositionSliderPointerReleased), true);
        _fullscreenHoverTimer = DispatcherQueue.CreateTimer();
        _fullscreenHoverTimer.Interval = TimeSpan.FromMilliseconds(100);
        _fullscreenHoverTimer.Tick += OnFullscreenHoverTick;
        BindDocument(new SubtitleDocument());
    }

    private async Task OpenInitialLaunchSourceAsync()
    {
        try
        {
            await _playbackInitializationTask.ConfigureAwait(false);
            if (!_playback.IsAvailable || _pendingDroppedFiles is { Length: > 0 } ||
                _pendingLaunchSource is not { Length: > 0 } launchSource) return;
            _pendingLaunchSource = null;
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
        await _playback.InitializeAsync(videoWindowHandle, _settings.Playback.HardwareDecoder, _settings.Playback.Renderer).ConfigureAwait(false);
        if (!_playback.IsAvailable) return;
        _playback.SetVolume(_settings.Playback.DefaultVolume);
        _playback.SetRate(_settings.Playback.PlaybackRate);
        _playback.ConfigureNetwork(TimeSpan.FromSeconds(_settings.Network.TimeoutSeconds), _settings.Network.Proxy);
        _playback.ConfigurePreferredLanguages(_settings.Playback.DefaultAudioLanguage, _settings.Playback.DefaultSubtitleLanguage);
        _playback.ConfigureSubtitleStyle(_settings.Subtitle.FontFamily, _settings.Subtitle.FontSize, _settings.Subtitle.Color, _settings.Subtitle.Background, _settings.Subtitle.Outline, _settings.Subtitle.BottomMargin);
        _playback.SetSubtitleVisibility(_settings.Playback.ShowSubtitles);
    }

    public void ApplySavedWindowPlacement(WindowLayoutSettings layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _rightPanelVisible = layout.IsRightPanelVisible;
        _bottomPanelVisible = layout.IsBottomPanelVisible;
        _rightPanelWidth = Math.Clamp(layout.RightPanelWidth, 240, 1200);
        _bottomPanelHeight = Math.Clamp(layout.BottomPanelHeight, WindowLayoutSettings.MinimumBottomPanelHeight, 800);
        ApplyPanelVisibility();
        if (_appWindow is null || !layout.HasPlacement) return;
        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var width = Math.Clamp(layout.Width, 640, Math.Max(640, workArea.Width));
        var height = Math.Clamp(layout.Height, 420, Math.Max(420, workArea.Height));
        var x = Math.Clamp(layout.X, workArea.X - width + 120, workArea.X + workArea.Width - 120);
        var y = Math.Clamp(layout.Y, workArea.Y, workArea.Y + workArea.Height - 80);
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        if (layout.IsMaximized && _appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_initialized || _changingFullscreen) return;
        if (_isFullscreen)
        {
            ApplyFullscreenWindowStyle();
            if (sender.Presenter.Kind != AppWindowPresenterKind.FullScreen) QueueFullscreenRepair();
            return;
        }
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
            _rightPanelVisible = _settings.Window.IsRightPanelVisible;
            _bottomPanelVisible = _settings.Window.IsBottomPanelVisible;
            _rightPanelWidth = Math.Clamp(_settings.Window.RightPanelWidth, 240, 1200);
            _bottomPanelHeight = Math.Clamp(_settings.Window.BottomPanelHeight, WindowLayoutSettings.MinimumBottomPanelHeight, 800);
            ClampPanelSizesToAvailable();
            ApplyPanelVisibility();
            var recentLoad = _historyService.LoadRecentAsync();
            _ = RefreshBrowserAsync(_browserDirectory);
            SubtitleVisibilityMenuItem.IsChecked = _settings.Playback.ShowSubtitles;
            RateCombo.ItemsSource = new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 };
            RateCombo.SelectedItem = RateCombo.Items.Cast<double>().OrderBy(value => Math.Abs(value - _settings.Playback.PlaybackRate)).First();
            UpdateShortcutHints();
            UpdatePlaylistButtons();
            await _playbackInitializationTask;
            StatusText.Text = _playback.IsAvailable ? L("StatusLibmpvReady") : L("StatusPlaybackUnavailable");
            if (_playback.IsAvailable && _pendingDroppedFiles is { Length: > 0 } droppedFiles)
            {
                _pendingDroppedFiles = null;
                _pendingLaunchSource = null;
                await OpenFilesAsPlaylistAsync(droppedFiles);
            }
            if (_initialLaunchOpenTask is not null) await _initialLaunchOpenTask;
            await recentLoad;
            RebuildRecentMenu();
            RefreshWebDavServerList();
            ApplyTheme(_settings.General.Theme);
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
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
                if (File.Exists(value)) await HandleDroppedFilesAsync([value]);
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
        var files = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
        if (_initialized && _playback.IsAvailable)
        {
            _ = OpenForwardedFilesAsync(filePaths);
        }
        else
        {
            _pendingDroppedFiles = filePaths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _pendingLaunchSource = null;
            StatusText.Text = L("StatusPreparingDroppedMedia");
        }
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
            await _playbackInitializationTask;
            if (!_playback.IsAvailable) throw new InvalidOperationException(L("StatusPlaybackUnavailable"));
            await _historyService.LoadRecentAsync();
            RememberCurrentPosition();
            await _playback.OpenAsync(source, httpHeaders);
            await aiPipelineCancellation;
            CompleteMediaOpen(source, httpHeaders, mediaSource, preservePlaylist, showInExplorer: false);
        }
        catch (Exception exception)
        {
            await aiPipelineCancellation;
            await AppLog.WriteAsync("error", "playback", "OPEN_MEDIA_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("PlaybackErrorTitle"), exception.Message);
        }
    }

    private void CompleteMediaOpen(string source, IReadOnlyDictionary<string, string>? httpHeaders, IMediaSource? mediaSource, bool preservePlaylist, bool showInExplorer)
    {
        if (!preservePlaylist)
        {
            _playlist.Clear();
            if (File.Exists(source)) { _playlist.Add(PlaylistEntry.FromLocal(source)); _playlistIndex = 0; }
            else _playlistIndex = -1;
        }
        _currentMediaSource = mediaSource ?? MediaSourceFactory.Parse(source);
        _currentHttpHeaders = httpHeaders is null ? null : new Dictionary<string, string>(httpHeaders, StringComparer.OrdinalIgnoreCase);
        UpdateWindowTitle(_currentMediaSource.DisplayName);
        if (_currentMediaSource is WebDavMediaSource webDavSource) SelectWebDavEntry(webDavSource.ServerId, webDavSource.Uri);
        _ = SaveHistoryAfterOpenAsync(_currentMediaSource);
        var blank = new SubtitleDocument(); blank.EnsureTrack(); blank.MarkSaved(); BindDocument(blank);
        _subtitleGenerationCompletedForCurrentMedia = false;
        _translationCompletedForCurrentMedia = false;
        StatusText.Text = source;
        VideoStatusText.Visibility = Visibility.Collapsed;
        QueuePostOpenWork(source, !preservePlaylist, showInExplorer);
        UpdatePlaylistButtons();
        FocusPlaybackSurface();
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

    private void QueuePostOpenWork(string source, bool populateSiblingPlaylist, bool showInExplorer)
    {
        _postOpenCancellation?.Cancel();
        _postOpenCancellation?.Dispose();
        _postOpenCancellation = new CancellationTokenSource();
        if (File.Exists(source)) PrepareBrowserForOpenedFile(Path.GetFullPath(source), showInExplorer);
        _pendingPostOpenWork = new PendingPostOpenWork(source, populateSiblingPlaylist, _postOpenCancellation.Token);
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
            if (File.Exists(work.Source))
            {
                var fullPath = Path.GetFullPath(work.Source);
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
        if (Path.GetDirectoryName(fullPath) is not { } directory) return;
        if (showInExplorer) ShowRightPanelSection(RightPanelSection.Explorer);
        if (AreSameDirectory(directory, _browserDirectory))
        {
            SelectBrowserEntry(fullPath);
            return;
        }

        _browserDirectory = directory;
        _loadedBrowserDirectory = null;
        BrowserFilterBox.Text = string.Empty;
        _browserEntries = [];
        UpdateBrowserBreadcrumbs();
        ApplyBrowserEntryView();
    }

    private async Task RefreshBrowserForOpenedFileAsync(string fullPath)
    {
        try
        {
            if (Path.GetDirectoryName(fullPath) is not { } directory) return;
            if (_loadedBrowserDirectory is not null && AreSameDirectory(directory, _loadedBrowserDirectory))
            {
                SelectBrowserEntry(fullPath);
                return;
            }
            await RefreshBrowserAsync(directory, fullPath);
        }
        catch (Exception exception) { await AppLog.WriteAsync("error", "browser", "BROWSER_SYNC_AFTER_OPEN_ERROR", exception.Message, exception); }
    }

    private static bool AreSameDirectory(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private void SelectBrowserEntry(string path)
    {
        if (FolderEntryList.ItemsSource is not IEnumerable<BrowserEntry> entries) return;
        var fullPath = Path.GetFullPath(path);
        var selectedEntry = entries.FirstOrDefault(item => item.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        if (selectedEntry is null) return;
        FolderEntryList.SelectedItem = selectedEntry;
        FolderEntryList.ScrollIntoView(selectedEntry);
    }

    private async Task OpenFilesAsPlaylistAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        if (files.Length == 1 && IsSubtitlePath(files[0])) { await LoadSubtitleFromPathAsync(files[0]); return; }
        _playlist.Clear();
        _playlist.AddRange(files.Where(path => !IsSubtitlePath(path)).Select(PlaylistEntry.FromLocal));
        if (_playlist.Count == 0) return;
        _playlistIndex = 0;
        await OpenPlaylistEntryAsync(_playlist[0]);
    }

    private static bool IsSubtitlePath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa";

    private async Task PopulateSiblingPlaylistAsync(string currentPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(currentPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null) return;
            var siblings = _loadedBrowserDirectory is not null && AreSameDirectory(directory, _loadedBrowserDirectory)
                ? _browserEntries.Where(entry => !entry.IsDirectory).Select(entry => entry.Path).ToArray()
                : await Task.Run(() => Directory.EnumerateFiles(directory).Where(IsPlayableMediaPath).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).Take(5000).Select(Path.GetFullPath).ToArray());
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
        ShowVolumeOverlay();
    }
    private void OnToggleSubtitleVisibilityClick(object sender, RoutedEventArgs e)
    {
        var visible = SubtitleVisibilityMenuItem.IsChecked;
        TryPlayback(() => _playback.SetSubtitleVisibility(visible));
        SubtitleVisibilityMenuItem.IsChecked = _playback.AreSubtitlesVisible;
        _settings.Playback.ShowSubtitles = SubtitleVisibilityMenuItem.IsChecked;
    }
    private void OnRateChanged(object sender, SelectionChangedEventArgs e) { if (RateCombo.SelectedItem is double rate && _playback.IsAvailable) TryPlayback(() => _playback.SetRate(rate)); }
    private void OnRepeatClick(object sender, RoutedEventArgs e)
    {
        _repeatMode = _repeatMode switch { RepeatMode.Off => RepeatMode.One, RepeatMode.One => RepeatMode.All, _ => RepeatMode.Off };
        RepeatIcon.Source = PlaybackIconSource(_repeatMode switch { RepeatMode.One => "repeat-one", RepeatMode.All => "repeat-all", _ => "repeat" });
        ToolTipService.SetToolTip(RepeatButton, L(_repeatMode switch { RepeatMode.One => "TooltipRepeatCurrent", RepeatMode.All => "TooltipRepeatPlaylist", _ => "TooltipRepeatOff" }));
        UpdatePlaylistButtons();
    }

    private async Task OpenAdjacentMediaAsync(int direction)
    {
        if (_playlist.Count == 0) return;
        var next = _playlistIndex + Math.Sign(direction);
        if (_repeatMode == RepeatMode.All) next = (next + _playlist.Count) % _playlist.Count;
        if (next < 0 || next >= _playlist.Count) return;
        _playlistIndex = next;
        await OpenPlaylistEntryAsync(_playlist[_playlistIndex]);
    }

    private Task OpenPlaylistEntryAsync(PlaylistEntry entry) =>
        OpenMediaAsync(entry.Path, entry.HttpHeaders, entry.MediaSource, preservePlaylist: true);

    private void UpdatePlaylistButtons()
    {
        PreviousButton.IsEnabled = _playlist.Count > 1 && (_playlistIndex > 0 || _repeatMode == RepeatMode.All);
        NextButton.IsEnabled = _playlist.Count > 1 && (_playlistIndex < _playlist.Count - 1 || _repeatMode == RepeatMode.All);
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

    private void OnPositionSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingPosition && !_positionSliderDragging && _playback.IsAvailable && PositionSlider.Maximum > 0) SeekAndRestartAi(TimeSpan.FromSeconds(e.NewValue), () => _playback.Seek(TimeSpan.FromSeconds(e.NewValue)));
    }

    private void OnPositionSliderPointerPressed(object sender, PointerRoutedEventArgs e) => _positionSliderDragging = true;
    private void OnPositionSliderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_positionSliderDragging) return;
        _positionSliderDragging = false;
        if (_playback.IsAvailable) SeekAndRestartAi(TimeSpan.FromSeconds(PositionSlider.Value), () => _playback.Seek(TimeSpan.FromSeconds(PositionSlider.Value), true));
    }

    private async void OnLoadSubtitleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var extension in new[] { ".srt", ".vtt", ".ass", ".ssa" }) picker.FileTypeFilter.Add(extension);
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
            var text = await File.ReadAllTextAsync(path, ResolveSubtitleEncoding());
            var document = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".srt" => SrtParser.Parse(text),
                ".vtt" => VttParser.Parse(text),
                ".ass" or ".ssa" => AssParser.Parse(text),
                _ => throw new InvalidDataException("Unsupported subtitle format.")
            };
            document.MarkSaved(path);
            BindDocument(document);
            _translationCompletedForCurrentMedia = false;
            if (_playback.IsAvailable) _playback.LoadSubtitle(path);
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
            var targetFormat = System.IO.Path.GetExtension(path).ToLowerInvariant() switch { ".vtt" => "vtt", ".ass" or ".ssa" => "ass", _ => "srt" };
            var text = targetFormat switch { "vtt" => VttWriter.Write(track), "ass" => AssWriter.Write(track, _settings.Subtitle.FontFamily), _ => SrtWriter.Write(track) };
            var convertedWithStyleLoss = !track.Format.Equals(targetFormat, StringComparison.OrdinalIgnoreCase) && track.Cues.Any(cue => !string.IsNullOrWhiteSpace(cue.Style));
            await File.WriteAllTextAsync(path, text, ResolveSubtitleEncoding());
            track.Format = targetFormat;
            _document.MarkSaved(path);
            StatusText.Text = convertedWithStyleLoss ? F("StatusSavedStyleLoss", path) : F("StatusSaved", path);
        }
        catch (Exception exception) { await ShowMessageAsync(L("SaveErrorTitle"), exception.Message); }
    }

    private void OnAddCueClick(object sender, RoutedEventArgs e)
    {
        var track = _document.EnsureTrack();
        var start = Math.Max(0, (long)(_playback.Position.TotalMilliseconds * 1000));
        var cue = new SubtitleCue { StartMicroseconds = start, EndMicroseconds = start + 2_000_000, Text = string.Empty, Source = SubtitleCueSource.Manual };
        _history.Execute(new AddSubtitleCommand(_document, track.Cues, cue));
        SubtitleList.SelectedItem = cue;
        DrawTimeline();
        ScheduleSubtitleOverlaySync();
    }

    private void OnDeleteCueClick(object sender, RoutedEventArgs e)
    {
        var track = _document.ActiveTrack;
        if (track is null) return;
        var selected = SubtitleList.SelectedItems.Cast<SubtitleCue>().ToArray();
        if (selected.Length == 0) return;
        _history.Execute(new DeleteSubtitleCommand(_document, track.Cues, selected));
        DrawTimeline();
        ScheduleSubtitleOverlaySync();
    }

    private void OnSplitCueClick(object sender, RoutedEventArgs e)
    {
        var track = _document.ActiveTrack;
        if (track is null || SubtitleList.SelectedItem is not SubtitleCue cue) return;
        var playhead = (long)(_playback.Position.TotalMilliseconds * 1000);
        var split = playhead > cue.StartMicroseconds && playhead < cue.EndMicroseconds ? playhead : cue.StartMicroseconds + cue.DurationMicroseconds / 2;
        _history.Execute(new SplitSubtitleCommand(_document, track.Cues, cue, split));
        DrawTimeline();
        ScheduleSubtitleOverlaySync();
    }

    private void OnMergeCueClick(object sender, RoutedEventArgs e)
    {
        var track = _document.ActiveTrack;
        if (track is null || SubtitleList.SelectedItem is not SubtitleCue first) return;
        var index = track.Cues.IndexOf(first);
        if (index < 0 || index + 1 >= track.Cues.Count) return;
        _history.Execute(new MergeSubtitleCommand(_document, track.Cues, first, track.Cues[index + 1]));
        DrawTimeline();
        ScheduleSubtitleOverlaySync();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) { _history.Undo(); DrawTimeline(); ScheduleSubtitleOverlaySync(); }
    private void OnRedoClick(object sender, RoutedEventArgs e) { _history.Redo(); DrawTimeline(); ScheduleSubtitleOverlaySync(); }
    private void OnCueTextGotFocus(object sender, RoutedEventArgs e) { _subtitleEditorHasFocus = true; if (sender is TextBox { DataContext: SubtitleCue cue }) _textBeforeEdit[cue.Id] = cue.Text; }
    private void OnCueTextLostFocus(object sender, RoutedEventArgs e)
    {
        _subtitleEditorHasFocus = false;
        if (sender is not TextBox { DataContext: SubtitleCue cue } box || !_textBeforeEdit.Remove(cue.Id, out var before) || before == box.Text) return;
        var after = box.Text; cue.Text = before; _history.Execute(new EditSubtitleTextCommand(_document, cue, after)); DrawTimeline(); ScheduleSubtitleOverlaySync();
    }

    private void OnCueTimeGotFocus(object sender, RoutedEventArgs e)
    {
        _subtitleEditorHasFocus = true;
        if (sender is TextBox { DataContext: SubtitleCue cue }) _timesBeforeEdit[cue.Id] = (cue.StartMicroseconds, cue.EndMicroseconds);
    }

    private void OnCueTimeLostFocus(object sender, RoutedEventArgs e)
    {
        _subtitleEditorHasFocus = false;
        if (sender is not TextBox { DataContext: SubtitleCue cue } box || !_timesBeforeEdit.Remove(cue.Id, out var before)) return;
        if (!long.TryParse(box.Text, out var value)) { box.Text = box.Tag?.ToString() switch { "Start" => before.Start.ToString(), "End" => before.End.ToString(), _ => (before.End - before.Start).ToString() }; return; }
        var start = before.Start; var end = before.End;
        switch (box.Tag?.ToString()) { case "Start": start = value; break; case "End": end = value; break; case "Duration": end = checked(start + value); break; }
        if (start < 0 || end <= start) { box.Text = box.Tag?.ToString() switch { "Start" => before.Start.ToString(), "End" => before.End.ToString(), _ => (before.End - before.Start).ToString() }; StatusText.Text = L("StatusInvalidSubtitleTime"); return; }
        _history.Execute(new MoveSubtitleCommand(_document, cue, start, end)); DrawTimeline(); ScheduleSubtitleOverlaySync();
    }

    private void OnDuplicateCueClick(object sender, RoutedEventArgs e)
    {
        var track = _document.ActiveTrack; if (track is null) return;
        var selected = SubtitleList.SelectedItems.Cast<SubtitleCue>().OrderBy(cue => cue.StartMicroseconds).ToArray(); if (selected.Length == 0) return;
        var copies = selected.Select(cue => { var copy = cue.Clone(false); copy.StartMicroseconds += 100_000; copy.EndMicroseconds += 100_000; return copy; }).ToArray();
        var commands = copies.Select(copy => (IUndoableSubtitleCommand)new AddSubtitleCommand(_document, track.Cues, copy)).ToArray();
        _history.Execute(new CompositeSubtitleCommand("Duplicate subtitles", commands)); SubtitleList.SelectedItems.Clear(); foreach (var copy in copies) SubtitleList.SelectedItems.Add(copy); DrawTimeline(); ScheduleSubtitleOverlaySync();
    }

    private async void OnShiftCueClick(object sender, RoutedEventArgs e)
    {
        var track = _document.ActiveTrack; if (track is null || track.Cues.Count == 0) return;
        var input = new NumberBox { Header = L("ShiftSecondsHeader"), Value = 0, SmallChange = 0.1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 320 };
        if (await ShowDialogAsync(CreateDialog(L("ShiftSubtitlesTitle"), input, L("ShiftButton"))) != ContentDialogResult.Primary) return;
        var selected = SubtitleList.SelectedItems.Cast<SubtitleCue>().ToArray(); var cues = selected.Length > 0 ? selected : track.Cues.ToArray();
        try { _history.Execute(new BatchShiftCommand(_document, cues, checked((long)Math.Round(input.Value * 1_000_000)))); DrawTimeline(); ScheduleSubtitleOverlaySync(); }
        catch (Exception exception) { await ShowMessageAsync(L("InvalidShiftTitle"), exception.Message); }
    }

    private void OnSelectAllCuesClick(object sender, RoutedEventArgs e) => SubtitleList.SelectAll();

    private void OnCopyCuesClick(object sender, RoutedEventArgs e)
    {
        var selected = SubtitleList.SelectedItems.Cast<SubtitleCue>().OrderBy(cue => cue.StartMicroseconds).ToArray(); if (selected.Length == 0) return;
        var track = new SubtitleTrack(); foreach (var cue in selected) track.Cues.Add(cue.Clone(false));
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy }; package.SetText(SrtWriter.Write(track)); Clipboard.SetContent(package);
    }

    private async void OnPasteCuesClick(object sender, RoutedEventArgs e)
    {
        var content = Clipboard.GetContent(); if (!content.Contains(StandardDataFormats.Text)) return;
        try
        {
            var text = await content.GetTextAsync(); var track = _document.EnsureTrack(); SubtitleCue[] cues;
            if (text.Contains("-->", StringComparison.Ordinal)) cues = SrtParser.Parse(text).ActiveTrack?.Cues.Select(cue => cue.Clone(false)).ToArray() ?? [];
            else
            {
                var start = (long)(_playback.Position.TotalMilliseconds * 1000);
                cues = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select((line, index) => new SubtitleCue { StartMicroseconds = start + index * 2_000_000, EndMicroseconds = start + (index + 1) * 2_000_000, Text = line, Source = SubtitleCueSource.Manual }).ToArray();
            }
            var commands = cues.Select(cue => (IUndoableSubtitleCommand)new AddSubtitleCommand(_document, track.Cues, cue)).ToArray();
            _history.Execute(new CompositeSubtitleCommand("Paste subtitles", commands)); DrawTimeline(); ScheduleSubtitleOverlaySync();
        }
        catch (Exception exception) { await ShowMessageAsync(L("PasteErrorTitle"), exception.Message); }
    }
    private void OnSubtitleItemClick(object sender, ItemClickEventArgs e) { if (e.ClickedItem is SubtitleCue cue) SeekAndRestartAi(TimeSpan.FromTicks(cue.StartMicroseconds * 10), () => _playback.Seek(TimeSpan.FromTicks(cue.StartMicroseconds * 10), true)); }

    private void OnTimelinePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TimelineCanvas);
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element != TimelineCanvas && element is not FrameworkElement { Tag: SubtitleCue }) element = VisualTreeHelper.GetParent(element);
        if (element is FrameworkElement { Tag: SubtitleCue cue } block)
        {
            _dragCue = cue; _dragStartX = point.Position.X; _dragOldStart = cue.StartMicroseconds; _dragOldEnd = cue.EndMicroseconds;
            var local = e.GetCurrentPoint(block).Position.X;
            _dragMode = local <= 8 ? TimelineDragMode.ResizeStart : local >= block.ActualWidth - 8 ? TimelineDragMode.ResizeEnd : TimelineDragMode.Move;
            SubtitleList.SelectedItem = cue; TimelineCanvas.CapturePointer(e.Pointer); e.Handled = true; return;
        }
        var time = _timelineTransform.XToTime(point.Position.X);
        SeekAndRestartAi(TimeSpan.FromTicks(time * 10), () => _playback.Seek(TimeSpan.FromTicks(time * 10), true));
    }

    private void OnTimelinePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCue is null || !e.GetCurrentPoint(TimelineCanvas).Properties.IsLeftButtonPressed) return;
        var currentX = e.GetCurrentPoint(TimelineCanvas).Position.X;
        var delta = _timelineTransform.XToTime(currentX) - _timelineTransform.XToTime(_dragStartX);
        var trackCues = _document.ActiveTrack?.Cues;
        var candidates = (trackCues is null ? Enumerable.Empty<long>() : trackCues.Where(cue => cue != _dragCue).SelectMany(cue => new[] { cue.StartMicroseconds, cue.EndMicroseconds }))
            .Append((long)(_playback.Position.TotalMilliseconds * 1000));
        var tolerance = Math.Max(1L, _timelineTransform.XToTime(8) - _timelineTransform.XToTime(0));
        switch (_dragMode)
        {
            case TimelineDragMode.Move:
                var duration = _dragOldEnd - _dragOldStart;
                var start = TimelineSnapper.Snap(Math.Max(0, _dragOldStart + delta), candidates, tolerance);
                _dragCue.StartMicroseconds = start; _dragCue.EndMicroseconds = start + duration; break;
            case TimelineDragMode.ResizeStart:
                _dragCue.StartMicroseconds = Math.Min(_dragCue.EndMicroseconds - 10_000, TimelineSnapper.Snap(Math.Max(0, _dragOldStart + delta), candidates, tolerance)); break;
            case TimelineDragMode.ResizeEnd:
                _dragCue.EndMicroseconds = Math.Max(_dragCue.StartMicroseconds + 10_000, TimelineSnapper.Snap(_dragOldEnd + delta, candidates, tolerance)); break;
        }
        DrawTimeline(); e.Handled = true;
    }

    private void OnTimelinePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCue is null) return;
        TimelineCanvas.ReleasePointerCapture(e.Pointer);
        var cue = _dragCue; var newStart = cue.StartMicroseconds; var newEnd = cue.EndMicroseconds;
        cue.StartMicroseconds = _dragOldStart; cue.EndMicroseconds = _dragOldEnd;
        if (newStart != _dragOldStart || newEnd != _dragOldEnd) _history.Execute(new MoveSubtitleCommand(_document, cue, newStart, newEnd));
        _dragCue = null; _dragMode = TimelineDragMode.None; DrawTimeline(); ScheduleSubtitleOverlaySync(); e.Handled = true;
    }

    private void OnTimelinePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TimelineCanvas); var delta = point.Properties.MouseWheelDelta;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl) _timelineTransform.ZoomAt(delta > 0 ? 1.25 : 0.8, point.Position.X);
        else
        {
            var visible = _timelineTransform.VisibleRange(TimelineCanvas.ActualWidth);
            var shift = (visible.End - visible.Start) / 8;
            _timelineTransform.PanTo(_timelineTransform.ViewStartMicroseconds + (delta > 0 ? -shift : shift));
        }
        DrawTimeline(); e.Handled = true;
    }
    private void OnVisualizationSizeChanged(object sender, SizeChangedEventArgs e) => DrawTimeline();

    private void DrawTimeline()
    {
        TimelineCanvas.Children.Clear();
        _timelinePlayhead = null;
        if (_document.ActiveTrack?.Cues is { } cues)
        {
            foreach (var cue in cues)
            {
                var left = _timelineTransform.TimeToX(cue.StartMicroseconds); var right = _timelineTransform.TimeToX(cue.EndMicroseconds);
                if (right < 0) continue;
                if (left > TimelineCanvas.ActualWidth) break;
                var border = new Border
                {
                    Width = Math.Max(3, right - left), Height = Math.Max(20, TimelineCanvas.ActualHeight - 8),
                    Background = ThemeBrush("AccentFillColorDefaultBrush", Windows.UI.Color.FromArgb(255, 40, 130, 220)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 2, 4, 2),
                    Child = new TextBlock
                    {
                        Text = cue.Text,
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 2,
                        FontSize = 13,
                        LineHeight = 17,
                        Foreground = ThemeBrush("TextOnAccentFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 255, 255, 255))
                    },
                    Tag = cue
                };
                Canvas.SetLeft(border, left); Canvas.SetTop(border, 4); TimelineCanvas.Children.Add(border);
            }
        }
        _timelinePlayhead = new Rectangle
        {
            Width = 2,
            Height = TimelineCanvas.ActualHeight,
            Fill = ThemeBrush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(255, 255, 69, 0)),
            IsHitTestVisible = false
        };
        Canvas.SetTop(_timelinePlayhead, 0);
        TimelineCanvas.Children.Add(_timelinePlayhead);
        UpdateTimelinePlayhead(Math.Max(0, _playback.Position.Ticks / 10));
    }

    private void UpdateTimelinePlayhead(long positionMicroseconds)
    {
        if (_timelinePlayhead is null) return;
        var playheadX = _timelineTransform.TimeToX(positionMicroseconds);
        _timelinePlayhead.Height = TimelineCanvas.ActualHeight;
        _timelinePlayhead.Visibility = playheadX >= 0 && playheadX <= TimelineCanvas.ActualWidth ? Visibility.Visible : Visibility.Collapsed;
        if (_timelinePlayhead.Visibility == Visibility.Visible) Canvas.SetLeft(_timelinePlayhead, playheadX);
    }

    private void BindDocument(SubtitleDocument document)
    {
        _document = document;
        _playbackLinkedCueId = null;
        _renderedOverlayContent = null;
        _renderedOverlayFontFamily = null;
        _renderedOverlayCues = [];
        var track = _document.EnsureTrack();
        if (_document.FilePath is null && track.Cues.Count == 0) _document.MarkSaved();
        SubtitleList.ItemsSource = track.Cues; _history.Clear(); DrawTimeline();
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        PlayPauseIcon.Source = PlaybackIconSource(_playback.State == PlaybackState.Playing ? "pause" : "play");
        StatusText.Text = L(_playback.State switch
        {
            PlaybackState.Playing => "PlaybackStatePlaying",
            PlaybackState.Paused => "PlaybackStatePaused",
            PlaybackState.Loading => "PlaybackStateLoading",
            PlaybackState.Idle => "PlaybackStateIdle",
            PlaybackState.Failed => "PlaybackStateFailed",
            _ => "PlaybackStateUninitialized"
        });
    });
    private void OnFirstFrameReady(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        StartPostOpenWorkIfReady();
        if (_playback.State == PlaybackState.Playing && _seekAiRestartCancellation is null) StartCheckedAiPipeline();
    });
    private void OnPlaybackPositionChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _playbackPositionUiRefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RefreshPlaybackPositionUi))
            Interlocked.Exchange(ref _playbackPositionUiRefreshQueued, 0);
    }

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
            var visualizationWidth = TimelineCanvas.ActualWidth;
            var viewportChanged = visualizationWidth > 0 && _timelineTransform.EnsureVisible(positionMicroseconds, visualizationWidth);
            var cue = _document.FindActiveCue(positionMicroseconds);
            if (!_subtitleEditorHasFocus)
            {
                var cueChanged = cue?.Id != _playbackLinkedCueId;
                _playbackLinkedCueId = cue?.Id;
                if (cue is not null)
                {
                    if (!SubtitleList.SelectedItems.Contains(cue)) SubtitleList.SelectedItem = cue;
                    if (cueChanged) SubtitleList.ScrollIntoView(cue, ScrollIntoViewAlignment.Leading);
                }
            }
            if (viewportChanged) DrawTimeline();
            else UpdateTimelinePlayhead(positionMicroseconds);
        }
        finally { Interlocked.Exchange(ref _playbackPositionUiRefreshQueued, 0); }
    }
    private void OnMediaEnded(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_repeatMode == RepeatMode.One)
        {
            TryPlayback(() => { _playback.Seek(TimeSpan.Zero, true); _playback.Play(); });
            return;
        }
        if (_playlistIndex >= 0 && (_playlistIndex < _playlist.Count - 1 || _repeatMode == RepeatMode.All)) await OpenAdjacentMediaAsync(1);
    });
    private void OnTracksChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        AudioTrackCombo.ItemsSource = _playback.Tracks.Where(t => t.Type == MediaTrackType.Audio).ToArray(); AudioTrackCombo.SelectedItem = _playback.Tracks.FirstOrDefault(t => t.Type == MediaTrackType.Audio && t.IsSelected);
        SubtitleTrackCombo.ItemsSource = _playback.Tracks.Where(t => t.Type == MediaTrackType.Subtitle).ToArray(); SubtitleTrackCombo.SelectedItem = _playback.Tracks.FirstOrDefault(t => t.Type == MediaTrackType.Subtitle && t.IsSelected);
    });
    private void OnAudioTrackChanged(object sender, SelectionChangedEventArgs e) { if (AudioTrackCombo.SelectedItem is MediaTrack track) TryPlayback(() => _playback.SelectTrack(MediaTrackType.Audio, track.Id)); }
    private void OnSubtitleTrackChanged(object sender, SelectionChangedEventArgs e) { if (SubtitleTrackCombo.SelectedItem is MediaTrack track) TryPlayback(() => _playback.SelectTrack(MediaTrackType.Subtitle, track.Id)); }
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
        if (e.Key == Windows.System.VirtualKey.Escape && _isFullscreen) { ExitFullscreen(); e.Handled = true; return; }
        var isTextInput = e.OriginalSource is TextBox or PasswordBox;
        if (ctrl && shift && !alt && e.Key == Windows.System.VirtualKey.N) { PlayFromBeginning(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Enter) { ToggleFullscreen(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.F) { ToggleFullscreen(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.M) { OnMuteClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (playbackHasFocus && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Up) { TryPlayback(() => _playback.SetVolume(_playback.Volume + 5)); VolumeSlider.Value = _playback.Volume; e.Handled = true; return; }
        if (playbackHasFocus && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Down) { TryPlayback(() => _playback.SetVolume(_playback.Volume - 5)); VolumeSlider.Value = _playback.Volume; e.Handled = true; return; }
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
        else if (toggleTimelinePanel) { ShowBottomPanelMenuItem.IsChecked = !_bottomPanelVisible; OnToggleBottomPanelClick(this, new RoutedEventArgs()); }
        else if (toggleSidePanel) { ShowRightPanelMenuItem.IsChecked = !_rightPanelVisible; OnToggleRightPanelClick(this, new RoutedEventArgs()); }
        else return;
        e.Handled = true;
    }
    private void SelectRelativeCue(int delta)
    {
        var cues = _document.ActiveTrack?.Cues; if (cues is null || cues.Count == 0) return;
        var index = SubtitleList.SelectedItem is SubtitleCue selected ? cues.IndexOf(selected) : 0; index = Math.Clamp(index + delta, 0, cues.Count - 1);
        SubtitleList.SelectedItem = cues[index]; SubtitleList.ScrollIntoView(cues[index]); SeekAndRestartAi(TimeSpan.FromTicks(cues[index].StartMicroseconds * 10), () => _playback.Seek(TimeSpan.FromTicks(cues[index].StartMicroseconds * 10), true));
    }

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
        FullscreenMenuItem.KeyboardAcceleratorTextOverride = $"{Combine(Shortcut(ShortcutActions.Fullscreen), "Enter", "F")} · Esc";

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
        ToolTipService.SetToolTip(RepeatButton, L(_repeatMode switch { RepeatMode.One => "TooltipRepeatCurrent", RepeatMode.All => "TooltipRepeatPlaylist", _ => "TooltipRepeatOff" }));
        ToolTipService.SetToolTip(CloseButton, L("TooltipClose"));
        UpdateFullscreenButton();
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_playback.State is PlaybackState.Playing or PlaybackState.Paused) TryPlayback(_playback.TogglePause);
        e.Handled = true;
    }
    private void OnToggleRightPanelClick(object sender, RoutedEventArgs e) { _rightPanelVisible = ShowRightPanelMenuItem.IsChecked; ApplyPanelVisibility(); }
    private void OnToggleBottomPanelClick(object sender, RoutedEventArgs e) { _bottomPanelVisible = ShowBottomPanelMenuItem.IsChecked; ApplyPanelVisibility(); }

    private void ToggleFullscreen()
    {
        try
        {
            if (_isFullscreen) ExitFullscreen(); else EnterFullscreen();
        }
        finally { UpdateFullscreenButton(); }
    }

    private void UpdateFullscreenButton()
    {
        FullscreenButton.IsChecked = _isFullscreen;
        FullscreenButtonIcon.Glyph = _isFullscreen ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(FullscreenButton, L(_isFullscreen ? "TooltipExitFullscreen" : "TooltipEnterFullscreen"));
    }

    private void EnterFullscreen()
    {
        if (_appWindow is null || _isFullscreen) return;
        _changingFullscreen = true;
        try
        {
            var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
            _wasMaximizedBeforeFullscreen = _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            if (_wasMaximizedBeforeFullscreen && _appWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
            _windowBoundsBeforeFullscreen = new RectInt32(_appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);
            _workAreaBeforeFullscreen = display.WorkArea;
            _isFullscreen = true;
            MainMenuBar.Visibility = Visibility.Collapsed;
            AppTitleBarArea.Visibility = Visibility.Collapsed;
            PlaybackControls.Visibility = Visibility.Collapsed;
            VisualizationPanel.Visibility = Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Collapsed;
            SubtitlePanel.Visibility = Visibility.Collapsed;
            RightPanelSplitter.Visibility = Visibility.Collapsed;
            RightPanelSplitterColumn.Width = new GridLength(0);
            RightPanelColumn.Width = new GridLength(0);
            BottomPanelSplitter.Visibility = Visibility.Collapsed;
            BottomPanelSplitterRow.Height = new GridLength(0);
            BottomPanelRow.Height = new GridLength(0);
            VideoPlaceholder.Margin = new Thickness(0);
            ApplyFullscreenWindowStyle();
            _appWindow.MoveAndResize(display.OuterBounds);
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            ApplyFullscreenWindowStyle();
            ResetFullscreenCursorIdle();
            _fullscreenHoverTimer?.Start();
            FocusPlaybackSurface();
        }
        catch (Exception exception)
        {
            _isFullscreen = false;
            SetFullscreenCursorHidden(false);
            RestoreWindowStyle();
            RestoreWindowBounds(DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest));
            ApplyPanelVisibility();
            StatusText.Text = exception.Message;
        }
        finally { _changingFullscreen = false; }
    }

    private void ExitFullscreen()
    {
        if (_appWindow is null || !_isFullscreen) return;
        _changingFullscreen = true;
        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
        _isFullscreen = false;
        try
        {
            _appWindow.SetPresenter(AppWindowPresenterKind.Default);
            RestoreWindowStyle();
            RestoreWindowBounds(display);
        }
        finally
        {
            _fullscreenHoverTimer?.Stop();
            SetFullscreenCursorHidden(false);
            _lastFullscreenCursorPosition = null;
            MainMenuBar.Visibility = Visibility.Visible;
            AppTitleBarArea.Visibility = Visibility.Visible;
            PlaybackControls.Visibility = Visibility.Visible;
            VideoPlaceholder.Margin = new Thickness(8, 4, 4, 4);
            ApplyPanelVisibility();
            _changingFullscreen = false;
        }
    }

    private void QueueFullscreenRepair()
    {
        if (_fullscreenRepairQueued || _appWindow is null) return;
        _fullscreenRepairQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _fullscreenRepairQueued = false;
            if (!_isFullscreen || _appWindow is null) return;
            _changingFullscreen = true;
            try
            {
                var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
                ApplyFullscreenWindowStyle();
                _appWindow.MoveAndResize(display.OuterBounds);
                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                ApplyFullscreenWindowStyle();
            }
            catch (Exception exception) { _ = AppLog.WriteAsync("error", "fullscreen", "FULLSCREEN_REPAIR_ERROR", exception.Message, exception); }
            finally { _changingFullscreen = false; }
        });
    }

    private void RestoreWindowBounds(DisplayArea currentDisplay)
    {
        if (_appWindow is null || _windowBoundsBeforeFullscreen is not { } bounds) return;
        if (_workAreaBeforeFullscreen is { } previousWorkArea && (previousWorkArea.X != currentDisplay.WorkArea.X || previousWorkArea.Y != currentDisplay.WorkArea.Y))
        {
            var width = Math.Min(bounds.Width, currentDisplay.WorkArea.Width);
            var height = Math.Min(bounds.Height, currentDisplay.WorkArea.Height);
            var relativeX = Math.Max(0, bounds.X - previousWorkArea.X);
            var relativeY = Math.Max(0, bounds.Y - previousWorkArea.Y);
            bounds = new RectInt32(
                currentDisplay.WorkArea.X + Math.Min(relativeX, Math.Max(0, currentDisplay.WorkArea.Width - width)),
                currentDisplay.WorkArea.Y + Math.Min(relativeY, Math.Max(0, currentDisplay.WorkArea.Height - height)),
                width,
                height);
        }
        _appWindow.MoveAndResize(bounds);
        if (_wasMaximizedBeforeFullscreen && _appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        _windowBoundsBeforeFullscreen = null;
        _workAreaBeforeFullscreen = null;
        _wasMaximizedBeforeFullscreen = false;
    }

    private void ApplyFullscreenWindowStyle()
    {
        var handle = WindowNative.GetWindowHandle(this);
        var style = GetWindowLong(handle, GwlStyle);
        if (!_fullscreenStyleCaptured) { _windowedStyle = style; _fullscreenStyleCaptured = true; }
        var framelessStyle = style & ~(WsCaption | WsThickFrame);
        if (framelessStyle == style) return;
        SetWindowLong(handle, GwlStyle, framelessStyle);
        SetWindowPos(handle, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void RestoreWindowStyle()
    {
        if (!_fullscreenStyleCaptured) return;
        var handle = WindowNative.GetWindowHandle(this);
        SetWindowLong(handle, GwlStyle, _windowedStyle);
        SetWindowPos(handle, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        _fullscreenStyleCaptured = false;
    }

    private void ApplyPanelVisibility()
    {
        if (_isFullscreen) return;
        SubtitlePanel.Visibility = _rightPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        RightPanelSplitter.Visibility = _rightPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        RightPanelSplitterColumn.Width = _rightPanelVisible ? new GridLength(6) : new GridLength(0);
        RightPanelColumn.Width = _rightPanelVisible ? new GridLength(_rightPanelWidth) : new GridLength(0);
        VisualizationPanel.Visibility = _bottomPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        BottomPanelSplitter.Visibility = _bottomPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        BottomPanelSplitterRow.Height = _bottomPanelVisible ? new GridLength(6) : new GridLength(0);
        BottomPanelRow.Height = _bottomPanelVisible ? new GridLength(_bottomPanelHeight) : new GridLength(0);
        StatusPanel.Visibility = _bottomPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        ShowRightPanelMenuItem.IsChecked = _rightPanelVisible;
        ShowBottomPanelMenuItem.IsChecked = _bottomPanelVisible;
        if (_initialized)
        {
            _settings.Window.IsRightPanelVisible = _rightPanelVisible;
            _settings.Window.IsBottomPanelVisible = _bottomPanelVisible;
            _settings.Window.RightPanelWidth = _rightPanelWidth;
            _settings.Window.BottomPanelHeight = _bottomPanelHeight;
        }
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
        RightPanelSectionList.SelectedIndex = Math.Clamp(selectedIndex, 0, 4);
        ApplyRightPanelSection((RightPanelSection)RightPanelSectionList.SelectedIndex);
    }

    private void OnRightPanelSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RightPanelSectionList.SelectedIndex >= 0) ApplyRightPanelSection((RightPanelSection)RightPanelSectionList.SelectedIndex);
    }

    private void ShowRightPanelSection(RightPanelSection section)
    {
        _rightPanelVisible = true;
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
        if (_isFullscreen) return;
        UpdateTitleBarDragRegion();
        var previousRightWidth = _rightPanelWidth;
        var previousBottomHeight = _bottomPanelHeight;
        ClampPanelSizesToAvailable();
        if (Math.Abs(previousRightWidth - _rightPanelWidth) > 0.1 || Math.Abs(previousBottomHeight - _bottomPanelHeight) > 0.1)
            ApplyPanelVisibility();
    }

    private void ClampPanelSizesToAvailable()
    {
        if (MainContentGrid.ActualWidth > 0)
            _rightPanelWidth = Math.Min(_rightPanelWidth, Math.Max(240, MainContentGrid.ActualWidth - 326));
        if (RootGrid.ActualHeight > 0)
            _bottomPanelHeight = Math.Min(_bottomPanelHeight, Math.Max(WindowLayoutSettings.MinimumBottomPanelHeight, RootGrid.ActualHeight - 320));
    }

    private void OnRightPanelSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isFullscreen || !_rightPanelVisible) return;
        var maximum = Math.Max(240, MainContentGrid.ActualWidth - 320 - RightPanelSplitterColumn.ActualWidth);
        _rightPanelWidth = Math.Clamp(_rightPanelWidth - e.HorizontalChange, 240, Math.Min(1200, maximum));
        RightPanelColumn.Width = new GridLength(_rightPanelWidth);
        if (_initialized) _settings.Window.RightPanelWidth = _rightPanelWidth;
    }

    private void OnBottomPanelSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isFullscreen || !_bottomPanelVisible) return;
        var maximum = Math.Max(WindowLayoutSettings.MinimumBottomPanelHeight, RootGrid.ActualHeight - 320);
        _bottomPanelHeight = Math.Clamp(_bottomPanelHeight - e.VerticalChange, WindowLayoutSettings.MinimumBottomPanelHeight, Math.Min(800, maximum));
        BottomPanelRow.Height = new GridLength(_bottomPanelHeight);
        if (_initialized) _settings.Window.BottomPanelHeight = _bottomPanelHeight;
    }

    private void OnFullscreenHoverTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isFullscreen || _appWindow is null) return;
        if (!GetCursorPos(out var cursor))
        {
            SetFullscreenCursorHidden(false);
            return;
        }
        var left = _appWindow.Position.X;
        var top = _appWindow.Position.Y;
        var right = left + _appWindow.Size.Width;
        var bottom = top + _appWindow.Size.Height;
        var now = DateTimeOffset.UtcNow;
        var inside = cursor.X >= left && cursor.X < right && cursor.Y >= top && cursor.Y < bottom;
        var moved = _lastFullscreenCursorPosition is not { } previous || previous.X != cursor.X || previous.Y != cursor.Y;
        _lastFullscreenCursorPosition = cursor;
        if (moved || !inside)
        {
            _fullscreenCursorLastMovedAt = now;
            SetFullscreenCursorHidden(false);
        }
        else if (now - _fullscreenCursorLastMovedAt >= FullscreenCursorHideDelay)
        {
            SetFullscreenCursorHidden(true);
        }
        if (inside)
        {
            if (cursor.Y <= top + 16 || MainMenuBar.Visibility == Visibility.Visible && cursor.Y <= top + 70) _showFullscreenMenuUntil = now.AddSeconds(1.5);
            if (cursor.Y >= bottom - 32 || PlaybackControls.Visibility == Visibility.Visible && cursor.Y >= bottom - 150) _showFullscreenControlsUntil = now.AddSeconds(1.5);
        }
        var verticallyAligned = cursor.Y >= top && cursor.Y < bottom;
        if (verticallyAligned && (cursor.X >= right - 64 && cursor.X <= right + 24 || SubtitlePanel.Visibility == Visibility.Visible && cursor.X >= right - _rightPanelWidth - 40 && cursor.X < right))
            _showFullscreenRightPanelUntil = now.AddSeconds(1.5);
        MainMenuBar.Visibility = now < _showFullscreenMenuUntil ? Visibility.Visible : Visibility.Collapsed;
        var showControls = now < _showFullscreenControlsUntil;
        PlaybackControls.Visibility = showControls ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Visibility = showControls ? Visibility.Visible : Visibility.Collapsed;
        var showRight = now < _showFullscreenRightPanelUntil;
        SubtitlePanel.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        RightPanelSplitter.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        RightPanelSplitterColumn.Width = showRight ? new GridLength(6) : new GridLength(0);
        RightPanelColumn.Width = showRight ? new GridLength(_rightPanelWidth) : new GridLength(0);
    }

    private void ResetFullscreenCursorIdle()
    {
        _fullscreenCursorLastMovedAt = DateTimeOffset.UtcNow;
        _lastFullscreenCursorPosition = GetCursorPos(out var cursor) ? cursor : null;
        SetFullscreenCursorHidden(false);
    }

    private void SetFullscreenCursorHidden(bool hidden)
    {
        if (_fullscreenCursorHidden == hidden)
        {
            if (hidden) _videoHost?.SetCursorHidden(true);
            return;
        }
        _fullscreenCursorHidden = hidden;
        _videoHost?.SetCursorHidden(hidden);
    }
    private async void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = L("StatusCollectingDiagnostics");
        var snapshot = await new DiagnosticsService().CollectAsync(_playback, _asrEngine.State, _settings.Asr.PythonExecutable, _settings.Asr.ModelPath, _settings.Asr.AlignerPath);
        var output = new TextBox { Text = snapshot.ToString(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 650, MinHeight = 420, FontFamily = new FontFamily("Consolas") };
        await ShowDialogAsync(new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = L("DiagnosticsTitle"), Content = output, CloseButtonText = L("CloseButton") });
        StatusText.Text = L("ReadyText");
    }
    private void OnGenerateSubtitleClick(object sender, RoutedEventArgs e)
    {
        _settings.Asr.GenerateSubtitles = GenerateSubtitlesMenuItem.IsChecked;
        if (!GenerateSubtitlesMenuItem.IsChecked) return;
        _subtitleGenerationCompletedForCurrentMedia = false;
        _translationCompletedForCurrentMedia = false;
        StartCheckedAiPipeline();
    }

    private void StartCheckedAiPipeline(long? requestedStartMicroseconds = null)
    {
        if (_aiPipelineTask is { IsCompleted: false } || _aiOperationCancellation is not null) return;
        _aiPipelineTask = RunCheckedAiPipelineAsync(requestedStartMicroseconds);
    }

    private async Task CancelAiPipelineAsync()
    {
        CancelPendingSeekAiRestart();
        var operation = _aiPipelineTask;
        _aiOperationCancellation?.Cancel();
        if (operation is null || operation.IsCompleted) return;
        try { await operation; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { await AppLog.WriteAsync("warning", "ai", "AI_CANCEL_WAIT_ERROR", exception.Message, exception); }
    }

    private void ScheduleAiRestartAfterSeek(TimeSpan requestedPosition)
    {
        if (!GenerateSubtitlesMenuItem.IsChecked && !TranslateMenuItem.IsChecked) return;
        CancelPendingSeekAiRestart();
        var cancellation = new CancellationTokenSource();
        _seekAiRestartCancellation = cancellation;
        var maximum = _playback.Duration > TimeSpan.Zero ? _playback.Duration : TimeSpan.MaxValue;
        var clampedPosition = requestedPosition < TimeSpan.Zero ? TimeSpan.Zero : requestedPosition > maximum ? maximum : requestedPosition;
        _ = RestartAiAfterSeekAsync(cancellation, Math.Max(0, clampedPosition.Ticks / 10));
    }

    private async Task RestartAiAfterSeekAsync(CancellationTokenSource cancellation, long requestedStartMicroseconds)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            await Task.Delay(250, cancellationToken);
            await CancelAiPipelineForSeekAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_seekAiRestartCancellation, cancellation)) return;
            if (GenerateSubtitlesMenuItem.IsChecked) _subtitleGenerationCompletedForCurrentMedia = false;
            if (TranslateMenuItem.IsChecked) _translationCompletedForCurrentMedia = false;
            StartCheckedAiPipeline(requestedStartMicroseconds);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_seekAiRestartCancellation, cancellation)) _seekAiRestartCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelPendingSeekAiRestart()
    {
        var cancellation = _seekAiRestartCancellation;
        _seekAiRestartCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task CancelAiPipelineForSeekAsync(CancellationToken seekCancellationToken)
    {
        var operation = _aiPipelineTask;
        _aiOperationCancellation?.Cancel();
        if (operation is null || operation.IsCompleted) return;
        try { await operation.WaitAsync(seekCancellationToken); }
        catch (OperationCanceledException) when (!seekCancellationToken.IsCancellationRequested) { }
    }

    private async Task RunCheckedAiPipelineAsync(long? requestedStartMicroseconds = null)
    {
        if (_aiOperationCancellation is not null) return;
        var generate = GenerateSubtitlesMenuItem.IsChecked && !_subtitleGenerationCompletedForCurrentMedia;
        var translate = TranslateMenuItem.IsChecked && !_translationCompletedForCurrentMedia;
        if (!generate && !translate) return;
        if (_playback.CurrentSource is not { } source || !File.Exists(source) && !(Uri.TryCreate(source, UriKind.Absolute, out var remoteUri) && remoteUri.Scheme is "http" or "https"))
        {
            StatusText.Text = L("AutomaticSubtitlesOpenMedia");
            return;
        }
        if (generate && !File.Exists(AsrRuntimePaths.PythonExecutable))
        {
            StatusText.Text = L("AsrInstallRequiredMessage");
            return;
        }

        var startMicroseconds = requestedStartMicroseconds ?? Math.Max(0, _playback.Position.Ticks / 10);
        _aiOperationCancellation = new CancellationTokenSource();
        string? temporaryInput = null;
        var translating = false;
        try
        {
            var token = _aiOperationCancellation.Token;
            if (generate)
            {
                if (!File.Exists(source) && _currentHttpHeaders is { Count: > 0 })
                {
                    StatusText.Text = L("StatusPreparingRemoteAsr");
                    temporaryInput = await DownloadAsrInputAsync(source, _currentHttpHeaders, token);
                    source = temporaryInput;
                }
                _translationCompletedForCurrentMedia = await GenerateSubtitlesAsync(source, startMicroseconds, token);
                _subtitleGenerationCompletedForCurrentMedia = true;
            }
            if (TranslateMenuItem.IsChecked && !_translationCompletedForCurrentMedia)
            {
                translating = true;
                _translationCompletedForCurrentMedia = await TranslateSubtitlesAsync(startMicroseconds, token);
            }
        }
        catch (OperationCanceledException) { StatusText.Text = L(translating ? "StatusTranslationCancelled" : "StatusSubtitleGenerationCancelled"); }
        catch (AsrWorkerException exception)
        {
            StatusText.Text = $"{exception.Code}: {exception.Message}";
            await AppLog.WriteAsync("error", "asr", exception.Code, exception.Message, exception);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"AI_ERROR: {exception.Message}";
            await AppLog.WriteAsync("error", "ai", "AI_PIPELINE_ERROR", exception.Message, exception);
        }
        finally
        {
            AsrDownloadProgressBar.Visibility = Visibility.Collapsed;
            AsrDownloadProgressBar.IsIndeterminate = false;
            if (temporaryInput is not null) try { File.Delete(temporaryInput); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            _aiOperationCancellation?.Dispose(); _aiOperationCancellation = null;
        }
    }

    private async Task<bool> GenerateSubtitlesAsync(string source, long startMicroseconds, CancellationToken token)
    {
        StatusText.Text = L("StatusStartingAsr");
        var worker = AsrRuntimePaths.WorkerScript;
        await _asrEngine.StartAsync(_settings.Asr.PythonExecutable, worker, token);
        StatusText.Text = L("StatusLoadingAsr");
        var acceptingLoadProgress = true;
        var loadProgress = new Progress<AsrEvent>(update => { if (acceptingLoadProgress) UpdateAsrModelProgress(update); });
        try { await _asrEngine.LoadModelAsync(_settings.Asr.ModelPath!, _settings.Asr.AlignerPath, _settings.Asr.Device.ToString(), _settings.Asr.Precision.ToString(), loadProgress, token); }
        finally { acceptingLoadProgress = false; }
        var document = new SubtitleDocument();
        var track = document.EnsureTrack("srt"); track.Name = "Qwen3-ASR";
        BindDocument(document);
        _rightPanelVisible = true;
        ApplyPanelVisibility();
        ShowRightPanelSection(RightPanelSection.Subtitles);
        StatusText.Text = F("StatusGeneratingSubtitles", 0d);
        EnableGeneratedSubtitleOverlay();
        var translationQueue = Channel.CreateUnbounded<SubtitleCue>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var translationTask = TranslateGeneratedCuesRealtimeAsync(translationQueue.Reader, document, track, token);
        var translatedCount = 0;
        var pendingCues = new List<SubtitleCue>(32);
        var segmentation = _settings.Subtitle.Segmentation;
        var asrSegmentation = new AsrSegmentationOptions(segmentation.MinimumCueSeconds, segmentation.MaximumCueSeconds, segmentation.MaximumLines, segmentation.TargetCharactersPerLine, segmentation.SilenceSplitSeconds, segmentation.MaximumCharactersPerSecond);
        try
        {
            await foreach (var result in _asrEngine.TranscribeFileAsync(source, _settings.Asr.Language, _settings.Asr.ChunkDurationSeconds, _settings.Asr.UseVad, asrSegmentation, startMicroseconds, token))
            {
                if (!ReferenceEquals(_document, document)) throw new OperationCanceledException(token);
                if (result.Event == "progress")
                {
                    FlushPendingCues();
                    if (result.Progress is { } progress) StatusText.Text = F("StatusGeneratingSubtitles", progress);
                }
                if (result.Event == "segment" && result.Segment is { } segment)
                {
                    var cue = new SubtitleCue { StartMicroseconds = segment.StartMicroseconds, EndMicroseconds = segment.EndMicroseconds, Text = segment.Text, Confidence = segment.Confidence, Source = SubtitleCueSource.AutomaticSpeechRecognition };
                    pendingCues.Add(cue);
                    if (pendingCues.Count >= 32) FlushPendingCues();
                }
            }
        }
        finally
        {
            FlushPendingCues();
            translationQueue.Writer.TryComplete();
            if (token.IsCancellationRequested)
            {
                try { await translationTask; }
                catch (OperationCanceledException) { }
                catch (Exception exception) { await AppLog.WriteAsync("warning", "translation", "TRANSLATION_CANCEL_WAIT_ERROR", exception.Message, exception); }
            }
            else translatedCount = await translationTask;
        }
        if (!ReferenceEquals(_document, document)) return false;
        document.Sort(); document.MarkDirty(); DrawTimeline(); ScheduleSubtitleOverlaySync(force: true);
        StatusText.Text = translatedCount > 0 ? F("StatusTranslated", translatedCount) : F("StatusGeneratedSubtitles", track.Cues.Count);
        return TranslateMenuItem.IsChecked && translatedCount == track.Cues.Count;

        void FlushPendingCues()
        {
            if (pendingCues.Count == 0 || !ReferenceEquals(_document, document)) return;
            track.Cues.AddRange(pendingCues);
            foreach (var cue in pendingCues) translationQueue.Writer.TryWrite(cue);
            pendingCues.Clear();
            ScheduleGeneratedSubtitleUiRefresh();
        }
    }

    private async Task<int> TranslateGeneratedCuesRealtimeAsync(ChannelReader<SubtitleCue> reader, SubtitleDocument targetDocument, SubtitleTrack track, CancellationToken cancellationToken)
    {
        const int realtimeBatchSize = 4;
        var pending = new List<SubtitleCue>(realtimeBatchSize);
        DateTimeOffset? firstPendingAt = null;
        ILlmProvider? provider = null;
        IDisposable? disposable = null;
        LlmService? service = null;
        var translatedCount = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (reader.TryRead(out var cue))
                {
                    pending.Add(cue);
                    firstPendingAt ??= DateTimeOffset.UtcNow;
                }

                if (pending.Count == 0)
                {
                    if (reader.Completion.IsCompleted) break;
                    await Task.Delay(100, cancellationToken);
                    continue;
                }
                if (!TranslateMenuItem.IsChecked)
                {
                    if (reader.Completion.IsCompleted) break;
                    await Task.Delay(100, cancellationToken);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(_settings.Llm.Model)) throw new InvalidOperationException(L("LlmModelMissingMessage"));

                var waitRemaining = TimeSpan.FromMilliseconds(750) - (DateTimeOffset.UtcNow - firstPendingAt!.Value);
                if (pending.Count < realtimeBatchSize && !reader.Completion.IsCompleted && waitRemaining > TimeSpan.Zero)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, waitRemaining.TotalMilliseconds)), cancellationToken);
                    continue;
                }

                provider ??= CreateLlmProvider();
                disposable ??= provider as IDisposable;
                service ??= new LlmService(provider, _settings.Llm.Model, _settings.Llm.ThinkingLevel);
                var batch = pending.Take(realtimeBatchSize).ToArray();
                pending.RemoveRange(0, batch.Length);
                firstPendingAt = pending.Count > 0 ? DateTimeOffset.UtcNow : null;
                var cuesById = batch.ToDictionary(cue => cue.Id);
                var translatedBeforeBatch = translatedCount;
                StatusText.Text = F("StatusTranslating", translatedCount, Math.Max(translatedCount + batch.Length, track.Cues.Count));
                var translated = await service.TranslateAsync(batch, _settings.Llm.TranslationLanguage, batchCompleted: (result, token) =>
                {
                    var completed = translatedBeforeBatch + result.Completed;
                    var total = Math.Max(completed, track.Cues.Count);
                    return ApplyTranslationBatchAsync(targetDocument, new TranslationBatch(result.Items, completed, total), cuesById, token);
                }, batchSize: realtimeBatchSize, cancellationToken: cancellationToken);
                translatedCount += translated.Count;
                await AppLog.WriteAsync("info", "translation", "TRANSLATION_BATCH_COMPLETED", $"Realtime translation completed {translatedCount} cues; {pending.Count} queued.");
            }
            return translatedCount;
        }
        finally { disposable?.Dispose(); }
    }

    private void UpdateAsrModelProgress(AsrEvent update)
    {
        if (update.Stage == "download" && update.Progress is { } progress)
        {
            AsrDownloadProgressBar.Visibility = Visibility.Visible;
            AsrDownloadProgressBar.IsIndeterminate = false;
            AsrDownloadProgressBar.Value = Math.Clamp(progress, 0, 1);
            var model = update.Message ?? "Qwen3-ASR";
            var modelProgress = update.ModelProgress ?? progress;
            StatusText.Text = update.TotalBytes is > 0 && update.DownloadedBytes is { } downloaded
                ? F("StatusDownloadingAsrModel", model, modelProgress, FormatDownloadSize(downloaded), FormatDownloadSize(update.TotalBytes.Value))
                : F("StatusPreparingAsrDownload", model);
            return;
        }
        if (update.Stage == "loading")
        {
            AsrDownloadProgressBar.Visibility = Visibility.Visible;
            AsrDownloadProgressBar.IsIndeterminate = true;
            StatusText.Text = update.ElapsedSeconds is > 0 ? $"{L("StatusLoadingAsr")} ({update.ElapsedSeconds}s)" : L("StatusLoadingAsr");
            return;
        }
        AsrDownloadProgressBar.Visibility = Visibility.Collapsed;
        AsrDownloadProgressBar.IsIndeterminate = false;
        StatusText.Text = L("StatusLoadingAsr");
    }

    private static string FormatDownloadSize(long bytes) => bytes >= 1_073_741_824
        ? $"{bytes / 1_073_741_824d:0.00} GB"
        : $"{bytes / 1_048_576d:0.0} MB";

    private async Task<string> DownloadAsrInputAsync(string source, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        if (Uri.TryCreate(_settings.Network.Proxy, UriKind.Absolute, out var proxyUri)) { handler.Proxy = new WebProxy(proxyUri); handler.UseProxy = true; }
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var headerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerCancellation.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings.Network.TimeoutSeconds, 5, 300)));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCancellation.Token);
        response.EnsureSuccessStatusCode();
        var extension = Path.GetExtension(new Uri(source).AbsolutePath);
        if (extension.Length is 0 or > 12 || extension.Any(character => !char.IsLetterOrDigit(character) && character != '.')) extension = ".media";
        var path = Path.Combine(Path.GetTempPath(), $"aimw-asr-{Guid.NewGuid():N}{extension}");
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
            return path;
        }
        catch { try { File.Delete(path); } catch (IOException) { } throw; }
    }

    private void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        _settings.Llm.TranslateSubtitles = TranslateMenuItem.IsChecked;
        if (!TranslateMenuItem.IsChecked) return;
        _translationCompletedForCurrentMedia = false;
        StartCheckedAiPipeline();
    }

    private async Task<bool> TranslateSubtitlesAsync(long startMicroseconds, CancellationToken cancellationToken)
    {
        var targetDocument = _document;
        var track = targetDocument.ActiveTrack;
        if (track is null || track.Cues.Count == 0)
        {
            StatusText.Text = L("LoadSubtitlesFirst");
            return false;
        }
        if (string.IsNullOrWhiteSpace(_settings.Llm.Model))
        {
            StatusText.Text = L("LlmModelMissingMessage");
            return false;
        }
        var cues = track.Cues
            .Where(cue => cue.EndMicroseconds > startMicroseconds)
            .OrderBy(cue => cue.StartMicroseconds)
            .ToArray();
        if (cues.Length == 0)
        {
            StatusText.Text = F("StatusTranslated", 0);
            return true;
        }

        EnableGeneratedSubtitleOverlay();
        StatusText.Text = F("StatusTranslating", 0, cues.Length);
        var provider = CreateLlmProvider();
        using var disposable = provider as IDisposable;
        var service = new LlmService(provider, _settings.Llm.Model, _settings.Llm.ThinkingLevel);
        var cuesById = cues.ToDictionary(cue => cue.Id);
        var translated = await service.TranslateAsync(cues, _settings.Llm.TranslationLanguage, batchCompleted: (batch, token) => ApplyTranslationBatchAsync(targetDocument, batch, cuesById, token), cancellationToken: cancellationToken);
        if (!ReferenceEquals(_document, targetDocument)) return false;
        StatusText.Text = F("StatusTranslated", translated.Count);
        await AppLog.WriteAsync("info", "translation", "TRANSLATION_COMPLETED", $"Translated {translated.Count} cues from {startMicroseconds} microseconds using {_settings.Llm.Provider}.");
        return true;
    }

    private Task ApplyTranslationBatchAsync(SubtitleDocument targetDocument, TranslationBatch batch, IReadOnlyDictionary<Guid, SubtitleCue> cuesById, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_document, targetDocument)) return Task.CompletedTask;
        if (DispatcherQueue.HasThreadAccess)
        {
            Apply();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try { Apply(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        })) completion.SetException(new InvalidOperationException("The translation result could not be dispatched to the UI thread."));
        return completion.Task;

        void Apply()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_document, targetDocument)) return;
            var commands = batch.Items
                .Where(item => cuesById.ContainsKey(item.Key))
                .Select(item => (IUndoableSubtitleCommand)new EditSubtitleTextCommand(targetDocument, cuesById[item.Key], item.Value))
                .ToArray();
            if (commands.Length > 0) _history.Execute(new CompositeSubtitleCommand("Translate subtitle batch", commands));
            ScheduleGeneratedSubtitleUiRefresh();
            StatusText.Text = F("StatusTranslating", batch.Completed, batch.Total);
        }
    }

    private async void OnSummarizeClick(object sender, RoutedEventArgs e)
    {
        var track = _document.ActiveTrack;
        if (track is null || track.Cues.Count == 0) { await ShowMessageAsync(L("SummaryTitle"), L("LoadSubtitlesFirst")); return; }
        if (string.IsNullOrWhiteSpace(_settings.Llm.Model)) { await ShowMessageAsync(L("LlmModelMissingTitle"), L("LlmModelMissingMessage")); return; }
        if (_aiOperationCancellation is not null) return;
        var choices = new ComboBox { Header = L("SummaryStyleHeader"), MinWidth = 300, ItemsSource = Enum.GetValues<SummaryKind>(), SelectedIndex = 0 };
        if (await ShowDialogAsync(CreateDialog(L("SummarizeTranscriptTitle"), choices, L("SummarizeButton"))) != ContentDialogResult.Primary) return;
        _aiOperationCancellation = new CancellationTokenSource();
        try
        {
            var provider = CreateLlmProvider();
            using var disposable = provider as IDisposable;
            var service = new LlmService(provider, _settings.Llm.Model, _settings.Llm.ThinkingLevel);
            var progress = new Progress<double>(value => StatusText.Text = F("StatusSummarizing", value));
            var summary = await service.SummarizeAsync(track.Cues, (SummaryKind)(choices.SelectedItem ?? SummaryKind.Short), progress, cancellationToken: _aiOperationCancellation.Token);
            var output = new TextBox { Text = summary, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 600, MinHeight = 380 };
            await ShowDialogAsync(new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = L("TranscriptSummaryTitle"), Content = output, CloseButtonText = L("CloseButton") });
            StatusText.Text = L("StatusSummaryComplete");
        }
        catch (OperationCanceledException) { StatusText.Text = L("StatusSummaryCancelled"); }
        catch (Exception exception) { await ShowMessageAsync("LLM_ERROR", exception.Message); }
        finally { _aiOperationCancellation?.Dispose(); _aiOperationCancellation = null; }
    }

    private void OnCancelAiClick(object sender, RoutedEventArgs e)
    {
        CancelPendingSeekAiRestart();
        _aiOperationCancellation?.Cancel();
    }

    private ILlmProvider CreateLlmProvider()
    {
        return new LlmProviderFactory(new WindowsCredentialService()).Create(_settings.Llm.Provider);
    }
    private async void OnAddWebDavServerClick(object sender, RoutedEventArgs e)
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
            _rightPanelVisible = true;
            ApplyPanelVisibility();
            ShowRightPanelSection(RightPanelSection.WebDav);
            RefreshWebDavServerList(server);
            await ConnectWebDavServerAsync(server);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "webdav", "WEBDAV_ADD_ERROR", exception.Message, exception);
            WebDavConnectionStatusText.Text = exception.Message;
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
        WebDavServerList.ItemsSource = null;
        WebDavServerList.ItemsSource = _settings.Network.WebDavServers;
        WebDavEmptyServersText.Visibility = _settings.Network.WebDavServers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected is not null) WebDavServerList.SelectedItem = selected;
        if (_webDavPanelDirectory is null) UpdateWebDavBreadcrumbs();
    }

    private async void OnWebDavServerClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WebDavServerSettings server) await ConnectWebDavServerAsync(server);
    }

    private async Task ConnectWebDavServerAsync(WebDavServerSettings server, Uri? directory = null)
    {
        var credential = _webDavCredentials.Read(server.Id);
        if (credential is null)
        {
            _webDavPanelServerId = null;
            _webDavPanelDirectory = null;
            _webDavEntries = [];
            ApplyWebDavEntryView();
            WebDavParentButton.IsEnabled = false;
            WebDavRefreshButton.IsEnabled = false;
            UpdateWebDavBreadcrumbs();
            WebDavConnectionStatusText.Text = L("WebDavCredentialMissingMessage");
            return;
        }
        _webDavPanelServerId = server.Id;
        _webDavPanelDirectory = EnsureWebDavDirectoryUri(directory ?? credential.RootUri);
        WebDavServerList.SelectedItem = server;
        await RefreshWebDavDirectoryAsync();
    }

    private async Task RefreshWebDavDirectoryAsync()
    {
        if (_webDavPanelServerId is not { } serverId || _webDavPanelDirectory is null) return;
        var server = _settings.Network.WebDavServers.FirstOrDefault(candidate => candidate.Id == serverId);
        if (server is null) return;

        _webDavListingCancellation?.Cancel();
        _webDavListingCancellation?.Dispose();
        _webDavListingCancellation = new CancellationTokenSource();
        var operation = _webDavListingCancellation;
        WebDavProgressRing.IsActive = true;
        WebDavPanelEntryList.IsEnabled = false;
        WebDavParentButton.IsEnabled = false;
        WebDavRefreshButton.IsEnabled = false;
        UpdateWebDavBreadcrumbs(server);
        WebDavConnectionStatusText.Text = F("WebDavConnectingMessage", server.Name);
        try
        {
            var entries = await _webDavClient.ListAsync(server, _webDavPanelDirectory, operation.Token);
            if (operation.IsCancellationRequested) return;
            _webDavEntries = entries.ToArray();
            ApplyWebDavEntryView();
            WebDavConnectionStatusText.Text = F("WebDavConnectedMessage", server.Name, entries.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (operation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "webdav", exception is WebDavException webDavException ? webDavException.Code : "WEBDAV_LIST_ERROR", exception.Message, exception);
            _webDavEntries = [];
            ApplyWebDavEntryView();
            WebDavConnectionStatusText.Text = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_webDavListingCancellation, operation))
            {
                WebDavProgressRing.IsActive = false;
                WebDavPanelEntryList.IsEnabled = true;
                WebDavParentButton.IsEnabled = true;
                WebDavRefreshButton.IsEnabled = true;
            }
        }
    }

    private async void OnWebDavParentClick(object sender, RoutedEventArgs e)
    {
        if (_webDavPanelServerId is not { } serverId || _webDavPanelDirectory is null) return;
        var credential = _webDavCredentials.Read(serverId);
        if (credential is null) return;
        var root = EnsureWebDavDirectoryUri(credential.RootUri);
        var parent = EnsureWebDavDirectoryUri(new Uri(_webDavPanelDirectory, "../"));
        if (!parent.AbsoluteUri.StartsWith(root.AbsoluteUri, StringComparison.OrdinalIgnoreCase)) parent = root;
        _webDavPanelDirectory = parent;
        await RefreshWebDavDirectoryAsync();
    }

    private async void OnWebDavRefreshClick(object sender, RoutedEventArgs e) => await RefreshWebDavDirectoryAsync();

    private async void OnWebDavBreadcrumbItemClick(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs e)
    {
        if (e.Item is not WebDavBreadcrumbEntry entry || entry.Uri is null || entry.Uri == _webDavPanelDirectory) return;
        _webDavPanelDirectory = entry.Uri;
        await RefreshWebDavDirectoryAsync();
    }

    private void UpdateWebDavBreadcrumbs(WebDavServerSettings? server = null)
    {
        if (server is null || _webDavPanelDirectory is null || _webDavPanelServerId is not { } serverId)
        {
            WebDavBreadcrumbBar.ItemsSource = new[] { new WebDavBreadcrumbEntry(L("WebDavSelectServerMessage"), null) };
            return;
        }

        var credential = _webDavCredentials.Read(serverId);
        if (credential is null)
        {
            WebDavBreadcrumbBar.ItemsSource = new[] { new WebDavBreadcrumbEntry(server.Name, null) };
            return;
        }

        var root = EnsureWebDavDirectoryUri(credential.RootUri);
        var current = EnsureWebDavDirectoryUri(_webDavPanelDirectory);
        var entries = new List<WebDavBreadcrumbEntry> { new(server.Name, root) };
        if (!current.AbsoluteUri.StartsWith(root.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            WebDavBreadcrumbBar.ItemsSource = entries;
            return;
        }

        var relativePath = root.MakeRelativeUri(current).OriginalString;
        var accumulatedPath = string.Empty;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            accumulatedPath += segment + "/";
            entries.Add(new WebDavBreadcrumbEntry(Uri.UnescapeDataString(segment), EnsureWebDavDirectoryUri(new Uri(root, accumulatedPath))));
        }
        WebDavBreadcrumbBar.ItemsSource = entries;
    }

    private void OnWebDavFilterTextChanged(object sender, TextChangedEventArgs e) => ApplyWebDavEntryView();

    private void OnWebDavSortClick(object sender, RoutedEventArgs e)
    {
        _webDavSortMode = NextSortMode(_webDavSortMode);
        ApplyWebDavEntryView();
    }

    private void ApplyWebDavEntryView()
    {
        var selectedUri = (WebDavPanelEntryList.SelectedItem as WebDavEntry)?.Uri;
        var filter = WebDavFilterBox.Text.Trim();
        IEnumerable<WebDavEntry> filtered = string.IsNullOrEmpty(filter)
            ? _webDavEntries
            : _webDavEntries.Where(entry => entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        filtered = _webDavSortMode switch
        {
            EntrySortMode.Newest => filtered.OrderByDescending(entry => entry.IsCollection).ThenBy(entry => entry.LastModified is null).ThenByDescending(entry => entry.LastModified).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase),
            EntrySortMode.Oldest => filtered.OrderByDescending(entry => entry.IsCollection).ThenBy(entry => entry.LastModified is null).ThenBy(entry => entry.LastModified).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => filtered.OrderByDescending(entry => entry.IsCollection).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        var view = filtered.ToArray();
        WebDavPanelEntryList.ItemsSource = view;
        if (selectedUri is not null) WebDavPanelEntryList.SelectedItem = view.FirstOrDefault(entry => entry.Uri == selectedUri);
        UpdateSortButton(WebDavSortButton, WebDavSortIcon, _webDavSortMode);
    }

    private static Uri EnsureWebDavDirectoryUri(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
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
    private async void OnScreenRecordingClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_screenRecordingWindow is not null) { _screenRecordingWindow.Activate(); return; }
            _screenRecordingWindow = new ScreenRecordingWindow(this, RootGrid.ActualTheme);
            _screenRecordingWindow.Closed += (_, _) => _screenRecordingWindow = null;
            _screenRecordingWindow.Activate();
        }
        catch (Exception exception)
        {
            _screenRecordingWindow = null;
            await AppLog.WriteAsync("error", "screen-recording", "SCREEN_RECORDING_WINDOW_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("ScreenRecordingErrorTitle"), exception.Message);
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
        RepeatIcon.Source = PlaybackIconSource(_repeatMode switch { RepeatMode.One => "repeat-one", RepeatMode.All => "repeat-all", _ => "repeat" });
    }

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

    private async void OnChooseBrowserFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) await RefreshBrowserAsync(folder.Path);
        }
        catch (Exception exception) { await ShowMessageAsync(L("FolderUnavailableTitle"), exception.Message); }
    }

    private async void OnBrowserHomeClick(object sender, RoutedEventArgs e) => await RefreshBrowserAsync(ResolveDefaultBrowserDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    private async void OnBrowserParentClick(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_browserDirectory);
        if (parent is not null) await RefreshBrowserAsync(parent.FullName);
    }

    private async void OnBrowserRefreshClick(object sender, RoutedEventArgs e) => await RefreshBrowserAsync(_browserDirectory);

    private string ResolveDefaultBrowserDirectory(string fallback)
    {
        var configured = _settings.General.DefaultFolder;
        return !string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured) ? configured : fallback;
    }

    private async Task RefreshBrowserAsync(string directory, string? selectedPath = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!Directory.Exists(directory)) return;
            }
            var entries = await Task.Run(() =>
            {
                const int maximumEntries = 5000;
                var result = new List<BrowserEntry>();
                foreach (var path in Directory.EnumerateDirectories(directory).Take(maximumEntries).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
                {
                    try { result.Add(BrowserEntry.FromDirectory(path)); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                var remaining = Math.Max(0, maximumEntries - result.Count);
                foreach (var path in Directory.EnumerateFiles(directory).Where(IsPlayableMediaPath).Take(remaining).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
                {
                    try { result.Add(BrowserEntry.FromFile(path)); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                if (selectedPath is not null && File.Exists(selectedPath) && !result.Any(item => item.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase)))
                {
                    try { result.Add(BrowserEntry.FromFile(selectedPath)); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                return result.ToArray();
            });
            _browserDirectory = Path.GetFullPath(directory);
            _loadedBrowserDirectory = _browserDirectory;
            UpdateBrowserBreadcrumbs();
            _browserEntries = entries;
            ApplyBrowserEntryView();
            if (selectedPath is not null) SelectBrowserEntry(selectedPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void OnFolderEntryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BrowserEntry entry) return;
        if (entry.IsDirectory) { await RefreshBrowserAsync(entry.Path); return; }
        // Issue loadfile before materializing a potentially large sibling playlist.
        // CompleteMediaOpen keeps the current item available immediately, then the
        // first-frame callback populates the rest of the folder asynchronously.
        await OpenMediaAsync(entry.Path);
    }

    private async void OnBrowserBreadcrumbItemClick(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs e)
    {
        if (e.Item is not BrowserBreadcrumbEntry entry || AreSameDirectory(entry.Path, _browserDirectory)) return;
        await RefreshBrowserAsync(entry.Path);
    }

    private void UpdateBrowserBreadcrumbs()
    {
        var fullPath = Path.GetFullPath(_browserDirectory);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            BrowserBreadcrumbBar.ItemsSource = new[] { new BrowserBreadcrumbEntry(fullPath, fullPath) };
            return;
        }

        var entries = new List<BrowserBreadcrumbEntry> { new(root, root) };
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath != ".")
        {
            var accumulatedPath = root;
            foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.IsNullOrEmpty(segment)) continue;
                accumulatedPath = Path.Combine(accumulatedPath, segment);
                entries.Add(new BrowserBreadcrumbEntry(segment, accumulatedPath));
            }
        }
        BrowserBreadcrumbBar.ItemsSource = entries;
    }

    private void OnBrowserFilterTextChanged(object sender, TextChangedEventArgs e) => ApplyBrowserEntryView();

    private void OnBrowserSortClick(object sender, RoutedEventArgs e)
    {
        _browserSortMode = NextSortMode(_browserSortMode);
        ApplyBrowserEntryView();
    }

    private void ApplyBrowserEntryView()
    {
        var selectedPath = (FolderEntryList.SelectedItem as BrowserEntry)?.Path;
        var filter = BrowserFilterBox.Text.Trim();
        IEnumerable<BrowserEntry> filtered = string.IsNullOrEmpty(filter)
            ? _browserEntries
            : _browserEntries.Where(entry => entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        filtered = _browserSortMode switch
        {
            EntrySortMode.Newest => filtered.OrderByDescending(entry => entry.IsDirectory).ThenByDescending(entry => entry.LastModified).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase),
            EntrySortMode.Oldest => filtered.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.LastModified).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => filtered.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        var view = filtered.ToArray();
        FolderEntryList.ItemsSource = view;
        if (selectedPath is not null) FolderEntryList.SelectedItem = view.FirstOrDefault(entry => entry.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
        UpdateSortButton(BrowserSortButton, BrowserSortIcon, _browserSortMode);
    }

    private static EntrySortMode NextSortMode(EntrySortMode mode) => mode switch
    {
        EntrySortMode.Name => EntrySortMode.Newest,
        EntrySortMode.Newest => EntrySortMode.Oldest,
        _ => EntrySortMode.Name
    };

    private static void UpdateSortButton(AppBarButton button, FontIcon icon, EntrySortMode mode)
    {
        button.Label = L(mode switch
        {
            EntrySortMode.Newest => "SortNewest",
            EntrySortMode.Oldest => "SortOldest",
            _ => "SortName"
        });
        icon.Glyph = mode switch
        {
            EntrySortMode.Newest => "\uE74B",
            EntrySortMode.Oldest => "\uE74A",
            _ => "\uE8CB"
        };
    }

    private void OnBrowserEntryRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserEntry entry }) FolderEntryList.SelectedItem = entry;
    }

    private async void OnAddBrowserFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (FolderEntryList.SelectedItem is not BrowserEntry entry) return;
        await AddFavoriteAsync(new LocalMediaSource(entry.Path), entry.IsDirectory);
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

    private async void OnWebDavPanelEntryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WebDavEntry entry || _webDavPanelServerId is not { } serverId) return;
        if (entry.IsCollection)
        {
            _webDavPanelDirectory = EnsureWebDavDirectoryUri(entry.Uri);
            await RefreshWebDavDirectoryAsync();
            return;
        }
        var server = _settings.Network.WebDavServers.FirstOrDefault(candidate => candidate.Id == serverId);
        if (server is null) { await ShowMessageAsync(L("WebDavServerMissingTitle"), L("RecentServerMissingMessage")); return; }
        await OpenWebDavMediaAsync(server, entry, (WebDavPanelEntryList.ItemsSource as IEnumerable<WebDavEntry>)?.ToArray());
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
                SynchronizeWebDavPanel(server, entry.Uri, siblings);
            }
            catch (Exception exception)
            {
                await AppLog.WriteAsync("warning", "webdav", "WEBDAV_SIBLING_LIST_ERROR", exception.Message, exception);
            }
        }

        var mediaEntries = (siblings ?? [])
            .Where(IsPlayableWebDavEntry)
            .ToList();
        if (!mediaEntries.Any(candidate => UrisEqual(candidate.Uri, entry.Uri))) mediaEntries.Add(entry);

        _playlist.Clear();
        _playlist.AddRange(mediaEntries.Select(candidate => PlaylistEntry.FromWebDav(server.Id, candidate, headers)));
        _playlistIndex = _playlist.FindIndex(item => UrisEqual(new Uri(item.Path), entry.Uri));
        if (_playlistIndex < 0) _playlistIndex = 0;
        await OpenPlaylistEntryAsync(_playlist[_playlistIndex]);
    }

    private static bool IsPlayableWebDavEntry(WebDavEntry entry) =>
        !entry.IsCollection &&
        (IsPlayableMediaPath(Uri.UnescapeDataString(entry.Uri.AbsolutePath)) ||
         entry.ContentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
         entry.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true);

    private static bool UrisEqual(Uri left, Uri right) =>
        left.AbsoluteUri.Equals(right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    private void SynchronizeWebDavPanel(WebDavServerSettings server, Uri mediaUri, IReadOnlyList<WebDavEntry> entries)
    {
        var directory = EnsureWebDavDirectoryUri(new Uri(mediaUri, "."));
        var changedDirectory = _webDavPanelServerId != server.Id || _webDavPanelDirectory is null || !UrisEqual(_webDavPanelDirectory, directory);
        _webDavPanelServerId = server.Id;
        _webDavPanelDirectory = directory;
        WebDavServerList.SelectedItem = server;
        if (changedDirectory) WebDavFilterBox.Text = string.Empty;
        _webDavEntries = entries.ToArray();
        UpdateWebDavBreadcrumbs(server);
        ApplyWebDavEntryView();
    }

    private void SelectWebDavEntry(Guid serverId, Uri uri)
    {
        if (_webDavPanelServerId != serverId || WebDavPanelEntryList.ItemsSource is not IEnumerable<WebDavEntry> entries) return;
        var selectedEntry = entries.FirstOrDefault(entry => UrisEqual(entry.Uri, uri));
        if (selectedEntry is null) return;
        WebDavPanelEntryList.SelectedItem = selectedEntry;
        WebDavPanelEntryList.ScrollIntoView(selectedEntry);
    }

    private void OnWebDavEntryRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WebDavEntry entry }) WebDavPanelEntryList.SelectedItem = entry;
    }

    private async void OnAddWebDavFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (WebDavPanelEntryList.SelectedItem is not WebDavEntry entry || _webDavPanelServerId is not { } serverId) return;
        await AddFavoriteAsync(new WebDavMediaSource(serverId, entry.Uri, entry.Name), entry.IsCollection);
    }

    private static bool IsPlayableMediaPath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".wmv" or ".m4v" or ".ts" or ".m2ts" or ".mp3" or ".flac" or ".wav" or ".m4a" or ".aac" or ".ogg" or ".opus";

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
        UpdateFavoriteCommands();
    }

    private async void OnFavoriteDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        await _historyService.LoadFavoritesAsync();
        if (!_historyService.ReorderFavorites(_favoriteEntries.Select(entry => entry.Item.Location))) return;
        RefreshFavoritesList();
        await _historyService.SaveFavoritesAsync();
    }

    private void OnFavoriteSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFavoriteCommands();

    private void UpdateFavoriteCommands()
    {
        var hasSelection = FavoriteList.SelectedItems.Count > 0;
        OpenFavoriteButton.IsEnabled = hasSelection;
        RemoveSelectedFavoritesButton.IsEnabled = hasSelection;
    }

    private async void OnOpenFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (FavoriteList.SelectedItem is FavoriteListEntry entry) await OpenFavoriteAsync(entry.Item);
    }

    private async void OnFavoriteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FavoriteListEntry entry) await OpenFavoriteAsync(entry.Item);
    }

    private async void OnRemoveSelectedFavoritesClick(object sender, RoutedEventArgs e)
    {
        await _historyService.LoadFavoritesAsync();
        var selected = FavoriteList.SelectedItems.OfType<FavoriteListEntry>().ToArray();
        if (selected.Length == 0) return;
        var removed = false;
        foreach (var entry in selected) removed |= _historyService.RemoveFavorite(entry.Item.Location);
        if (!removed) return;
        await _historyService.SaveFavoritesAsync();
        RefreshFavoritesList();
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
                _rightPanelVisible = true;
                ApplyPanelVisibility();
                ShowRightPanelSection(RightPanelSection.WebDav);
                await ConnectWebDavServerAsync(server, new Uri(favorite.Location));
                return;
            }
            if (!Directory.Exists(favorite.Location)) { await ShowMessageAsync(L("FolderUnavailableTitle"), favorite.Location); return; }
            _rightPanelVisible = true;
            ApplyPanelVisibility();
            ShowRightPanelSection(RightPanelSection.Explorer);
            await RefreshBrowserAsync(favorite.Location);
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
        _historyService.AddRecent(_currentMediaSource, (long)(_playback.Position.TotalMilliseconds * 1000), _settings.General.RecentMediaCount);
    }

    private void ScheduleSubtitleOverlaySync(bool force = false)
    {
        if (!_playback.IsAvailable || _playback.CurrentSource is null || _document.ActiveTrack is null) return;
        _overlaySyncCancellation?.Cancel(); _overlaySyncCancellation?.Dispose(); _overlaySyncCancellation = new CancellationTokenSource();
        var token = _overlaySyncCancellation.Token;
        _ = SyncSubtitleOverlayAsync(token, force);
    }

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
            ScheduleSubtitleOverlaySync();
        })) Interlocked.Exchange(ref _generatedSubtitleUiRefreshQueued, 0);
    }

    private void EnableGeneratedSubtitleOverlay()
    {
        if (!_playback.IsAvailable) return;
        TryPlayback(() => _playback.SetSubtitleVisibility(true));
        SubtitleVisibilityMenuItem.IsChecked = _playback.AreSubtitlesVisible;
        _settings.Playback.ShowSubtitles = _playback.AreSubtitlesVisible;
    }

    private async Task SyncSubtitleOverlayAsync(CancellationToken cancellationToken, bool force)
    {
        try
        {
            await Task.Delay(150, cancellationToken);
            var document = _document;
            var track = document.ActiveTrack;
            if (track is null) return;
            var fontFamily = _settings.Subtitle.FontFamily;
            var cues = track.Cues
                .Select(cue => new AssCueSnapshot(cue.Id, cue.StartMicroseconds, cue.EndMicroseconds, cue.Text, cue.Style, cue.Speaker))
                .OrderBy(cue => cue.StartMicroseconds)
                .ToArray();
            var nativeHeader = track.NativeHeader;
            var content = await Task.Run(() => AssWriter.Write(cues, nativeHeader, fontFamily), cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(_document, document)) return;
            if (string.Equals(content, _renderedOverlayContent, StringComparison.Ordinal)) return;
            while (true)
            {
                var position = (long)(_playback.Position.TotalMilliseconds * 1000);
                var renderedActive = FindActiveOverlayCue(_renderedOverlayCues, position);
                var currentActive = FindActiveOverlayCue(cues, position);
                var currentSubtitleIsUnchanged = renderedActive == currentActive;
                var fontIsUnchanged = string.Equals(fontFamily, _renderedOverlayFontFamily, StringComparison.OrdinalIgnoreCase);
                if (!force && _playback.State == PlaybackState.Playing && _renderedOverlayContent is not null && currentSubtitleIsUnchanged && fontIsUnchanged)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                if (!ReferenceEquals(_document, document)) return;
                await _overlayWriteLock.WaitAsync(cancellationToken);
                try
                {
                    await File.WriteAllTextAsync(_editorOverlayPath, content, new System.Text.UTF8Encoding(false), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    _playback.UpdateEditorSubtitle(_editorOverlayPath);
                    _renderedOverlayContent = content;
                    _renderedOverlayFontFamily = fontFamily;
                    _renderedOverlayCues = cues;
                }
                finally { _overlayWriteLock.Release(); }
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { DispatcherQueue.TryEnqueue(() => StatusText.Text = $"Subtitle overlay update failed: {exception.Message}"); }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_isFullscreen && sender.Presenter is OverlappedPresenter presenter && presenter.State != OverlappedPresenterState.Minimized) CaptureWindowPlacement(sender, presenter);
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
    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
    private System.Text.Encoding ResolveSubtitleEncoding()
    {
        var name = string.IsNullOrWhiteSpace(_settings.Subtitle.Encoding) ? "utf-8" : _settings.Subtitle.Encoding.Trim();
        return name.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || name.Equals("utf8", StringComparison.OrdinalIgnoreCase)
            ? new System.Text.UTF8Encoding(false, true)
            : System.Text.Encoding.GetEncoding(name, System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback);
    }
    private async Task ShutdownAsync()
    {
        _fullscreenHoverTimer?.Stop();
        SetFullscreenCursorHidden(false);
        if (_appWindow is not null)
        {
            _appWindow.Changed -= OnAppWindowChanged;
        }
        await _historyService.LoadRecentAsync();
        RememberCurrentPosition();

        _postOpenCancellation?.Cancel();
        _overlaySyncCancellation?.Cancel();
        CancelPendingSeekAiRestart();
        _aiOperationCancellation?.Cancel();
        _webDavListingCancellation?.Cancel();

        _settingsWindow?.Close();
        if (_cameraWindow is { } cameraWindow) await cameraWindow.CloseAsync();
        if (_screenRecordingWindow is { } screenRecordingWindow) await screenRecordingWindow.CloseAsync();

        if (_aiPipelineTask is { IsCompleted: false } aiPipeline)
        {
            try { await aiPipeline.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException exception) { await AppLog.WriteAsync("warning", "shutdown", "AI_PIPELINE_SHUTDOWN_TIMEOUT", exception.Message, exception); }
        }

        try { await _historyService.SaveRecentAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "HISTORY_SAVE_ERROR", exception.Message, exception); }
        try { await SettingsService.CreateDefault().SaveAsync(_settings); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "SETTINGS_SAVE_ERROR", exception.Message, exception); }
        _postOpenCancellation?.Dispose(); _postOpenCancellation = null;
        _overlaySyncCancellation?.Dispose(); _overlaySyncCancellation = null;
        _aiOperationCancellation?.Dispose(); _aiOperationCancellation = null;
        _webDavListingCancellation?.Dispose(); _webDavListingCancellation = null;
        _webDavClient.Dispose();
        try { await _asrEngine.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "ASR_DISPOSE_ERROR", exception.Message, exception); }
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
        try { File.Delete(_editorOverlayPath); } catch (IOException) { }

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
        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
        }
        Closed -= OnWindowClosed;
        Application.Current.Exit();
    }

    private sealed record PlaylistEntry(string Path, string DisplayName, IReadOnlyDictionary<string, string>? HttpHeaders = null, IMediaSource? MediaSource = null)
    {
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

    private sealed record BrowserBreadcrumbEntry(string Label, string Path);
    private sealed record WebDavBreadcrumbEntry(string Label, Uri? Uri);

    private sealed record RightPanelSectionEntry(string IconGlyph, string Label);
    private enum RightPanelSection { Explorer, Playlist, WebDav, Favorites, Subtitles }

    private sealed record PendingPostOpenWork(string Source, bool PopulateSiblingPlaylist, CancellationToken CancellationToken);
    private static AssCueSnapshot? FindActiveOverlayCue(IReadOnlyList<AssCueSnapshot> cues, long positionMicroseconds)
    {
        var low = 0;
        var high = cues.Count - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (cues[middle].StartMicroseconds <= positionMicroseconds) { candidate = middle; low = middle + 1; }
            else high = middle - 1;
        }
        if (candidate < 0) return null;
        var cue = cues[candidate];
        return positionMicroseconds < cue.EndMicroseconds ? cue : null;
    }

    private sealed record BrowserEntry(string Path, bool IsDirectory, long? Length, DateTime LastModified)
    {
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";
        public string Details => IsDirectory || Length is null ? string.Empty : FormatBytes(Length.Value);
        public static BrowserEntry FromDirectory(string path)
        {
            var info = new DirectoryInfo(path);
            return new BrowserEntry(path, true, null, info.LastWriteTimeUtc);
        }
        public static BrowserEntry FromFile(string path)
        {
            var info = new FileInfo(path);
            return new BrowserEntry(path, false, info.Length, info.LastWriteTimeUtc);
        }
        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            var display = (double)Math.Max(0, bytes);
            var unit = 0;
            while (display >= 1024 && unit < units.Length - 1) { display /= 1024; unit++; }
            return $"{display:0.##} {units[unit]}";
        }
    }

    private enum EntrySortMode { Name, Newest, Oldest }

    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly TimeSpan FullscreenCursorHideDelay = TimeSpan.FromSeconds(2);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);

    private enum TimelineDragMode { None, Move, ResizeStart, ResizeEnd }
    private enum RepeatMode { Off, One, All }
}
