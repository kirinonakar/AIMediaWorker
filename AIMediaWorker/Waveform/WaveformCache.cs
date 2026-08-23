using System.Security.Cryptography;
using System.Text;

namespace AIMediaWorker.Waveform;

public sealed class WaveformCache
{
    private const int Magic = 0x57464D41; // AMFW
    private const int Version = 1;
    private readonly string _directory;
    private readonly int _maximumEntries;

    public WaveformCache(string directory, int maximumEntries = 32)
    {
        if (maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _directory = Path.GetFullPath(directory);
        _maximumEntries = maximumEntries;
    }

    public async Task<WaveformData?> TryLoadAsync(string source, CancellationToken cancellationToken = default)
    {
        var fingerprint = CreateFingerprint(source);
        var path = GetCachePath(fingerprint);
        if (!File.Exists(path)) { Trim(); return null; }
        try
        {
            WaveformData data;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var reader = new BinaryReader(stream, Encoding.UTF8, true);
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version || reader.ReadString() != fingerprint) return null;
                var sampleRate = reader.ReadInt32();
                var duration = TimeSpan.FromTicks(reader.ReadInt64());
                var count = reader.ReadInt32();
                if (sampleRate <= 0 || count is < 0 or > 1_000_000) return null;
                var peaks = new WaveformPeak[count];
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    peaks[index] = new WaveformPeak(reader.ReadSingle(), reader.ReadSingle());
                }
                data = new WaveformData(sampleRate, duration, peaks);
            }
            TryTouch(path);
            Trim();
            return data;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or UnauthorizedAccessException) { return null; }
    }

    public async Task SaveAsync(string source, WaveformData data, CancellationToken cancellationToken = default)
    {
        var fingerprint = CreateFingerprint(source);
        Directory.CreateDirectory(_directory);
        var path = GetCachePath(fingerprint);
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
                writer.Write(Magic); writer.Write(Version); writer.Write(fingerprint); writer.Write(data.SampleRate); writer.Write(data.Duration.Ticks); writer.Write(data.Peaks.Count);
                foreach (var peak in data.Peaks) { cancellationToken.ThrowIfCancellationRequested(); writer.Write(peak.Minimum); writer.Write(peak.Maximum); }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
            Trim();
        }
        finally { TryDelete(temporary); }
    }

    public static string CreateFingerprint(string source)
    {
        string identity;
        if (File.Exists(source))
        {
            var info = new FileInfo(source);
            identity = $"file|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        else identity = $"remote|{source}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private string GetCachePath(string fingerprint) => Path.Combine(_directory, fingerprint + ".waveform");

    private void Trim()
    {
        try
        {
            var stale = new DirectoryInfo(_directory).EnumerateFiles("*.waveform", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(_maximumEntries)
                .ToArray();
            foreach (var file in stale) TryDelete(file.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }

    private static void TryTouch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
