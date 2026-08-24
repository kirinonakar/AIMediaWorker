namespace AIMediaWorker.Subtitle;

public sealed class SubtitleTrack
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Default";
    public string? Language { get; set; }
    public string Format { get; set; } = "srt";
    public string? NativeHeader { get; set; }
    public RangeObservableCollection<SubtitleCue> Cues { get; } = [];
}
