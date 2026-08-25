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
    private long _nextCommandId;
    private PlaybackState _state = PlaybackState.Uninitialized;
    private string? _editorSubtitlePath;
    private volatile bool _firstFrameReady;
    private volatile bool _loadfileIssued;
    private bool _disposed;
    private RtxVideoSuperResolutionMode _rtxVideoSuperResolutionMode = RtxVideoSuperResolutionMode.Off;
    private bool _rtxVideoSuperResolutionFilterApplied;

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
    public string? LibraryVersion { get; private set; }
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
    public Task PrepareAsync(HardwareDecoder hardwareDecoder, string renderer = "gpu-next", RtxVideoSuperResolutionMode rtxVideoSuperResolution = RtxVideoSuperResolutionMode.Auto, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _preparationTask ??= PrepareCoreAsync(hardwareDecoder, renderer, rtxVideoSuperResolution);
            return _preparationTask.WaitAsync(cancellationToken);
        }
    }

    private async Task PrepareCoreAsync(HardwareDecoder hardwareDecoder, string renderer, RtxVideoSuperResolutionMode rtxVideoSuperResolution)
    {
        await PreloadAsync().ConfigureAwait(false);
        await Task.Run(() => CreateAndConfigureCore(hardwareDecoder, renderer, rtxVideoSuperResolution), CancellationToken.None).ConfigureAwait(false);
    }

    public async Task InitializeAsync(nint videoWindowHandle, HardwareDecoder hardwareDecoder, string renderer = "gpu-next", RtxVideoSuperResolutionMode rtxVideoSuperResolution = RtxVideoSuperResolutionMode.Auto, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsAvailable) return;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await PrepareAsync(hardwareDecoder, renderer, rtxVideoSuperResolution, cancellationToken).ConfigureAwait(false);
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

    private void CreateAndConfigureCore(HardwareDecoder hardwareDecoder, string renderer, RtxVideoSuperResolutionMode rtxVideoSuperResolution)
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
        SetOption("sub-fonts-dir", ".");
        SetOption("audio-client-name", "AIMediaWorker");
        // Keep the Windows audio endpoint alive across loadfile replacements. Reopening it at
        // each playlist boundary can produce a short click on some drivers and receivers.
        TrySetOption("gapless-audio", "yes");
        TrySetOption("audio-stream-silence", "yes");
        TrySetOption("audio-buffer", "0.2");
        TrySetOption("audio-pitch-correction", "yes");
        // Let mpv bypass the stream cache for fast local files while retaining it for
        // network sources. Forcing the cache on adds another producer/consumer hop to
        // every local file and is most noticeable on the first item opened.
        TrySetOption("cache", "auto");
        TrySetOption("cache-secs", "20");
        TrySetOption("demuxer-readahead-secs", "20");
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
        _firstFrameReady = false;
        SetState(PlaybackState.Loading);
        CurrentSource = source;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        VideoWidth = null;
        VideoHeight = null;
        _editorSubtitlePath = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (httpHeaders is null || httpHeaders.Count == 0) SetProperty("http-header-fields", string.Empty);
        else
        {
            foreach (var header in httpHeaders)
                if (header.Key.Any(c => c is '\r' or '\n' or ':' or ',') || header.Value.Any(c => c is '\r' or '\n' or ',')) throw new ArgumentException("Invalid HTTP header.", nameof(httpHeaders));
            SetProperty("http-header-fields", string.Join(',', httpHeaders.Select(header => $"{header.Key}: {header.Value}")));
        }
        _loadfileIssued = true;
        StartupProfiler.Mark("loadfile-command");
        MpvInterop.CommandAsync(_context, NextCommandId(), "loadfile", source, "replace");
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
        Command("show-text", text, durationMilliseconds.ToString(CultureInfo.InvariantCulture));
    }
    public void SetMute(bool muted) { IsMuted = muted; SetProperty("mute", muted ? "yes" : "no"); }
    public void SetSubtitleVisibility(bool visible)
    {
        SetProperty("sub-visibility", visible ? "yes" : "no");
        AreSubtitlesVisible = visible;
    }
    public void SetRate(double rate) { Rate = Math.Clamp(rate, 0.25, 4); SetProperty("speed", Rate.ToString("0.###", CultureInfo.InvariantCulture)); }
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
        if (select)
        {
            SetProperty("sid", "no");
            SetProperty("secondary-sid", "no");
        }
        Command("sub-add", path, select ? "select" : "auto");
        RestoreSubtitleVisibility();
    }

    public void UpdateEditorSubtitle(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_subtitleCommandSync)
        {
            EnsureAvailable();
            var wasPlaying = _state == PlaybackState.Playing;
            var trackIds = FindExternalSubtitleTrackIds(fullPath);
            if (trackIds.Count == 0)
            {
                SetProperty("sid", "no");
                SetProperty("secondary-sid", "no");
                Command("sub-add", fullPath, "select");
            }
            else
            {
                // A previous interrupted update can leave duplicate instances of the
                // temporary track behind. Keep one track, select it explicitly, and
                // reload that track so mpv cannot continue rendering an older selection.
                SetProperty("sid", "no");
                SetProperty("secondary-sid", "no");
                foreach (var duplicateId in trackIds.Skip(1).OrderByDescending(id => id))
                    Command("sub-remove", duplicateId.ToString(CultureInfo.InvariantCulture));

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
            _editorSubtitlePath = fullPath;
        }
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

    public void ConfigurePreferredLanguages(string? audioLanguage, string? subtitleLanguage)
    {
        TrySetProperty("alang", audioLanguage ?? string.Empty);
        TrySetProperty("slang", subtitleLanguage ?? string.Empty);
    }

    public void ConfigureSubtitleStyle(string fontFamily, double fontSize, string color, string background, double outline, int bottomMargin)
    {
        TrySetProperty("sub-font", fontFamily);
        TrySetProperty("sub-font-size", fontSize.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetProperty("sub-color", color);
        TrySetProperty("sub-back-color", background);
        TrySetProperty("sub-border-size", outline.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetProperty("sub-margin-y", Math.Max(0, bottomMargin).ToString(CultureInfo.InvariantCulture));
    }

    public void ConfigureRtxVideoSuperResolution(RtxVideoSuperResolutionMode mode)
    {
        EnsureAvailable();
        var wasEnabled = _rtxVideoSuperResolutionFilterApplied;
        var shouldEnable = mode != RtxVideoSuperResolutionMode.Off;
        if (wasEnabled == shouldEnable)
        {
            _rtxVideoSuperResolutionMode = mode;
            RtxVideoSuperResolutionStatus = shouldEnable
                ? "NVIDIA RTX Video Super Resolution filter configured; per-frame activation is driver controlled."
                : "Disabled";
            return;
        }

        try
        {
            if (wasEnabled)
            {
                MpvInterop.Command(_context, "vf", "remove", $"@{RtxVideoSuperResolutionFilter.Label}");
                _rtxVideoSuperResolutionFilterApplied = false;
            }

            if (shouldEnable)
            {
                var filter = RtxVideoSuperResolutionFilter.Build(mode) ?? throw new InvalidOperationException("Could not build the RTX VSR filter.");
                MpvInterop.Command(_context, "vf", "add", filter);
                _rtxVideoSuperResolutionFilterApplied = true;
            }

            _rtxVideoSuperResolutionMode = mode;
            RtxVideoSuperResolutionStatus = shouldEnable
                ? "NVIDIA RTX Video Super Resolution filter configured; per-frame activation is driver controlled."
                : "Disabled";
        }
        catch (MpvException exception)
        {
            _rtxVideoSuperResolutionFilterApplied = false;
            _rtxVideoSuperResolutionMode = mode;
            RtxVideoSuperResolutionStatus = $"Unavailable: {exception.Message}";
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
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
                {
                    SetState(PlaybackState.Ended);
                    MediaEnded?.Invoke(this, EventArgs.Empty);
                }
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
                if (_firstFrameReady) break;
                _firstFrameReady = true;
                var codec = GetString("video-codec");
                var decoder = GetString("hwdec-current") ?? "software";
                DecoderDescription = codec is null ? decoder : $"{codec} / {decoder}";
                StartupProfiler.CompleteAtFirstFrame(DecoderDescription);
                FirstFrameReady?.Invoke(this, EventArgs.Empty);
                ScheduleTrackRefresh();
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
            if (!cancellation.IsCancellationRequested && _context != 0) ReadTracks();
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
            DecoderDescription = GetString("video-codec") is { } codec ? $"{codec} / {GetString("hwdec-current") ?? "software"}" : null;
        }
        if (changed) PositionChanged?.Invoke(this, EventArgs.Empty);
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

        var filter = RtxVideoSuperResolutionFilter.Build(mode) ?? throw new InvalidOperationException("Could not build the RTX VSR filter.");
        try
        {
            SetOption("vf", filter);
            _rtxVideoSuperResolutionFilterApplied = true;
            RtxVideoSuperResolutionStatus = "NVIDIA RTX Video Super Resolution filter configured; per-frame activation is driver controlled.";
        }
        catch (MpvException exception)
        {
            RtxVideoSuperResolutionStatus = $"Unavailable: {exception.Message}";
        }
    }
}
