using System.Xml.Linq;

namespace AIMediaWorker.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void EveryCultureContainsTheSameUniqueResourceKeys()
    {
        var strings = Path.Combine(FindRepositoryRoot(), "AIMediaWorker", "Strings");
        var cultures = new[] { "en-US", "ko-KR", "ja-JP" };
        var keySets = cultures.Select(culture => ReadKeys(Path.Combine(strings, culture, "Resources.resw"))).ToArray();

        Assert.NotEmpty(keySets[0]);
        Assert.Equal(keySets[0].ToArray(), keySets[1].ToArray());
        Assert.Equal(keySets[0].ToArray(), keySets[2].ToArray());
    }

    [Fact]
    public void SubtitleVisibilityMenuUsesLocalizedTextResource()
    {
        var root = FindRepositoryRoot();
        var xaml = XDocument.Load(Path.Combine(root, "AIMediaWorker", "MainWindow.xaml"));
        var menu = xaml.Descendants().Single(element =>
            element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "SubtitleVisibilityMenuItem"));
        Assert.Equal("SubtitleVisibility", menu.Attributes().Single(attribute => attribute.Name.LocalName == "Uid").Value);

        var expected = new Dictionary<string, string>
        {
            ["en-US"] = "Show subtitles",
            ["ko-KR"] = "자막 표시",
            ["ja-JP"] = "字幕を表示"
        };
        foreach (var (culture, value) in expected)
        {
            var resources = XDocument.Load(Path.Combine(root, "AIMediaWorker", "Strings", culture, "Resources.resw"));
            var text = resources.Root!.Elements("data")
                .Single(element => (string?)element.Attribute("name") == "SubtitleVisibility.Text")
                .Element("value")!.Value;
            Assert.Equal(value, text);
        }
    }

    private static SortedSet<string> ReadKeys(string path)
    {
        var keys = XDocument.Load(path).Root!.Elements("data").Select(element => (string?)element.Attribute("name")).Where(name => name is not null).Cast<string>().ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        return new SortedSet<string>(keys, StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AIMediaWorker.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
