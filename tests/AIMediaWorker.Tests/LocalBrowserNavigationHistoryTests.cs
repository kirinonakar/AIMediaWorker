using AIMediaWorker.Media;

namespace AIMediaWorker.Tests;

public sealed class LocalBrowserNavigationHistoryTests
{
    [Fact]
    public void EmptyAndInitialHistoryHaveNoBackOrForwardTarget()
    {
        var history = new LocalBrowserNavigationHistory();
        Assert.Null(history.GetTarget(false));
        Assert.Null(history.GetTarget(true));

        history.Record(@"C:\Media");

        Assert.Null(history.GetTarget(false));
        Assert.Null(history.GetTarget(true));
    }

    [Fact]
    public void BackAndForwardFollowVisitsIncludingVirtualRootAndDrives()
    {
        var history = new LocalBrowserNavigationHistory();
        history.Record("/");
        history.Record(@"C:\");
        history.Record(@"C:\Media");

        Assert.Equal(@"C:\", Move(history, forward: false));
        Assert.Equal("/", Move(history, forward: false));
        Assert.Null(history.GetTarget(false));
        Assert.Equal(@"C:\", Move(history, forward: true));
        Assert.Equal(@"C:\Media", Move(history, forward: true));
        Assert.Null(history.GetTarget(true));
    }

    [Fact]
    public void RefreshAndEquivalentPathsPreserveForwardHistory()
    {
        var history = new LocalBrowserNavigationHistory();
        history.Record(@"C:\Media");
        history.Record(@"C:\Shows");
        Move(history, forward: false);

        history.Record(@"c:\media\");

        Assert.Null(history.GetTarget(false));
        Assert.Equal(@"C:\Shows", Move(history, forward: true));
    }

    [Fact]
    public void NewFolderAfterGoingBackReplacesForwardBranch()
    {
        var history = new LocalBrowserNavigationHistory();
        history.Record(@"C:\Media");
        history.Record(@"C:\Shows");
        Move(history, forward: false);

        history.Record(@"D:\Music");

        Assert.Null(history.GetTarget(true));
        Assert.Equal(@"C:\Media", Move(history, forward: false));
        Assert.Equal(@"D:\Music", Move(history, forward: true));
    }

    [Fact]
    public void FailedNavigationLeavesHistoryPositionUnchanged()
    {
        var history = new LocalBrowserNavigationHistory();
        history.Record(@"C:\Media");
        history.Record(@"C:\Shows");

        // Looking up a request does not commit it; the browser can fail to load it.
        var failedTarget = history.GetTarget(false);

        Assert.Equal(failedTarget, history.GetTarget(false));
        Assert.Null(history.GetTarget(true));
        Assert.Equal(@"C:\Media", Move(history, forward: false));
    }

    [Fact]
    public void SupersededOrAlreadyCommittedRequestsCannotChangePosition()
    {
        var history = new LocalBrowserNavigationHistory();
        history.Record(@"C:\Media");
        history.Record(@"C:\Shows");
        var supersededTarget = Assert.IsType<LocalBrowserHistoryTarget>(history.GetTarget(false));
        history.Record(@"D:\Music");

        Assert.False(history.TryCommit(supersededTarget));
        var target = Assert.IsType<LocalBrowserHistoryTarget>(history.GetTarget(false));
        Assert.True(history.TryCommit(target));
        Assert.False(history.TryCommit(target));
        Assert.Equal(@"D:\Music", history.GetTarget(true)?.Directory);
        Assert.Equal(@"C:\Media", history.GetTarget(false)?.Directory);
    }

    private static string Move(LocalBrowserNavigationHistory history, bool forward)
    {
        var target = Assert.IsType<LocalBrowserHistoryTarget>(history.GetTarget(forward));
        Assert.True(history.TryCommit(target));
        return target.Directory;
    }
}
