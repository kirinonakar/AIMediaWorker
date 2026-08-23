using AIMediaWorker.Waveform;

namespace AIMediaWorker.Tests;

public sealed class WaveformTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AIMediaWorker.WaveformTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CacheRoundTripPreservesPeaks()
    {
        Directory.CreateDirectory(_folder);
        var media = Path.Combine(_folder, "sample.bin");
        await File.WriteAllBytesAsync(media, [1, 2, 3]);
        var cache = new WaveformCache(Path.Combine(_folder, "cache"));
        var data = new WaveformData(16000, TimeSpan.FromSeconds(2), [new(-0.5f, 0.75f), new(-1f, 1f)]);
        await cache.SaveAsync(media, data);
        var loaded = await cache.TryLoadAsync(media);
        Assert.NotNull(loaded);
        Assert.Equal(data.Duration, loaded.Duration);
        Assert.Equal(data.Peaks, loaded.Peaks);
        await File.AppendAllTextAsync(media, "changed");
        Assert.Null(await cache.TryLoadAsync(media));
    }

    [Fact]
    public async Task CacheRemovesOldEntriesInsteadOfGrowingWithoutLimit()
    {
        var directory = Path.Combine(_folder, "bounded-cache");
        var cache = new WaveformCache(directory, maximumEntries: 2);
        var data = new WaveformData(16000, TimeSpan.FromSeconds(1), [new(-0.5f, 0.5f)]);

        await cache.SaveAsync("https://example.test/one.mp4", data);
        await Task.Delay(20);
        await cache.SaveAsync("https://example.test/two.mp4", data);
        await Task.Delay(20);
        await cache.SaveAsync("https://example.test/three.mp4", data);

        Assert.Equal(2, Directory.GetFiles(directory, "*.waveform").Length);
        Assert.Null(await cache.TryLoadAsync("https://example.test/one.mp4"));
        Assert.NotNull(await cache.TryLoadAsync("https://example.test/three.mp4"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FfmpegGeneratesBoundedWaveformFromWav()
    {
        Directory.CreateDirectory(_folder);
        var wav = Path.Combine(_folder, "tone.wav");
        WriteToneWav(wav, 16000, 1.0);
        var data = await new WaveformGenerator().GenerateAsync(wav, maximumPeaks: 1000);
        Assert.InRange(data.Peaks.Count, 1, 1000);
        Assert.InRange(data.Duration.TotalSeconds, 0.99, 1.01);
        Assert.Contains(data.Peaks, peak => peak.Minimum < -0.4f && peak.Maximum > 0.4f);
    }

    private static void WriteToneWav(string path, int sampleRate, double seconds)
    {
        var samples = (int)(sampleRate * seconds);
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVE"u8); writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        for (var index = 0; index < samples; index++) writer.Write((short)(Math.Sin(index * 2 * Math.PI * 440 / sampleRate) * short.MaxValue * 0.7));
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
