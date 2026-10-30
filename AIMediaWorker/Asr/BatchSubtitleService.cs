using AIMediaWorker.Llm;
using AIMediaWorker.Settings;
using AIMediaWorker.Subtitle;
using AIMediaWorker.Subtitle.Writing;
using System.Text;

namespace AIMediaWorker.Asr;

public enum BatchSubtitleStage { Loading, Generating, Translating, Completed, Failed, Cancelled }

public sealed record BatchSubtitleUpdate(string? Path, BatchSubtitleStage Stage, double? Progress = null,
    string? OriginalPath = null, string? TranslationPath = null, string? Error = null);

/// <summary>Processes local media sequentially, keeping each file's results independent of playback.</summary>
public sealed class BatchSubtitleService(IAsrEngine engine, LlmService? translator = null)
{
    public async Task RunAsync(IReadOnlyList<string> paths, AppSettings settings,
        IProgress<BatchSubtitleUpdate>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new(null, BatchSubtitleStage.Loading));
        await engine.StartAsync(AsrRuntimePaths.GetCrispAsrRuntimeDirectory(settings.Asr.CrispAsrRuntimeDirectory), cancellationToken).ConfigureAwait(false);
        await engine.LoadModelAsync(settings.Asr.ModelPath!, settings.Asr.AlignerPath,
            settings.Asr.Device.ToString(), settings.Asr.Precision.ToString(), cancellationToken: cancellationToken).ConfigureAwait(false);
        var segmentation = settings.Subtitle.Segmentation;
        var options = new AsrSegmentationOptions(segmentation.MinimumCueSeconds, segmentation.MaximumCueSeconds,
            segmentation.MaximumLines, segmentation.TargetCharactersPerLine, segmentation.SilenceSplitSeconds,
            segmentation.MaximumCharactersPerSecond);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? originalPath = null;
            string? translationPath = null;
            try
            {
                progress?.Report(new(path, BatchSubtitleStage.Generating));
                if (!File.Exists(path)) throw new FileNotFoundException("Media file was not found.", path);
                var track = new SubtitleTrack();
                await foreach (var update in engine.TranscribeFileAsync(path, settings.Asr.Language,
                    settings.Asr.ChunkDurationSeconds, settings.Asr.UseVad, options,
                    cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (update.Event == "error") throw new AsrWorkerException(update.Code ?? "ASR_ERROR", update.Message ?? "ASR failed.");
                    if (update.Event == "segment" && update.Segment is { } segment && !string.IsNullOrWhiteSpace(segment.Text))
                        track.Cues.Add(new SubtitleCue
                        {
                            StartMicroseconds = segment.StartMicroseconds, EndMicroseconds = segment.EndMicroseconds,
                            Text = segment.Text, Confidence = segment.Confidence,
                            Source = SubtitleCueSource.AutomaticSpeechRecognition
                        });
                    if (update.Progress is { } value) progress?.Report(new(path, BatchSubtitleStage.Generating, value));
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (track.Cues.Count == 0) throw new InvalidDataException("No speech subtitles were detected.");
                originalPath = await SaveUniqueAsync(track, path, false, cancellationToken).ConfigureAwait(false);
                if (translator is not null)
                {
                    progress?.Report(new(path, BatchSubtitleStage.Translating, OriginalPath: originalPath));
                    // Retry missing cues, rather than silently saving source text in a translated SRT.
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        var missing = track.Cues.Where(cue => string.IsNullOrWhiteSpace(cue.TranslatedText)).ToArray();
                        if (missing.Length == 0) break;
                        await translator.TranslateAsync(missing, settings.Llm.TranslationLanguage,
                            batchCompleted: (batch, token) =>
                            {
                                token.ThrowIfCancellationRequested();
                                foreach (var cue in missing)
                                    if (batch.Items.TryGetValue(cue.Id, out var text)) cue.TranslatedText = text;
                                var done = track.Cues.Count(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText));
                                progress?.Report(new(path, BatchSubtitleStage.Translating, (double)done / track.Cues.Count, originalPath));
                                return Task.CompletedTask;
                            }, contextCues: track.Cues.ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    if (track.Cues.Any(cue => string.IsNullOrWhiteSpace(cue.TranslatedText)))
                        throw new InvalidDataException("Some subtitles could not be translated. The original SRT was saved.");
                    translationPath = await SaveUniqueAsync(track, path, true, cancellationToken).ConfigureAwait(false);
                }
                progress?.Report(new(path, BatchSubtitleStage.Completed, 1, originalPath, translationPath));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                progress?.Report(new(path, BatchSubtitleStage.Cancelled, OriginalPath: originalPath, TranslationPath: translationPath));
                throw;
            }
            catch (Exception exception)
            {
                progress?.Report(new(path, BatchSubtitleStage.Failed, OriginalPath: originalPath, TranslationPath: translationPath, Error: exception.Message));
            }
        }
    }

    private static async Task<string> SaveUniqueAsync(SubtitleTrack track, string mediaPath, bool translated, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(mediaPath))!;
        var stem = Path.GetFileNameWithoutExtension(mediaPath) + (translated ? ".translated" : "");
        var text = SrtWriter.Write(track, translated ? SubtitleDisplayMode.Translation : SubtitleDisplayMode.Original);
        // Write privately first, then atomically publish without replacing an existing subtitle.
        var temporaryPath = Path.Combine(directory, $".aimedia-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, text, new UTF8Encoding(false), token).ConfigureAwait(false);
            for (var suffix = 0; ; suffix++)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(directory, stem + (suffix == 0 ? "" : $" ({suffix})") + ".srt");
                try { File.Move(temporaryPath, destination, overwrite: false); return destination; }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
