using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AIMediaWorker.Asr;

internal static class AsrAudioDecoder
{
    internal const int SampleRate = 16_000;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)];

    internal static async IAsyncEnumerable<float[]> DecodeAsync(
        string ffmpeg, string input, long startMicroseconds, int chunkSamples, int? audioStreamIndex,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            var receivedAudio = false;
            await foreach (var chunk in DecodeOnceAsync(ffmpeg, input, startMicroseconds, chunkSamples,
                audioStreamIndex, attempt > 0, false, cancellationToken).ConfigureAwait(false))
            {
                receivedAudio = true;
                yield return chunk;
            }
            // Never retry after publishing audio: that would duplicate subtitles.
            if (receivedAudio) yield break;
            if (attempt < RetryDelays.Length)
                await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }

        if (startMicroseconds > 0)
        {
            // Preserve the WebM/Opus empty-seek fallback without retaining the prefix.
            await foreach (var chunk in DecodeOnceAsync(ffmpeg, input, startMicroseconds, chunkSamples,
                audioStreamIndex, true, true, cancellationToken).ConfigureAwait(false))
                yield return chunk;
        }
    }

    private static async IAsyncEnumerable<float[]> DecodeOnceAsync(
        string ffmpeg, string input, long startMicroseconds, int chunkSamples, int? audioStreamIndex,
        bool probeMore, bool discardPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var argument in BuildArguments(input, discardPrefix ? 0 : startMicroseconds, probeMore, audioStreamIndex))
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new AsrWorkerException("AUDIO_DECODE_ERROR", $"FFmpeg is required to decode ASR input: {exception.Message}");
        }

        // Cancellation must also kill a decoder blocked on a full stdout pipe while
        // the caller is running inference or has paused enumeration.
        using var registration = cancellationToken.Register(() => TryTerminate(process));
        var errorTask = ReadErrorTailAsync(process.StandardError);
        try
        {
            var skipSamples = discardPrefix ? (long)Math.Floor(startMicroseconds * (double)SampleRate / 1_000_000) : 0;
            await foreach (var chunk in ReadChunksAsync(process.StandardOutput.BaseStream, chunkSamples, skipSamples, cancellationToken).ConfigureAwait(false))
                yield return chunk;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new AsrWorkerException("AUDIO_DECODE_ERROR", string.IsNullOrWhiteSpace(error) ? "FFmpeg could not decode the media." : error.Trim());
        }
        finally
        {
            TryTerminate(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
        }
    }

    internal static async IAsyncEnumerable<float[]> ReadChunksAsync(
        Stream pcm, int chunkSamples, long skipSamples = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chunkSamples <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSamples));
        var buffer = new byte[checked(chunkSamples * sizeof(float))];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await pcm.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (count == 0) yield break;
            if (count % sizeof(float) != 0)
                throw new AsrWorkerException("AUDIO_DECODE_ERROR", "FFmpeg returned an incomplete float PCM frame.");
            var skipped = (int)Math.Min(skipSamples, count / sizeof(float));
            skipSamples -= skipped;
            if (skipped * sizeof(float) < count)
                yield return MemoryMarshal.Cast<byte, float>(buffer.AsSpan(skipped * sizeof(float), count - skipped * sizeof(float))).ToArray();
        }
    }

    internal static IReadOnlyList<string> BuildArguments(string input, long startMicroseconds, bool probeMore, int? audioStreamIndex)
    {
        if (audioStreamIndex < 0) throw new ArgumentOutOfRangeException(nameof(audioStreamIndex));
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (probeMore) arguments.AddRange(["-probesize", "100000000", "-analyzeduration", "10000000"]);
        arguments.AddRange(["-i", input]);
        if (startMicroseconds > 0)
            arguments.AddRange(["-ss", (startMicroseconds / 1_000_000d).ToString("0.######", CultureInfo.InvariantCulture)]);
        // ff-index is an absolute stream index, not the mpv track ID or audio ordinal.
        arguments.AddRange(["-map", audioStreamIndex is { } index ? $"0:{index}" : "0:a:0?",
            "-vn", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1"]);
        return arguments;
    }

    private static async Task<string> ReadErrorTailAsync(StreamReader reader)
    {
        const int maximum = 8192;
        var tail = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > maximum) tail.Remove(0, tail.Length - maximum);
        }
        return tail.ToString();
    }

    private static void TryTerminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
