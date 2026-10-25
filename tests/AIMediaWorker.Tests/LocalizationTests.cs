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
