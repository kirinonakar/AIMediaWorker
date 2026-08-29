using AIMediaWorker.Playback;
using AIMediaWorker.Localization;
using AIMediaWorker.Subtitle;
using AIMediaWorker.Subtitle.Editing;
using Microsoft.UI.Xaml.Controls;

namespace AIMediaWorker.Controllers;

/// <summary>Owns the editor/native subtitle track selection and its display-mode projection.</summary>
internal sealed class SubtitleTrackController : IDisposable
{
    private readonly MpvPlaybackEngine _playback;
    private readonly SubtitleSessionController _session;
    private readonly SubtitleOverlayController _overlay;
    private readonly SubtitleEditorController _editor;
    private readonly ComboBox _selector;
    private readonly Action<bool> _scheduleOverlaySync;
    private readonly Action<Action> _tryPlayback;
    private bool _updatingSelector;
    private bool _disposed;

    public SubtitleTrackController(
        MpvPlaybackEngine playback,
        SubtitleSessionController session,
        SubtitleOverlayController overlay,
        SubtitleEditorController editor,
        ComboBox selector,
        Action<bool> scheduleOverlaySync,
        Action<Action> tryPlayback)
    {
        _playback = playback;
        _session = session;
        _overlay = overlay;
        _editor = editor;
        _selector = selector;
        _scheduleOverlaySync = scheduleOverlaySync;
        _tryPlayback = tryPlayback;
        _selector.SelectionChanged += OnSelectionChanged;
    }

    public SubtitleDisplayMode? DisplayMode { get; private set; }
    public int? SelectedNativeTrackId { get; private set; }

    public void BindDocument(SubtitleDocument document)
    {
        _overlay.ResetForDocument();
        DisplayMode = null;
        SelectedNativeTrackId = null;
        var track = document.EnsureTrack();
        if (track.Cues.Count > 0) DisplayMode = SubtitleDisplayMode.Original;
        _editor.BindDocument(document);
        Refresh();
    }

    public void SetDisplayMode(SubtitleDisplayMode displayMode, bool refreshOverlay)
    {
        DisplayMode = displayMode;
        SelectedNativeTrackId = null;
        Refresh();
        if (refreshOverlay && _session.Document.ActiveTrack is { Cues.Count: > 0 })
            _scheduleOverlaySync(true);
    }

    public void Refresh()
    {
        var options = new List<SubtitleSelectionOption>();
        if (_session.Document.ActiveTrack is { Cues.Count: > 0 } || DisplayMode is not null)
        {
            options.Add(new SubtitleSelectionOption(LocalizationService.Get("SubtitleOptionOriginal"), SubtitleDisplayMode.Original, null));
            options.Add(new SubtitleSelectionOption(LocalizationService.Get("SubtitleOptionTranslation"), SubtitleDisplayMode.Translation, null));
            options.Add(new SubtitleSelectionOption(LocalizationService.Get("SubtitleOptionBoth"), SubtitleDisplayMode.OriginalAndTranslation, null));
        }
        options.AddRange(_playback.Tracks
            .Where(track => track.Type == MediaTrackType.Subtitle)
            .Select(track => new SubtitleSelectionOption(track.DisplayName, null, track.Id)));

        _updatingSelector = true;
        try
        {
            _selector.ItemsSource = options;
            _selector.SelectedItem = DisplayMode is { } displayMode
                ? options.FirstOrDefault(option => option.DisplayMode == displayMode)
                : SelectedNativeTrackId is { } trackId
                    ? options.FirstOrDefault(option => option.TrackId == trackId)
                    : options.FirstOrDefault(option => option.TrackId is not null &&
                        _playback.Tracks.Any(track => track.Id == option.TrackId && track.IsSelected));
        }
        finally { _updatingSelector = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selector.SelectionChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelector || _selector.SelectedItem is not SubtitleSelectionOption option) return;
        if (option.DisplayMode is { } displayMode)
        {
            SetDisplayMode(displayMode, refreshOverlay: true);
            return;
        }

        if (option.TrackId is not { } trackId) return;
        if (_overlay.IsGeneratedOverlayActive) _overlay.DisableGeneratedOverlay();
        DisplayMode = null;
        SelectedNativeTrackId = trackId;
        _tryPlayback(() => _playback.SelectTrack(MediaTrackType.Subtitle, trackId));
    }

    private sealed record SubtitleSelectionOption(string DisplayName, SubtitleDisplayMode? DisplayMode, int? TrackId);
}
