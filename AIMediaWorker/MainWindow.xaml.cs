using AIMediaWorker.Playback;
using AIMediaWorker.Subtitle;
using AIMediaWorker.Subtitle.Editing;
using AIMediaWorker.Subtitle.Parsing;
using AIMediaWorker.Subtitle.Writing;
using AIMediaWorker.Timeline;
using AIMediaWorker.Waveform;
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
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Line = Microsoft.UI.Xaml.Shapes.Line;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.Storage;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

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
    private SettingsWindow? _settingsWindow;
    private AppSettings _settings = new();
    private readonly WindowsCredentialService _windowsCredentials = new();
    private readonly WebDavCredentialStore _webDavCredentials;
    private readonly WebDavClient _webDavClient;
    private CancellationTokenSource? _webDavListingCancellation;
    private Uri? _webDavPanelDirectory;
    private readonly AsrWorkerClient _asrEngine = new();
    private CancellationTokenSource? _aiOperationCancellation;
    private readonly SemaphoreSlim _dialogLock = new(1, 1);
    private CancellationTokenSource? _waveformCancellation;
    private WaveformData _waveform = WaveformData.Empty;
    private Rectangle? _waveformPlayhead;
    private readonly WaveformGenerator _waveformGenerator = new();
    private readonly WaveformCache _waveformCache = new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIMediaWorker", "Waveforms"));
    private readonly MediaHistoryService _historyService = new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIMediaWorker", "history.json"));
    private IMediaSource? _currentMediaSource;
    private IReadOnlyDictionary<string, string>? _currentHttpHeaders;
    private SubtitleCue? _dragCue;
    private TimelineDragMode _dragMode;
    private double _dragStartX;
    private long _dragOldStart;
    private long _dragOldEnd;
    private bool _allowClose;
    private TimeSpan? _abStart;
    private CancellationTokenSource? _overlaySyncCancellation;
    private bool _subtitleEditorHasFocus;
    private readonly List<string> _playlist = [];
    private int _playlistIndex = -1;
    private RepeatMode _repeatMode;
    private string _browserDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    private Guid? _webDavPanelServerId;
    private DispatcherQueueTimer? _fullscreenHoverTimer;
    private DateTimeOffset _showFullscreenMenuUntil;
    private DateTimeOffset _showFullscreenControlsUntil;
    private DateTimeOffset _showFullscreenRightPanelUntil;
    private string? _pendingLaunchSource;
    private string[]? _pendingDroppedFiles;
    private PendingPostOpenWork? _pendingPostOpenWork;
    private CancellationTokenSource? _postOpenCancellation;
    private readonly string _editorOverlayPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AIMediaWorker-{Environment.ProcessId}-{Guid.NewGuid():N}.ass");

    public MainWindow() : this(null, new AppSettings()) { }

    public MainWindow(string? initialSource) : this(initialSource, new AppSettings()) { }

    public MainWindow(string? initialSource, AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _webDavCredentials = new WebDavCredentialStore(_windowsCredentials);
        _webDavClient = new WebDavClient(_windowsCredentials, timeout: TimeSpan.FromSeconds(_settings.Network.TimeoutSeconds));
        InitializeComponent();
        _pendingLaunchSource = initialSource;
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
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
        PositionSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPositionSliderPointerPressed), true);
        PositionSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPositionSliderPointerReleased), true);
        PositionSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPositionSliderPointerReleased), true);
        _playback.StateChanged += OnPlaybackStateChanged;
        _playback.PositionChanged += OnPlaybackPositionChanged;
        _playback.TracksChanged += OnTracksChanged;
        _playback.ErrorOccurred += OnPlaybackError;
        _playback.MediaEnded += OnMediaEnded;
        _fullscreenHoverTimer = DispatcherQueue.CreateTimer();
        _fullscreenHoverTimer.Interval = TimeSpan.FromMilliseconds(100);
        _fullscreenHoverTimer.Tick += OnFullscreenHoverTick;
        BindDocument(new SubtitleDocument());
    }

    public void ApplySavedWindowPlacement(WindowLayoutSettings layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _rightPanelVisible = layout.IsRightPanelVisible;
        _bottomPanelVisible = layout.IsBottomPanelVisible;
        _rightPanelWidth = Math.Clamp(layout.RightPanelWidth, 240, 1200);
        _bottomPanelHeight = Math.Clamp(layout.BottomPanelHeight, 100, 800);
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
        try
        {
            _rightPanelVisible = _settings.Window.IsRightPanelVisible;
            _bottomPanelVisible = _settings.Window.IsBottomPanelVisible;
            _rightPanelWidth = Math.Clamp(_settings.Window.RightPanelWidth, 240, 1200);
            _bottomPanelHeight = Math.Clamp(_settings.Window.BottomPanelHeight, 100, 800);
            ClampPanelSizesToAvailable();
            ApplyPanelVisibility();
            var historyLoad = _historyService.LoadAsync();
            _videoHost = new NativeVideoHost(this, VideoPlaceholder);
            _videoHost.FilesDropped += OnNativeVideoFilesDropped;
            _videoHost.Clicked += OnNativeVideoClicked;
            var playbackInitialization = _playback.InitializeAsync(_videoHost.Create(), _settings.Playback.HardwareDecoder, _settings.Playback.Renderer);
            await Task.WhenAll(historyLoad, playbackInitialization);
            RebuildRecentMenu();
            RebuildFavoritesMenu();
            RefreshWebDavServerList();
            ApplyTheme(_settings.General.Theme);
            if (_playback.IsAvailable)
            {
                _playback.SetVolume(_settings.Playback.DefaultVolume); _playback.SetRate(_settings.Playback.PlaybackRate);
                _playback.ConfigureNetwork(TimeSpan.FromSeconds(_settings.Network.TimeoutSeconds), _settings.Network.Proxy);
                _playback.ConfigurePreferredLanguages(_settings.Playback.DefaultAudioLanguage, _settings.Playback.DefaultSubtitleLanguage);
                _playback.ConfigureSubtitleStyle(_settings.Subtitle.FontFamily, _settings.Subtitle.FontSize, _settings.Subtitle.Color, _settings.Subtitle.Background, _settings.Subtitle.Outline, _settings.Subtitle.BottomMargin);
                _playback.SetSubtitleVisibility(_settings.Playback.ShowSubtitles);
            }
            SubtitleVisibilityMenuItem.IsChecked = _settings.Playback.ShowSubtitles;
            RateCombo.ItemsSource = new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 };
            RateCombo.SelectedItem = RateCombo.Items.Cast<double>().OrderBy(value => Math.Abs(value - _settings.Playback.PlaybackRate)).First();
            SeekBackButton.Content = $"−{_settings.Playback.SeekIntervalSeconds:0.#}s";
            SeekForwardButton.Content = $"+{_settings.Playback.SeekIntervalSeconds:0.#}s";
            UpdateShortcutHints();
            UpdatePlaylistButtons();
            StatusText.Text = _playback.IsAvailable ? L("StatusLibmpvReady") : L("StatusPlaybackUnavailable");
            if (_playback.IsAvailable && _pendingDroppedFiles is { Length: > 0 } droppedFiles)
            {
                _pendingDroppedFiles = null;
                _pendingLaunchSource = null;
                await OpenFilesAsPlaylistAsync(droppedFiles);
            }
            else if (_playback.IsAvailable && _pendingLaunchSource is { Length: > 0 } launchSource)
            {
                _pendingLaunchSource = null;
                await OpenMediaAsync(launchSource);
            }
            else _ = RefreshBrowserAsync(_browserDirectory);
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

    private void OnNativeVideoClicked(object? sender, EventArgs e)
    {
        if (_playback.State is PlaybackState.Playing or PlaybackState.Paused) TryPlayback(_playback.TogglePause);
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

    private async Task OpenMediaAsync(string source, IReadOnlyDictionary<string, string>? httpHeaders = null, IMediaSource? mediaSource = null, bool preservePlaylist = false)
    {
        if (!await ConfirmDiscardChangesAsync(L("ActionOpenMedia"))) return;
        try
        {
            if (!preservePlaylist)
            {
                _playlist.Clear();
                if (File.Exists(source)) { _playlist.Add(Path.GetFullPath(source)); _playlistIndex = 0; }
                else _playlistIndex = -1;
            }
            RememberCurrentPosition();
            await _playback.OpenAsync(source, httpHeaders);
            _currentMediaSource = mediaSource ?? MediaSourceFactory.Parse(source);
            _currentHttpHeaders = httpHeaders is null ? null : new Dictionary<string, string>(httpHeaders, StringComparer.OrdinalIgnoreCase);
            _historyService.AddRecent(_currentMediaSource, 0, _settings.General.RecentMediaCount);
            _ = SaveHistoryAfterOpenAsync();
            RebuildRecentMenu();
            var blank = new SubtitleDocument(); blank.EnsureTrack(); blank.MarkSaved(); BindDocument(blank);
            StatusText.Text = source;
            VideoStatusText.Visibility = Visibility.Collapsed;
            QueuePostOpenWork(source, httpHeaders is null || httpHeaders.Count == 0, !preservePlaylist);
            UpdatePlaylistButtons();
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "playback", "OPEN_MEDIA_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("PlaybackErrorTitle"), exception.Message);
        }
    }

    private async Task SaveHistoryAfterOpenAsync()
    {
        try { await _historyService.SaveAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "history", "HISTORY_SAVE_AFTER_OPEN_ERROR", exception.Message, exception); }
    }

    private void QueuePostOpenWork(string source, bool generateWaveform, bool populateSiblingPlaylist)
    {
        _waveformCancellation?.Cancel();
        _postOpenCancellation?.Cancel();
        _postOpenCancellation?.Dispose();
        _postOpenCancellation = new CancellationTokenSource();
        _pendingPostOpenWork = new PendingPostOpenWork(source, generateWaveform, populateSiblingPlaylist, _postOpenCancellation.Token);
        if (!generateWaveform) { _waveform = WaveformData.Empty; DrawWaveform(); }
        if (_playback.State is PlaybackState.Playing or PlaybackState.Paused) StartPostOpenWorkIfReady();
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
            // Let mpv present its first frames before FFmpeg and folder enumeration compete for I/O and CPU.
            await Task.Delay(750, work.CancellationToken);
            if (!string.Equals(_playback.CurrentSource, work.Source, StringComparison.OrdinalIgnoreCase)) return;
            var tasks = new List<Task>();
            if (File.Exists(work.Source))
            {
                var fullPath = Path.GetFullPath(work.Source);
                tasks.Add(RefreshBrowserForOpenedFileAsync(fullPath));
                if (work.PopulateSiblingPlaylist) tasks.Add(PopulateSiblingPlaylistAsync(fullPath));
            }
            if (work.GenerateWaveform) tasks.Add(GenerateWaveformAsync(work.Source));
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "post-open", "POST_OPEN_WORK_ERROR", exception.Message, exception);
        }
    }

    private async Task RefreshBrowserForOpenedFileAsync(string fullPath)
    {
        try
        {
            if (Path.GetDirectoryName(fullPath) is not { } directory) return;
            if (AreSameDirectory(directory, _browserDirectory))
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
        _playlist.AddRange(files.Where(path => !IsSubtitlePath(path)));
        if (_playlist.Count == 0) return;
        _playlistIndex = 0;
        await OpenMediaAsync(_playlist[0], preservePlaylist: true);
    }

    private static bool IsSubtitlePath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa";

    private async Task PopulateSiblingPlaylistAsync(string currentPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(currentPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null) return;
            var siblings = await Task.Run(() => Directory.EnumerateFiles(directory).Where(IsPlayableMediaPath).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).Take(5000).Select(Path.GetFullPath).ToArray());
            if (!string.Equals(_playback.CurrentSource, fullPath, StringComparison.OrdinalIgnoreCase)) return;
            var index = Array.FindIndex(siblings, path => path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            _playlist.Clear(); _playlist.AddRange(siblings); _playlistIndex = index; UpdatePlaylistButtons();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TryPlayback(_playback.TogglePause);
    private void PlayFromBeginning() => TryPlayback(() => { _playback.Seek(TimeSpan.Zero, true); _playback.Play(); });
    private void OnGoToBeginningClick(object sender, RoutedEventArgs e) => TryPlayback(() => _playback.Seek(TimeSpan.Zero, true));
    private void OnStopClick(object sender, RoutedEventArgs e) => TryPlayback(_playback.Stop);
    private async void OnPreviousMediaClick(object sender, RoutedEventArgs e) => await OpenAdjacentMediaAsync(-1);
    private async void OnNextMediaClick(object sender, RoutedEventArgs e) => await OpenAdjacentMediaAsync(1);
    private void OnFrameStepClick(object sender, RoutedEventArgs e) => TryPlayback(() => _playback.FrameStep());
    private void OnSeekBackClick(object sender, RoutedEventArgs e) => TryPlayback(() => _playback.SeekRelative(TimeSpan.FromSeconds(-_settings.Playback.SeekIntervalSeconds)));
    private void OnSeekForwardClick(object sender, RoutedEventArgs e) => TryPlayback(() => _playback.SeekRelative(TimeSpan.FromSeconds(_settings.Playback.SeekIntervalSeconds)));
    private void OnMuteClick(object sender, RoutedEventArgs e) => TryPlayback(() => _playback.SetMute(!_playback.IsMuted));
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
        RepeatButton.Content = _repeatMode switch { RepeatMode.One => "↻1", RepeatMode.All => "↻∞", _ => "↪" };
        ToolTipService.SetToolTip(RepeatButton, _repeatMode switch { RepeatMode.One => "Repeat current media", RepeatMode.All => "Repeat playlist", _ => "Repeat off" });
        UpdatePlaylistButtons();
    }

    private async Task OpenAdjacentMediaAsync(int direction)
    {
        if (_playlist.Count == 0) return;
        var next = _playlistIndex + Math.Sign(direction);
        if (_repeatMode == RepeatMode.All) next = (next + _playlist.Count) % _playlist.Count;
        if (next < 0 || next >= _playlist.Count) return;
        _playlistIndex = next;
        await OpenMediaAsync(_playlist[_playlistIndex], preservePlaylist: true);
    }

    private void UpdatePlaylistButtons()
    {
        PreviousButton.IsEnabled = _playlist.Count > 1 && (_playlistIndex > 0 || _repeatMode == RepeatMode.All);
        NextButton.IsEnabled = _playlist.Count > 1 && (_playlistIndex < _playlist.Count - 1 || _repeatMode == RepeatMode.All);
        PlaylistList.ItemsSource = _playlist.Select(path => new PlaylistEntry(path)).ToArray();
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
        if (_initialized && _playback.IsAvailable) TryPlayback(() => _playback.SetVolume(e.NewValue));
    }

    private void OnPositionSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingPosition && !_positionSliderDragging && _playback.IsAvailable && PositionSlider.Maximum > 0) TryPlayback(() => _playback.Seek(TimeSpan.FromSeconds(e.NewValue)));
    }

    private void OnPositionSliderPointerPressed(object sender, PointerRoutedEventArgs e) => _positionSliderDragging = true;
    private void OnPositionSliderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_positionSliderDragging) return;
        _positionSliderDragging = false;
        if (_playback.IsAvailable) TryPlayback(() => _playback.Seek(TimeSpan.FromSeconds(PositionSlider.Value), true));
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
            var text = targetFormat switch { "vtt" => VttWriter.Write(track), "ass" => AssWriter.Write(track), _ => SrtWriter.Write(track) };
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
    private void OnSubtitleItemClick(object sender, ItemClickEventArgs e) { if (e.ClickedItem is SubtitleCue cue) TryPlayback(() => _playback.Seek(TimeSpan.FromTicks(cue.StartMicroseconds * 10), true)); }

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
        TryPlayback(() => _playback.Seek(TimeSpan.FromTicks(time * 10), true));
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
        DrawTimeline(); DrawWaveform(); e.Handled = true;
    }
    private void OnVisualizationSizeChanged(object sender, SizeChangedEventArgs e) { DrawTimeline(); DrawWaveform(); }

    private void DrawTimeline()
    {
        TimelineCanvas.Children.Clear();
        if (_document.ActiveTrack?.Cues is { } cues)
        {
            foreach (var cue in cues)
            {
                var left = _timelineTransform.TimeToX(cue.StartMicroseconds); var right = _timelineTransform.TimeToX(cue.EndMicroseconds);
                if (right < 0) continue;
                if (left > TimelineCanvas.ActualWidth) break;
                var border = new Border
                {
                    Width = Math.Max(3, right - left), Height = Math.Max(20, TimelineCanvas.ActualHeight - 16),
                    Background = ThemeBrush("AccentFillColorDefaultBrush", Windows.UI.Color.FromArgb(255, 40, 130, 220)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 2, 4, 2),
                    Child = new TextBlock { Text = cue.Text.Replace('\n', ' '), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeBrush("TextOnAccentFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 255, 255, 255)) }, Tag = cue
                };
                Canvas.SetLeft(border, left); Canvas.SetTop(border, 8); TimelineCanvas.Children.Add(border);
            }
        }
        UpdateWaveformPlayhead();
    }

    private async Task GenerateWaveformAsync(string source)
    {
        _waveformCancellation?.Cancel();
        _waveformCancellation?.Dispose();
        _waveformCancellation = new CancellationTokenSource();
        var token = _waveformCancellation.Token;
        _waveform = WaveformData.Empty;
        DrawWaveform();
        try
        {
            var cached = await _waveformCache.TryLoadAsync(source, token);
            if (cached is not null) _waveform = cached;
            else
            {
                var progress = new ThrottledProgress(value => DispatcherQueue.TryEnqueue(() => StatusText.Text = F("StatusGeneratingWaveform", value)));
                _waveform = await _waveformGenerator.GenerateAsync(source, progress: progress, cancellationToken: token);
                await _waveformCache.SaveAsync(source, _waveform, token);
            }
            if (!token.IsCancellationRequested) { DrawWaveform(); StatusText.Text = L("StatusWaveformReady"); }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!token.IsCancellationRequested) StatusText.Text = F("StatusWaveformUnavailable", exception.Message); }
    }

    private void DrawWaveform()
    {
        WaveformCanvas.Children.Clear();
        _waveformPlayhead = null;
        if (WaveformCanvas.ActualWidth <= 0) return;
        if (_waveform.Peaks.Count == 0)
        {
            var text = new TextBlock { Text = L("WaveformEmptyMessage"), Opacity = 0.55 };
            Canvas.SetLeft(text, 12); Canvas.SetTop(text, 12); WaveformCanvas.Children.Add(text);
            UpdateWaveformPlayhead();
            return;
        }
        var width = WaveformCanvas.ActualWidth;
        var height = WaveformCanvas.ActualHeight;
        var center = height / 2;
        var count = Math.Max(1, (int)Math.Ceiling(width));
        var brush = ThemeBrush("AccentTextFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 75, 150, 240));
        var durationMicroseconds = Math.Max(1d, _waveform.Duration.TotalMilliseconds * 1000d);
        for (var pixel = 0; pixel < count; pixel++)
        {
            var startTime = _timelineTransform.XToTime(pixel);
            if (startTime >= durationMicroseconds) break;
            var endTime = _timelineTransform.XToTime(pixel + 1);
            var start = Math.Min(_waveform.Peaks.Count - 1, (int)Math.Floor(startTime / durationMicroseconds * _waveform.Peaks.Count));
            var end = Math.Min(_waveform.Peaks.Count, Math.Max(start + 1, (int)Math.Ceiling(endTime / durationMicroseconds * _waveform.Peaks.Count)));
            var minimum = 0f; var maximum = 0f;
            for (var index = start; index < end; index++) { minimum = Math.Min(minimum, _waveform.Peaks[index].Minimum); maximum = Math.Max(maximum, _waveform.Peaks[index].Maximum); }
            WaveformCanvas.Children.Add(new Line { X1 = pixel, X2 = pixel, Y1 = center - maximum * center, Y2 = center - minimum * center, Stroke = brush, StrokeThickness = 1 });
        }
        UpdateWaveformPlayhead();
    }

    private void UpdateWaveformPlayhead()
    {
        if (WaveformCanvas.ActualWidth <= 0 || WaveformCanvas.ActualHeight <= 0) return;
        if (_waveformPlayhead is null)
        {
            _waveformPlayhead = new Rectangle
            {
                Width = 2,
                Fill = ThemeBrush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(255, 255, 69, 0)),
                IsHitTestVisible = false
            };
            WaveformCanvas.Children.Add(_waveformPlayhead);
        }
        _waveformPlayhead.Height = WaveformCanvas.ActualHeight;
        Canvas.SetLeft(_waveformPlayhead, _timelineTransform.TimeToX((long)(_playback.Position.TotalMilliseconds * 1000)));
    }

    private void BindDocument(SubtitleDocument document)
    {
        _document = document;
        var track = _document.EnsureTrack();
        if (_document.FilePath is null && track.Cues.Count == 0) _document.MarkSaved();
        SubtitleList.ItemsSource = track.Cues; _history.Clear(); DrawTimeline();
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        PlayPauseButton.Content = _playback.State == PlaybackState.Playing ? "⏸" : "▶"; StatusText.Text = _playback.State.ToString();
        if (_playback.State is PlaybackState.Playing or PlaybackState.Paused) StartPostOpenWorkIfReady();
    });
    private void OnPlaybackPositionChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        _updatingPosition = true; PositionSlider.Maximum = Math.Max(1, _playback.Duration.TotalSeconds); if (!_positionSliderDragging) PositionSlider.Value = Math.Clamp(_playback.Position.TotalSeconds, 0, PositionSlider.Maximum); _updatingPosition = false;
        PositionText.Text = $"{FormatTime(_playback.Position)} / {FormatTime(_playback.Duration)}"; DecoderText.Text = _playback.DecoderDescription ?? string.Empty;
        var cue = _document.FindActiveCue((long)(_playback.Position.TotalMilliseconds * 1000));
        if (!_subtitleEditorHasFocus && cue is not null && !SubtitleList.SelectedItems.Contains(cue)) SubtitleList.SelectedItem = cue;
        DrawTimeline();
    });
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

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var key = e.Key.ToString();
        if (e.Key == Windows.System.VirtualKey.Escape && _isFullscreen) { ExitFullscreen(); e.Handled = true; return; }
        var isTextInput = e.OriginalSource is TextBox or PasswordBox;
        if (ctrl && shift && !alt && e.Key == Windows.System.VirtualKey.N) { PlayFromBeginning(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Enter) { ToggleFullscreen(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.F) { ToggleFullscreen(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.M) { OnMuteClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Up) { TryPlayback(() => _playback.SetVolume(_playback.Volume + 5)); VolumeSlider.Value = _playback.Volume; e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Down) { TryPlayback(() => _playback.SetVolume(_playback.Volume - 5)); VolumeSlider.Value = _playback.Volume; e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Home) { TryPlayback(() => _playback.Seek(TimeSpan.Zero, true)); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.End) { TryPlayback(() => _playback.Seek(_playback.Duration, true)); e.Handled = true; return; }
        bool Is(string action) => _settings.General.Shortcuts.TryGetValue(action, out var gesture) && ShortcutGesture.Matches(gesture, key, ctrl, shift, alt);
        var save = Is(ShortcutActions.SaveSubtitle);
        var saveAs = Is(ShortcutActions.SaveSubtitleAs);
        var close = Is(ShortcutActions.CloseWindow);
        var playPauseAlternate = Is(ShortcutActions.PlayPauseAlternate);
        var playFromBeginning = Is(ShortcutActions.PlayFromBeginning);
        var previousMedia = Is(ShortcutActions.PreviousMedia);
        var nextMedia = Is(ShortcutActions.NextMedia);
        if (isTextInput && !save && !saveAs && !close && !playPauseAlternate && !playFromBeginning && !previousMedia && !nextMedia) return;
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
        else return;
        e.Handled = true;
    }
    private void SelectRelativeCue(int delta)
    {
        var cues = _document.ActiveTrack?.Cues; if (cues is null || cues.Count == 0) return;
        var index = SubtitleList.SelectedItem is SubtitleCue selected ? cues.IndexOf(selected) : 0; index = Math.Clamp(index + delta, 0, cues.Count - 1);
        SubtitleList.SelectedItem = cues[index]; SubtitleList.ScrollIntoView(cues[index]); TryPlayback(() => _playback.Seek(TimeSpan.FromTicks(cues[index].StartMicroseconds * 10), true));
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
        FullscreenMenuItem.KeyboardAcceleratorTextOverride = $"{Combine(Shortcut(ShortcutActions.Fullscreen), "Enter", "F")} · Esc";

        ToolTipService.SetToolTip(PlayPauseButton, $"{L("PlayPause.Text")} ({Combine(Shortcut(ShortcutActions.PlayPause), Shortcut(ShortcutActions.PlayPauseAlternate))})");
        ToolTipService.SetToolTip(BeginningButton, "Go to beginning (Home)");
        ToolTipService.SetToolTip(PreviousButton, $"Previous media ({Shortcut(ShortcutActions.PreviousMedia)})");
        ToolTipService.SetToolTip(NextButton, $"Next media ({Shortcut(ShortcutActions.NextMedia)})");
        ToolTipService.SetToolTip(SeekBackButton, $"Seek backward ({Shortcut(ShortcutActions.SeekBackward)})");
        ToolTipService.SetToolTip(SeekForwardButton, $"Seek forward ({Shortcut(ShortcutActions.SeekForward)})");
        ToolTipService.SetToolTip(MuteButton, "Mute (M)");
        ToolTipService.SetToolTip(VolumeSlider, "Volume (↑ / ↓)");
        ToolTipService.SetToolTip(PositionSlider, $"Seek (Home / End) · Play from beginning ({Shortcut(ShortcutActions.PlayFromBeginning)})");
        ToolTipService.SetToolTip(SubtitleList, $"Previous: {Shortcut(ShortcutActions.PreviousSubtitle)} · Next: {Shortcut(ShortcutActions.NextSubtitle)}");
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { ToggleFullscreen(); e.Handled = true; }
    private void OnToggleRightPanelClick(object sender, RoutedEventArgs e) { _rightPanelVisible = ShowRightPanelMenuItem.IsChecked; ApplyPanelVisibility(); }
    private void OnToggleBottomPanelClick(object sender, RoutedEventArgs e) { _bottomPanelVisible = ShowBottomPanelMenuItem.IsChecked; ApplyPanelVisibility(); }

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen(); else EnterFullscreen();
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
            _fullscreenHoverTimer?.Start();
        }
        catch (Exception exception)
        {
            _isFullscreen = false;
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
            MainMenuBar.Visibility = Visibility.Visible;
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

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFullscreen) return;
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
            _bottomPanelHeight = Math.Min(_bottomPanelHeight, Math.Max(100, RootGrid.ActualHeight - 320));
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
        var maximum = Math.Max(100, RootGrid.ActualHeight - 320);
        _bottomPanelHeight = Math.Clamp(_bottomPanelHeight - e.VerticalChange, 100, Math.Min(800, maximum));
        BottomPanelRow.Height = new GridLength(_bottomPanelHeight);
        if (_initialized) _settings.Window.BottomPanelHeight = _bottomPanelHeight;
    }

    private void OnFullscreenHoverTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isFullscreen || _appWindow is null) return;
        if (!GetCursorPos(out var cursor)) return;
        var left = _appWindow.Position.X;
        var top = _appWindow.Position.Y;
        var right = left + _appWindow.Size.Width;
        var bottom = top + _appWindow.Size.Height;
        var now = DateTimeOffset.UtcNow;
        var inside = cursor.X >= left && cursor.X < right && cursor.Y >= top && cursor.Y < bottom;
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
    private async void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = L("StatusCollectingDiagnostics");
        var snapshot = await new DiagnosticsService().CollectAsync(_playback, _asrEngine.State, _settings.Asr.PythonExecutable, _settings.Asr.ModelPath, _settings.Asr.AlignerPath);
        var output = new TextBox { Text = snapshot.ToString(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 650, MinHeight = 420, FontFamily = new FontFamily("Consolas") };
        await ShowDialogAsync(new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = L("DiagnosticsTitle"), Content = output, CloseButtonText = L("CloseButton") });
        StatusText.Text = L("ReadyText");
    }
    private async void OnGenerateSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (_playback.CurrentSource is not { } source || !File.Exists(source) && !(Uri.TryCreate(source, UriKind.Absolute, out var remoteUri) && remoteUri.Scheme is "http" or "https")) { await ShowMessageAsync(L("AutomaticSubtitlesTitle"), L("AutomaticSubtitlesOpenMedia")); return; }
        if (string.IsNullOrWhiteSpace(_settings.Asr.ModelPath)) { await ShowMessageAsync(L("AsrModelMissingTitle"), L("AsrModelMissingMessage")); return; }
        if (_aiOperationCancellation is not null) { await ShowMessageAsync(L("AiBusyTitle"), L("AiBusyMessage")); return; }
        _aiOperationCancellation = new CancellationTokenSource();
        var token = _aiOperationCancellation.Token;
        string? temporaryInput = null;
        try
        {
            if (!File.Exists(source) && _currentHttpHeaders is { Count: > 0 })
            {
                StatusText.Text = L("StatusPreparingRemoteAsr");
                temporaryInput = await DownloadAsrInputAsync(source, _currentHttpHeaders, token);
                source = temporaryInput;
            }
            StatusText.Text = L("StatusStartingAsr");
            var worker = System.IO.Path.Combine(AppContext.BaseDirectory, "asr-worker", "main.py");
            await _asrEngine.StartAsync(_settings.Asr.PythonExecutable, worker, token);
            StatusText.Text = L("StatusLoadingAsr");
            await _asrEngine.LoadModelAsync(_settings.Asr.ModelPath, _settings.Asr.AlignerPath, _settings.Asr.Device.ToString(), _settings.Asr.Precision.ToString(), token);
            var document = new SubtitleDocument();
            var track = document.EnsureTrack("srt"); track.Name = "Qwen3-ASR";
            BindDocument(document);
            var segmentation = _settings.Subtitle.Segmentation;
            var asrSegmentation = new AsrSegmentationOptions(segmentation.MinimumCueSeconds, segmentation.MaximumCueSeconds, segmentation.MaximumLines, segmentation.TargetCharactersPerLine, segmentation.SilenceSplitSeconds, segmentation.MaximumCharactersPerSecond);
            await foreach (var result in _asrEngine.TranscribeFileAsync(source, _settings.Asr.Language, _settings.Asr.ChunkDurationSeconds, _settings.Asr.UseVad, asrSegmentation, token))
            {
                if (result.Event == "progress" && result.Progress is { } progress) StatusText.Text = F("StatusGeneratingSubtitles", progress);
                if (result.Event == "segment" && result.Segment is { } segment)
                {
                    track.Cues.Add(new SubtitleCue { StartMicroseconds = segment.StartMicroseconds, EndMicroseconds = segment.EndMicroseconds, Text = segment.Text, Confidence = segment.Confidence, Source = SubtitleCueSource.AutomaticSpeechRecognition });
                    DrawTimeline();
                }
            }
            document.Sort(); document.MarkDirty(); ScheduleSubtitleOverlaySync(); StatusText.Text = F("StatusGeneratedSubtitles", track.Cues.Count);
        }
        catch (OperationCanceledException) { StatusText.Text = L("StatusSubtitleGenerationCancelled"); }
        catch (AsrWorkerException exception) { await ShowMessageAsync(exception.Code, exception.Message); }
        catch (Exception exception) { await ShowMessageAsync("ASR_ERROR", exception.Message); }
        finally
        {
            if (temporaryInput is not null) try { File.Delete(temporaryInput); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            _aiOperationCancellation?.Dispose(); _aiOperationCancellation = null;
        }
    }

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

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        var track = _document.ActiveTrack;
        if (track is null || track.Cues.Count == 0) { await ShowMessageAsync(L("TranslationTitle"), L("LoadSubtitlesFirst")); return; }
        if (string.IsNullOrWhiteSpace(_settings.Llm.Model)) { await ShowMessageAsync(L("LlmModelMissingTitle"), L("LlmModelMissingMessage")); return; }
        if (_aiOperationCancellation is not null) return;
        var targetBox = new TextBox { Text = _settings.Llm.TranslationLanguage, Header = L("TargetLanguageHeader"), MinWidth = 320 };
        if (await ShowDialogAsync(CreateDialog(L("TranslateSubtitlesTitle"), targetBox, L("TranslateButton"))) != ContentDialogResult.Primary) return;
        var selected = SubtitleList.SelectedItems.Cast<SubtitleCue>().ToArray();
        var cues = selected.Length > 0 ? selected : track.Cues.ToArray();
        _aiOperationCancellation = new CancellationTokenSource();
        try
        {
            var provider = CreateLlmProvider();
            using var disposable = provider as IDisposable;
            var service = new LlmService(provider, _settings.Llm.Model, _settings.Llm.ThinkingLevel);
            var progress = new Progress<TranslationProgress>(value => StatusText.Text = F("StatusTranslating", value.Completed, value.Total));
            var translated = await service.TranslateAsync(cues, targetBox.Text, progress, cancellationToken: _aiOperationCancellation.Token);
            var commands = cues.Where(cue => translated.ContainsKey(cue.Id)).Select(cue => (IUndoableSubtitleCommand)new EditSubtitleTextCommand(_document, cue, translated[cue.Id])).ToArray();
            if (commands.Length > 0) _history.Execute(new CompositeSubtitleCommand("Translate subtitles", commands));
            DrawTimeline(); ScheduleSubtitleOverlaySync(); StatusText.Text = F("StatusTranslated", translated.Count);
        }
        catch (OperationCanceledException) { StatusText.Text = L("StatusTranslationCancelled"); }
        catch (Exception exception) { await ShowMessageAsync("LLM_ERROR", exception.Message); }
        finally { _aiOperationCancellation?.Dispose(); _aiOperationCancellation = null; }
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
            await ShowDialogAsync(new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = L("TranscriptSummaryTitle"), Content = output, CloseButtonText = L("CloseButton") });
            StatusText.Text = L("StatusSummaryComplete");
        }
        catch (OperationCanceledException) { StatusText.Text = L("StatusSummaryCancelled"); }
        catch (Exception exception) { await ShowMessageAsync("LLM_ERROR", exception.Message); }
        finally { _aiOperationCancellation?.Dispose(); _aiOperationCancellation = null; }
    }

    private void OnCancelAiClick(object sender, RoutedEventArgs e) => _aiOperationCancellation?.Cancel();

    private ILlmProvider CreateLlmProvider()
    {
        return new LlmProviderFactory(new WindowsCredentialService()).Create(_settings.Llm.Provider);
    }
    private void OnWebDavClick(object sender, RoutedEventArgs e)
    {
        _rightPanelVisible = true;
        ApplyPanelVisibility();
        RightPanelTabs.SelectedIndex = 2;
        WebDavServerList.Focus(FocusState.Programmatic);
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
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || parsedAddress is null) return;

            var server = new WebDavServerSettings { Name = string.IsNullOrWhiteSpace(name.Text) ? parsedAddress.Host : name.Text.Trim() };
            _webDavCredentials.Save(server.Id, new WebDavConnectionCredential(WebDavConnectionCredential.NormalizeAddress(parsedAddress), (int)port.Value, username.Text.Trim(), password.Password));
            _settings.Network.WebDavServers.Add(server);
            await SettingsService.CreateDefault().SaveAsync(_settings);
            RefreshWebDavServerList(server);
            WebDavConnectionStatusText.Text = F("WebDavServerAddedMessage", server.Name);
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
            WebDavPanelEntryList.ItemsSource = null;
            WebDavParentButton.IsEnabled = false;
            WebDavRefreshButton.IsEnabled = false;
            WebDavPanelPathText.Text = L("WebDavSelectServerMessage");
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
        WebDavPanelPathText.Text = _webDavPanelDirectory.AbsoluteUri;
        WebDavConnectionStatusText.Text = F("WebDavConnectingMessage", server.Name);
        try
        {
            var entries = await _webDavClient.ListAsync(server, _webDavPanelDirectory, operation.Token);
            if (operation.IsCancellationRequested) return;
            WebDavPanelEntryList.ItemsSource = entries;
            WebDavConnectionStatusText.Text = F("WebDavConnectedMessage", server.Name, entries.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (operation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "webdav", exception is WebDavException webDavException ? webDavException.Code : "WEBDAV_LIST_ERROR", exception.Message, exception);
            WebDavPanelEntryList.ItemsSource = null;
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
                ApplyTheme(settings.General.Theme);
                SeekBackButton.Content = $"−{settings.Playback.SeekIntervalSeconds:0.#}s";
                SeekForwardButton.Content = $"+{settings.Playback.SeekIntervalSeconds:0.#}s";
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

    private void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch { AppTheme.Light => ElementTheme.Light, AppTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyTitleBarTheme(RootGrid.ActualTheme);
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args) => ApplyTitleBarTheme(sender.ActualTheme);

    private void ApplyTitleBarTheme(ElementTheme theme)
    {
        if (_appWindow?.TitleBar is not { } titleBar) return;
        var dark = theme == ElementTheme.Dark;
        var background = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 24, 24, 24);
        var inactiveForeground = dark ? Windows.UI.Color.FromArgb(255, 160, 160, 160) : Windows.UI.Color.FromArgb(255, 110, 110, 110);
        var hover = dark ? Windows.UI.Color.FromArgb(255, 58, 58, 58) : Windows.UI.Color.FromArgb(255, 224, 224, 224);
        var pressed = dark ? Windows.UI.Color.FromArgb(255, 72, 72, 72) : Windows.UI.Color.FromArgb(255, 208, 208, 208);
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

    private void OnAddFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_currentMediaSource is null) return;
        _historyService.AddFavorite(_currentMediaSource);
        _ = _historyService.SaveAsync();
        RebuildFavoritesMenu();
        StatusText.Text = L("StatusAddedFavorite");
    }

    private async void OnAddFavoriteFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            _historyService.AddFavorite(new LocalMediaSource(folder.Path), true);
            await _historyService.SaveAsync();
            RebuildFavoritesMenu();
            StatusText.Text = F("StatusAddedFavoriteFolder", folder.Name);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "file-picker", "FOLDER_PICKER_ERROR", exception.Message, exception);
            await ShowMessageAsync(L("FolderUnavailableTitle"), exception.Message);
        }
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

    private async void OnBrowserParentClick(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_browserDirectory);
        if (parent is not null) await RefreshBrowserAsync(parent.FullName);
    }

    private async void OnBrowserRefreshClick(object sender, RoutedEventArgs e) => await RefreshBrowserAsync(_browserDirectory);

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
            BrowserPathBox.Text = _browserDirectory;
            FolderEntryList.ItemsSource = entries;
            if (selectedPath is not null) SelectBrowserEntry(selectedPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void OnFolderEntryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FolderEntryList.SelectedItem is not BrowserEntry entry) return;
        if (entry.IsDirectory) { await RefreshBrowserAsync(entry.Path); return; }
        var files = (FolderEntryList.ItemsSource as IEnumerable<BrowserEntry>)?.Where(item => !item.IsDirectory).Select(item => item.Path).ToArray() ?? [entry.Path];
        _playlist.Clear(); _playlist.AddRange(files); _playlistIndex = Math.Max(0, _playlist.FindIndex(path => path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)));
        await OpenMediaAsync(entry.Path, preservePlaylist: true);
    }

    private async void OnPlaylistDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not PlaylistEntry entry) return;
        var index = _playlist.FindIndex(path => path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        _playlistIndex = index;
        await OpenMediaAsync(entry.Path, preservePlaylist: true);
    }

    private void OnClearPlaylistClick(object sender, RoutedEventArgs e) { _playlist.Clear(); _playlistIndex = -1; UpdatePlaylistButtons(); }

    private async void OnWebDavPanelEntryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (WebDavPanelEntryList.SelectedItem is not WebDavEntry entry || _webDavPanelServerId is not { } serverId) return;
        if (entry.IsCollection)
        {
            _webDavPanelDirectory = EnsureWebDavDirectoryUri(entry.Uri);
            await RefreshWebDavDirectoryAsync();
            return;
        }
        var server = _settings.Network.WebDavServers.FirstOrDefault(candidate => candidate.Id == serverId);
        if (server is null) { await ShowMessageAsync(L("WebDavServerMissingTitle"), L("RecentServerMissingMessage")); return; }
        using var request = _webDavClient.CreateMediaRequest(server, entry.Uri);
        var headers = request.Headers.Authorization is { } authorization ? new Dictionary<string, string> { ["Authorization"] = authorization.ToString() } : null;
        await OpenMediaAsync(entry.Uri.AbsoluteUri, headers, new WebDavMediaSource(server.Id, entry.Uri, entry.Name));
    }

    private static bool IsPlayableMediaPath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".wmv" or ".m4v" or ".ts" or ".m2ts" or ".mp3" or ".flac" or ".wav" or ".m4a" or ".aac" or ".ogg" or ".opus";

    private void RebuildFavoritesMenu()
    {
        FavoritesMenu.Items.Clear();
        foreach (var favorite in _historyService.Favorites.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var group = new MenuFlyoutSubItem { Text = favorite.DisplayName, Tag = favorite };
            var open = new MenuFlyoutItem { Text = favorite.IsFolder ? L("BrowseButton") : L("OpenButton") };
            open.Click += async (_, _) => await OpenFavoriteAsync(favorite);
            var remove = new MenuFlyoutItem { Text = L("RemoveFavoriteButton") };
            remove.Click += async (_, _) =>
            {
                _historyService.RemoveFavorite(favorite.Location);
                await _historyService.SaveAsync();
                RebuildFavoritesMenu();
            };
            group.Items.Add(open);
            group.Items.Add(remove);
            FavoritesMenu.Items.Add(group);
        }
        if (FavoritesMenu.Items.Count == 0) FavoritesMenu.Items.Add(new MenuFlyoutItem { Text = L("NoFavoritesText"), IsEnabled = false });
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
                RightPanelTabs.SelectedIndex = 2;
                await ConnectWebDavServerAsync(server, new Uri(favorite.Location));
                return;
            }
            await BrowseLocalFavoriteFolderAsync(favorite);
            return;
        }
        await OpenRecentAsync(new RecentMediaItem(favorite.SourceType, favorite.DisplayName, favorite.Location, favorite.Added, 0));
    }

    private async Task BrowseLocalFavoriteFolderAsync(FavoriteItem favorite)
    {
        if (!Directory.Exists(favorite.Location)) { await ShowMessageAsync(L("FolderUnavailableTitle"), favorite.Location); return; }
        string[] files;
        try
        {
            files = await Task.Run(() => Directory.EnumerateFiles(favorite.Location).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).Take(1000).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { await ShowMessageAsync(L("FolderUnavailableTitle"), exception.Message); return; }
        if (files.Length == 0) { await ShowMessageAsync(L("FavoriteFolderTitle"), L("FavoriteFolderEmptyMessage")); return; }
        var list = new ListView { ItemsSource = files, SelectionMode = ListViewSelectionMode.Single, MinWidth = 520, MinHeight = 360 };
        var dialog = CreateDialog(favorite.DisplayName, list, L("OpenButton"));
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && list.SelectedItem is string selected) await OpenMediaAsync(selected);
    }

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var recent in _historyService.Recent)
        {
            var item = new MenuFlyoutItem { Text = recent.DisplayName, Tag = recent };
            item.Click += async (_, _) => await OpenRecentAsync(recent);
            RecentMenu.Items.Add(item);
        }
        if (RecentMenu.Items.Count == 0) RecentMenu.Items.Add(new MenuFlyoutItem { Text = L("NoRecentMediaText"), IsEnabled = false });
    }

    private async Task OpenRecentAsync(RecentMediaItem recent)
    {
        IReadOnlyDictionary<string, string>? headers = null;
        IMediaSource source;
        if (recent.SourceType == MediaSourceKind.WebDav)
        {
            var server = FindWebDavServerForLocation(recent.Location);
            if (server is null) { await ShowMessageAsync(L("WebDavServerMissingTitle"), L("RecentServerMissingMessage")); return; }
            using var client = new WebDavClient(new WindowsCredentialService());
            using var request = client.CreateMediaRequest(server, new Uri(recent.Location));
            headers = request.Headers.Authorization is { } authorization ? new Dictionary<string, string> { ["Authorization"] = authorization.ToString() } : null;
            source = new WebDavMediaSource(server.Id, new Uri(recent.Location), recent.DisplayName);
        }
        else source = MediaSourceFactory.Parse(recent.Location);
        await OpenMediaAsync(recent.Location, headers, source);
        if (_settings.General.ResumePlayback && recent.LastPlaybackPositionMicroseconds > 0) _playback.Seek(TimeSpan.FromTicks(recent.LastPlaybackPositionMicroseconds * 10), true);
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

    private void ScheduleSubtitleOverlaySync()
    {
        if (!_playback.IsAvailable || _playback.CurrentSource is null || _document.ActiveTrack is null) return;
        _overlaySyncCancellation?.Cancel(); _overlaySyncCancellation?.Dispose(); _overlaySyncCancellation = new CancellationTokenSource();
        var token = _overlaySyncCancellation.Token;
        _ = SyncSubtitleOverlayAsync(token);
    }

    private async Task SyncSubtitleOverlayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(150, cancellationToken);
            var track = _document.ActiveTrack;
            if (track is null) return;
            var content = AssWriter.Write(track);
            await File.WriteAllTextAsync(_editorOverlayPath, content, new System.Text.UTF8Encoding(false), cancellationToken);
            if (!cancellationToken.IsCancellationRequested) _playback.UpdateEditorSubtitle(_editorOverlayPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { DispatcherQueue.TryEnqueue(() => StatusText.Text = $"Subtitle overlay update failed: {exception.Message}"); }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_isFullscreen && sender.Presenter is OverlappedPresenter presenter && presenter.State != OverlappedPresenterState.Minimized) CaptureWindowPlacement(sender, presenter);
        if (_allowClose || !_document.IsDirty) return;
        args.Cancel = true;
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = L("UnsavedChangesTitle"), Content = L("UnsavedChangesCloseMessage"), PrimaryButtonText = L("SaveButtonText"), SecondaryButtonText = L("DiscardButton"), CloseButtonText = L("CancelButtonText"), DefaultButton = ContentDialogButton.Primary };
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.None) return;
        if (result == ContentDialogResult.Primary)
        {
            if (_document.FilePath is null) await SaveSubtitleAsAsync(); else await SaveSubtitleAsync(_document.FilePath);
            if (_document.IsDirty) return;
        }
        _allowClose = true;
        Close();
    }

    private async Task<bool> ConfirmDiscardChangesAsync(string action)
    {
        if (!_document.IsDirty) return true;
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = L("UnsavedChangesTitle"), Content = F("UnsavedChangesActionMessage", action), PrimaryButtonText = L("SaveButtonText"), SecondaryButtonText = L("DiscardButton"), CloseButtonText = L("CancelButtonText"), DefaultButton = ContentDialogButton.Primary };
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
    private ContentDialog CreateDialog(string title, object content, string primaryText) => new() { XamlRoot = RootGrid.XamlRoot, Title = title, Content = content, PrimaryButtonText = primaryText, CloseButtonText = L("CancelButtonText"), DefaultButton = ContentDialogButton.Primary };
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
    private async Task ShowMessageAsync(string title, string message) => await ShowDialogAsync(new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = L("OkButton") });
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
    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _fullscreenHoverTimer?.Stop();
        if (_appWindow is not null)
        {
            _appWindow.Changed -= OnAppWindowChanged;
        }
        RememberCurrentPosition();
        try { await _historyService.SaveAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "HISTORY_SAVE_ERROR", exception.Message, exception); }
        try { await SettingsService.CreateDefault().SaveAsync(_settings); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "SETTINGS_SAVE_ERROR", exception.Message, exception); }
        _waveformCancellation?.Cancel(); _waveformCancellation?.Dispose();
        _postOpenCancellation?.Cancel(); _postOpenCancellation?.Dispose();
        _overlaySyncCancellation?.Cancel(); _overlaySyncCancellation?.Dispose();
        _aiOperationCancellation?.Cancel(); _aiOperationCancellation?.Dispose();
        _webDavListingCancellation?.Cancel(); _webDavListingCancellation?.Dispose();
        _webDavClient.Dispose();
        try { await _asrEngine.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "ASR_DISPOSE_ERROR", exception.Message, exception); }
        try { await _playback.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "shutdown", "PLAYBACK_DISPOSE_ERROR", exception.Message, exception); }
        if (_videoHost is not null)
        {
            _videoHost.FilesDropped -= OnNativeVideoFilesDropped;
            _videoHost.Clicked -= OnNativeVideoClicked;
            _videoHost.Dispose();
        }
        try { File.Delete(_editorOverlayPath); } catch (IOException) { }
    }

    private sealed record PlaylistEntry(string Path)
    {
        public string DisplayName => System.IO.Path.GetFileName(Path);
    }

    private sealed record PendingPostOpenWork(string Source, bool GenerateWaveform, bool PopulateSiblingPlaylist, CancellationToken CancellationToken);

    private sealed class ThrottledProgress(Action<double> callback, int intervalMilliseconds = 200) : IProgress<double>
    {
        private readonly object _sync = new();
        private long _lastReport = Environment.TickCount64 - intervalMilliseconds;

        public void Report(double value)
        {
            lock (_sync)
            {
                var now = Environment.TickCount64;
                if (value < 1 && now - _lastReport < intervalMilliseconds) return;
                _lastReport = now;
            }
            callback(value);
        }
    }

    private sealed record BrowserEntry(string Path, bool IsDirectory, long? Length)
    {
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";
        public string Details => IsDirectory || Length is null ? string.Empty : FormatBytes(Length.Value);
        public static BrowserEntry FromDirectory(string path) => new(path, true, null);
        public static BrowserEntry FromFile(string path) => new(path, false, new FileInfo(path).Length);
        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            var display = (double)Math.Max(0, bytes);
            var unit = 0;
            while (display >= 1024 && unit < units.Length - 1) { display /= 1024; unit++; }
            return $"{display:0.##} {units[unit]}";
        }
    }

    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    private enum TimelineDragMode { None, Move, ResizeStart, ResizeEnd }
    private enum RepeatMode { Off, One, All }
}
