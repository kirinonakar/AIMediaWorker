using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AIMediaWorker.Playback;

public sealed class MpvPlaybackEngine : IPlaybackEngine
{
    private readonly object _sync = new();
    private readonly List<MediaTrack> _tracks = [];
    private nint _context;
    private CancellationTokenSource? _eventLoopCancellation;
    private Task? _eventLoop;
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
    public IReadOnlyList<MediaTrack> Tracks { get { lock (_sync) return new ReadOnlyCollection<MediaTrack>(_tracks.ToArray()); } }
    public string? DecoderDescription { get; private set; }
    public string? LibraryVersion { get; private set; }

    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;
    public event EventHandler? TracksChanged;
    public event EventHandler<PlaybackError>? ErrorOccurred;
    public event EventHandler? MediaEnded;

    public Task InitializeAsync(nint videoWindowHandle, HardwareDecoder hardwareDecoder, string renderer = "gpu-next", CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_context != 0) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _context = MpvInterop.mpv_create();
            if (_context == 0) throw new InvalidOperationException("libmpv could not create a playback context.");
            SetOption("wid", unchecked((ulong)videoWindowHandle).ToString(CultureInfo.InvariantCulture));
            SetOption("terminal", "no");
            SetOption("input-default-bindings", "no");
            SetOption("keep-open", "yes");
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
            TrySetOption("cache", "yes");
            TrySetOption("cache-secs", "20");
            TrySetOption("stream-lavf-o", "reconnect=1,reconnect_at_eof=1,reconnect_streamed=1,reconnect_delay_max=5");
            MpvInterop.EnsureSuccess(MpvInterop.mpv_initialize(_context), "initialize libmpv");
            LibraryVersion = GetString("mpv-version");
            _eventLoopCancellation = new CancellationTokenSource();
            _eventLoop = Task.Run(() => EventLoop(_eventLoopCancellation.Token), CancellationToken.None);
            SetState(PlaybackState.Idle);
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
        return Task.CompletedTask;
    }

    public Task OpenAsync(string source, IReadOnlyDictionary<string, string>? httpHeaders = null, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("A media source is required.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        SetState(PlaybackState.Loading);
        CurrentSource = source;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        _editorSubtitlePath = null;
        if (httpHeaders is null || httpHeaders.Count == 0) SetProperty("http-header-fields", string.Empty);
        else
        {
            foreach (var header in httpHeaders)
                if (header.Key.Any(c => c is '\r' or '\n' or ':' or ',') || header.Value.Any(c => c is '\r' or '\n' or ',')) throw new ArgumentException("Invalid HTTP header.", nameof(httpHeaders));
            SetProperty("http-header-fields", string.Join(',', httpHeaders.Select(header => $"{header.Key}: {header.Value}")));
        }
        Command("loadfile", source, "replace");
        return Task.CompletedTask;
    }

    public void Play() { SetProperty("pause", "no"); SetState(PlaybackState.Playing); }
    public void Pause() { SetProperty("pause", "yes"); SetState(PlaybackState.Paused); }
    public void TogglePause() { if (State == PlaybackState.Playing) Pause(); else Play(); }
    public void Stop() { Command("stop"); SetState(PlaybackState.Idle); Position = TimeSpan.Zero; PositionChanged?.Invoke(this, EventArgs.Empty); }

    public void Seek(TimeSpan position, bool exact = false) => Command("seek", Math.Max(0, position.TotalSeconds).ToString("0.######", CultureInfo.InvariantCulture), "absolute" + (exact ? "+exact" : "+keyframes"));
    public void SeekRelative(TimeSpan offset) => Command("seek", offset.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture), "relative");
    public void SetVolume(double volume) { Volume = Math.Clamp(volume, 0, 130); SetProperty("volume", Volume.ToString("0.##", CultureInfo.InvariantCulture)); }
    public void SetMute(bool muted) { IsMuted = muted; SetProperty("mute", muted ? "yes" : "no"); }
    public void SetRate(double rate) { Rate = Math.Clamp(rate, 0.25, 4); SetProperty("speed", Rate.ToString("0.###", CultureInfo.InvariantCulture)); }
    public void FrameStep(bool backwards = false) => Command(backwards ? "frame-back-step" : "frame-step");

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
        var cancellation = _eventLoopCancellation;
        cancellation?.Cancel();
        if (_eventLoop is not null)
        {
            try { await _eventLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
        CleanupContext();
        SetState(PlaybackState.Disposed);
        GC.SuppressFinalize(this);
    }

    private void EventLoop(CancellationToken cancellationToken)
    {
        var nextPoll = DateTime.UtcNow;
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
                if (DateTime.UtcNow >= nextPoll)
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
            case MpvInterop.MpvEventId.FileLoaded:
                ReadTracks();
                SetState(GetBool("pause") ? PlaybackState.Paused : PlaybackState.Playing);
                break;
            case MpvInterop.MpvEventId.EndFile:
                if (mpvEvent.Data != 0)
                {
                    var end = Marshal.PtrToStructure<MpvInterop.MpvEventEndFile>(mpvEvent.Data);
                    if (end.Error < 0)
                    {
                        var network = CurrentSource is not null && Uri.TryCreate(CurrentSource, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
                        RaiseError(network ? "NETWORK_ERROR" : "PLAYBACK_ERROR", MpvInterop.ErrorString(end.Error));
                    }
                }
                SetState(PlaybackState.Ended);
                MediaEnded?.Invoke(this, EventArgs.Empty);
                break;
            case MpvInterop.MpvEventId.VideoReconfig:
            case MpvInterop.MpvEventId.AudioReconfig:
                ReadTracks();
                break;
            case MpvInterop.MpvEventId.Shutdown:
                return;
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
