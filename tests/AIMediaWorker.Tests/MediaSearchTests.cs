using AIMediaWorker.Media;

namespace AIMediaWorker.Tests;

public sealed class MediaSearchTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AIMediaWorker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LocalSearchFindsPlayableMediaInSubfolders()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_folder, "Shows", "Season 01"));
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "Episode 01.MKV"), "test");
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "Episode 01.OPUS"), "test");
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "Episode 01.txt"), "ignored");

        var results = await LocalMediaSearchService.SearchAsync(_folder, "episode", useRegex: false);

        Assert.Equal(
            new[] { "Episode 01.MKV", "Episode 01.OPUS" }.Select(name => Path.Combine("Shows", "Season 01", name)),
            results.Where(item => !item.IsDirectory).Select(item => item.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task LocalSearchSupportsRegularExpressionsAgainstRelativePaths()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_folder, "Concerts", "2026"));
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "live-final.mp4"), "test");
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "live-draft.mp4"), "test");

        var results = await LocalMediaSearchService.SearchAsync(_folder, @"Concerts[\\/]2026[\\/]live-final\.mp4$", useRegex: true);

        Assert.Single(results, item => !item.IsDirectory);
    }

    [Fact]
    public void SearchPatternRejectsInvalidRegularExpression()
    {
        Assert.ThrowsAny<ArgumentException>(() => SearchPatternMatcher.Create("[", useRegex: true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
