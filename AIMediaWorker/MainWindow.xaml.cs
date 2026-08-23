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
    private bool _isFullscreen;
    private bool _initialized;
    private CameraWindow? _cameraWindow;
    private SettingsWindow? _settingsWindow;
    private WebDavWindow? _webDavWindow;
    private AppSettings _settings = new();
    private readonly AsrWorkerClient _asrEngine = new();
    private CancellationTokenSource? _aiOperationCancellation;
    private CancellationTokenSource? _waveformCancellation;
    private WaveformData _waveform = WaveformData.Empty;
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
    private readonly string _editorOverlayPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AIMediaWorker-{Environment.ProcessId}-{Guid.NewGuid():N}.ass");

    public MainWindow()
    {
        InitializeComponent();
        var handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        _appWindow?.Resize(new SizeInt32(1280, 820));
        if (_appWindow is not null) _appWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
        _playback.StateChanged += OnPlaybackStateChanged;
        _playback.PositionChanged += OnPlaybackPositionChanged;
        _playback.TracksChanged += OnTracksChanged;
        _playback.ErrorOccurred += OnPlaybackError;
        BindDocument(new SubtitleDocument());
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _settings = await SettingsService.CreateDefault().LoadAsync();
            await _historyService.LoadAsync();
            RebuildRecentMenu();
            RebuildFavoritesMenu();
            ApplyTheme(_settings.General.Theme);
            _videoHost = new NativeVideoHost(this, VideoPlaceholder);
            await _playback.InitializeAsync(_videoHost.Create(), _settings.Playback.HardwareDecoder, _settings.Playback.Renderer);
            if (_playback.IsAvailable)
            {
                _playback.SetVolume(_settings.Playback.DefaultVolume); _playback.SetRate(_settings.Playback.PlaybackRate);
                _playback.ConfigureNetwork(TimeSpan.FromSeconds(_settings.Network.TimeoutSeconds), _settings.Network.Proxy);
                _playback.ConfigurePreferredLanguages(_settings.Playback.DefaultAudioLanguage, _settings.Playback.DefaultSubtitleLanguage);
                _playback.ConfigureSubtitleStyle(_settings.Subtitle.FontFamily, _settings.Subtitle.FontSize, _settings.Subtitle.Color, _settings.Subtitle.Background, _settings.Subtitle.Outline, _settings.Subtitle.BottomMargin);
            }
            RateCombo.ItemsSource = new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 };
            RateCombo.SelectedItem = RateCombo.Items.Cast<double>().OrderBy(value => Math.Abs(value - _settings.Playback.PlaybackRate)).First();
            StatusText.Text = _playback.IsAvailable ? L("StatusLibmpvReady") : L("StatusPlaybackUnavailable");
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void OnOpenMediaClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await OpenMediaAsync(file.Path);
    }

    private async void OnOpenUrlClick(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "https://example.com/video.m3u8", MinWidth = 460 };
        var dialog = CreateDialog(L("OpenUrlTitle"), input, L("OpenButton"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!Uri.TryCreate(input.Text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            await ShowMessageAsync(L("InvalidUrlTitle"), L("InvalidUrlMessage"));
            return;
        }
        await OpenMediaAsync(uri.AbsoluteUri);
    }

    private async Task OpenMediaAsync(string source, IReadOnlyDictionary<string, string>? httpHeaders = null, IMediaSource? mediaSource = null)
    {
        if (!await ConfirmDiscardChangesAsync(L("ActionOpenMedia"))) return;
        try
        {
            RememberCurrentPosition();
            await _playback.OpenAsync(source, httpHeaders);
            _currentMediaSource = mediaSource ?? MediaSourceFactory.Parse(source);
            _currentHttpHeaders = httpHeaders is null ? null : new Dictionary<string, string>(httpHeaders, StringComparer.OrdinalIgnoreCase);
            _historyService.AddRecent(_currentMediaSource, 0, _settings.General.RecentMediaCount);
            await _historyService.SaveAsync();
            RebuildRecentMenu();
            var blank = new SubtitleDocument(); blank.EnsureTrack(); blank.MarkSaved(); BindDocument(blank);
            StatusText.Text = source;
            VideoStatusText.Visibility = Visibility.Collapsed;
            if (httpHeaders is null || httpHeaders.Count == 0) _ = GenerateWaveformAsync(source);
            else { _waveform = WaveformData.Empty; DrawWaveform(); }
        }
        catch (Exception exception) { await ShowMessageAsync(L("PlaybackErrorTitle"), exception.Message); }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TryPlayback(_playback.TogglePause);
    private void OnStopClick(object sender, RoutedEventArgs e) => TryPlayback(_playback.Stop);
    private void OnFrameStepClick(object sender, RoutedEventArgs e) => TryPlayback(() => _playback.FrameStep());
    private void OnSeekBackClick(object sender, RoutedEventArgs e) => TryPlayback(() => _playback.SeekRelative(TimeSpan.FromSeconds(-_settings.Playback.SeekIntervalSeconds)));
    private void OnSeekForwardClick(object sender, RoutedEventArgs e) => TryPlayback(() => _playback.SeekRelative(TimeSpan.FromSeconds(_settings.Playback.SeekIntervalSeconds)));
    private void OnMuteClick(object sender, RoutedEventArgs e) => TryPlayback(() => _playback.SetMute(!_playback.IsMuted));
    private void OnRateChanged(object sender, SelectionChangedEventArgs e) { if (RateCombo.SelectedItem is double rate && _playback.IsAvailable) TryPlayback(() => _playback.SetRate(rate)); }
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
        if (!_updatingPosition && _playback.IsAvailable && PositionSlider.Maximum > 0) TryPlayback(() => _playback.Seek(TimeSpan.FromSeconds(e.NewValue)));
    }

    private async void OnLoadSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChangesAsync(L("ActionLoadSubtitle"))) return;
        var picker = new FileOpenPicker();
        foreach (var extension in new[] { ".srt", ".vtt", ".ass", ".ssa" }) picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var text = await File.ReadAllTextAsync(file.Path, ResolveSubtitleEncoding());
            var document = System.IO.Path.GetExtension(file.Path).ToLowerInvariant() switch
            {
                ".srt" => SrtParser.Parse(text),
                ".vtt" => VttParser.Parse(text),
                ".ass" or ".ssa" => AssParser.Parse(text),
                _ => throw new InvalidDataException("Unsupported subtitle format.")
            };
            document.MarkSaved(file.Path);
            BindDocument(document);
            if (_playback.IsAvailable) _playback.LoadSubtitle(file.Path);
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
        var picker = new FileSavePicker { SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_playback.CurrentSource ?? "subtitles") };
        picker.FileTypeChoices.Add(L("SubRipFileType"), [".srt"]);
        picker.FileTypeChoices.Add(L("WebVttFileType"), [".vtt"]);
        picker.FileTypeChoices.Add(L("AssFileType"), [".ass"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is not null) await SaveSubtitleAsync(file.Path);
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
        if (await CreateDialog(L("ShiftSubtitlesTitle"), input, L("ShiftButton")).ShowAsync() != ContentDialogResult.Primary) return;
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
        DrawTimeline(); e.Handled = true;
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
        var playhead = new Rectangle { Width = 2, Height = TimelineCanvas.ActualHeight, Fill = ThemeBrush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(255, 255, 69, 0)), IsHitTestVisible = false };
        Canvas.SetLeft(playhead, _timelineTransform.TimeToX((long)(_playback.Position.TotalMilliseconds * 1000))); TimelineCanvas.Children.Add(playhead);
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
                var progress = new Progress<double>(value => StatusText.Text = F("StatusGeneratingWaveform", value));
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
        if (WaveformCanvas.ActualWidth <= 0) return;
        if (_waveform.Peaks.Count == 0)
        {
            var text = new TextBlock { Text = L("WaveformEmptyMessage"), Opacity = 0.55 };
            Canvas.SetLeft(text, 12); Canvas.SetTop(text, 12); WaveformCanvas.Children.Add(text); return;
        }
        var width = WaveformCanvas.ActualWidth;
        var height = WaveformCanvas.ActualHeight;
        var center = height / 2;
        var count = Math.Max(1, (int)Math.Ceiling(width));
        var brush = ThemeBrush("AccentTextFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 75, 150, 240));
        for (var pixel = 0; pixel < count; pixel++)
        {
            var start = Math.Min(_waveform.Peaks.Count - 1, (int)(pixel / width * _waveform.Peaks.Count));
            var end = Math.Min(_waveform.Peaks.Count, Math.Max(start + 1, (int)((pixel + 1) / width * _waveform.Peaks.Count)));
            var minimum = 0f; var maximum = 0f;
            for (var index = start; index < end; index++) { minimum = Math.Min(minimum, _waveform.Peaks[index].Minimum); maximum = Math.Max(maximum, _waveform.Peaks[index].Maximum); }
            WaveformCanvas.Children.Add(new Line { X1 = pixel, X2 = pixel, Y1 = center - maximum * center, Y2 = center - minimum * center, Stroke = brush, StrokeThickness = 1 });
        }
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
    });
    private void OnPlaybackPositionChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        _updatingPosition = true; PositionSlider.Maximum = Math.Max(1, _playback.Duration.TotalSeconds); PositionSlider.Value = Math.Clamp(_playback.Position.TotalSeconds, 0, PositionSlider.Maximum); _updatingPosition = false;
        PositionText.Text = $"{FormatTime(_playback.Position)} / {FormatTime(_playback.Duration)}"; DecoderText.Text = _playback.DecoderDescription ?? string.Empty;
        var cue = _document.FindActiveCue((long)(_playback.Position.TotalMilliseconds * 1000));
        if (!_subtitleEditorHasFocus && cue is not null && !SubtitleList.SelectedItems.Contains(cue)) SubtitleList.SelectedItem = cue;
        DrawTimeline();
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
        bool Is(string action) => _settings.General.Shortcuts.TryGetValue(action, out var gesture) && ShortcutGesture.Matches(gesture, key, ctrl, shift, alt);
        var save = Is(ShortcutActions.SaveSubtitle);
        var saveAs = Is(ShortcutActions.SaveSubtitleAs);
        if (e.OriginalSource is TextBox && !save && !saveAs) return;
        if (saveAs) OnSaveSubtitleAsClick(this, new RoutedEventArgs());
        else if (save) OnSaveSubtitleClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.PlayPause)) OnPlayPauseClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.PreviousSubtitle)) SelectRelativeCue(-1);
        else if (Is(ShortcutActions.NextSubtitle)) SelectRelativeCue(1);
        else if (Is(ShortcutActions.SeekBackward)) OnSeekBackClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.SeekForward)) OnSeekForwardClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.Undo)) OnUndoClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.Redo)) OnRedoClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.DeleteCue)) OnDeleteCueClick(this, new RoutedEventArgs());
        else if (Is(ShortcutActions.Fullscreen)) ToggleFullscreen();
        else return;
        e.Handled = true;
    }
    private void SelectRelativeCue(int delta)
    {
        var cues = _document.ActiveTrack?.Cues; if (cues is null || cues.Count == 0) return;
        var index = SubtitleList.SelectedItem is SubtitleCue selected ? cues.IndexOf(selected) : 0; index = Math.Clamp(index + delta, 0, cues.Count - 1);
        SubtitleList.SelectedItem = cues[index]; SubtitleList.ScrollIntoView(cues[index]); TryPlayback(() => _playback.Seek(TimeSpan.FromTicks(cues[index].StartMicroseconds * 10), true));
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen() { if (_appWindow is null) return; _isFullscreen = !_isFullscreen; _appWindow.SetPresenter(_isFullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default); }
    private async void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = L("StatusCollectingDiagnostics");
        var snapshot = await new DiagnosticsService().CollectAsync(_playback, _asrEngine.State, _settings.Asr.PythonExecutable, _settings.Asr.ModelPath, _settings.Asr.AlignerPath);
        var output = new TextBox { Text = snapshot.ToString(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 650, MinHeight = 420, FontFamily = new FontFamily("Consolas") };
        await new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = L("DiagnosticsTitle"), Content = output, CloseButtonText = L("CloseButton") }.ShowAsync();
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
        if (await CreateDialog(L("TranslateSubtitlesTitle"), targetBox, L("TranslateButton")).ShowAsync() != ContentDialogResult.Primary) return;
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
        if (await CreateDialog(L("SummarizeTranscriptTitle"), choices, L("SummarizeButton")).ShowAsync() != ContentDialogResult.Primary) return;
        _aiOperationCancellation = new CancellationTokenSource();
        try
        {
            var provider = CreateLlmProvider();
            using var disposable = provider as IDisposable;
            var service = new LlmService(provider, _settings.Llm.Model, _settings.Llm.ThinkingLevel);
            var progress = new Progress<double>(value => StatusText.Text = F("StatusSummarizing", value));
            var summary = await service.SummarizeAsync(track.Cues, (SummaryKind)(choices.SelectedItem ?? SummaryKind.Short), progress, cancellationToken: _aiOperationCancellation.Token);
            var output = new TextBox { Text = summary, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 600, MinHeight = 380 };
            await new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = L("TranscriptSummaryTitle"), Content = output, CloseButtonText = L("CloseButton") }.ShowAsync();
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
        ShowWebDavWindow();
    }

    private void ShowWebDavWindow(Guid? serverId = null, Uri? directory = null)
    {
        if (_webDavWindow is not null)
        {
            if (serverId is null) { _webDavWindow.Activate(); return; }
            _webDavWindow.Close();
        }
        _webDavWindow = new WebDavWindow(serverId, directory);
        _webDavWindow.MediaSelected += async (_, selection) => { await OpenMediaAsync(selection.Uri.AbsoluteUri, selection.Headers, new WebDavMediaSource(selection.ServerId, selection.Uri, selection.Name)); _webDavWindow?.Close(); };
        _webDavWindow.FolderFavoriteRequested += async (_, selection) =>
        {
            _historyService.AddFavorite(new WebDavMediaSource(selection.ServerId, selection.Uri, selection.Name), true);
            await _historyService.SaveAsync();
            RebuildFavoritesMenu();
        };
        _webDavWindow.Closed += (_, _) => _webDavWindow = null;
        _webDavWindow.Activate();
    }
    private void OnCameraClick(object sender, RoutedEventArgs e)
    {
        _cameraWindow = new CameraWindow();
        _cameraWindow.Closed += (_, _) => _cameraWindow = null;
        _cameraWindow.Activate();
    }
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.SettingsSaved += (_, settings) =>
        {
            _settings = settings;
            LocalizationService.Apply(settings.General.Language);
            ApplyTheme(settings.General.Theme);
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

    private void ApplyTheme(AppTheme theme) => RootGrid.RequestedTheme = theme switch { AppTheme.Light => ElementTheme.Light, AppTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };

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
                var server = _settings.Network.WebDavServers.FirstOrDefault(candidate => favorite.Location.StartsWith(candidate.Url, StringComparison.OrdinalIgnoreCase));
                if (server is null) { await ShowMessageAsync(L("WebDavServerMissingTitle"), L("FavoriteServerMissingMessage")); return; }
                ShowWebDavWindow(server.Id, new Uri(favorite.Location));
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is string selected) await OpenMediaAsync(selected);
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
            var server = _settings.Network.WebDavServers.Where(candidate => recent.Location.StartsWith(candidate.Url, StringComparison.OrdinalIgnoreCase)).OrderByDescending(candidate => candidate.Url.Length).FirstOrDefault();
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
        if (_allowClose || !_document.IsDirty) return;
        args.Cancel = true;
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = L("UnsavedChangesTitle"), Content = L("UnsavedChangesCloseMessage"), PrimaryButtonText = L("SaveButtonText"), SecondaryButtonText = L("DiscardButton"), CloseButtonText = L("CancelButtonText"), DefaultButton = ContentDialogButton.Primary };
        var result = await dialog.ShowAsync();
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
        var result = await dialog.ShowAsync();
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
    private async Task ShowMessageAsync(string title, string message) => await new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = L("OkButton") }.ShowAsync();
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
    private async void OnWindowClosed(object sender, WindowEventArgs args) { RememberCurrentPosition(); await _historyService.SaveAsync(); _waveformCancellation?.Cancel(); _waveformCancellation?.Dispose(); _overlaySyncCancellation?.Cancel(); _overlaySyncCancellation?.Dispose(); _aiOperationCancellation?.Cancel(); _aiOperationCancellation?.Dispose(); await _asrEngine.DisposeAsync(); await _playback.DisposeAsync(); _videoHost?.Dispose(); try { File.Delete(_editorOverlayPath); } catch (IOException) { } }

    private enum TimelineDragMode { None, Move, ResizeStart, ResizeEnd }
}
