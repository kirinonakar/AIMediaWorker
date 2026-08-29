using AIMediaWorker.Asr;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using AIMediaWorker.Media;
using AIMediaWorker.Playback;
using AIMediaWorker.Subtitle;
using AIMediaWorker.Views;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AIMediaWorker.Controllers;

/// <summary>
/// Owns the lifetime of the active media source, including queued launches, open preparation,
/// first-frame readiness, and the reset of per-media collaborators.
/// </summary>
internal sealed class MediaSessionController
{
    private readonly MpvPlaybackEngine _playback;
    private readonly AudioPresentationController _audioPresentation;
    private readonly SubtitleSessionController _subtitleSession;
    private readonly MediaSessionHost _host;
    private bool _mediaOpenReady;
    private bool _firstFrameReadyForMedia;
    private string? _pendingMediaOpenSource;
    private string? _firstFrameWaitSource;
    private TaskCompletionSource? _firstFrameWaiter;
    private string? _pendingLaunchSource;
    private string[]? _pendingDroppedFiles;

    public MediaSessionController(
        MpvPlaybackEngine playback,
        AudioPresentationController audioPresentation,
        SubtitleSessionController subtitleSession,
        string? initialSource,
        MediaSessionHost host)
    {
        _playback = playback;
        _audioPresentation = audioPresentation;
        _subtitleSession = subtitleSession;
        _pendingLaunchSource = initialSource;
        _host = host;
    }

    public IMediaSource? CurrentSource { get; private set; }
    public IReadOnlyDictionary<string, string>? CurrentHttpHeaders { get; private set; }

    public async Task PickAndOpenMediaAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _host.WindowHandle);
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0) await OpenDroppedFilesAsync(files.Select(file => file.Path));
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "file-picker", "OPEN_MEDIA_PICKER_ERROR", exception.Message, exception);
            await _host.ShowMessageAsync(L("PlaybackErrorTitle"), exception.Message);
        }
    }

    public async Task OpenDroppedFilesAsync(IEnumerable<string> paths)
    {
        var pathSnapshot = paths.ToArray();
        var files = await Task.Run(() => pathSnapshot
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
        if (files.Length == 0) return;
        if (!_playback.IsAvailable)
        {
            _pendingDroppedFiles = files;
            _host.SetStatus(L("StatusPreparingDroppedMedia"));
            return;
        }
        await _host.OpenFilesAsync(files);
    }

    public async Task OpenForwardedFilesAsync(IReadOnlyList<string> filePaths)
    {
        _pendingLaunchSource = null;
        try
        {
            var files = await Task.Run(() => filePaths
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

            // A shell file-association launch represents a new primary media selection.
            // Open it without preserving the temporary one-item playlist so the normal
            // post-open flow can repopulate the playlist with playable sibling files.
            if (files.Length == 1 && MediaFileClassifier.IsPlayable(files[0]))
            {
                _pendingDroppedFiles = null;
                await OpenAsync(files[0]);
                return;
            }

            await OpenDroppedFilesAsync(files);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "activation", "REDIRECTED_OPEN_ERROR", exception.Message, exception);
            await _host.ShowMessageAsync(L("PlaybackErrorTitle"), exception.Message);
        }
    }

    public async Task OpenPendingAsync()
    {
        if (_playback.IsAvailable && _pendingDroppedFiles is { Length: > 0 } droppedFiles)
        {
            _pendingDroppedFiles = null;
            _pendingLaunchSource = null;
            await _host.OpenFilesAsync(droppedFiles);
        }
        if (_pendingLaunchSource is { Length: > 0 }) await OpenInitialSourceAsync();
    }

    public async Task OpenAsync(
        string source,
        IReadOnlyDictionary<string, string>? httpHeaders = null,
        IMediaSource? mediaSource = null,
        bool preservePlaylist = false)
    {
        // Signal AI cancellation immediately, while the dirty-document decision is shown.
        var aiPipelineCancellation = _host.CancelAiAsync();
        if (!await _subtitleSession.ConfirmDiscardChangesAsync(L("ActionOpenMedia")))
        {
            await aiPipelineCancellation;
            return;
        }

        try
        {
            await _host.FirstUiFrameReady;
            await _host.PlaybackInitialization;
            if (!_playback.IsAvailable) throw new InvalidOperationException(L("StatusPlaybackUnavailable"));
            await _host.PrepareForMediaOpenAsync();
            BeginMediaOpen(source);
            await _playback.OpenAsync(source, httpHeaders);
            await aiPipelineCancellation;
            CompleteMediaOpen(source, httpHeaders, mediaSource ?? MediaSourceFactory.Parse(source), preservePlaylist, showInExplorer: false);
        }
        catch (Exception exception)
        {
            if (string.Equals(_pendingMediaOpenSource, source, StringComparison.OrdinalIgnoreCase))
            {
                _mediaOpenReady = false;
                _pendingMediaOpenSource = null;
                _audioPresentation.Reset();
            }
            await aiPipelineCancellation;
            await AppLog.WriteAsync("error", "playback", "OPEN_MEDIA_ERROR", exception.Message, exception);
            await _host.ShowMessageAsync(L("PlaybackErrorTitle"), exception.Message);
        }
    }

    public void FirstFrameReady()
    {
        if (string.Equals(_firstFrameWaitSource, _playback.CurrentSource, StringComparison.OrdinalIgnoreCase))
        {
            _firstFrameReadyForMedia = true;
            _firstFrameWaiter?.TrySetResult();
        }
        _host.NotifyFirstFrameReady();
        StartAutomaticSubtitleGenerationIfReady();
    }

    public async Task<bool> WaitForFirstFrameAsync(string source)
    {
        if (!_mediaOpenReady || !string.Equals(_playback.CurrentSource, source, StringComparison.OrdinalIgnoreCase)) return false;
        if (!_firstFrameReadyForMedia)
        {
            var waiter = _firstFrameWaiter;
            if (waiter is null || !string.Equals(_firstFrameWaitSource, source, StringComparison.OrdinalIgnoreCase)) return false;
            try { await waiter.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (TimeoutException) { return false; }
            catch (OperationCanceledException) { return false; }
        }
        return _mediaOpenReady && _firstFrameReadyForMedia && _playback.IsFirstFrameReady &&
               string.Equals(_playback.CurrentSource, source, StringComparison.OrdinalIgnoreCase);
    }

    private async Task OpenInitialSourceAsync()
    {
        try
        {
            await _host.FirstUiFrameReady.ConfigureAwait(false);
            await _host.PlaybackInitialization.ConfigureAwait(false);
            if (!_playback.IsAvailable || _pendingDroppedFiles is { Length: > 0 } ||
                _pendingLaunchSource is not { Length: > 0 } launchSource) return;
            _pendingLaunchSource = null;
            BeginMediaOpen(launchSource);
            await _playback.OpenAsync(launchSource).ConfigureAwait(false);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_host.DispatchToUi(() =>
            {
                try
                {
                    CompleteMediaOpen(launchSource, null, null, preservePlaylist: false, showInExplorer: true);
                    completion.SetResult();
                }
                catch (Exception exception) { completion.SetException(exception); }
            })) throw new InvalidOperationException("Could not complete the initial media open on the UI thread.");
            await completion.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "startup", "INITIAL_MEDIA_OPEN_ERROR", exception.Message, exception);
        }
    }

    private void BeginMediaOpen(string source)
    {
        _audioPresentation.BeginMediaOpen(source);
        _mediaOpenReady = false;
        _firstFrameReadyForMedia = false;
        _pendingMediaOpenSource = source;
        _firstFrameWaiter?.TrySetCanceled();
        _firstFrameWaitSource = source;
        _firstFrameWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void CompleteMediaOpen(
        string source,
        IReadOnlyDictionary<string, string>? httpHeaders,
        IMediaSource? mediaSource,
        bool preservePlaylist,
        bool showInExplorer)
    {
        CurrentSource = mediaSource ?? MediaSourceFactory.Parse(source);
        CurrentHttpHeaders = httpHeaders is null
            ? null
            : new Dictionary<string, string>(httpHeaders, StringComparer.OrdinalIgnoreCase);
        _audioPresentation.CompleteMediaOpen(CurrentSource);
        _host.ApplyRepeatMode();
        _host.UpdateWindowTitle(CurrentSource.DisplayName);
        _host.MediaOpened(CurrentSource, preservePlaylist, showInExplorer);
        if (_playback.IsFirstFrameReady) _host.NotifyFirstFrameReady();
        _host.ResetTimeline();
        _subtitleSession.ResetForMedia();
        _host.ResetAiForMedia();
        _host.SetStatus(source);
        _host.FocusPlaybackSurface();
        _mediaOpenReady = true;
        _pendingMediaOpenSource = null;
        if (_firstFrameReadyForMedia) StartAutomaticSubtitleGenerationIfReady();
    }

    private void StartAutomaticSubtitleGenerationIfReady()
    {
        if (!_mediaOpenReady || !_firstFrameReadyForMedia ||
            !string.Equals(CurrentSource?.Location, _playback.CurrentSource, StringComparison.OrdinalIgnoreCase)) return;
        if (_playback.State == PlaybackState.Playing && !_host.IsAiSeekRestartPending())
            _host.StartAiPipeline();
    }

    private static string L(string key) => LocalizationService.Get(key);
}

internal sealed record MediaSessionHost(
    nint WindowHandle,
    Task FirstUiFrameReady,
    Task PlaybackInitialization,
    Func<Action, bool> DispatchToUi,
    Func<Task> CancelAiAsync,
    Func<Task> PrepareForMediaOpenAsync,
    Func<IReadOnlyList<string>, Task> OpenFilesAsync,
    Action<IMediaSource, bool, bool> MediaOpened,
    Action NotifyFirstFrameReady,
    Action ApplyRepeatMode,
    Action ResetTimeline,
    Action ResetAiForMedia,
    Action<string> UpdateWindowTitle,
    Action FocusPlaybackSurface,
    Func<bool> IsAiSeekRestartPending,
    Action StartAiPipeline,
    Action<string> SetStatus,
    Func<string, string, Task> ShowMessageAsync);
