using AIMediaWorker.Asr;
using AIMediaWorker.Media;
using AIMediaWorker.Network;
using AIMediaWorker.Subtitle;

namespace AIMediaWorker.Tests;

public sealed class RefactoredServicesTests
{
    [Theory]
    [InlineData("movie.MKV", true)]
    [InlineData("audio.opus", true)]
    [InlineData("captions.srt", false)]
    public void MediaFileClassifierRecognizesPlayableFiles(string path, bool expected) =>
        Assert.Equal(expected, MediaFileClassifier.IsPlayable(path));

    [Theory]
    [InlineData("captions.SMI", true)]
    [InlineData("captions.ass", true)]
    [InlineData("movie.mp4", false)]
    public void MediaFileClassifierRecognizesSubtitleFiles(string path, bool expected) =>
        Assert.Equal(expected, MediaFileClassifier.IsSubtitle(path));

    [Fact]
    public void WebDavUriNormalizesDirectoriesAndComparesWithoutCase()
    {
        var directory = WebDavUri.AsDirectory(new Uri("https://example.test/Dav/Folder"));

        Assert.Equal("https://example.test/Dav/Folder/", directory.AbsoluteUri);
        Assert.True(WebDavUri.Equals(directory, new Uri("https://EXAMPLE.test/dav/folder/")));
    }

    [Fact]
    public void SubtitleFileServiceDecodesAndParsesByExtension()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        var document = new SubtitleFileService().DecodeAndParse("captions.srt", bytes, "utf-8");

        Assert.Equal("Hello", Assert.Single(document.ActiveTrack!.Cues).Text);
    }

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
