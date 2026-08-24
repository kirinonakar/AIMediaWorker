namespace AIMediaWorker.Playback;

public interface IPlaybackEngine : IAsyncDisposable
{
    PlaybackState State { get; }
    bool IsAvailable { get; }
    string? CurrentSource { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    double Volume { get; }
    double Rate { get; }
    bool IsMuted { get; }
    bool AreSubtitlesVisible { get; }
    IReadOnlyList<MediaTrack> Tracks { get; }
    string? DecoderDescription { get; }
    int? VideoWidth { get; }
    int? VideoHeight { get; }
    string? LibraryVersion { get; }

    event EventHandler? StateChanged;
    event EventHandler? PositionChanged;
    event EventHandler? TracksChanged;
    event EventHandler<PlaybackError>? ErrorOccurred;
    event EventHandler? MediaEnded;

    Task InitializeAsync(nint videoWindowHandle, HardwareDecoder hardwareDecoder, string renderer = "gpu-next", CancellationToken cancellationToken = default);
    Task OpenAsync(string source, IReadOnlyDictionary<string, string>? httpHeaders = null, CancellationToken cancellationToken = default);
    void Play();
    void Pause();
    void TogglePause();
    void Stop();
    void Seek(TimeSpan position, bool exact = false);
    void SeekRelative(TimeSpan offset);
    void SetVolume(double volume);
    void SetMute(bool muted);
    void SetSubtitleVisibility(bool visible);
    void SetRate(double rate);
    void FrameStep(bool backwards = false);
    void SaveScreenshot(string path, bool includeSubtitles = true);
    void SetAbLoop(TimeSpan? start, TimeSpan? end);
    void SelectTrack(MediaTrackType type, int? id);
    void LoadSubtitle(string path, bool select = true);
    void UpdateEditorSubtitle(string path);
    void ConfigureNetwork(TimeSpan timeout, string? proxy);
    void ConfigurePreferredLanguages(string? audioLanguage, string? subtitleLanguage);
    void ConfigureSubtitleStyle(string fontFamily, double fontSize, string color, string background, double outline, int bottomMargin);
}
