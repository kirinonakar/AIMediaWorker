using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AIMediaWorker.Asr;

/// <summary>
/// In-process WinUI3 C# client for the prebuilt CrispASR native runtime.
/// CrispASR owns ASR and forced alignment; this class only adapts its C ABI to
/// the application's asynchronous subtitle and live-caption interfaces.
/// </summary>
public sealed class AsrWorkerClient : IAsrEngine
{
    private const int SampleRate = 16_000;
    private const long CentisecondMicroseconds = 10_000;
    private static readonly TimeSpan[] EmptyDecodeRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(300)
    ];
    private readonly object _lifecycleLock = new();
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LiveStream> _streams = new(StringComparer.Ordinal);
    private nint _session;
    private string? _runtimeDirectory;
    private string? _modelPath;
    private string? _alignerPath;
    private bool _disposed;

    public AsrWorkerState State { get; private set; } = AsrWorkerState.NotStarted;
    public event EventHandler<AsrWorkerState>? StateChanged;
    public event EventHandler<string>? WorkerLog;
    public event EventHandler<AsrEvent>? LiveResultReceived;

    public Task StartAsync(string crispAsrRuntimeDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var runtimeDirectory = Path.GetFullPath(crispAsrRuntimeDirectory);
        if (State is AsrWorkerState.Ready or AsrWorkerState.Busy &&
            string.Equals(_runtimeDirectory, runtimeDirectory, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        return StartCoreAsync(runtimeDirectory, cancellationToken);
    }

    private async Task StartCoreAsync(string runtimeDirectory, CancellationToken cancellationToken)
    {
        if (State is not AsrWorkerState.NotStarted)
        {
            await ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }

        SetState(AsrWorkerState.Starting);
        try
        {
            await Task.Run(() => CrispAsrNative.EnsureLoaded(runtimeDirectory), cancellationToken).ConfigureAwait(false);
            lock (_lifecycleLock) _runtimeDirectory = runtimeDirectory;
            SetState(AsrWorkerState.Ready);
        }
        catch
        {
            SetState(AsrWorkerState.Failed);
            throw;
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        var runtimeDirectory = _runtimeDirectory ?? throw new InvalidOperationException("The CrispASR runtime has not been configured.");
        var modelPath = _modelPath;
        var alignerPath = _alignerPath;

        await ShutdownAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(runtimeDirectory, cancellationToken).ConfigureAwait(false);
        if (modelPath is not null)
            await LoadModelAsync(modelPath, alignerPath, "auto", "auto", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task LoadModelAsync(string modelPath, string? alignerPath, string device, string precision,
                                     IProgress<AsrEvent>? progress = null,
                                     CancellationToken cancellationToken = default)
    {
        _ = device;
        _ = precision;
        ObjectDisposedException.ThrowIf(_disposed, this);
        var runtimeDirectory = _runtimeDirectory ?? throw new InvalidOperationException("StartAsync must be called before loading an ASR model.");
        var resolvedModelPath = ResolveModelPath(modelPath, runtimeDirectory);
        var resolvedAlignerPath = ResolveModelPath(alignerPath ?? AsrRuntimePaths.AlignerModelFileName, runtimeDirectory);

        if (!File.Exists(resolvedModelPath))
        {
            progress?.Report(new AsrEvent
            {
                Event = "progress",
                Stage = "loading",
                Progress = 0,
                Message = $"Model not found: {resolvedModelPath}"
            });
            SetState(AsrWorkerState.Ready);
            throw new AsrWorkerException("MODEL_NOT_FOUND", $"The Qwen3 ASR model was not found: {resolvedModelPath}");
        }

        try
        {
            CrispAsrModelFormat.ValidateCrispAsrQwen3Model(resolvedModelPath);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            SetState(AsrWorkerState.Ready);
            throw new AsrWorkerException("MODEL_FORMAT_ERROR", exception.Message);
        }

        if (!File.Exists(resolvedAlignerPath))
        {
            progress?.Report(new AsrEvent
            {
                Event = "progress",
                Stage = "loading",
                Progress = 0,
                Message = $"Aligner model not found: {resolvedAlignerPath}"
            });
            SetState(AsrWorkerState.Ready);
            throw new AsrWorkerException("MODEL_NOT_FOUND", $"The CrispASR forced aligner model was not found: {resolvedAlignerPath}");
        }

        SetState(AsrWorkerState.Busy);
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new AsrEvent
        {
            Event = "progress",
            Stage = "loading",
            Progress = 0.05,
            ElapsedSeconds = 0,
            Message = $"Loading {Path.GetFileName(resolvedModelPath)} with CrispASR qwen3"
        });

        nint newSession = 0;
        try
        {
            var threadCount = NativeThreadCount;
            await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                newSession = await Task.Run(() =>
                    CrispAsrNative.OpenSession(resolvedModelPath, threadCount), cancellationToken).ConfigureAwait(false);

                nint previousSession;
                lock (_lifecycleLock)
                {
                    previousSession = _session;
                    _session = newSession;
                    _modelPath = resolvedModelPath;
                    _alignerPath = resolvedAlignerPath;
                    newSession = 0;
                }
                CrispAsrNative.CloseSession(previousSession);
            }
            finally
            {
                _inferenceLock.Release();
            }

            stopwatch.Stop();
            progress?.Report(new AsrEvent
            {
                Event = "progress",
                Stage = "loading",
                Progress = 1,
                ElapsedSeconds = (int)Math.Round(stopwatch.Elapsed.TotalSeconds),
                Message = "CrispASR Qwen3 ASR and forced aligner are ready."
            });
        }
        catch (OperationCanceledException)
        {
            CrispAsrNative.CloseSession(newSession);
            throw;
        }
        catch (AsrWorkerException)
        {
            CrispAsrNative.CloseSession(newSession);
            throw;
        }
        catch (Exception exception)
        {
            CrispAsrNative.CloseSession(newSession);
            throw new AsrWorkerException("MODEL_LOAD_ERROR", exception.Message);
        }
        finally
        {
            if (State != AsrWorkerState.Failed) SetState(AsrWorkerState.Ready);
        }
    }

    public async IAsyncEnumerable<AsrEvent> TranscribeFileAsync(
        string path,
        string language,
        double chunkDurationSeconds = 30,
        bool useVad = true,
        AsrSegmentationOptions? segmentation = null,
        long startMicroseconds = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = segmentation;
        if (startMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(startMicroseconds));
        var input = ResolveInput(path);
        var requestId = $"job-{Guid.NewGuid():N}";
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_requests.TryAdd(requestId, requestCancellation)) throw new InvalidOperationException($"Duplicate ASR request id: {requestId}");

        var token = requestCancellation.Token;
        SetState(AsrWorkerState.Busy);
        try
        {
            yield return new AsrEvent { Id = requestId, Event = "progress", Stage = "decode", Progress = 0, Message = "Decoding audio to 16 kHz mono PCM." };
            var samples = await DecodeMediaAsync(input, startMicroseconds, token).ConfigureAwait(false);
            if (samples.Length == 0) throw new AsrWorkerException("AUDIO_EMPTY", "The media file contains no decodable audio samples.");

            var chunkSamples = Math.Clamp((int)Math.Round(Math.Clamp(chunkDurationSeconds, 5, 180) * SampleRate), SampleRate, SampleRate * 180);
            var processedSamples = 0;
            while (processedSamples < samples.Length)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(chunkSamples, samples.Length - processedSamples);
                var chunk = samples.AsSpan(processedSamples, count).ToArray();
                var progressValue = Math.Clamp((double)(processedSamples + count) / samples.Length, 0, 1);
                if (!useVad || HasSpeech(chunk))
                {
                    var segments = await TranscribeChunkAsync(chunk, processedSamples, startMicroseconds, language, token).ConfigureAwait(false);
                    var subtitleSegments = AsrSubtitleSegmenter.Segment(segments, segmentation);
                    foreach (var segment in subtitleSegments)
                    {
                        token.ThrowIfCancellationRequested();
                        yield return new AsrEvent { Id = requestId, Event = "segment", Segment = segment };
                    }
                }

                processedSamples += count;
                yield return new AsrEvent
                {
                    Id = requestId,
                    Event = "progress",
                    Stage = "transcribe",
                    Progress = progressValue,
                    Message = "Transcribing with CrispASR."
                };
            }

            yield return new AsrEvent { Id = requestId, Event = "completed", Progress = 1 };
        }
        finally
        {
            _requests.TryRemove(requestId, out _);
            if (State != AsrWorkerState.Failed && _streams.IsEmpty) SetState(AsrWorkerState.Ready);
        }
    }

    public Task CancelAsync(string requestId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_requests.TryGetValue(requestId, out var cancellation)) cancellation.Cancel();
        return Task.CompletedTask;
    }

    public Task<string> StartStreamingAsync(string language, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSessionLoaded();
        var streamId = $"stream-{Guid.NewGuid():N}";
        if (!_streams.TryAdd(streamId, new LiveStream(streamId, language)))
            throw new InvalidOperationException($"Duplicate ASR stream id: {streamId}");
        SetState(AsrWorkerState.Busy);
        return Task.FromResult(streamId);
    }

    public async Task PushAudioAsync(string streamId, ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default)
    {
        if (!_streams.TryGetValue(streamId, out var stream)) throw new InvalidOperationException($"Unknown ASR stream: {streamId}");
        if (pcm16.Length == 0) return;
        if ((pcm16.Length & 1) != 0) throw new ArgumentException("PCM16 data must contain complete samples.", nameof(pcm16));

        await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        float[]? snapshot = null;
        try
        {
            stream.Append(pcm16.Span);
            if (stream.SampleCount - stream.LastDecodedSampleCount >= SampleRate * 2)
            {
                snapshot = stream.Samples.ToArray();
                stream.LastDecodedSampleCount = stream.SampleCount;
            }
        }
        finally
        {
            stream.Gate.Release();
        }

        if (snapshot is not null) await EmitLiveResultAsync(stream, snapshot, stream.WindowStartSample, "partial", cancellationToken).ConfigureAwait(false);
    }

    public async Task StopStreamingAsync(string streamId, CancellationToken cancellationToken = default)
    {
        if (!_streams.TryGetValue(streamId, out var stream)) return;
        await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stream.SampleCount > 0)
                await EmitLiveResultAsync(stream, stream.Samples.ToArray(), stream.WindowStartSample, "final", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _streams.TryRemove(streamId, out _);
            stream.Gate.Release();
            if (_streams.IsEmpty && State != AsrWorkerState.Failed) SetState(AsrWorkerState.Ready);
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (State == AsrWorkerState.NotStarted && _session == 0) return;
        SetState(AsrWorkerState.Stopping);
        foreach (var cancellation in _requests.Values) cancellation.Cancel();

        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nint session;
            lock (_lifecycleLock)
            {
                session = _session;
                _session = 0;
            }
            CrispAsrNative.CloseSession(session);
            _streams.Clear();
        }
        finally
        {
            _inferenceLock.Release();
        }

        if (!_disposed) SetState(AsrWorkerState.NotStarted);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await ShutdownAsync(cancellation.Token).ConfigureAwait(false); } catch { }
        _disposed = true;
        foreach (var request in _requests.Values) request.Dispose();
        _requests.Clear();
        _inferenceLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<IReadOnlyList<AsrSegment>> TranscribeChunkAsync(
        float[] samples,
        int absoluteSampleOffset,
        long startMicroseconds,
        string language,
        CancellationToken cancellationToken)
    {
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            var alignerPath = _alignerPath;
            var normalizedLanguage = NormalizeLanguage(language);
            var threadCount = NativeThreadCount;
            return await Task.Run(() =>
            {
                var nativeSegments = CrispAsrNative.Transcribe(session, samples, normalizedLanguage);
                return MapSegments(nativeSegments, samples, absoluteSampleOffset, startMicroseconds, alignerPath, threadCount,
                    exception => WorkerLog?.Invoke(this, $"CrispASR forced alignment fallback: {exception.Message}"));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AsrWorkerException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AsrWorkerException("TRANSCRIBE_ERROR", exception.Message);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private async Task EmitLiveResultAsync(LiveStream stream, float[] samples, long windowStartSample, string eventName, CancellationToken cancellationToken)
    {
        var segments = await TranscribeChunkAsync(samples, 0, SamplesToMicroseconds(windowStartSample), stream.Language, cancellationToken).ConfigureAwait(false);
        var text = string.Join(" ", segments.Select(segment => segment.Text).Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
        if (text.Length == 0) return;

        var result = new AsrEvent
        {
            Id = stream.Id,
            Event = eventName,
            Text = text,
            Segment = segments.FirstOrDefault(),
            Segments = segments
        };
        LiveResultReceived?.Invoke(this, result);
    }

    private static IReadOnlyList<AsrSegment> MapSegments(
        CrispAsrNative.NativeSegment[] nativeSegments,
        float[] chunkSamples,
        int absoluteSampleOffset,
        long startMicroseconds,
        string? alignerPath,
        int threadCount,
        Action<Exception>? alignmentLog)
    {
        var result = new List<AsrSegment>(nativeSegments.Length);
        foreach (var nativeSegment in nativeSegments)
        {
            var text = nativeSegment.Text.Trim();
            if (text.Length == 0) continue;

            var segmentStartCentiseconds = Math.Max(0, nativeSegment.StartCentiseconds);
            var segmentEndCentiseconds = Math.Max(segmentStartCentiseconds + 1, nativeSegment.EndCentiseconds);
            var absoluteChunkStart = startMicroseconds + SamplesToMicroseconds(absoluteSampleOffset);
            var segmentStart = absoluteChunkStart + segmentStartCentiseconds * CentisecondMicroseconds;
            var segmentEnd = absoluteChunkStart + segmentEndCentiseconds * CentisecondMicroseconds;
            var words = CreateWords(nativeSegment, text, chunkSamples, segmentStart, segmentEnd, alignerPath, threadCount, alignmentLog);
            var confidenceValues = nativeSegment.Words.Where(word => word.Probability is > 0).Select(word => (double)word.Probability!.Value).ToArray();

            result.Add(new AsrSegment
            {
                StartMicroseconds = segmentStart,
                EndMicroseconds = Math.Max(segmentStart + 1, segmentEnd),
                Text = text,
                Confidence = confidenceValues.Length == 0 ? null : confidenceValues.Average(),
                Words = words
            });
        }
        return result;
    }

    private static IReadOnlyList<AsrWord>? CreateWords(
        CrispAsrNative.NativeSegment nativeSegment,
        string text,
        float[] chunkSamples,
        long segmentStart,
        long segmentEnd,
        string? alignerPath,
        int threadCount,
        Action<Exception>? alignmentLog)
    {
        if (alignerPath is not null && File.Exists(alignerPath))
        {
            var localStart = (int)Math.Clamp(nativeSegment.StartCentiseconds * SampleRate / 100, 0, chunkSamples.Length);
            var localEnd = (int)Math.Clamp(nativeSegment.EndCentiseconds * SampleRate / 100, localStart, chunkSamples.Length);
            if (localEnd <= localStart)
            {
                localStart = 0;
                localEnd = chunkSamples.Length;
            }

            try
            {
                var aligned = CrispAsrNative.AlignWords(alignerPath, text, chunkSamples[localStart..localEnd], threadCount);
                var words = aligned
                    .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                    .Select(word => new AsrWord
                    {
                        Text = word.Text.Trim(),
                        StartMicroseconds = segmentStart + Math.Max(0, word.StartCentiseconds) * CentisecondMicroseconds,
                        EndMicroseconds = segmentStart + Math.Max(Math.Max(0, word.StartCentiseconds) + 1, word.EndCentiseconds) * CentisecondMicroseconds
                    })
                    .ToArray();
                if (words.Length > 0) return words;
            }
            catch (Exception exception)
            {
                // Native timestamps are a usable fallback when a short or noisy
                // segment cannot be aligned by the optional second pass.
                alignmentLog?.Invoke(exception);
            }
        }

        if (nativeSegment.Words.Length > 0)
        {
            return nativeSegment.Words
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .Select(word => new AsrWord
                {
                    Text = word.Text.Trim(),
                    StartMicroseconds = segmentStart + Math.Max(0, word.StartCentiseconds) * CentisecondMicroseconds,
                    EndMicroseconds = segmentStart + Math.Max(Math.Max(0, word.StartCentiseconds) + 1, word.EndCentiseconds) * CentisecondMicroseconds
                })
                .ToArray();
        }

        var fallbackWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fallbackWords.Length == 0) return null;
        var duration = Math.Max(1, segmentEnd - segmentStart);
        return fallbackWords.Select((word, index) => new AsrWord
        {
            Text = word,
            StartMicroseconds = segmentStart + duration * index / fallbackWords.Length,
            EndMicroseconds = segmentStart + duration * (index + 1) / fallbackWords.Length
        }).ToArray();
    }

    private async Task<float[]> DecodeMediaAsync(string input, long startMicroseconds, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var samples = await DecodeMediaOnceAsync(input, startMicroseconds, probeMore: attempt > 0, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (samples.Length > 0) return samples;
            if (attempt >= EmptyDecodeRetryDelays.Length) break;
            await Task.Delay(EmptyDecodeRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }

        if (startMicroseconds > 0)
        {
            // WebM/Opus can occasionally produce no packets when an output seek is
            // requested immediately after the player opens the file. Decode from the
            // beginning as a last resort, then trim in memory so cue timestamps still
            // refer to the requested media position.
            var completeSamples = await DecodeMediaOnceAsync(input, 0, probeMore: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            return TrimSamplesFromStart(completeSamples, startMicroseconds);
        }

        return [];
    }

    private async Task<float[]> DecodeMediaOnceAsync(
        string input,
        long startMicroseconds,
        bool probeMore,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = AsrRuntimePaths.TryGetFfmpegPath(_runtimeDirectory) ?? "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in BuildDecodeArguments(input, startMicroseconds, probeMore)) process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new AsrWorkerException("AUDIO_DECODE_ERROR", $"FFmpeg is required to decode ASR input: {exception.Message}");
        }

        using var output = new MemoryStream();
        try
        {
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(copyTask, errorTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new AsrWorkerException("AUDIO_DECODE_ERROR", string.IsNullOrWhiteSpace(error) ? "FFmpeg could not decode the media." : error.Trim());
        }
        catch
        {
            TryTerminate(process);
            throw;
        }

        var bytes = output.ToArray();
        if (bytes.Length == 0) return [];
        if (bytes.Length % sizeof(float) != 0) throw new AsrWorkerException("AUDIO_DECODE_ERROR", "FFmpeg returned an incomplete float PCM frame.");
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    private static IReadOnlyList<string> BuildDecodeArguments(string input, long startMicroseconds, bool probeMore = false)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (probeMore)
        {
            // Some WebM files put the first audio packets far enough after the video
            // header that FFmpeg's default probe can finish without exposing audio.
            arguments.AddRange(["-probesize", "100000000", "-analyzeduration", "10000000"]);
        }
        arguments.AddRange(["-i", input]);
        if (startMicroseconds > 0)
        {
            // Seek after opening the input so the timestamps are accurate. The decoded
            // PCM starts at the requested media position, while TranscribeChunkAsync
            // restores that same position to every emitted subtitle cue.
            arguments.Add("-ss");
            arguments.Add((startMicroseconds / 1_000_000d).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        }
        arguments.AddRange(["-map", "0:a:0?", "-vn", "-ac", "1", "-ar", SampleRate.ToString(), "-f", "f32le", "pipe:1"]);
        return arguments;
    }

    private static float[] TrimSamplesFromStart(float[] samples, long startMicroseconds)
    {
        if (samples.Length == 0 || startMicroseconds <= 0) return samples;
        var offset = (long)Math.Floor(startMicroseconds * (double)SampleRate / 1_000_000d);
        if (offset <= 0) return samples;
        if (offset >= samples.LongLength) return [];
        return samples[(int)offset..];
    }

    private static string ResolveInput(string path)
    {
        if (File.Exists(path)) return Path.GetFullPath(path);
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri.AbsoluteUri;
        throw new FileNotFoundException("ASR input media was not found.", path);
    }

    private static string ResolveModelPath(string path, string runtimeDirectory)
    {
        var workerDirectory = AsrRuntimePaths.GetWorkerDirectory(runtimeDirectory);
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        return Path.Combine(AsrRuntimePaths.GetModelsDirectory(workerDirectory), Path.GetFileName(path));
    }

    private void EnsureSessionLoaded()
    {
        lock (_lifecycleLock)
        {
            if (_session == 0) throw new AsrWorkerException("MODEL_NOT_LOADED", "Load the Qwen3 ASR model before starting recognition.");
        }
    }

    private nint RequireSession()
    {
        lock (_lifecycleLock)
        {
            if (_session == 0) throw new AsrWorkerException("MODEL_NOT_LOADED", "Load the Qwen3 ASR model before starting recognition.");
            return _session;
        }
    }

    private static bool HasSpeech(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return false;
        double sum = 0;
        var peak = 0f;
        foreach (var sample in samples)
        {
            var absolute = Math.Abs(sample);
            peak = Math.Max(peak, absolute);
            sum += sample * sample;
        }
        var rms = Math.Sqrt(sum / samples.Length);
        return peak >= 0.01f || rms >= 0.004;
    }

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase) ? null : language.Trim();

    private static long SamplesToMicroseconds(long samples) => samples * 1_000_000 / SampleRate;

    // Keep ASR from consuming all CPU resources while mpv is decoding/rendering.
    // CrispASR creates native worker threads, so the limit must leave headroom for
    // playback (including AV1 decoding and NVIDIA VSR).
    private static int NativeThreadCount => Math.Clamp(Environment.ProcessorCount / 4, 1, 2);

    private static void TryTerminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private void SetState(AsrWorkerState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private sealed class LiveStream(string id, string language)
    {
        public string Id { get; } = id;
        public string Language { get; } = language;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public List<float> Samples { get; } = [];
        public int SampleCount => Samples.Count;
        public int LastDecodedSampleCount { get; set; }
        public long WindowStartSample { get; private set; }

        public void Append(ReadOnlySpan<byte> pcm16)
        {
            for (var index = 0; index < pcm16.Length; index += 2)
            {
                var value = (short)(pcm16[index] | pcm16[index + 1] << 8);
                Samples.Add(value / 32768f);
            }

            // Keep live inference bounded while preserving enough context for a
            // rolling Qwen3 decode. The UI displays the latest partial text.
            const int maximumSamples = SampleRate * 12;
            if (Samples.Count > maximumSamples)
            {
                var remove = Samples.Count - SampleRate * 8;
                Samples.RemoveRange(0, remove);
                WindowStartSample += remove;
                LastDecodedSampleCount = Math.Max(0, LastDecodedSampleCount - remove);
            }
        }
    }
}

public sealed class AsrWorkerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
