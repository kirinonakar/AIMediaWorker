using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AIMediaWorker.Waveform;

public sealed class WaveformGenerator(string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe")
{
    public async Task<WaveformData> GenerateAsync(string source, int maximumPeaks = 100_000, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("A media source is required.", nameof(source));
        if (maximumPeaks is < 100 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumPeaks));
        var duration = await ProbeDurationAsync(source, cancellationToken).ConfigureAwait(false);
        const int sampleRate = 16_000;
        var totalSamples = Math.Max(1L, checked((long)Math.Ceiling(duration.TotalSeconds * sampleRate)));
        var samplesPerPeak = Math.Max(1L, checked((long)Math.Ceiling(totalSamples / (double)maximumPeaks)));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", source, "-vn", "-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "pipe:1" }
            },
            EnableRaisingEvents = true
        };
        if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
        using var registration = cancellationToken.Register(() => TryKill(process));
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var peaks = new List<WaveformPeak>((int)Math.Min(maximumPeaks, int.MaxValue));
        var buffer = new byte[64 * 1024];
        long sampleCount = 0;
        long bucketCount = 0;
        short minimum = short.MaxValue;
        short maximum = short.MinValue;
        try
        {
            while (true)
            {
                var bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0) break;
                var aligned = bytesRead - bytesRead % 2;
                for (var index = 0; index < aligned; index += 2)
                {
                    var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index, 2));
                    if (sample < minimum) minimum = sample;
                    if (sample > maximum) maximum = sample;
                    bucketCount++;
                    sampleCount++;
                    if (bucketCount < samplesPerPeak) continue;
                    peaks.Add(new WaveformPeak(minimum / 32768f, maximum / 32768f));
                    bucketCount = 0; minimum = short.MaxValue; maximum = short.MinValue;
                }
                progress?.Report(Math.Clamp(sampleCount / (double)totalSamples, 0, 1));
            }
            if (bucketCount > 0) peaks.Add(new WaveformPeak(minimum / 32768f, maximum / 32768f));
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"FFmpeg exited with code {process.ExitCode}." : error.Trim());
            progress?.Report(1);
            return new WaveformData(sampleRate, duration, peaks);
        }
        catch (OperationCanceledException) { TryKill(process); throw; }
    }

    private async Task<TimeSpan> ProbeDurationAsync(string source, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-v", "error", "-show_entries", "format=duration", "-of", "json", source }
            }
        };
        if (!process.Start()) throw new InvalidOperationException("FFprobe did not start.");
        using var registration = cancellationToken.Register(() => TryKill(process));
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException((await error.ConfigureAwait(false)).Trim());
        using var document = JsonDocument.Parse(await output.ConfigureAwait(false));
        var durationText = document.RootElement.GetProperty("format").GetProperty("duration").GetString();
        if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0) throw new InvalidDataException("FFprobe did not return a valid duration.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
}
