using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AIMediaWorker.Asr;

public sealed class AsrWorkerClient : IAsrEngine
{
    private readonly ConcurrentDictionary<string, Channel<AsrEvent>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _lifecycleLock = new();
    private Process? _process;
    private CancellationTokenSource? _readerCancellation;
    private Task? _stdoutReader;
    private Task? _stderrReader;
    private string? _pythonExecutable;
    private string? _workerScript;
    private bool _disposed;

    public AsrWorkerState State { get; private set; } = AsrWorkerState.NotStarted;
    public event EventHandler<AsrWorkerState>? StateChanged;
    public event EventHandler<string>? WorkerLog;
    public event EventHandler<AsrEvent>? LiveResultReceived;

    public async Task StartAsync(string pythonExecutable, string workerScript, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State is AsrWorkerState.Ready or AsrWorkerState.Busy) return;
        if (_process is not null) await StopProcessAsync().ConfigureAwait(false);
        _workerScript = Path.GetFullPath(workerScript);
        if (!File.Exists(_workerScript)) throw new FileNotFoundException("The ASR worker script was not found.", _workerScript);
        _pythonExecutable = PythonEnvironment.ResolveExecutable(pythonExecutable, _workerScript);
        SetState(AsrWorkerState.Starting);
        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonExecutable,
            ArgumentList = { "-u", _workerScript },
            WorkingDirectory = Path.GetDirectoryName(_workerScript)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        process.Exited += OnProcessExited;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Python did not start the ASR worker.");
            lock (_lifecycleLock) _process = process;
            _readerCancellation = new CancellationTokenSource();
            _stdoutReader = Task.Run(() => ReadStdoutAsync(process, _readerCancellation.Token), CancellationToken.None);
            _stderrReader = Task.Run(() => ReadStderrAsync(process, _readerCancellation.Token), CancellationToken.None);
            var response = await SendAndWaitAsync(AsrRequest.Create("initialize"), cancellationToken).ConfigureAwait(false);
            if (response.Event == "error") throw new AsrWorkerException(response.Code ?? "ASR_ERROR", response.Message ?? "ASR worker initialization failed.");
            SetState(AsrWorkerState.Ready);
        }
        catch
        {
            SetState(AsrWorkerState.Failed);
            TryTerminate(process);
            await StopProcessAsync().ConfigureAwait(false);
            process.Dispose();
            throw;
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        if (_pythonExecutable is null || _workerScript is null) throw new InvalidOperationException("The ASR worker has not been configured.");
        await StopProcessAsync().ConfigureAwait(false);
        SetState(AsrWorkerState.NotStarted);
        await StartAsync(_pythonExecutable, _workerScript, cancellationToken).ConfigureAwait(false);
    }

    public async Task LoadModelAsync(string modelPath, string? alignerPath, string device, string precision, IProgress<AsrEvent>? progress = null, CancellationToken cancellationToken = default)
    {
        var request = AsrRequest.Create("load_model", new { model_path = modelPath, aligner_path = alignerPath, device, precision });
        var response = await SendAndWaitAsync(request, cancellationToken, progress).ConfigureAwait(false);
        ThrowIfError(response);
    }

    public async IAsyncEnumerable<AsrEvent> TranscribeFileAsync(string path, string language, double chunkDurationSeconds = 30, bool useVad = true, AsrSegmentationOptions? segmentation = null, long startMicroseconds = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (startMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(startMicroseconds));
        var input = File.Exists(path) ? Path.GetFullPath(path) : Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : throw new FileNotFoundException("ASR input media was not found.", path);
        var request = AsrRequest.Create("transcribe_file", new { input, language, timestamps = true, chunk_duration = chunkDurationSeconds, vad = useVad, segmentation, start_us = startMicroseconds });
        var channel = Register(request.Id);
        using var registration = cancellationToken.Register(() => _ = CancelAsync(request.Id, CancellationToken.None));
        SetState(AsrWorkerState.Busy);
        await WriteAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                ThrowIfError(result);
                yield return result;
            }
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
            if (State != AsrWorkerState.Failed) SetState(AsrWorkerState.Ready);
        }
    }

    public async Task CancelAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited) return;
        var response = await SendAndWaitAsync(AsrRequest.Create("cancel", new { target_id = requestId }), cancellationToken).ConfigureAwait(false);
        ThrowIfError(response);
    }

    public async Task<string> StartStreamingAsync(string language, CancellationToken cancellationToken = default)
    {
        var streamId = $"stream-{Guid.NewGuid():N}";
        var result = await SendAndWaitAsync(AsrRequest.Create("start_streaming", new { stream_id = streamId, language }), cancellationToken).ConfigureAwait(false);
        ThrowIfError(result);
        return streamId;
    }

    public async Task PushAudioAsync(string streamId, ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default)
    {
        var result = await SendAndWaitAsync(AsrRequest.Create("push_audio", new { stream_id = streamId, audio_base64 = Convert.ToBase64String(pcm16.Span) }), cancellationToken).ConfigureAwait(false);
        ThrowIfError(result);
    }

    public async Task StopStreamingAsync(string streamId, CancellationToken cancellationToken = default)
    {
        var result = await SendAndWaitAsync(AsrRequest.Create("stop_streaming", new { stream_id = streamId, sample_rate = 16000, channels = 1 }), cancellationToken).ConfigureAwait(false);
        ThrowIfError(result);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited) return;
        SetState(AsrWorkerState.Stopping);
        try { _ = await SendAndWaitAsync(AsrRequest.Create("shutdown"), cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or AsrWorkerException or OperationCanceledException) { }
        await StopProcessAsync().ConfigureAwait(false);
        if (!_disposed) SetState(AsrWorkerState.NotStarted);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await ShutdownAsync(cancellation.Token).ConfigureAwait(false); } catch { }
        _disposed = true;
        await StopProcessAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<AsrEvent> SendAndWaitAsync(AsrRequest request, CancellationToken cancellationToken, IProgress<AsrEvent>? progress = null)
    {
        var channel = Register(request.Id);
        try
        {
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (result.Event == "progress") progress?.Report(result);
                if (result.Event is "partial" or "final") LiveResultReceived?.Invoke(this, result);
                if (result.Event is "completed" or "ready" or "status" or "cancelled" or "error") return result;
            }
            throw new IOException("The ASR worker ended the response without a terminal event.");
        }
        finally { _pending.TryRemove(request.Id, out _); }
    }

    private Channel<AsrEvent> Register(string id)
    {
        var channel = Channel.CreateUnbounded<AsrEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        if (!_pending.TryAdd(id, channel)) throw new InvalidOperationException($"Duplicate ASR request id: {id}");
        return channel;
    }

    private async Task WriteAsync(AsrRequest request, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited) throw new AsrWorkerException("ASR_ERROR", "The ASR worker is not running.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteLineAsync(AsrJson.Serialize(request).AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadStdoutAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                AsrEvent result;
                try { result = AsrJson.DeserializeEvent(line); }
                catch (JsonException exception) { WorkerLog?.Invoke(this, $"Invalid ASR protocol message: {exception.Message}"); continue; }
                if (result.Id is null || !_pending.TryGetValue(result.Id, out var channel)) continue;
                await channel.Writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
                if (result.Event is "completed" or "ready" or "status" or "cancelled" or "error") channel.Writer.TryComplete();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                WorkerLog?.Invoke(this, line);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (State is AsrWorkerState.Stopping or AsrWorkerState.NotStarted || _disposed) return;
        SetState(AsrWorkerState.Failed);
        var exception = new AsrWorkerException("ASR_ERROR", "The Python ASR worker exited unexpectedly.");
        foreach (var channel in _pending.Values) channel.Writer.TryComplete(exception);
    }

    private async Task StopProcessAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        var stdoutReader = Interlocked.Exchange(ref _stdoutReader, null);
        var stderrReader = Interlocked.Exchange(ref _stderrReader, null);
        _readerCancellation?.Cancel();
        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (TimeoutException) { TryTerminate(process); }
            }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { TryTerminate(process); }
            var readers = new[] { stdoutReader, stderrReader }.Where(task => task is not null).Cast<Task>().ToArray();
            if (readers.Length > 0) try { await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException) { }
            process.Dispose();
        }
        _readerCancellation?.Dispose(); _readerCancellation = null;
    }

    private static void TryTerminate(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
    private static void ThrowIfError(AsrEvent result) { if (result.Event == "error") throw new AsrWorkerException(result.Code ?? "ASR_ERROR", result.Message ?? "The ASR worker reported an error."); }
    private void SetState(AsrWorkerState state) { if (State == state) return; State = state; StateChanged?.Invoke(this, state); }
}

public sealed class AsrWorkerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
