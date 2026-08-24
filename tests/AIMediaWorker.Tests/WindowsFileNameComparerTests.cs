using AIMediaWorker.Media;

namespace AIMediaWorker.Tests;

public sealed class WindowsFileNameComparerTests
{
    [Fact]
    public void SortsNumericFileNameSegmentsLikeWindowsExplorer()
    {
        string[] names = ["clip10.mkv", "clip2.mkv", "clip1.mkv", "clip20.mkv", "clip11.mkv"];

        var sorted = names.OrderBy(name => name, WindowsFileNameComparer.Instance).ToArray();

        Assert.Equal(["clip1.mkv", "clip2.mkv", "clip10.mkv", "clip11.mkv", "clip20.mkv"], sorted);
    }
}
