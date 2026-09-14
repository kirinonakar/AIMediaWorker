using AIMediaWorker.Subtitle;

namespace AIMediaWorker.Tests;

public sealed class SubtitleSaveTests
{
    [Theory]
    [InlineData("srt", SubtitleDisplayMode.Original)]
    [InlineData("srt", SubtitleDisplayMode.Translation)]
    [InlineData("vtt", SubtitleDisplayMode.OriginalAndTranslation)]
    [InlineData("ass", SubtitleDisplayMode.OriginalAndTranslation)]
    public async Task SavesBothLanguagesWithMediaNamesRegardlessOfDisplayMode(string extension, SubtitleDisplayMode mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aimw-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var service = new SubtitleFileService();
            var track = new SubtitleTrack();
            track.Cues.Add(new SubtitleCue
            {
                StartMicroseconds = 1_000_000, EndMicroseconds = 2_000_000,
                Text = "Original text", TranslatedText = "번역 자막"
            });
            var result = await service.SaveDocumentAsync(track, Path.Combine(directory, $"chosen.{extension}"),
                "https://example.com/videos/My%20Movie.mp4?token=ignored", mode, "Arial", "utf-8");
            Assert.Equal(Path.Combine(directory, $"My Movie.{extension}"), result.PrimaryPath);
            Assert.Equal(Path.Combine(directory, $"My Movie.original.{extension}"), result.OriginalPath);
            var translation = await service.LoadAsync(result.PrimaryPath, "utf-8");
            var original = await service.LoadAsync(result.OriginalPath!, "utf-8");
            Assert.Equal("번역 자막", Assert.Single(translation.ActiveTrack!.Cues).Text);
            Assert.Equal("Original text", Assert.Single(original.ActiveTrack!.Cues).Text);
            Assert.Equal(1_000_000, original.ActiveTrack.Cues[0].StartMicroseconds);
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OriginalOnlyKeepsChosenFilenameAndWritesOneFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aimw-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "chosen.srt");
            var track = new SubtitleTrack();
            track.Cues.Add(new SubtitleCue { StartMicroseconds = 0, EndMicroseconds = 1_000_000, Text = "Original" });
            var result = await new SubtitleFileService().SaveDocumentAsync(track, path, "movie.mp4",
                SubtitleDisplayMode.Original, "Arial", "utf-8");
            Assert.Equal(path, result.PrimaryPath);
            Assert.Null(result.OriginalPath);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
