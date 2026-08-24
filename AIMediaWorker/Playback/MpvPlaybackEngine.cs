using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AIMediaWorker.Playback;

public sealed class MpvPlaybackEngine : IPlaybackEngine
{
    private static readonly Lazy<Task> LibraryPreload = new(
        () => Task.Run(PreloadLibraryCore),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _openLock = new(1, 1);
    private readonly List<MediaTrack> _tracks = [];
    private nint _context;
    private CancellationTokenSource? _eventLoopCancellation;
    private Task? _eventLoop;
    private Task? _initializationTask;
    private CancellationTokenSource? _trackRefreshCancellation;
    private long _nextCommandId;
    private PlaybackState _state = PlaybackState.Uninitialized;
    private string? _editorSubtitlePath;
    private bool _disposed;

    public PlaybackState State => _state;
    public bool IsAvailable => _context != 0 && !_disposed;
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

    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;
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
        try { _ = MpvInterop.mpv_client_api_version(); }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            // InitializeAsync reports the actionable playback error through the normal UI path.
        }
    }

    public async Task InitializeAsync(nint videoWindowHandle, HardwareDecoder hardwareDecoder, string renderer = "gpu-next", CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_context != 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await PreloadAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var initialization = Task.Run(() => InitializeCore(videoWindowHandle, hardwareDecoder, renderer, cancellationToken), cancellationToken);
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

    private void InitializeCore(nint videoWindowHandle, HardwareDecoder hardwareDecoder, string renderer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = MpvInterop.mpv_create();
        if (_context == 0) throw new InvalidOperationException("libmpv could not create a playback context.");
        SetOption("wid", unchecked((ulong)videoWindowHandle).ToString(CultureInfo.InvariantCulture));
        SetOption("terminal", "no");
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
        SetOption("sub-auto", "fuzzy");
        SetOption("sub-fonts-dir", ".");
        SetOption("audio-client-name", "AIMediaWorker");
        TrySetOption("audio-buffer", "0.2");
        TrySetOption("audio-pitch-correction", "yes");
        TrySetOption("cache", "yes");
        TrySetOption("cache-secs", "20");
        TrySetOption("demuxer-readahead-secs", "20");
        TrySetOption("cache-pause", "no");
        TrySetOption("stream-lavf-o", "reconnect=1,reconnect_at_eof=1,reconnect_streamed=1,reconnect_delay_max=5");
        MpvInterop.EnsureSuccess(MpvInterop.mpv_initialize(_context), "initialize libmpv");
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
            cancellationToken.ThrowIfCancellationRequested();
            var trackRefreshCancellation = Interlocked.Exchange(ref _trackRefreshCancellation, null);
            trackRefreshCancellation?.Cancel();
            trackRefreshCancellation?.Dispose();
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
            MpvInterop.CommandAsync(_context, unchecked((ulong)Interlocked.Increment(ref _nextCommandId)), "loadfile", source, "replace");
            SetProperty("pause", "no");
        }
        finally { _openLock.Release(); }
    }

    public void Play() { SetProperty("pause", "no"); SetState(PlaybackState.Playing); }
    public void Pause() { SetProperty("pause", "yes"); SetState(PlaybackState.Paused); }
    public void TogglePause() { if (State == PlaybackState.Playing) Pause(); else Play(); }
    public void Stop() { Command("stop"); SetState(PlaybackState.Idle); Position = TimeSpan.Zero; PositionChanged?.Invoke(this, EventArgs.Empty); }

    public void Seek(TimeSpan position, bool exact = false) => Command("seek", Math.Max(0, position.TotalSeconds).ToString("0.######", CultureInfo.InvariantCulture), "absolute" + (exact ? "+exact" : "+keyframes"));
    public void SeekRelative(TimeSpan offset) => Command("seek", offset.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture), "relative");
    public void SetVolume(double volume) { Volume = Math.Clamp(volume, 0, 130); SetProperty("volume", Volume.ToString("0.##", CultureInfo.InvariantCulture)); }
    public void SetMute(bool muted) { IsMuted = muted; SetProperty("mute", muted ? "yes" : "no"); }
    public void SetSubtitleVisibility(bool visible) { AreSubtitlesVisible = visible; SetProperty("sub-visibility", visible ? "yes" : "no"); }
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

    public void SelectTrack(MediaTrackType type, int? id) => SetProperty(type switch
    {
        MediaTrackType.Video => "vid",
        MediaTrackType.Audio => "aid",
        MediaTrackType.Subtitle => "sid",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    }, id?.ToString(CultureInfo.InvariantCulture) ?? "no");

    public void LoadSubtitle(string path, bool select = true) => Command("sub-add", path, select ? "select" : "auto");

    public void UpdateEditorSubtitle(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (_editorSubtitlePath is not null && string.Equals(_editorSubtitlePath, fullPath, StringComparison.OrdinalIgnoreCase)) Command("sub-reload");
        else { LoadSubtitle(fullPath, true); _editorSubtitlePath = fullPath; }
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
                    PollProperties();
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
            case MpvInterop.MpvEventId.FileLoaded:
                SetState(GetBool("pause") ? PlaybackState.Paused : PlaybackState.Playing);
                ScheduleTrackRefresh();
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
            case MpvInterop.MpvEventId.VideoReconfig:
            case MpvInterop.MpvEventId.AudioReconfig:
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

    private void PollProperties()
    {
        if (_context == 0) return;
        var position = GetDouble("time-pos");
        var duration = GetDouble("duration");
        var changed = false;
        if (position is not null)
        {
            Position = TimeSpan.FromSeconds(Math.Max(0, position.Value));
            changed = true;
        }
        if (duration is not null) Duration = TimeSpan.FromSeconds(Math.Max(0, duration.Value));
        VideoWidth = GetInt("dwidth") ?? GetInt("width");
        VideoHeight = GetInt("dheight") ?? GetInt("height");
        DecoderDescription = GetString("video-codec") is { } codec ? $"{codec} / {GetString("hwdec-current") ?? "software"}" : null;
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
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetOption(string name, string value) => MpvInterop.EnsureSuccess(MpvInterop.mpv_set_option_string(_context, name, value), $"set option {name}");
    private bool TrySetOption(string name, string value) { try { SetOption(name, value); return true; } catch (MpvException) { return false; } }
    private void SetProperty(string name, string value) { EnsureAvailable(); MpvInterop.EnsureSuccess(MpvInterop.mpv_set_property_string(_context, name, value), $"set property {name}"); }
    private bool TrySetProperty(string name, string value) { try { SetProperty(name, value); return true; } catch (MpvException) { return false; } }
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
}
