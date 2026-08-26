using AIMediaWorker.Playback;
using AIMediaWorker.Settings;
using AIMediaWorker.Subtitle.Writing;
using Microsoft.UI.Dispatching;

namespace AIMediaWorker.Subtitle;

/// <summary>
/// Synchronizes the editable subtitle document with mpv's native subtitle track and
/// manages the lightweight OSD path used while subtitles are generated in real time.
/// </summary>
internal sealed class SubtitleOverlayController : IDisposable
{
    private readonly MpvPlaybackEngine _playback;
    private readonly Func<SubtitleDocument> _document;
    private readonly Func<AppSettings> _settings;
    private readonly Func<SubtitleDisplayMode?> _displayMode;
    private readonly Func<int?> _selectedNativeTrackId;
    private readonly Func<long> _playbackPositionMicroseconds;
    private readonly Action<bool> _visibilityChanged;
    private readonly Action<string> _setStatus;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SemaphoreSlim _overlayWriteLock = new(1, 1);
    private readonly string _editorOverlayPath = Path.Combine(
        Path.GetTempPath(), $"AIMediaWorker-{Environment.ProcessId}-{Guid.NewGuid():N}.ass");
    private CancellationTokenSource? _syncCancellation;
    private string? _renderedContent;
    private string? _renderedFontFamily;
    private SubtitleDisplayMode? _renderedDisplayMode;
    private bool _generatedOsdConfigured;
    private Guid? _generatedOsdCueId;
    private string? _generatedOsdText;
    private bool _disposed;

    public SubtitleOverlayController(
        MpvPlaybackEngine playback,
        Func<SubtitleDocument> document,
        Func<AppSettings> settings,
        Func<SubtitleDisplayMode?> displayMode,
        Func<int?> selectedNativeTrackId,
        Func<long> playbackPositionMicroseconds,
        Action<bool> visibilityChanged,
        Action<string> setStatus,
        DispatcherQueue dispatcherQueue)
    {
        _playback = playback;
        _document = document;
        _settings = settings;
        _displayMode = displayMode;
        _selectedNativeTrackId = selectedNativeTrackId;
        _playbackPositionMicroseconds = playbackPositionMicroseconds;
        _visibilityChanged = visibilityChanged;
        _setStatus = setStatus;
        _dispatcherQueue = dispatcherQueue;
    }

    public bool IsGeneratedOverlayActive { get; private set; }

    public void ResetForDocument()
    {
        ClearGeneratedOsd(force: true);
        IsGeneratedOverlayActive = false;
        CancelPendingSync();
        _renderedContent = null;
        _renderedFontFamily = null;
        _renderedDisplayMode = null;
    }

    public void InvalidateGeneratedCue()
    {
        _generatedOsdCueId = null;
        _generatedOsdText = null;
    }

    public void DisableGeneratedOverlay()
    {
        IsGeneratedOverlayActive = false;
        ClearGeneratedOsd(force: true);
    }

    public void ScheduleSync(bool force = false)
    {
        if (_disposed || _displayMode() is null || !_playback.IsAvailable ||
            _playback.CurrentSource is null || _document().ActiveTrack is null) return;
        if (IsGeneratedOverlayActive)
        {
            RefreshGeneratedOsd(_playbackPositionMicroseconds());
            return;
        }

        CancelPendingSync();
        _syncCancellation = new CancellationTokenSource();
        _ = SyncAsync(_syncCancellation.Token, force);
    }

    public void EnableGeneratedOverlay()
    {
        if (!_playback.IsAvailable) return;
        IsGeneratedOverlayActive = true;
        ClearGeneratedOsd(force: true);
        CancelPendingSync();
        TryPlayback(() =>
        {
            _playback.SetSubtitleVisibility(true);
            _playback.SelectTrack(MediaTrackType.Subtitle, null);
            _playback.ConfigureGeneratedSubtitleOsd(true);
            _generatedOsdConfigured = true;
        });
        var visible = _playback.AreSubtitlesVisible;
        _settings().Playback.ShowSubtitles = visible;
        _visibilityChanged(visible);
        RefreshGeneratedOsd(_playbackPositionMicroseconds());
    }

    public void ApplyVisibilityPreference()
    {
        var visible = _settings().Playback.ShowSubtitles;
        TryPlayback(() =>
        {
            _playback.SetSubtitleVisibility(visible);
            if (IsGeneratedOverlayActive)
            {
                _playback.SelectTrack(MediaTrackType.Subtitle, null);
                _playback.ConfigureGeneratedSubtitleOsd(visible);
                _generatedOsdConfigured = visible;
            }
            else if (visible)
            {
                _playback.RestoreSubtitleSelection(_selectedNativeTrackId(), _displayMode() is not null);
            }
        });
        _visibilityChanged(_playback.AreSubtitlesVisible);
        if (!IsGeneratedOverlayActive) return;
        if (visible) RefreshGeneratedOsd(_playbackPositionMicroseconds());
        else ClearGeneratedOsd();
    }

    public void RefreshGeneratedOsd(long positionMicroseconds)
    {
        if (!IsGeneratedOverlayActive || !_playback.IsAvailable) return;
        if (!_settings().Playback.ShowSubtitles || _displayMode() is not { } displayMode)
        {
            ClearGeneratedOsd();
            return;
        }

        var cue = _document().FindActiveCue(positionMicroseconds);
        var text = cue?.GetDisplayText(displayMode).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (cue is null || string.IsNullOrWhiteSpace(text))
        {
            ClearGeneratedOsd();
            return;
        }
        if (_generatedOsdCueId == cue.Id && string.Equals(_generatedOsdText, text, StringComparison.Ordinal)) return;

        var remainingSeconds = Math.Clamp((cue.EndMicroseconds - positionMicroseconds) / 1_000_000d + 0.5, 0.2, 60);
        _generatedOsdCueId = cue.Id;
        _generatedOsdText = text;
        _generatedOsdConfigured = true;
        TryPlayback(() => _playback.ShowSubtitleOsdText(text, remainingSeconds));
    }

    public void ClearGeneratedOsd(bool force = false)
    {
        var wasShowing = _generatedOsdCueId is not null || _generatedOsdText is not null;
        var shouldClear = force || wasShowing || _generatedOsdConfigured;
        _generatedOsdConfigured = false;
        _generatedOsdCueId = null;
        _generatedOsdText = null;
        if (shouldClear && _playback.IsAvailable) TryPlayback(_playback.ClearSubtitleOsdText);
    }

    public void CancelPendingSync()
    {
        var cancellation = _syncCancellation;
        _syncCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task SyncAsync(CancellationToken cancellationToken, bool force)
    {
        try
        {
            await Task.Delay(150, cancellationToken);
            var document = _document();
            var track = document.ActiveTrack;
            if (track is null) return;
            var fontFamily = _settings().Subtitle.FontFamily;
            var displayMode = _displayMode() ?? SubtitleDisplayMode.Original;
            var cues = track.Cues
                .Select(cue => new AssCueSnapshot(cue.Id, cue.StartMicroseconds, cue.EndMicroseconds,
                    cue.GetDisplayText(displayMode), cue.Style, cue.Speaker))
                .OrderBy(cue => cue.StartMicroseconds)
                .ToArray();
            var content = await Task.Run(
                () => AssWriter.Write(cues, track.NativeHeader, fontFamily), cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(_document(), document)) return;
            var contentChanged = !string.Equals(content, _renderedContent, StringComparison.Ordinal);
            var fontChanged = !string.Equals(fontFamily, _renderedFontFamily, StringComparison.OrdinalIgnoreCase);
            var displayModeChanged = displayMode != _renderedDisplayMode;

            if (!contentChanged && !fontChanged && (displayModeChanged || force) && _playback.RestoreEditorSubtitleAfterSeek())
            {
                _renderedDisplayMode = displayMode;
                return;
            }
            if (!force && !contentChanged && !fontChanged && !displayModeChanged) return;

            await _overlayWriteLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_document(), document)) return;
                await File.WriteAllTextAsync(
                    _editorOverlayPath, content, new System.Text.UTF8Encoding(false), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_document(), document)) return;
                _playback.UpdateEditorSubtitle(_editorOverlayPath);
                _renderedContent = content;
                _renderedFontFamily = fontFamily;
                _renderedDisplayMode = displayMode;
            }
            finally
            {
                _overlayWriteLock.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _dispatcherQueue.TryEnqueue(() => _setStatus($"Subtitle overlay update failed: {exception.Message}"));
        }
    }

    private void TryPlayback(Action action)
    {
        try { action(); }
        catch (Exception exception) { _dispatcherQueue.TryEnqueue(() => _setStatus(exception.Message)); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingSync();
        _overlayWriteLock.Dispose();
        try { File.Delete(_editorOverlayPath); }
        catch (IOException) { }
    }
}
