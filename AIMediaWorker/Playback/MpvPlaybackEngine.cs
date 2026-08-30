using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using AIMediaWorker.Diagnostics;

namespace AIMediaWorker.Playback;

public sealed class MpvPlaybackEngine : IPlaybackEngine
{
    private static readonly Lazy<Task> LibraryPreload = new(
        () => Task.Run(PreloadLibraryCore),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly object _sync = new();
    private readonly object _subtitleCommandSync = new();
    private readonly SemaphoreSlim _openLock = new(1, 1);
    private readonly List<MediaTrack> _tracks = [];
    private nint _context;
    private CancellationTokenSource? _eventLoopCancellation;
    private Task? _eventLoop;
    private Task? _preparationTask;
    private Task? _initializationTask;
    private CancellationTokenSource? _trackRefreshCancellation;
    private CancellationTokenSource? _editorSubtitleSelectionCancellation;
    private long _nextCommandId;
    private PlaybackState _state = PlaybackState.Uninitialized;
    private string? _editorSubtitlePath;
    private volatile bool _firstFrameReady;
    private volatile bool _loadfileIssued;
    private bool _disposed;
    private bool _dolbyVisionCompatibilityEvaluated;
    private bool _dolbyVisionCompatibilityFilterApplied;
    private RtxVideoSuperResolutionMode _rtxVideoSuperResolutionMode = RtxVideoSuperResolutionMode.Off;
    private bool _rtxVideoSuperResolutionFilterApplied;
    private volatile bool _eofReached;
    private string _subtitleFontFamily = "Noto Sans CJK JP";
    private double _subtitleFontSize = 42;
    private string _subtitleColor = "#FFFFFFFF";
    private string _subtitleBackground = "#80000000";
    private double _subtitleOutline = 2;
    private int _subtitleBottomMargin = 45;

    public PlaybackState State => _state;
    public bool IsAvailable => _context != 0 && !_disposed && _state is not PlaybackState.Uninitialized and not PlaybackState.Failed;
    public bool IsFirstFrameReady => _firstFrameReady;
    public string? CurrentSource { get; private set; }
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public double Volume { get; private set; } = 100;
    public double Rate { get; private set; } = 1;
    public bool IsMuted { get; private set; }
    public bool AreSubtitlesVisible { get; private set; } = true;
    public IReadOnlyList<MediaTrack> Tracks { get { lock (_sync) return new ReadOnlyCollection<MediaTrack>(_tracks.ToArray()); } }
    public string? DecoderDescription { get; private set; }
    public int? VideoWidth { get; private set; }
    public int? VideoHeight { get; private set; }
    public double? VideoFrameRate { get; private set; }
    public double? VideoBitrate { get; private set; }
    public double? AudioBitrate { get; private set; }
    public string? LibraryVersion { get; private set; }
    public string HdrOutputStatus { get; private set; } = "Automatic HDR output is pending initialization.";
    public string DolbyVisionCompatibilityStatus { get; private set; } = "Not active.";
    public string RtxVideoSuperResolutionStatus { get; private set; } = "Disabled";

    public event EventHandler? StateChanged;
    public event EventHandler? FirstFrameReady;
    public event EventHandler? PositionChanged;
    public event EventHandler? Seeked;
    public event EventHandler? TracksChanged;
    public event EventHandler<PlaybackError>? ErrorOccurred;
    public event EventHandler? MediaEnded;

    /// <summary>
    /// Starts loading libmpv and its native dependencies without creating a playback context.
    /// Calling this during application construction moves cold DLL I/O off the first-play path.
    /// </summary>
    public static Task PreloadAsync() => LibraryPreload.Value;

    private static void PreloadLibraryCore()
    {
        StartupProfiler.Mark("mpv-dll-load-start");
        try { _ = MpvInterop.mpv_client_api_version(); }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            // InitializeAsync reports the actionable playback error through the normal UI path.
        }
        finally { StartupProfiler.Mark("mpv-dll-load-end"); }
    }

    /// <summary>
    /// Creates the libmpv context and applies pre-initialization options. It deliberately
    /// does not need an HWND, so callers can overlap this work with XAML construction.
    /// </summary>
    public Task PrepareAsync(HardwareDecoder hardwareDecoder, string renderer = "gpu-next", RtxVideoSuperResolutionMode rtxVideoSuperResolution = RtxVideoSuperResolutionMode.Auto, HdrOutputMode hdrOutput = HdrOutputMode.Auto, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _preparationTask ??= PrepareCoreAsync(hardwareDecoder, renderer, rtxVideoSuperResolution, hdrOutput);
            return _preparationTask.WaitAsync(cancellationToken);
        }
    }

    private async Task PrepareCoreAsync(HardwareDecoder hardwareDecoder, string renderer, RtxVideoSuperResolutionMode rtxVideoSuperResolution, HdrOutputMode hdrOutput)
    {
        await PreloadAsync().ConfigureAwait(false);
        await Task.Run(() => CreateAndConfigureCore(hardwareDecoder, renderer, rtxVideoSuperResolution, hdrOutput), CancellationToken.None).ConfigureAwait(false);
    }

    public async Task InitializeAsync(nint videoWindowHandle, HardwareDecoder hardwareDecoder, string renderer = "gpu-next", RtxVideoSuperResolutionMode rtxVideoSuperResolution = RtxVideoSuperResolutionMode.Auto, HdrOutputMode hdrOutput = HdrOutputMode.Auto, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsAvailable) return;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await PrepareAsync(hardwareDecoder, renderer, rtxVideoSuperResolution, hdrOutput, cancellationToken).ConfigureAwait(false);
            if (IsAvailable) return;
            var initialization = Task.Run(() => InitializeCore(videoWindowHandle, cancellationToken), cancellationToken);
            _initializationTask = initialization;
            await initialization.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CleanupContext();
            SetState(PlaybackState.Uninitialized);
            throw;
        }
        catch (DllNotFoundException exception)
        {
            CleanupContext();
            SetState(PlaybackState.Failed);
            RaiseError("PLAYBACK_ERROR", "libmpv (mpv-2.dll) was not found. Install libmpv or place the DLL beside the application.", exception);
        }
        catch (BadImageFormatException exception)
        {
            CleanupContext();
            SetState(PlaybackState.Failed);
            RaiseError("PLAYBACK_ERROR", "The libmpv architecture does not match the x64 application.", exception);
        }
        catch (Exception exception)
        {
            CleanupContext();
            SetState(PlaybackState.Failed);
            RaiseError("PLAYBACK_ERROR", exception.Message, exception);
        }
        finally { _initializationTask = null; }
    }

    private void CreateAndConfigureCore(HardwareDecoder hardwareDecoder, string renderer, RtxVideoSuperResolutionMode rtxVideoSuperResolution, HdrOutputMode hdrOutput)
    {
        StartupProfiler.Mark("mpv-create-start");
        _context = MpvInterop.mpv_create();
        if (_context == 0) throw new InvalidOperationException("libmpv could not create a playback context.");
        StartupProfiler.Mark("mpv-create-end");
        StartupProfiler.Mark("mpv-options-start");
        SetOption("config", "no");
        SetOption("load-scripts", "no");
        SetOption("terminal", "no");
        SetOption("osc", "no");
        SetOption("input-default-bindings", "no");
        SetOption("keep-open", "yes");
        SetOption("idle", "yes");
        TrySetOption("force-window", "immediate");
        SetOption("vo", string.IsNullOrWhiteSpace(renderer) ? "gpu-next" : renderer);
        SetOption("gpu-api", "d3d11");
        ConfigureHdrOutputOption(hdrOutput);
        SetOption("hwdec", hardwareDecoder switch
        {
            HardwareDecoder.D3D11VA => "d3d11va-copy,auto-safe",
            HardwareDecoder.Nvdec => "nvdec-copy,auto-safe",
            HardwareDecoder.Off => "no",
            _ => "auto-safe"
        });
        ConfigureRtxVideoSuperResolutionOption(rtxVideoSuperResolution);
        // mpv defaults deinterlacing to "no". Use automatic detection so interlaced
        // MPEG-2 sources such as DVD/VOB are passed through bwdif while progressive
        // sources remain untouched.
        SetOption("deinterlace", "auto");
        SetOption("sub-auto", "fuzzy");
        // Do not use the process working directory as sub-fonts-dir. Windows shell
        // activation can make it the media folder, causing libass to scan that entire
        // directory before the first frame. mpv already registers Matroska font
        // attachments directly and uses the system font provider for fallbacks.
        SetOption("audio-client-name", "AIMediaWorker");
        // Keep the Windows audio endpoint alive across loadfile replacements. Reopening it at
        // each playlist boundary can produce a short click on some drivers and receivers.
        TrySetOption("gapless-audio", "yes");
        TrySetOption("audio-stream-silence", "yes");
        TrySetOption("audio-buffer", "0.2");
        TrySetOption("audio-pitch-correction", "yes");
        // Keep the initial local-file read-ahead small. The source-specific settings in
        // OpenCore expand this for network media, but a 20-second local read-ahead can
        // make a large file wait on disk I/O before the first frame is presented.
        TrySetOption("cache", "auto");
        TrySetOption("cache-secs", "2");
        TrySetOption("demuxer-readahead-secs", "2");
        TrySetOption("cache-pause", "no");
        TrySetOption("stream-lavf-o", "reconnect=1,reconnect_at_eof=1,reconnect_streamed=1,reconnect_delay_max=5");
        // Volume overlay: show transient OSD messages at the top-left of the video.
        TrySetOption("osd-align-x", "left");
        TrySetOption("osd-align-y", "top");
        TrySetOption("osd-margin-x", "20");
        TrySetOption("osd-margin-y", "16");
        StartupProfiler.Mark("mpv-options-end");
    }

    private void InitializeCore(nint videoWindowHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetOption("wid", unchecked((ulong)videoWindowHandle).ToString(CultureInfo.InvariantCulture));
        StartupProfiler.Mark("mpv-initialize-start");
        MpvInterop.EnsureSuccess(MpvInterop.mpv_initialize(_context), "initialize libmpv");
        StartupProfiler.Mark("mpv-initialize-end");
        LibraryVersion = GetString("mpv-version");
        _eventLoopCancellation = new CancellationTokenSource();
        _eventLoop = Task.Run(() => EventLoop(_eventLoopCancellation.Token), CancellationToken.None);
        SetState(PlaybackState.Idle);
    }

    public async Task OpenAsync(string source, IReadOnlyDictionary<string, string>? httpHeaders = null, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("A media source is required.", nameof(source));
        await _openLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A synchronously acquired semaphore does not switch threads. Always perform the
            // native open sequence on a worker so decoder, network, and mpv queue delays cannot
            // block WinUI's dispatcher, including during shell/file-association startup.
            await Task.Run(() => OpenCore(source, httpHeaders, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _openLock.Release(); }
    }

    private void OpenCore(string source, IReadOnlyDictionary<string, string>? httpHeaders, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trackRefreshCancellation = Interlocked.Exchange(ref _trackRefreshCancellation, null);
        trackRefreshCancellation?.Cancel();
        trackRefreshCancellation?.Dispose();
        // A video filter that is kept across loadfile can make the next hardware
        // decoder negotiate its surface format while the first frame is already
        // being requested. Re-attach RTX VSR after the new decoder has produced a
        // frame instead of making the decoder and d3d11vpp initialize together.
        RemoveRtxVideoSuperResolutionFilter();
        RemoveDolbyVisionCompatibilityFilter();
        _firstFrameReady = false;
        _eofReached = false;
        SetState(PlaybackState.Loading);
        CurrentSource = source;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        VideoWidth = null;
        VideoHeight = null;
        VideoFrameRate = null;
        VideoBitrate = null;
        AudioBitrate = null;
        CancelPendingEditorSubtitleSelection();
        _editorSubtitlePath = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (httpHeaders is null || httpHeaders.Count == 0) SetProperty("http-header-fields", string.Empty);
        else
        {
            foreach (var header in httpHeaders)
                if (header.Key.Any(c => c is '\r' or '\n' or ':' or ',') || header.Value.Any(c => c is '\r' or '\n' or ',')) throw new ArgumentException("Invalid HTTP header.", nameof(httpHeaders));
            SetProperty("http-header-fields", string.Join(',', httpHeaders.Select(header => $"{header.Key}: {header.Value}")));
        }
        ConfigureSourceBuffering(source);
        // Keep mpv's automatically selected subtitle track attached even when it is
        // hidden. That lets libass prepare ASS data and embedded fonts as part of the
        // normal file load, so showing subtitles later is only a visibility change and
        // cannot interrupt active playback with first-time track initialization.
        // Clear the previous file's video selection before replacement. Pass vid=auto
        // and sid=auto as file-local load options so the new video and hidden subtitle
        // tracks are selected during initial track selection. Restoring them from
        // FileLoaded is too late:
        // loadfile completes asynchronously and can leave a one-frame cover-art track
        // displaying the previous file's already-presented frame.
        TrySetProperty("vid", "no");
        _loadfileIssued = true;
        StartupProfiler.Mark("loadfile-command");
        MpvInterop.CommandAsync(_context, NextCommandId(), "loadfile", source, "replace", "-1", "vid=auto,sid=auto");
        // A synchronous write here waits behind loadfile until playback is ready. Preserve the
        // required order without waiting for it to finish.
        MpvInterop.CommandAsync(_context, NextCommandId(), "set", "pause", "no");
    }

    private ulong NextCommandId() => unchecked((ulong)Interlocked.Increment(ref _nextCommandId));

    public void Play() { SetProperty("pause", "no"); SetState(PlaybackState.Playing); }
    public void Pause() { SetProperty("pause", "yes"); SetState(PlaybackState.Paused); }
    public void TogglePause() { if (State == PlaybackState.Playing) Pause(); else Play(); }
    public void Stop() { Command("stop"); SetState(PlaybackState.Idle); Position = TimeSpan.Zero; PositionChanged?.Invoke(this, EventArgs.Empty); }

    public void Seek(TimeSpan position, bool exact = false) => Command("seek", Math.Max(0, position.TotalSeconds).ToString("0.######", CultureInfo.InvariantCulture), "absolute" + (exact ? "+exact" : "+keyframes"));
    public void SeekRelative(TimeSpan offset) => Command("seek", offset.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture), "relative");
    public void SetVolume(double volume)
    {
        if (!double.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume));
        var normalizedVolume = Math.Clamp(volume, 0, 130);
        SetProperty("volume", normalizedVolume.ToString("0.##", CultureInfo.InvariantCulture));
        Volume = normalizedVolume;
    }

    public void ShowOsdText(string text, double durationSeconds = 1.2)
    {
        var normalizedDuration = double.IsFinite(durationSeconds)
            ? Math.Clamp(durationSeconds, 0.1, int.MaxValue / 1000d)
            : 1.2;
        var durationMilliseconds = (int)Math.Round(normalizedDuration * 1000, MidpointRounding.AwayFromZero);
        EnsureAvailable();
        MpvInterop.CommandAsync(_context, NextCommandId(), "show-text", text, durationMilliseconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Displays one generated cue through mpv's lightweight OSD. Updating an ASS
    /// subtitle track can make libass rebuild the track on mpv's playback core;
    /// OSD messages avoid that track-level reconfiguration while still rendering
    /// the cue over the video.
    /// </summary>
    public void ShowSubtitleOsdText(string text, double durationSeconds = 1.2)
    {
        var normalizedDuration = double.IsFinite(durationSeconds)
            ? Math.Clamp(durationSeconds, 0.1, int.MaxValue / 1000d)
            : 1.2;
        var durationMilliseconds = (int)Math.Round(normalizedDuration * 1000, MidpointRounding.AwayFromZero);
        EnsureAvailable();
        ConfigureSubtitleOsdPlacement();
        MpvInterop.CommandAsync(_context, NextCommandId(), "show-text", text, durationMilliseconds.ToString(CultureInfo.InvariantCulture));
    }

    public void ConfigureGeneratedSubtitleOsd(bool enabled)
    {
        EnsureAvailable();
        if (enabled) ConfigureSubtitleOsdPlacement();
        else RestoreDefaultOsdPlacement();
    }

    public void ClearSubtitleOsdText()
    {
        if (!IsAvailable) return;
        MpvInterop.CommandAsync(_context, NextCommandId(), "show-text", string.Empty, "100");
        RestoreDefaultOsdPlacement();
    }
    public void SetMute(bool muted) { IsMuted = muted; SetProperty("mute", muted ? "yes" : "no"); }
    public void SetSubtitleVisibility(bool visible)
    {
        // Do not rewrite mpv's embeddedfonts property here. Setting it to "yes" for
        // the first time can synchronously initialize libass's system-font provider,
        // which blocks startup for tens of seconds on machines with a large font set.
        // mpv already defaults embeddedfonts to enabled. Hidden tracks stay selected
        // and prepared; sub-visibility alone controls whether they are rendered.
        SetProperty("sub-visibility", visible ? "yes" : "no");
        AreSubtitlesVisible = visible;
    }
    public void SetRate(double rate) { Rate = Math.Clamp(rate, 0.25, 4); SetProperty("speed", Rate.ToString("0.###", CultureInfo.InvariantCulture)); }
    public void SetLoopFile(bool enabled) => SetProperty("loop-file", enabled ? "inf" : "no");
    public void FrameStep(bool backwards = false) => Command(backwards ? "frame-back-step" : "frame-step");
    public void SaveScreenshot(string path, bool includeSubtitles = true)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A screenshot path is required.", nameof(path));
        Command("screenshot-to-file", Path.GetFullPath(path), includeSubtitles ? "subtitles" : "video");
    }

    public void SetAbLoop(TimeSpan? start, TimeSpan? end)
    {
        SetProperty("ab-loop-a", start is null ? "no" : start.Value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        SetProperty("ab-loop-b", end is null ? "no" : end.Value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
    }

    public void SelectTrack(MediaTrackType type, int? id)
    {
        SetProperty(type switch
        {
            MediaTrackType.Video => "vid",
            MediaTrackType.Audio => "aid",
            MediaTrackType.Subtitle => "sid",
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        }, id?.ToString(CultureInfo.InvariantCulture) ?? "no");
        if (type == MediaTrackType.Subtitle) RestoreSubtitleVisibility();
    }

    public void LoadSubtitle(string path, bool select = true)
    {
        EnsureAvailable();
        if (select)
        {
            SetProperty("sid", "no");
            SetProperty("secondary-sid", "no");
        }
        // Loading/parsing an external subtitle can take long enough to stall the
        // playback thread. Queue it through libmpv instead of waiting synchronously.
        MpvInterop.CommandAsync(_context, NextCommandId(), "sub-add", path, select ? "select" : "auto");
        RestoreSubtitleVisibility();
    }

    public void UpdateEditorSubtitle(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var selectionCancellation = new CancellationTokenSource();
        try
        {
            lock (_subtitleCommandSync)
            {
                EnsureAvailable();
                var wasPlaying = _state == PlaybackState.Playing;
                var trackIds = FindExternalSubtitleTrackIds(fullPath);
                _editorSubtitlePath = fullPath;

                SetProperty("sid", "no");
                SetProperty("secondary-sid", "no");

                if (trackIds.Count == 0)
                {
                    // sub-add loads the file asynchronously inside mpv. The follow-up
                    // selector below will select this track again once it is visible in
                    // track-list, which is important when an embedded subtitle was
                    // selected before the editor overlay was added.
                    MpvInterop.CommandAsync(_context, NextCommandId(), "sub-add", fullPath, "select");
                }
                else
                {
                    // A previous interrupted update can leave duplicate instances of the
                    // temporary track behind. Keep one track, select it explicitly, and
                    // reload that track so mpv cannot continue rendering an older selection.
                    foreach (var duplicateId in trackIds.Skip(1).OrderByDescending(id => id))
                        MpvInterop.CommandAsync(_context, NextCommandId(), "sub-remove", duplicateId.ToString(CultureInfo.InvariantCulture));

                    var editorTrackId = trackIds[0];
                    SetProperty("sid", editorTrackId.ToString(CultureInfo.InvariantCulture));
                    // Queue the reload instead of synchronously waiting for libass to
                    // rebuild the track. This method is called while ASR/translation
                    // results are being applied and must not block playback.
                    MpvInterop.CommandAsync(_context, NextCommandId(), "sub-reload", editorTrackId.ToString(CultureInfo.InvariantCulture));
                }

                // Subtitle track changes can briefly inherit mpv's paused state while
                // the external file is parsed. Preserve the user's active playback.
                if (wasPlaying) MpvInterop.CommandAsync(_context, NextCommandId(), "set", "pause", "no");
                RestoreSubtitleVisibility();

                var previousSelection = Interlocked.Exchange(ref _editorSubtitleSelectionCancellation, selectionCancellation);
                previousSelection?.Cancel();
                previousSelection?.Dispose();
            }
        }
        catch
        {
            selectionCancellation.Dispose();
            throw;
        }

        _ = EnsureEditorSubtitleSelectedAsync(fullPath, selectionCancellation.Token);
    }

    public bool IsEditorSubtitleSelected
    {
        get
        {
            if (_context == 0 || _editorSubtitlePath is null) return false;
            return FindExternalSubtitleTrackIds(_editorSubtitlePath, selectedOnly: true).Count > 0;
        }
    }

    public bool RestoreEditorSubtitleAfterSeek()
    {
        string? path;
        lock (_subtitleCommandSync) path = _editorSubtitlePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        lock (_subtitleCommandSync)
        {
            var trackId = FindExternalSubtitleTrackIds(path).FirstOrDefault();
            if (trackId == 0) return false;
            SetProperty("secondary-sid", "no");
            SetProperty("sid", trackId.ToString(CultureInfo.InvariantCulture));
            RestoreSubtitleVisibility();
            return true;
        }
    }

    public bool RestoreSubtitleSelection(int? preferredTrackId = null, bool preferEditor = true)
    {
        lock (_subtitleCommandSync)
        {
            EnsureAvailable();
            if (preferEditor && !string.IsNullOrWhiteSpace(_editorSubtitlePath) && File.Exists(_editorSubtitlePath))
            {
                var editorTrackId = FindExternalSubtitleTrackIds(_editorSubtitlePath).FirstOrDefault();
                if (editorTrackId != 0)
                {
                    SetProperty("secondary-sid", "no");
                    SetProperty("sid", editorTrackId.ToString(CultureInfo.InvariantCulture));
                    RestoreSubtitleVisibility();
                    return true;
                }
            }

            var trackId = FindSubtitleTrackId(preferredTrackId);
            if (trackId is not { } selectedTrackId) return false;
            SetProperty("secondary-sid", "no");
            // Reassigning an already selected ASS track can make mpv rebuild libass
            // state. Keep the prepared track intact when visibility is the only change.
            if (GetInt("sid") != selectedTrackId)
                SetProperty("sid", selectedTrackId.ToString(CultureInfo.InvariantCulture));
            RestoreSubtitleVisibility();
            return true;
        }
    }

    public void ConfigureNetwork(TimeSpan timeout, string? proxy)
    {
        TrySetProperty("network-timeout", Math.Clamp(timeout.TotalSeconds, 1, 600).ToString("0", CultureInfo.InvariantCulture));
        TrySetProperty("http-proxy", string.IsNullOrWhiteSpace(proxy) ? string.Empty : proxy);
    }

    private void ConfigureSourceBuffering(string source)
    {
        var isNetworkSource = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var readAheadSeconds = isNetworkSource ? "20" : "2";

        // These are writable mpv properties. Applying them immediately before loadfile
        // keeps network buffering intact while preventing a local file on a slow disk
        // from being read ahead for 20 seconds before playback can start.
        TrySetProperty("cache-secs", readAheadSeconds);
        TrySetProperty("demuxer-readahead-secs", readAheadSeconds);
    }

    public void ConfigurePreferredLanguages(string? audioLanguage, string? subtitleLanguage)
    {
        TrySetProperty("alang", audioLanguage ?? string.Empty);
        TrySetProperty("slang", subtitleLanguage ?? string.Empty);
    }

    public void ConfigureSubtitleStyle(string fontFamily, double fontSize, string color, string background, double outline, int bottomMargin)
    {
        _subtitleFontFamily = string.IsNullOrWhiteSpace(fontFamily) ? "Noto Sans CJK JP" : fontFamily;
        _subtitleFontSize = double.IsFinite(fontSize) ? Math.Clamp(fontSize, 1, 500) : 42;
        _subtitleColor = string.IsNullOrWhiteSpace(color) ? "#FFFFFFFF" : color;
        _subtitleBackground = string.IsNullOrWhiteSpace(background) ? "#80000000" : background;
        _subtitleOutline = double.IsFinite(outline) ? Math.Clamp(outline, 0, 100) : 2;
        _subtitleBottomMargin = Math.Max(0, bottomMargin);
        TrySetProperty("sub-font", _subtitleFontFamily);
        TrySetProperty("sub-font-size", _subtitleFontSize.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetProperty("sub-color", _subtitleColor);
        TrySetProperty("sub-back-color", _subtitleBackground);
        TrySetProperty("sub-border-size", _subtitleOutline.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetProperty("sub-margin-y", _subtitleBottomMargin.ToString(CultureInfo.InvariantCulture));
    }

    public void ConfigureHdrOutput(HdrOutputMode mode)
    {
        EnsureAvailable();
        var configured = TrySetProperty("target-colorspace-hint", HdrOutputOptions.GetColorspaceHint(mode));
        TrySetProperty("target-colorspace-hint-mode", "target");
        TrySetProperty("d3d11-output-format", "auto");
        TrySetProperty("d3d11-output-csp", "auto");
        HdrOutputStatus = configured ? DescribeHdrOutput(mode) : "Unavailable: this renderer or libmpv build does not support HDR color-space signaling.";
    }

    public void ConfigureRtxVideoSuperResolution(RtxVideoSuperResolutionMode mode)
    {
        EnsureAvailable();
        _rtxVideoSuperResolutionMode = mode;
        if (mode == RtxVideoSuperResolutionMode.Off)
        {
            RemoveRtxVideoSuperResolutionFilter();
            RtxVideoSuperResolutionStatus = "Disabled";
            return;
        }

        // Do not change the filter chain while the file is still negotiating its
        // decoder. The first PlaybackRestart event is the earliest point at which
        // AV1 and other hardware-decoded streams have a stable D3D11 surface.
        if (_firstFrameReady) ApplyRtxVideoSuperResolutionFilter();
        else RtxVideoSuperResolutionStatus = "NVIDIA RTX Video Super Resolution will be applied after the decoder is ready.";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingEditorSubtitleSelection();
        if (_initializationTask is { } initialization)
        {
            try { await initialization.ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or DllNotFoundException or BadImageFormatException or MpvException or InvalidOperationException) { }
        }
        var cancellation = _eventLoopCancellation;
        cancellation?.Cancel();
        var trackRefreshCancellation = Interlocked.Exchange(ref _trackRefreshCancellation, null);
        trackRefreshCancellation?.Cancel();
        if (_eventLoop is not null)
        {
            try { await _eventLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
        trackRefreshCancellation?.Dispose();
        CleanupContext();
        SetState(PlaybackState.Disposed);
        GC.SuppressFinalize(this);
    }

    private void EventLoop(CancellationToken cancellationToken)
    {
        var nextPoll = DateTime.UtcNow.AddMilliseconds(200);
        var nextMetadataPoll = DateTime.MinValue;
        try
        {
            while (!cancellationToken.IsCancellationRequested && _context != 0)
            {
                var eventPointer = MpvInterop.mpv_wait_event(_context, 0.1);
                if (eventPointer != 0)
                {
                    var mpvEvent = Marshal.PtrToStructure<MpvInterop.MpvEvent>(eventPointer);
                    HandleEvent(mpvEvent);
                }
                if (_state is PlaybackState.Playing or PlaybackState.Paused && DateTime.UtcNow >= nextPoll)
                {
                    var includeMetadata = DateTime.UtcNow >= nextMetadataPoll;
                    PollProperties(includeMetadata);
                    if (includeMetadata) nextMetadataPoll = DateTime.UtcNow.AddSeconds(1);
                    nextPoll = DateTime.UtcNow.AddMilliseconds(200);
                }
            }
        }
        catch (Exception exception) when (!_disposed)
        {
            SetState(PlaybackState.Failed);
            RaiseError("PLAYBACK_ERROR", "The libmpv event loop stopped unexpectedly.", exception);
        }
    }

    private void HandleEvent(MpvInterop.MpvEvent mpvEvent)
    {
        switch (mpvEvent.EventId)
        {
            case MpvInterop.MpvEventId.CommandReply:
                if (mpvEvent.Error < 0) RaiseError("PLAYBACK_ERROR", MpvInterop.ErrorString(mpvEvent.Error));
                break;
            case MpvInterop.MpvEventId.StartFile:
                if (_loadfileIssued) StartupProfiler.Mark("start-file");
                _firstFrameReady = false;
                break;
            case MpvInterop.MpvEventId.FileLoaded:
                StartupProfiler.Mark("file-loaded");
                ApplyDolbyVisionCompatibilityFallbackIfNeeded();
                // libmpv can restore per-file subtitle state when a new source is
                // loaded. Reapply the user's preference after the file is ready so
                // the menu checkmark and the renderer cannot drift apart.
                RestoreSubtitleVisibility();
                SetState(GetBool("pause") ? PlaybackState.Paused : PlaybackState.Playing);
                break;
            case MpvInterop.MpvEventId.EndFile:
                var endedNaturally = true;
                if (mpvEvent.Data != 0)
                {
                    var end = Marshal.PtrToStructure<MpvInterop.MpvEventEndFile>(mpvEvent.Data);
                    endedNaturally = end.Reason == 0;
                    if (end.Error < 0)
                    {
                        var network = CurrentSource is not null && Uri.TryCreate(CurrentSource, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
                        RaiseError(network ? "NETWORK_ERROR" : "PLAYBACK_ERROR", MpvInterop.ErrorString(end.Error));
                    }
                }
                if (endedNaturally)
                    RaiseMediaEnded();
                break;
            case MpvInterop.MpvEventId.Seek:
                Seeked?.Invoke(this, EventArgs.Empty);
                break;
            case MpvInterop.MpvEventId.VideoReconfig:
                if (_loadfileIssued) StartupProfiler.Mark("video-reconfig");
                if (_firstFrameReady) ScheduleTrackRefresh();
                break;
            case MpvInterop.MpvEventId.AudioReconfig:
                if (_loadfileIssued) StartupProfiler.Mark("audio-reconfig");
                if (_firstFrameReady) ScheduleTrackRefresh();
                break;
            case MpvInterop.MpvEventId.PlaybackRestart:
                ApplyDolbyVisionCompatibilityFallbackIfNeeded();
                if (!_firstFrameReady)
                {
                    _firstFrameReady = true;
                    var codec = GetString("video-codec");
                    var decoder = GetString("hwdec-current") ?? "software";
                    DecoderDescription = FormatDecoderDescription(codec, decoder);
                    StartupProfiler.CompleteAtFirstFrame(DecoderDescription);
                    FirstFrameReady?.Invoke(this, EventArgs.Empty);
                    ScheduleTrackRefresh();
                }
                // Apply after FirstFrameReady has been raised so AV1 can use its
                // D3D11 hardware surface without delaying the first visible frame.
                ApplyRtxVideoSuperResolutionFilter();
                break;
            case MpvInterop.MpvEventId.Shutdown:
                return;
        }
    }

    private void ScheduleTrackRefresh()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _trackRefreshCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _ = RefreshTracksAfterFirstFrameAsync(cancellation);
    }

    private async Task RefreshTracksAfterFirstFrameAsync(CancellationTokenSource cancellation)
    {
        try
        {
            // Track metadata uses many synchronous property reads. Keep those reads away from
            // file loading and the renderer's first-frame setup, then coalesce reconfigure events.
            await Task.Delay(300, cancellation.Token).ConfigureAwait(false);
            if (!cancellation.IsCancellationRequested && _context != 0)
            {
                // Some MPEG-TS sources expose Dolby Vision track metadata only after
                // the decoder has produced a frame. Retry here after the initial events.
                ApplyDolbyVisionCompatibilityFallbackIfNeeded();
                ReadTracks();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (Interlocked.CompareExchange(ref _trackRefreshCancellation, null, cancellation) == cancellation)
                cancellation.Dispose();
        }
    }

    private void PollProperties(bool includeMetadata)
    {
        if (_context == 0) return;
        var position = GetDouble("time-pos");
        // keep-open leaves the current file loaded at EOF, so mpv does not always
        // emit MPV_EVENT_END_FILE. Convert the eof-reached property into the same
        // one-shot event used by the normal end-file path.
        var eofReached = GetBool("eof-reached");
        var changed = false;
        if (position is not null)
        {
            Position = TimeSpan.FromSeconds(Math.Max(0, position.Value));
            changed = true;
        }
        if (includeMetadata)
        {
            if (GetDouble("duration") is { } duration) Duration = TimeSpan.FromSeconds(Math.Max(0, duration));
            VideoWidth = GetInt("dwidth") ?? GetInt("width");
            VideoHeight = GetInt("dheight") ?? GetInt("height");
            var frameRate = GetDouble("container-fps") ?? GetDouble("estimated-vf-fps");
            VideoFrameRate = frameRate is > 0 && double.IsFinite(frameRate.Value) ? frameRate : null;
            var videoBitrate = GetDouble("video-bitrate") ?? GetDouble("current-tracks/video/demux-bitrate");
            VideoBitrate = videoBitrate is > 0 && double.IsFinite(videoBitrate.Value) ? videoBitrate : null;
            var audioBitrate = GetDouble("audio-bitrate") ?? GetDouble("current-tracks/audio/demux-bitrate");
            AudioBitrate = audioBitrate is > 0 && double.IsFinite(audioBitrate.Value) ? audioBitrate : null;
            DecoderDescription = FormatDecoderDescription(GetString("video-codec"), GetString("hwdec-current") ?? "software");
        }
        if (changed) PositionChanged?.Invoke(this, EventArgs.Empty);
        if (eofReached) RaiseMediaEnded();
        else _eofReached = false;
    }

    private void RaiseMediaEnded()
    {
        if (_eofReached) return;
        _eofReached = true;
        SetState(PlaybackState.Ended);
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private void ReadTracks()
    {
        var count = GetInt("track-list/count") ?? 0;
        var result = new List<MediaTrack>(count);
        for (var i = 0; i < count; i++)
        {
            var prefix = $"track-list/{i}/";
            var type = GetString(prefix + "type") switch { "video" => MediaTrackType.Video, "audio" => MediaTrackType.Audio, "sub" => MediaTrackType.Subtitle, _ => MediaTrackType.Unknown };
            result.Add(new MediaTrack(GetInt(prefix + "id") ?? -1, type, GetString(prefix + "lang"), GetString(prefix + "title"), GetString(prefix + "codec"), GetBool(prefix + "default"), GetBool(prefix + "forced"), GetBool(prefix + "selected")));
        }
        lock (_sync) { _tracks.Clear(); _tracks.AddRange(result); }
        RestoreSubtitleVisibility();
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatDecoderDescription(string? codec, string decoder) =>
        string.IsNullOrWhiteSpace(codec) ? decoder : $"{ShortenVideoCodec(codec)} / {decoder}";

    private static string ShortenVideoCodec(string codec)
    {
        var name = codec.Trim();
        var detailsStart = name.IndexOf(" (", StringComparison.Ordinal);
        if (detailsStart > 0) name = name[..detailsStart];
        else
        {
            var aliasStart = name.IndexOf(" / ", StringComparison.Ordinal);
            if (aliasStart > 0) name = name[..aliasStart];
        }

        return name.ToLowerInvariant() switch
        {
            "h264" or "avc" => "H.264",
            "h265" or "hevc" => "H.265",
            "av1" => "AV1",
            "vp9" => "VP9",
            "vp8" => "VP8",
            "mpeg2video" => "MPEG-2",
            "mpeg4" => "MPEG-4",
            "vc1" => "VC-1",
            "prores" => "ProRes",
            _ => name
        };
    }

    private async Task EnsureEditorSubtitleSelectedAsync(string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            // mpv processes sub-add asynchronously. Retry briefly so an existing native
            // subtitle cannot remain selected simply because track-list has not caught up
            // when UpdateEditorSubtitle returns.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(attempt == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                lock (_subtitleCommandSync)
                {
                    if (_disposed || _context == 0 || !PathsEqual(_editorSubtitlePath ?? string.Empty, fullPath)) return;
                    var trackIds = FindExternalSubtitleTrackIds(fullPath);
                    if (trackIds.Count == 0) continue;

                    SetProperty("secondary-sid", "no");
                    foreach (var duplicateId in trackIds.Skip(1).OrderByDescending(id => id))
                        MpvInterop.CommandAsync(_context, NextCommandId(), "sub-remove", duplicateId.ToString(CultureInfo.InvariantCulture));
                    SetProperty("sid", trackIds[0].ToString(CultureInfo.InvariantCulture));
                    RestoreSubtitleVisibility();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (!_disposed)
        {
            RaiseError("PLAYBACK_ERROR", "The editor subtitle could not be selected.", exception);
        }
    }

    private void CancelPendingEditorSubtitleSelection()
    {
        var cancellation = Interlocked.Exchange(ref _editorSubtitleSelectionCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private List<int> FindExternalSubtitleTrackIds(string fullPath, bool selectedOnly = false)
    {
        var result = new List<int>();
        var count = GetInt("track-list/count") ?? 0;
        for (var i = 0; i < count; i++)
        {
            var prefix = $"track-list/{i}/";
            if (!string.Equals(GetString(prefix + "type"), "sub", StringComparison.OrdinalIgnoreCase) ||
                selectedOnly && !GetBool(prefix + "selected")) continue;

            var externalFilename = GetString(prefix + "external-filename");
            if (externalFilename is not null && PathsEqual(externalFilename, fullPath) && GetInt(prefix + "id") is { } id)
                result.Add(id);
        }
        return result;
    }

    private int? FindSubtitleTrackId(int? preferredTrackId)
    {
        var count = GetInt("track-list/count") ?? 0;
        var ids = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            var prefix = $"track-list/{i}/";
            if (string.Equals(GetString(prefix + "type"), "sub", StringComparison.OrdinalIgnoreCase) && GetInt(prefix + "id") is { } id)
                ids.Add(id);
        }

        if (preferredTrackId is { } preferred && ids.Contains(preferred)) return preferred;
        if (GetInt("sid") is { } current && ids.Contains(current)) return current;
        return ids.Count > 0 ? ids[0] : null;
    }

    private static bool PathsEqual(string first, string second)
    {
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return string.Equals(first, second, StringComparison.OrdinalIgnoreCase); }
    }

    private void ConfigureSubtitleOsdPlacement()
    {
        TrySetProperty("osd-align-x", "center");
        TrySetProperty("osd-align-y", "bottom");
        TrySetProperty("osd-margin-x", "20");
        TrySetProperty("osd-margin-y", _subtitleBottomMargin.ToString(CultureInfo.InvariantCulture));
        TrySetProperty("osd-font", _subtitleFontFamily);
        TrySetProperty("osd-font-size", _subtitleFontSize.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetProperty("osd-color", _subtitleColor);
        TrySetProperty("osd-back-color", _subtitleBackground);
        TrySetProperty("osd-border-size", _subtitleOutline.ToString("0.##", CultureInfo.InvariantCulture));
    }

    private void RestoreDefaultOsdPlacement()
    {
        TrySetProperty("osd-align-x", "left");
        TrySetProperty("osd-align-y", "top");
        TrySetProperty("osd-margin-x", "20");
        TrySetProperty("osd-margin-y", "16");
    }

    private void SetOption(string name, string value) => MpvInterop.EnsureSuccess(MpvInterop.mpv_set_option_string(_context, name, value), $"set option {name}");
    private bool TrySetOption(string name, string value) { try { SetOption(name, value); return true; } catch (MpvException) { return false; } }
    private void SetProperty(string name, string value) { EnsureAvailable(); MpvInterop.EnsureSuccess(MpvInterop.mpv_set_property_string(_context, name, value), $"set property {name}"); }
    private bool TrySetProperty(string name, string value) { try { SetProperty(name, value); return true; } catch (MpvException) { return false; } }
    private void RestoreSubtitleVisibility() => TrySetProperty("sub-visibility", AreSubtitlesVisible ? "yes" : "no");
    private void Command(params string[] args) { EnsureAvailable(); MpvInterop.Command(_context, args); }
    private string? GetString(string property) => _context == 0 ? null : MpvInterop.GetString(_context, property);
    private int? GetInt(string property) => int.TryParse(GetString(property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private double? GetDouble(string property) => double.TryParse(GetString(property), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    private bool GetBool(string property) => GetString(property) is "yes" or "true";

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_context == 0) throw new InvalidOperationException("libmpv is not initialized.");
    }

    private void CleanupContext()
    {
        var context = Interlocked.Exchange(ref _context, nint.Zero);
        if (context != 0) MpvInterop.mpv_terminate_destroy(context);
    }

    private void SetState(PlaybackState state) { if (_state == state) return; _state = state; StateChanged?.Invoke(this, EventArgs.Empty); }
    private void RaiseError(string code, string message, Exception? exception = null) => ErrorOccurred?.Invoke(this, new PlaybackError(code, message, exception));

    private void ConfigureRtxVideoSuperResolutionOption(RtxVideoSuperResolutionMode mode)
    {
        _rtxVideoSuperResolutionMode = mode;
        _rtxVideoSuperResolutionFilterApplied = false;
        if (mode == RtxVideoSuperResolutionMode.Off)
        {
            RtxVideoSuperResolutionStatus = "Disabled";
            return;
        }

        // The filter is intentionally attached at the first PlaybackRestart event.
        // Pre-initializing d3d11vpp here races AV1/D3D11VA surface negotiation and can
        // leave libmpv on the first frame with a stalled video clock.
        RtxVideoSuperResolutionStatus = "NVIDIA RTX Video Super Resolution will be applied after the decoder is ready.";
    }

    private void ConfigureHdrOutputOption(HdrOutputMode mode)
    {
        var configured = TrySetOption("target-colorspace-hint", HdrOutputOptions.GetColorspaceHint(mode));
        // target mode adapts HDR to the active display capabilities instead of blindly
        // forwarding source mastering metadata. D3D11 auto selects a 10-bit swap chain
        // and the desktop color space when Windows HDR is enabled.
        TrySetOption("target-colorspace-hint-mode", "target");
        TrySetOption("d3d11-output-format", "auto");
        TrySetOption("d3d11-output-csp", "auto");
        HdrOutputStatus = configured ? DescribeHdrOutput(mode) : "Unavailable: this renderer or libmpv build does not support HDR color-space signaling.";
    }

    private static string DescribeHdrOutput(HdrOutputMode mode) => mode switch
    {
        HdrOutputMode.Off => "Disabled; HDR sources are rendered without display color-space signaling.",
        HdrOutputMode.Auto => "Automatic; HDR metadata and a 10-bit D3D11 swap chain are negotiated when supported by Windows and the active display.",
        HdrOutputMode.On => "Forced; libmpv always requests display color-space signaling.",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private void ApplyDolbyVisionCompatibilityFallbackIfNeeded()
    {
        if (_context == 0 || _dolbyVisionCompatibilityEvaluated) return;
        var profile = GetInt("current-tracks/video/dolby-vision-profile");
        if (profile is null) return;

        _dolbyVisionCompatibilityEvaluated = true;
        if (!DolbyVisionCompatibilityFallback.IsRequired(profile))
        {
            DolbyVisionCompatibilityStatus = $"Not required for Dolby Vision Profile {profile}.";
            return;
        }

        try
        {
            MpvInterop.Command(_context, "vf", "add", DolbyVisionCompatibilityFallback.BuildFilter(profile.Value));
            _dolbyVisionCompatibilityFilterApplied = true;
            DolbyVisionCompatibilityStatus = profile == DolbyVisionCompatibilityFallback.SdrBaseLayerProfile
                ? "Active: Dolby Vision Profile 4 is using its SDR Rec.709 base layer."
                : "Active: Dolby Vision Profile 8 is using its tagged compatible base layer.";
        }
        catch (MpvException exception)
        {
            _dolbyVisionCompatibilityFilterApplied = false;
            DolbyVisionCompatibilityStatus = $"Unavailable: {exception.Message}";
        }
    }

    private void RemoveDolbyVisionCompatibilityFilter()
    {
        if (_dolbyVisionCompatibilityFilterApplied && _context != 0)
        {
            try { MpvInterop.Command(_context, "vf", "remove", $"@{DolbyVisionCompatibilityFallback.Label}"); }
            catch (MpvException) { }
        }

        _dolbyVisionCompatibilityFilterApplied = false;
        _dolbyVisionCompatibilityEvaluated = false;
        DolbyVisionCompatibilityStatus = "Not active.";
    }

    private void ApplyRtxVideoSuperResolutionFilter()
    {
        if (_context == 0 || _rtxVideoSuperResolutionMode == RtxVideoSuperResolutionMode.Off || _rtxVideoSuperResolutionFilterApplied) return;
        var dolbyVisionProfile = GetInt("current-tracks/video/dolby-vision-profile");
        var transferFunction = GetString("video-dec-params/gamma");
        if (!RtxVideoSuperResolutionFilter.ShouldApply(_rtxVideoSuperResolutionMode, dolbyVisionProfile, transferFunction))
        {
            RtxVideoSuperResolutionStatus = "Skipped for HDR or Dolby Vision content to preserve 10-bit color metadata.";
            return;
        }
        var filter = RtxVideoSuperResolutionFilter.Build(_rtxVideoSuperResolutionMode);
        if (filter is null) return;
        try
        {
            MpvInterop.Command(_context, "vf", "add", filter);
            _rtxVideoSuperResolutionFilterApplied = true;
            var codec = GetString("video-codec") ?? "video";
            var decoder = GetString("hwdec-current") ?? "software";
            RtxVideoSuperResolutionStatus = $"NVIDIA RTX Video Super Resolution configured for {codec} / {decoder}; per-frame activation remains driver controlled.";
        }
        catch (MpvException exception)
        {
            _rtxVideoSuperResolutionFilterApplied = false;
            RtxVideoSuperResolutionStatus = $"Unavailable: {exception.Message}";
        }
    }

    private void RemoveRtxVideoSuperResolutionFilter()
    {
        if (!_rtxVideoSuperResolutionFilterApplied || _context == 0) return;
        try { MpvInterop.Command(_context, "vf", "remove", $"@{RtxVideoSuperResolutionFilter.Label}"); }
        catch (MpvException) { }
        finally { _rtxVideoSuperResolutionFilterApplied = false; }
    }
}
