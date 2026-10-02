using AIMediaWorker.Media;

namespace AIMediaWorker.Tests;

public sealed class LocalBrowserPathTests
{
    [Fact]
    public void VirtualRootHasNoParentAndContainsOnlyTheRootBreadcrumb()
    {
        Assert.Null(LocalBrowserPath.GetParent("/"));
        Assert.Equal(new LocalBrowserBreadcrumb("/", "/"), Assert.Single(LocalBrowserPath.GetBreadcrumbs("/")));
        Assert.True(LocalBrowserPath.AreSameDirectory("/", "/"));
        Assert.False(LocalBrowserPath.AreSameDirectory("/", @"C:\"));
    }

    [Fact]
    public void DriveRootNavigatesUpToVirtualRoot()
    {
        Assert.Equal("/", LocalBrowserPath.GetParent(@"C:\"));
        Assert.Equal(["/", @"C:\"], LocalBrowserPath.GetBreadcrumbs(@"C:\").Select(entry => entry.Label));
    }

    [Theory]
    [InlineData(@"C:\Media\Shows\Season 01", @"C:\", @"C:\Media\Shows")]
    [InlineData(@"\\server\share\Media\Shows", @"\\server\share", @"\\server\share\Media")]
    public void AllAncestorsHaveNavigablePathsInRootToCurrentOrder(string path, string driveRoot, string parent)
    {
        var entries = LocalBrowserPath.GetBreadcrumbs(path);

        Assert.Equal("/", entries[0].Path);
        Assert.Equal(driveRoot, entries[1].Path);
        Assert.Equal(path, entries[^1].Path);
        Assert.Equal(parent, entries[^2].Path);
        Assert.Equal(parent, LocalBrowserPath.GetParent(path));
        Assert.All(entries.Skip(2), entry => Assert.Equal(Path.GetFileName(entry.Path), entry.Label));
    }

    [Theory]
    [InlineData(340, 0)]
    [InlineData(339, 1)]
    [InlineData(280, 2)]
    [InlineData(100, 3)]
    [InlineData(10, 3)]
    public void OverflowHidesOnlyAncestorsAndKeepsTheCurrentLocation(double width, int expectedHiddenCount)
    {
        double[] widths = [50, 50, 80, 160];

        var hiddenCount = LocalBrowserPath.GetHiddenBreadcrumbCount(widths, width, overflowWidth: 40);

        Assert.Equal(expectedHiddenCount, hiddenCount);
        Assert.True(hiddenCount < widths.Length);
    }

    [Fact]
    public void ResizingRestoresPreviouslyHiddenAncestors()
    {
        var entries = LocalBrowserPath.GetBreadcrumbs(@"C:\Media\Shows");
        double[] widths = [50, 50, 80, 160];
        var hiddenCount = LocalBrowserPath.GetHiddenBreadcrumbCount(widths, 280, 40);

        Assert.Equal(["/", @"C:\"], entries.Take(hiddenCount).Select(entry => entry.Path));
        Assert.Equal(0, LocalBrowserPath.GetHiddenBreadcrumbCount(widths, 400, 40));
    }
}
