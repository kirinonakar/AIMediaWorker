using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AIMediaWorker.Subtitle;

public sealed class SubtitleDocument : INotifyPropertyChanged
{
    private bool _isDirty;
    private string? _filePath;

    public SubtitleDocument()
    {
        Tracks.CollectionChanged += TracksChanged;
    }

    public ObservableCollection<SubtitleTrack> Tracks { get; } = [];
    public SubtitleTrack? ActiveTrack => Tracks.FirstOrDefault();
    public bool IsDirty { get => _isDirty; private set { if (_isDirty == value) return; _isDirty = value; PropertyChanged?.Invoke(this, new(nameof(IsDirty))); } }
    public string? FilePath { get => _filePath; set { if (_filePath == value) return; _filePath = value; PropertyChanged?.Invoke(this, new(nameof(FilePath))); } }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SubtitleTrack EnsureTrack(string format = "srt")
    {
        if (ActiveTrack is { } existing) return existing;
        var track = new SubtitleTrack { Format = format };
        Tracks.Add(track);
        return track;
    }

    public SubtitleCue? FindActiveCue(long positionMicroseconds)
    {
        var cues = ActiveTrack?.Cues;
        if (cues is null || cues.Count == 0) return null;
        var low = 0;
        var high = cues.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var cue = cues[mid];
            if (positionMicroseconds < cue.StartMicroseconds) high = mid - 1;
            else if (positionMicroseconds >= cue.EndMicroseconds) low = mid + 1;
            else return cue;
        }
        return null;
    }

    public void Sort() => Sort(ActiveTrack);

    public static void Sort(SubtitleTrack? track)
    {
        if (track is null) return;
        var ordered = track.Cues.OrderBy(c => c.StartMicroseconds).ThenBy(c => c.EndMicroseconds).ToArray();
        track.Cues.Clear();
        foreach (var cue in ordered) track.Cues.Add(cue);
    }

    public void MarkSaved(string? path = null)
    {
        if (path is not null) FilePath = path;
        IsDirty = false;
    }

    public void MarkDirty() => IsDirty = true;

    private void TracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (SubtitleTrack track in e.OldItems) track.Cues.CollectionChanged -= CuesChanged;
        if (e.NewItems is not null)
            foreach (SubtitleTrack track in e.NewItems) track.Cues.CollectionChanged += CuesChanged;
        MarkDirty();
        PropertyChanged?.Invoke(this, new(nameof(ActiveTrack)));
    }

    private void CuesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset && sender is IEnumerable<SubtitleCue> currentCues)
        {
            foreach (var cue in currentCues)
            {
                cue.PropertyChanged -= CueChanged;
                cue.PropertyChanged += CueChanged;
            }
        }
        if (e.OldItems is not null)
            foreach (SubtitleCue cue in e.OldItems) cue.PropertyChanged -= CueChanged;
        if (e.NewItems is not null)
            foreach (SubtitleCue cue in e.NewItems) cue.PropertyChanged += CueChanged;
        MarkDirty();
    }

    private void CueChanged(object? sender, PropertyChangedEventArgs e) => MarkDirty();
}
