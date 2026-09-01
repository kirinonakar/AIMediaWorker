using AIMediaWorker.Diagnostics;
using AIMediaWorker.Llm;
using AIMediaWorker.Llm.Providers;
using AIMediaWorker.Media;
using AIMediaWorker.Network;
using AIMediaWorker.Playback;
using AIMediaWorker.Settings;
using AIMediaWorker.Subtitle;
using AIMediaWorker.Subtitle.Editing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using System.Threading.Channels;

namespace AIMediaWorker.Asr;

internal interface IAiWorkflowHost
{
    AppSettings Settings { get; }
    SubtitleDocument Document { get; }
    SubtitleDisplayMode? CurrentSubtitleDisplayMode { get; }
    IReadOnlyDictionary<string, string>? CurrentHttpHeaders { get; }
    long CurrentPlaybackPositionMicroseconds { get; }
    double ViewWidth { get; }
    double ViewHeight { get; }
    DispatcherQueue DispatcherQueue { get; }
    void BindDocument(SubtitleDocument document);
    void SetSubtitleDisplayMode(SubtitleDisplayMode displayMode, bool refreshOverlay);
    void ShowSubtitlePanel();
    void DrawTimeline();
    void ScheduleSubtitleOverlaySync(bool force = false);
    void ScheduleGeneratedSubtitleUiRefresh();
    void EnableGeneratedSubtitleOverlay();
    void ExecuteSubtitleCommand(IUndoableSubtitleCommand command);
    void SetStatus(string message);
    void SetDownloadProgress(bool visible, bool indeterminate, double value = 0);
    void SetRetryAvailable(bool available);
    Task<ContentDialogResult> ShowDialogAsync(string title, object content, string primaryText);
    Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog);
    Task ShowMessageAsync(string title, string message);
}

/// <summary>
/// Coordinates ASR, real-time translation, transcript summaries, cancellation, and retries.
/// UI rendering is expressed through <see cref="IAiWorkflowHost"/> instead of window controls.
/// </summary>
internal sealed class AiWorkflowController : IAsyncDisposable
{
    private static readonly TimeSpan AutomaticStartDelay = TimeSpan.FromSeconds(2);
    private readonly IAiWorkflowHost _host;
    private readonly MpvPlaybackEngine _playback;
    private readonly AsrWorkerClient _asrEngine = new();
    private readonly AiProgressTracker _combinedProgress = new();
    private readonly RemoteMediaDownloadService _remoteMediaDownloader = new();
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _seekRestartCancellation;
    private Task? _pipelineTask;
    private Task? _summaryTask;
    private SummaryKind _activeSummaryKind = SummaryKind.Short;
    private AiRetryRequest? _retryableOperation;
    private bool _subtitleGenerationCompleted;
    private bool _translationCompleted;

    public AiWorkflowController(IAiWorkflowHost host, MpvPlaybackEngine playback, bool generateEnabled, bool translateEnabled)
    {
        _host = host;
        _playback = playback;
        GenerateEnabled = generateEnabled;
        TranslateEnabled = translateEnabled;
        _combinedProgress.ProgressChanged += OnCombinedProgressChanged;
    }

    public AsrWorkerState AsrState => _asrEngine.State;
    public bool GenerateEnabled { get; private set; }
    public bool TranslateEnabled { get; private set; }
    public bool IsSeekRestartPending => _seekRestartCancellation is not null;

    public void UpdateModes(bool generateEnabled, bool translateEnabled)
    {
        GenerateEnabled = generateEnabled;
        TranslateEnabled = translateEnabled;
    }

    public void ResetForMedia()
    {
        _subtitleGenerationCompleted = false;
        _translationCompleted = false;
    }

    public void ResetTranslation() => _translationCompleted = false;

    public void RequestSubtitleGeneration(bool enabled)
    {
        GenerateEnabled = enabled;
        _host.Settings.Asr.GenerateSubtitles = enabled;
        if (!enabled) return;
        ResetForMedia();
        StartPipeline();
    }

    public void RequestTranslation(bool enabled)
    {
        TranslateEnabled = enabled;
        _host.Settings.Llm.TranslateSubtitles = enabled;
        if (!enabled) return;
        _host.SetSubtitleDisplayMode(SubtitleDisplayMode.Translation, refreshOverlay: false);
        _translationCompleted = false;
        StartPipeline();
    }

    public void StartPipeline(long? requestedStartMicroseconds = null, bool waitForMediaReady = false, bool continueExistingResults = false)
    {
        if (_pipelineTask is { IsCompleted: false } || _operationCancellation is not null) return;
        SetRetryableOperation(null);
        _pipelineTask = RunPipelineAsync(requestedStartMicroseconds, waitForMediaReady, continueExistingResults);
    }

    public async Task CancelAsync()
    {
        CancelPendingSeekRestart();
        var operations = new[] { _pipelineTask, _summaryTask };
        _operationCancellation?.Cancel();
        foreach (var operation in operations)
        {
            if (operation is null || operation.IsCompleted) continue;
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                await AppLog.WriteAsync("warning", "ai", "AI_CANCEL_WAIT_ERROR", exception.Message, exception);
            }
        }
    }

    public void ScheduleRestartAfterSeek(TimeSpan requestedPosition)
    {
        if (!GenerateEnabled && !TranslateEnabled) return;
        CancelPendingSeekRestart();
        var cancellation = new CancellationTokenSource();
        _seekRestartCancellation = cancellation;
        var maximum = _playback.Duration > TimeSpan.Zero ? _playback.Duration : TimeSpan.MaxValue;
        var position = requestedPosition < TimeSpan.Zero ? TimeSpan.Zero : requestedPosition > maximum ? maximum : requestedPosition;
        _ = RestartAfterSeekAsync(cancellation, Math.Max(0, position.Ticks / 10));
    }

    public void CancelPendingSeekRestart()
    {
        var cancellation = _seekRestartCancellation;
        _seekRestartCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    public void CancelWithRetry()
    {
        CancelPendingSeekRestart();
        if (_operationCancellation is null) return;
        var kind = _summaryTask is { IsCompleted: false } ? AiRetryOperationKind.Summary : AiRetryOperationKind.SubtitlePipeline;
        SetRetryableOperation(new AiRetryRequest(kind, _activeSummaryKind, _host.Document, _playback.CurrentSource));
        _operationCancellation.Cancel();
    }

    public async Task RetryAsync()
    {
        var retry = _retryableOperation;
        if (retry is null) return;
        SetRetryableOperation(null);
        if (!IsRetryStillValid(retry)) return;
        try
        {
            await CancelAsync();
            if (retry.Kind == AiRetryOperationKind.Summary)
            {
                var track = _host.Document.ActiveTrack;
                if (track is null || track.Cues.Count == 0 || string.IsNullOrWhiteSpace(_host.Settings.Llm.Model)) return;
                await RunSummaryWithTrackingAsync(track, retry.SummaryKind);
                return;
            }
            if (GenerateEnabled || TranslateEnabled) StartPipeline(continueExistingResults: true);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("warning", "ai", "AI_RETRY_ERROR", exception.Message, exception);
        }
    }

    public async Task SummarizeAsync()
    {
        var track = _host.Document.ActiveTrack;
        if (track is null || track.Cues.Count == 0)
        {
            await _host.ShowMessageAsync(L("SummaryTitle"), L("LoadSubtitlesFirst"));
            return;
        }
        if (string.IsNullOrWhiteSpace(_host.Settings.Llm.Model))
        {
            await _host.ShowMessageAsync(L("LlmModelMissingTitle"), L("LlmModelMissingMessage"));
            return;
        }
        if (_operationCancellation is not null) return;
        var choices = new ComboBox
        {
            Header = L("SummaryStyleHeader"), MinWidth = 300,
            ItemsSource = Enum.GetValues<SummaryKind>(), SelectedIndex = 0
        };
        if (await _host.ShowDialogAsync(L("SummarizeTranscriptTitle"), choices, L("SummarizeButton")) != ContentDialogResult.Primary) return;
        await RunSummaryWithTrackingAsync(track, (SummaryKind)(choices.SelectedItem ?? SummaryKind.Short));
    }

    private async Task RestartAfterSeekAsync(CancellationTokenSource cancellation, long requestedStartMicroseconds)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(AutomaticStartDelay, token);
            await CancelPipelineForSeekAsync(token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_seekRestartCancellation, cancellation)) return;
            if (GenerateEnabled) _subtitleGenerationCompleted = false;
            if (TranslateEnabled) _translationCompleted = false;
            StartPipeline(requestedStartMicroseconds);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_seekRestartCancellation, cancellation)) _seekRestartCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task CancelPipelineForSeekAsync(CancellationToken seekCancellationToken)
    {
        var operation = _pipelineTask;
        _operationCancellation?.Cancel();
        if (operation is null || operation.IsCompleted) return;
        try { await operation.WaitAsync(seekCancellationToken); }
        catch (OperationCanceledException) when (!seekCancellationToken.IsCancellationRequested) { }
    }

    private async Task RunPipelineAsync(long? requestedStartMicroseconds, bool waitForMediaReady, bool continueExistingResults)
    {
        if (_operationCancellation is not null) return;
        var generate = GenerateEnabled && !_subtitleGenerationCompleted;
        var translate = TranslateEnabled && !_translationCompleted;
        if (!generate && !translate) return;
        if (_playback.CurrentSource is not { } source ||
            !File.Exists(source) && !(Uri.TryCreate(source, UriKind.Absolute, out var remoteUri) && remoteUri.Scheme is "http" or "https"))
        {
            _host.SetStatus(L("AutomaticSubtitlesOpenMedia"));
            return;
        }
        if (generate && !File.Exists(AsrRuntimePaths.CrispAsrDllPath))
        {
            _host.SetStatus(L("AsrInstallRequiredMessage"));
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        var combineProgress = generate && translate;
        if (combineProgress) _combinedProgress.Begin();
        string? temporaryInput = null;
        var translating = false;
        try
        {
            var token = _operationCancellation.Token;
            if (waitForMediaReady)
            {
                await Task.Delay(AutomaticStartDelay, token);
                token.ThrowIfCancellationRequested();
                if (!string.Equals(_playback.CurrentSource, source, StringComparison.OrdinalIgnoreCase) ||
                    _playback.State is not (PlaybackState.Playing or PlaybackState.Paused)) return;
            }
            var startMicroseconds = waitForMediaReady ? 0 : requestedStartMicroseconds ?? _host.CurrentPlaybackPositionMicroseconds;
            var preserveExisting = continueExistingResults || requestedStartMicroseconds.HasValue && !waitForMediaReady;
            if (generate)
            {
                if (!File.Exists(source) && _host.CurrentHttpHeaders is { Count: > 0 } headers)
                {
                    _host.SetStatus(L("StatusPreparingRemoteAsr"));
                    temporaryInput = await _remoteMediaDownloader.DownloadAsync(
                        source, headers, _host.Settings.Network.Proxy, _host.Settings.Network.TimeoutSeconds, token);
                    source = temporaryInput;
                }
                _translationCompleted = await GenerateSubtitlesAsync(source, startMicroseconds, token, preserveExisting);
                _subtitleGenerationCompleted = true;
            }
            if (TranslateEnabled && !_translationCompleted)
            {
                translating = true;
                _translationCompleted = await TranslateSubtitlesAsync(
                    startMicroseconds, token, includeAllMissing: preserveExisting && !continueExistingResults);
            }
        }
        catch (OperationCanceledException)
        {
            _host.SetStatus(L(translating ? "StatusTranslationCancelled" : "StatusSubtitleGenerationCancelled"));
        }
        catch (AsrWorkerException exception)
        {
            _host.SetStatus($"{exception.Code}: {exception.Message}");
            await AppLog.WriteAsync("error", "asr", exception.Code, exception.Message, exception);
        }
        catch (Exception exception)
        {
            _host.SetStatus($"AI_ERROR: {exception.Message}");
            await AppLog.WriteAsync("error", "ai", "AI_PIPELINE_ERROR", exception.Message, exception);
        }
        finally
        {
            _host.SetDownloadProgress(false, false);
            if (temporaryInput is not null)
                try { File.Delete(temporaryInput); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            if (combineProgress) _combinedProgress.End();
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private async Task<bool> GenerateSubtitlesAsync(string source, long startMicroseconds, CancellationToken token, bool preserveExisting)
    {
        var document = preserveExisting ? _host.Document : new SubtitleDocument();
        var track = document.EnsureTrack("srt");
        if (string.IsNullOrWhiteSpace(track.Name)) track.Name = "Qwen3-ASR";
        var generationStart = preserveExisting ? FindGenerationStartMicroseconds(track, startMicroseconds) : Math.Max(0, startMicroseconds);
        var translateGeneratedCues = TranslateEnabled;

        _host.SetStatus(L("StatusStartingAsr"));
        var settings = _host.Settings;
        var runtimeDirectory = AsrRuntimePaths.GetCrispAsrRuntimeDirectory(settings.Asr.CrispAsrRuntimeDirectory);
        await _asrEngine.StartAsync(runtimeDirectory, token);
        _host.SetStatus(L("StatusLoadingAsr"));
        var acceptingLoadProgress = true;
        var loadProgress = new Progress<AsrEvent>(update => { if (acceptingLoadProgress) UpdateAsrModelProgress(update); });
        try
        {
            await _asrEngine.LoadModelAsync(settings.Asr.ModelPath!, settings.Asr.AlignerPath,
                settings.Asr.Device.ToString(), settings.Asr.Precision.ToString(), loadProgress, token);
        }
        finally { acceptingLoadProgress = false; }

        if (!preserveExisting) _host.BindDocument(document);
        else if (!ReferenceEquals(_host.Document, document)) return false;

        var displayMode = preserveExisting && _host.CurrentSubtitleDisplayMode is { } existingDisplayMode
            ? existingDisplayMode
            : TranslateEnabled ? SubtitleDisplayMode.Translation : SubtitleDisplayMode.Original;
        _host.SetSubtitleDisplayMode(displayMode, refreshOverlay: false);
        _host.ShowSubtitlePanel();
        SetSubtitleGenerationStatus(0);
        _host.EnableGeneratedSubtitleOverlay();

        var durationMicroseconds = Math.Max(0, _playback.Duration.Ticks / 10);
        if (preserveExisting && durationMicroseconds > 0 && generationStart >= durationMicroseconds)
        {
            await DispatchSubtitleUiAsync(() =>
            {
                _host.DrawTimeline();
                if (translateGeneratedCues)
                {
                    _combinedProgress.CompleteSubtitle();
                    var translatedCount = track.Cues.Count(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText));
                    if (track.Cues.All(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText)))
                        _combinedProgress.CompleteTranslation(translatedCount, track.Cues.Count);
                    else if (!_combinedProgress.UpdateTranslation(translatedCount, track.Cues.Count))
                        _host.SetStatus(F("StatusTranslated", translatedCount));
                }
                else _host.SetStatus(F("StatusGeneratedSubtitles", track.Cues.Count));
            }, document, token);
            return !translateGeneratedCues || track.Cues.All(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText));
        }

        var translationQueue = Channel.CreateUnbounded<SubtitleCue>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var translationTask = TranslateGeneratedCuesRealtimeAsync(translationQueue.Reader, document, track, token);
        var translatedCountResult = 0;
        var segmentation = settings.Subtitle.Segmentation;
        var options = new AsrSegmentationOptions(segmentation.MinimumCueSeconds, segmentation.MaximumCueSeconds,
            segmentation.MaximumLines, segmentation.TargetCharactersPerLine, segmentation.SilenceSplitSeconds,
            segmentation.MaximumCharactersPerSecond);
        var generationCompleted = false;
        try
        {
            await foreach (var result in _asrEngine.TranscribeFileAsync(source, settings.Asr.Language,
                settings.Asr.ChunkDurationSeconds, settings.Asr.UseVad, options, generationStart, token).ConfigureAwait(false))
            {
                if (!ReferenceEquals(_host.Document, document)) throw new OperationCanceledException(token);
                if (result.Event == "progress" && result.Progress is { } progress)
                    await DispatchSubtitleUiAsync(() => SetSubtitleGenerationStatus(progress), document, token);
                if (result.Event != "segment" || result.Segment is not { } segment) continue;
                var cue = new SubtitleCue
                {
                    StartMicroseconds = segment.StartMicroseconds, EndMicroseconds = segment.EndMicroseconds,
                    Text = segment.Text, Confidence = segment.Confidence,
                    Source = SubtitleCueSource.AutomaticSpeechRecognition
                };
                await DispatchSubtitleUiAsync(() =>
                {
                    if (IsAlreadyGeneratedCue(track, segment)) return;
                    track.Cues.Add(cue);
                    translationQueue.Writer.TryWrite(cue);
                    _host.ScheduleGeneratedSubtitleUiRefresh();
                }, document, token);
            }
            generationCompleted = true;
        }
        finally
        {
            translationQueue.Writer.TryComplete();
            if (generationCompleted && translateGeneratedCues) _combinedProgress.CompleteSubtitle();
            if (token.IsCancellationRequested)
            {
                try { await translationTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    await AppLog.WriteAsync("warning", "translation", "TRANSLATION_CANCEL_WAIT_ERROR", exception.Message, exception);
                }
            }
            else translatedCountResult = await translationTask.ConfigureAwait(false);
        }
        if (!ReferenceEquals(_host.Document, document)) return false;
        await DispatchSubtitleUiAsync(() =>
        {
            SubtitleDocument.Sort(track);
            document.MarkDirty();
            _host.DrawTimeline();
            _host.ScheduleSubtitleOverlaySync(force: true);
            var completedTranslations = track.Cues.Count(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText));
            if (translateGeneratedCues)
            {
                if (track.Cues.All(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText)))
                    _combinedProgress.CompleteTranslation(completedTranslations, track.Cues.Count);
                else if (!_combinedProgress.UpdateTranslation(completedTranslations, track.Cues.Count))
                    _host.SetStatus(F("StatusTranslated", translatedCountResult));
            }
            else _host.SetStatus(translatedCountResult > 0
                ? F("StatusTranslated", translatedCountResult)
                : F("StatusGeneratedSubtitles", track.Cues.Count));
        }, document, token);
        return !translateGeneratedCues || track.Cues.All(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText));
    }

    private async Task<int> TranslateGeneratedCuesRealtimeAsync(
        ChannelReader<SubtitleCue> reader, SubtitleDocument targetDocument, SubtitleTrack track,
        CancellationToken cancellationToken)
    {
        const int batchSize = 10;
        var pending = new List<SubtitleCue>(batchSize);
        DateTimeOffset? firstPendingAt = null;
        ILlmProvider? provider = null;
        IDisposable? disposable = null;
        LlmService? service = null;
        var translatedCount = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (reader.TryRead(out var cue))
                {
                    pending.Add(cue);
                    firstPendingAt ??= DateTimeOffset.UtcNow;
                }
                if (pending.Count == 0)
                {
                    if (reader.Completion.IsCompleted) break;
                    await Task.Delay(100, cancellationToken);
                    continue;
                }
                if (!TranslateEnabled)
                {
                    if (reader.Completion.IsCompleted) break;
                    await Task.Delay(100, cancellationToken);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(_host.Settings.Llm.Model))
                    throw new InvalidOperationException(L("LlmModelMissingMessage"));

                var waitRemaining = TimeSpan.FromMilliseconds(750) - (DateTimeOffset.UtcNow - firstPendingAt!.Value);
                if (pending.Count < batchSize && !reader.Completion.IsCompleted && waitRemaining > TimeSpan.Zero)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, waitRemaining.TotalMilliseconds)), cancellationToken);
                    continue;
                }

                provider ??= CreateLlmProvider();
                disposable ??= provider as IDisposable;
                service ??= new LlmService(provider, _host.Settings.Llm.Model, _host.Settings.Llm.ThinkingLevel);
                var batch = pending.Take(batchSize).ToArray();
                pending.RemoveRange(0, batch.Length);
                firstPendingAt = pending.Count > 0 ? DateTimeOffset.UtcNow : null;
                var cuesById = batch.ToDictionary(cue => cue.Id);
                var translatedBeforeBatch = translatedCount;
                SetTranslationProgressStatus(translatedCount, Math.Max(translatedCount + batch.Length, track.Cues.Count));
                var translated = await service.TranslateAsync(batch, _host.Settings.Llm.TranslationLanguage,
                    batchCompleted: (result, token) => ApplyTranslationBatchAsync(targetDocument,
                        new TranslationBatch(result.Items, translatedBeforeBatch + result.Completed,
                            Math.Max(translatedBeforeBatch + result.Completed, track.Cues.Count)), cuesById, token),
                    batchSize: batchSize, contextCues: track.Cues.ToArray(), cancellationToken: cancellationToken);
                translatedCount += translated.Count;
                await AppLog.WriteAsync("info", "translation", "TRANSLATION_BATCH_COMPLETED",
                    $"Realtime translation completed {translatedCount} cues; {pending.Count} queued.");
            }
            return translatedCount;
        }
        finally { disposable?.Dispose(); }
    }

    private async Task<bool> TranslateSubtitlesAsync(long startMicroseconds, CancellationToken cancellationToken, bool includeAllMissing)
    {
        var targetDocument = _host.Document;
        var track = targetDocument.ActiveTrack;
        if (track is null || track.Cues.Count == 0)
        {
            _host.SetStatus(L("LoadSubtitlesFirst"));
            return false;
        }
        if (string.IsNullOrWhiteSpace(_host.Settings.Llm.Model))
        {
            _host.SetStatus(L("LlmModelMissingMessage"));
            return false;
        }
        var cues = track.Cues
            .Where(cue => string.IsNullOrWhiteSpace(cue.TranslatedText) &&
                          (includeAllMissing || cue.EndMicroseconds > startMicroseconds))
            .OrderBy(cue => cue.StartMicroseconds)
            .ToArray();
        _host.SetSubtitleDisplayMode(SubtitleDisplayMode.Translation, refreshOverlay: false);
        if (cues.Length == 0)
        {
            _host.ScheduleSubtitleOverlaySync(force: true);
            var existingCount = track.Cues.Count(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText));
            if (track.Cues.All(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText)))
                _combinedProgress.CompleteTranslation(existingCount, track.Cues.Count);
            else if (!_combinedProgress.UpdateTranslation(existingCount, track.Cues.Count))
                _host.SetStatus(F("StatusTranslated", 0));
            return track.Cues.All(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText));
        }

        _host.EnableGeneratedSubtitleOverlay();
        var translatedBefore = track.Cues.Count(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText));
        SetTranslationProgressStatus(translatedBefore, track.Cues.Count);
        var provider = CreateLlmProvider();
        using var disposable = provider as IDisposable;
        var service = new LlmService(provider, _host.Settings.Llm.Model, _host.Settings.Llm.ThinkingLevel);
        var cuesById = cues.ToDictionary(cue => cue.Id);
        var translated = await service.TranslateAsync(cues, _host.Settings.Llm.TranslationLanguage,
            batchCompleted: (batch, token) => ApplyTranslationBatchAsync(targetDocument,
                new TranslationBatch(batch.Items, translatedBefore + batch.Completed, track.Cues.Count), cuesById, token),
            contextCues: track.Cues.ToArray(),
            cancellationToken: cancellationToken);
        if (!ReferenceEquals(_host.Document, targetDocument)) return false;
        _host.ScheduleSubtitleOverlaySync(force: true);
        var translatedCount = track.Cues.Count(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText));
        if (cues.All(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText)))
            _combinedProgress.CompleteTranslation(translatedCount, track.Cues.Count);
        else if (!_combinedProgress.UpdateTranslation(translatedCount, track.Cues.Count))
            _host.SetStatus(F("StatusTranslated", translated.Count));
        await AppLog.WriteAsync("info", "translation", "TRANSLATION_COMPLETED",
            $"Translated {translated.Count} cues from {startMicroseconds} microseconds using {_host.Settings.Llm.Provider}.");
        return cues.All(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText));
    }

    private Task ApplyTranslationBatchAsync(SubtitleDocument targetDocument, TranslationBatch batch,
        IReadOnlyDictionary<Guid, SubtitleCue> cuesById, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_host.Document, targetDocument)) return Task.CompletedTask;
        if (_host.DispatcherQueue.HasThreadAccess)
        {
            Apply();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_host.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try { Apply(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        })) completion.SetException(new InvalidOperationException("The translation result could not be dispatched to the UI thread."));
        return completion.Task;

        void Apply()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_host.Document, targetDocument)) return;
            var commands = batch.Items.Where(item => cuesById.ContainsKey(item.Key))
                .Select(item => (IUndoableSubtitleCommand)new SetSubtitleTranslationCommand(
                    targetDocument, cuesById[item.Key], item.Value)).ToArray();
            if (commands.Length > 0)
                _host.ExecuteSubtitleCommand(new CompositeSubtitleCommand("Translate subtitle batch", commands));
            _host.ScheduleSubtitleOverlaySync();
            _host.ScheduleGeneratedSubtitleUiRefresh();
            SetTranslationProgressStatus(batch.Completed, batch.Total);
        }
    }

    private Task DispatchSubtitleUiAsync(Action action, SubtitleDocument targetDocument, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_host.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReferenceEquals(_host.Document, targetDocument)) action();
                completion.TrySetResult();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetException(new InvalidOperationException("The subtitle result could not be dispatched to the UI thread."));
        return completion.Task;
    }

    private async Task RunSummaryWithTrackingAsync(SubtitleTrack track, SummaryKind summaryKind)
    {
        if (_operationCancellation is not null) return;
        SetRetryableOperation(null);
        _activeSummaryKind = summaryKind;
        var operation = RunSummaryAsync(track, summaryKind);
        _summaryTask = operation;
        try { await operation; }
        finally { if (ReferenceEquals(_summaryTask, operation)) _summaryTask = null; }
    }

    private async Task RunSummaryAsync(SubtitleTrack track, SummaryKind summaryKind)
    {
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        try
        {
            var provider = CreateLlmProvider();
            using var disposable = provider as IDisposable;
            var service = new LlmService(provider, _host.Settings.Llm.Model!, _host.Settings.Llm.ThinkingLevel);
            var progress = new Progress<double>(value => _host.SetStatus(F("StatusSummarizing", value)));
            var summary = await service.SummarizeAsync(track.Cues, summaryKind,
                _host.Settings.Llm.TranslationLanguage.Trim(), progress, cancellationToken: cancellation.Token);
            var width = Math.Min(480, Math.Max(240, _host.ViewWidth - 96));
            var height = Math.Clamp(_host.ViewHeight - 440, 140, 280);
            var output = new TextBlock
            {
                Text = summary, TextWrapping = TextWrapping.Wrap, Width = Math.Max(200, width - 32)
            };
            var viewer = new ScrollViewer
            {
                Content = output, Width = width, MaxWidth = width, Height = height,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Auto,
                Padding = new Thickness(4, 0, 4, 0)
            };
            var copyButton = new Button
            {
                Content = new SymbolIcon(Symbol.Copy), Width = 40, Height = 40,
                Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right
            };
            ToolTipService.SetToolTip(copyButton, L("Copy.Text"));
            AutomationProperties.SetName(copyButton, L("Copy.Text"));
            copyButton.Click += (_, _) =>
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(summary);
                Clipboard.SetContent(package);
            };
            var content = new StackPanel { Width = width, Spacing = 8 };
            content.Children.Add(viewer);
            content.Children.Add(copyButton);
            await _host.ShowDialogAsync(new ContentDialog
            {
                Title = L("TranscriptSummaryTitle"), Content = content, CloseButtonText = L("CloseButton")
            });
            _host.SetStatus(L("StatusSummaryComplete"));
        }
        catch (OperationCanceledException) { _host.SetStatus(L("StatusSummaryCancelled")); }
        catch (Exception exception) { await _host.ShowMessageAsync("LLM_ERROR", exception.Message); }
        finally { if (ReferenceEquals(_operationCancellation, cancellation)) _operationCancellation = null; }
    }

    private void UpdateAsrModelProgress(AsrEvent update)
    {
        if (update.Stage == "download" && update.Progress is { } progress)
        {
            _host.SetDownloadProgress(true, false, Math.Clamp(progress, 0, 1));
            var model = update.Message ?? "Qwen3-ASR";
            var modelProgress = update.ModelProgress ?? progress;
            _host.SetStatus(update.TotalBytes is > 0 && update.DownloadedBytes is { } downloaded
                ? F("StatusDownloadingAsrModel", model, modelProgress, FormatDownloadSize(downloaded), FormatDownloadSize(update.TotalBytes.Value))
                : F("StatusPreparingAsrDownload", model));
            return;
        }
        if (update.Stage == "loading")
        {
            _host.SetDownloadProgress(true, true);
            _host.SetStatus(update.ElapsedSeconds is > 0 ? $"{L("StatusLoadingAsr")} ({update.ElapsedSeconds}s)" : L("StatusLoadingAsr"));
            return;
        }
        _host.SetDownloadProgress(false, false);
        _host.SetStatus(L("StatusLoadingAsr"));
    }

    private void SetSubtitleGenerationStatus(double progress)
    {
        if (!_combinedProgress.UpdateSubtitle(progress)) _host.SetStatus(F("StatusGeneratingSubtitles", progress));
    }

    private void SetTranslationProgressStatus(int completed, int total)
    {
        if (!_combinedProgress.UpdateTranslation(completed, total)) _host.SetStatus(F("StatusTranslating", completed, total));
    }

    private void OnCombinedProgressChanged(object? sender, AiProgressChangedEventArgs e)
    {
        void Apply()
        {
            var progress = e.Progress;
            _host.SetStatus(progress.SubtitleGenerationComplete
                ? progress.TranslationComplete
                    ? L("StatusSubtitlesAndTranslationComplete")
                    : F("StatusSubtitlesGeneratedAndTranslating", progress.TranslatedCount, progress.TranslationTotal)
                : F("StatusGeneratingSubtitlesAndTranslating", progress.SubtitleProgress,
                    progress.TranslatedCount, progress.TranslationTotal));
        }
        if (_host.DispatcherQueue.HasThreadAccess) Apply();
        else _host.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Apply);
    }

    private void SetRetryableOperation(AiRetryRequest? retry)
    {
        _retryableOperation = retry;
        _host.SetRetryAvailable(retry is not null);
    }

    private bool IsRetryStillValid(AiRetryRequest retry) =>
        ReferenceEquals(_host.Document, retry.Document) &&
        (retry.Kind != AiRetryOperationKind.SubtitlePipeline ||
         string.Equals(_playback.CurrentSource, retry.Source, StringComparison.OrdinalIgnoreCase));

    private ILlmProvider CreateLlmProvider() =>
        new LlmProviderFactory(new WindowsCredentialService()).Create(_host.Settings.Llm.Provider);

    private static long FindGenerationStartMicroseconds(SubtitleTrack track, long requestedStartMicroseconds)
    {
        var cursor = Math.Max(0, requestedStartMicroseconds);
        foreach (var cue in track.Cues.OrderBy(cue => cue.StartMicroseconds))
        {
            if (cue.EndMicroseconds <= cursor) continue;
            if (cue.StartMicroseconds > cursor) break;
            cursor = Math.Max(cursor, cue.EndMicroseconds);
        }
        return cursor;
    }

    private static bool IsAlreadyGeneratedCue(SubtitleTrack track, AsrSegment segment)
    {
        var start = Math.Max(0, segment.StartMicroseconds);
        var end = Math.Max(start + 1, segment.EndMicroseconds);
        var duration = end - start;
        var text = segment.Text.Trim();
        foreach (var existing in track.Cues)
        {
            var overlap = Math.Min(end, existing.EndMicroseconds) - Math.Max(start, existing.StartMicroseconds);
            if (overlap <= 0) continue;
            if (string.Equals(existing.Text.Trim(), text, StringComparison.OrdinalIgnoreCase)) return true;
            var existingDuration = Math.Max(1, existing.EndMicroseconds - existing.StartMicroseconds);
            if (overlap >= Math.Max(1, Math.Min(duration, existingDuration) / 2) &&
                overlap >= Math.Max(1, duration / 3)) return true;
        }
        return false;
    }

    private static string FormatDownloadSize(long bytes) => bytes >= 1_073_741_824
        ? $"{bytes / 1_073_741_824d:0.00} GB"
        : $"{bytes / 1_048_576d:0.0} MB";

    private static string L(string key) => Localization.LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);

    public async ValueTask DisposeAsync()
    {
        _combinedProgress.ProgressChanged -= OnCombinedProgressChanged;
        try
        {
            await CancelAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException exception)
        {
            await AppLog.WriteAsync("warning", "shutdown", "AI_PIPELINE_SHUTDOWN_TIMEOUT", exception.Message, exception);
        }
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        await _asrEngine.DisposeAsync();
    }

    private enum AiRetryOperationKind { SubtitlePipeline, Summary }
    private sealed record AiRetryRequest(AiRetryOperationKind Kind, SummaryKind SummaryKind, SubtitleDocument Document, string? Source);
}
