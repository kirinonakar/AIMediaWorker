using System.Text;
using AIMediaWorker.Subtitle;

namespace AIMediaWorker.Tests;

public sealed class SubtitleSidecarTests
{
    [Theory]
    [InlineData("movie.SRT")]
    [InlineData("movie.smi")]
    [InlineData("movie.ass")]
    [InlineData("movie.ssa")]
    [InlineData("movie.vtt")]
    [InlineData("movie.ko.srt")]
    public void RecognizesSupportedCompanionFiles(string subtitle)
        => Assert.True(SubtitleSidecar.IsSidecarFor("Movie.mkv", subtitle));

    [Theory]
    [InlineData("movie2.srt")]
    [InlineData("movie.txt")]
    [InlineData("another.smi")]
    public void RejectsUnrelatedFiles(string subtitle)
        => Assert.False(SubtitleSidecar.IsSidecarFor("movie.mkv", subtitle));

    [Fact]
    public void PrefersExactNameThenFormatRegardlessOfDirectoryOrder()
    {
        string[] candidates = ["movie.ko.srt", "movie.smi", "movie.srt"];
        Assert.Equal("movie.srt", SubtitleSidecar.FindMatch("movie.mp4", candidates));
        Assert.Equal("movie.srt", SubtitleSidecar.FindMatch("movie.mp4", candidates.Reverse()));
        Assert.Equal("movie.smi", SubtitleSidecar.FindMatch("movie.mp4", candidates.Take(2)));
    }

    [Theory]
    [InlineData("movie.srt", "1\n00:00:01,000 --> 00:00:02,000\nHello\n")]
    [InlineData("movie.smi", "<SAMI><BODY><SYNC Start=1000><P>Hello<SYNC Start=2000><P>&nbsp;</BODY></SAMI>")]
    public void LoadedSubtitlesBlockAiWithoutRequiringASavePath(string path, string text)
    {
        var document = new SubtitleFileService().DecodeAndParse(path, Encoding.UTF8.GetBytes(text), "utf-8");
        Assert.Null(document.FilePath);
        Assert.True(document.HasCompletedFileSubtitles);
        document.ActiveTrack!.Cues[0].Text = "Edited";
        Assert.True(document.HasCompletedFileSubtitles);
        document.ActiveTrack.Cues.Clear();
        Assert.False(document.HasCompletedFileSubtitles);
    }

    [Fact]
    public void SavingGeneratedResultsDoesNotTurnThemIntoImportedSubtitles()
    {
        var document = new SubtitleDocument();
        document.EnsureTrack().Cues.Add(new SubtitleCue { Text = "Generated" });
        document.MarkSaved("generated.srt");
        Assert.False(document.HasCompletedFileSubtitles);
    }

    [Fact]
    public void EmptyImportedFileDoesNotBlockAi()
    {
        var document = new SubtitleFileService().DecodeAndParse("empty.srt", [], "utf-8");
        Assert.False(document.HasCompletedFileSubtitles);
    }
}
