using AIMediaWorker.Asr;

namespace AIMediaWorker.Tests;

public sealed class AiProgressTrackerTests
{
    [Fact]
    public void AiProgressTrackerCombinesBothOperations()
    {
        var tracker = new AiProgressTracker();
        AiProgressSnapshot? latest = null;
        tracker.ProgressChanged += (_, args) => latest = args.Progress;

        tracker.Begin();
        Assert.True(tracker.UpdateSubtitle(0.5));
        Assert.True(tracker.UpdateTranslation(2, 5));
        Assert.True(tracker.CompleteSubtitle());

        Assert.NotNull(latest);
        Assert.Equal(0.5, latest.SubtitleProgress);
        Assert.Equal(2, latest.TranslatedCount);
        Assert.True(latest.SubtitleGenerationComplete);

        tracker.End();
        Assert.False(tracker.UpdateSubtitle(0.75));
    }
}
