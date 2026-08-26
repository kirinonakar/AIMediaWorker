namespace AIMediaWorker.Asr;

/// <summary>Combines concurrent subtitle-generation and translation progress without UI dependencies.</summary>
public sealed class AiProgressTracker
{
    private readonly object _sync = new();
    private AiProgressSnapshot? _state;

    public event EventHandler<AiProgressChangedEventArgs>? ProgressChanged;

    public void Begin()
    {
        lock (_sync) _state = new AiProgressSnapshot(0, 0, 0, false, false);
    }

    public void End()
    {
        lock (_sync) _state = null;
    }

    public bool UpdateSubtitle(double progress) => Update(state => state with
    {
        SubtitleProgress = Math.Clamp(progress, 0d, 1d)
    });

    public bool CompleteSubtitle() => Update(state => state with { SubtitleGenerationComplete = true });

    public bool UpdateTranslation(int completed, int total) => Update(state => state with
    {
        TranslatedCount = Math.Max(0, completed),
        TranslationTotal = Math.Max(Math.Max(0, total), Math.Max(0, completed))
    });

    public bool CompleteTranslation(int completed, int total) => Update(state => state with
    {
        TranslatedCount = Math.Max(0, completed),
        TranslationTotal = Math.Max(Math.Max(0, total), Math.Max(0, completed)),
        TranslationComplete = true
    });

    private bool Update(Func<AiProgressSnapshot, AiProgressSnapshot> update)
    {
        AiProgressSnapshot snapshot;
        lock (_sync)
        {
            if (_state is null) return false;
            snapshot = update(_state);
            _state = snapshot;
        }
        ProgressChanged?.Invoke(this, new AiProgressChangedEventArgs(snapshot));
        return true;
    }
}

public sealed record AiProgressSnapshot(
    double SubtitleProgress,
    int TranslatedCount,
    int TranslationTotal,
    bool SubtitleGenerationComplete,
    bool TranslationComplete);

public sealed class AiProgressChangedEventArgs(AiProgressSnapshot progress) : EventArgs
{
    public AiProgressSnapshot Progress { get; } = progress;
}
