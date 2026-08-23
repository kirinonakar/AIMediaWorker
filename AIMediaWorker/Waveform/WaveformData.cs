namespace AIMediaWorker.Waveform;

public readonly record struct WaveformPeak(float Minimum, float Maximum);

public sealed record WaveformData(int SampleRate, TimeSpan Duration, IReadOnlyList<WaveformPeak> Peaks)
{
    public static WaveformData Empty { get; } = new(16_000, TimeSpan.Zero, []);
}
