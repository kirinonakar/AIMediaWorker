namespace AIMediaWorker.Media;

internal sealed record LocalBrowserHistoryTarget(string Directory, int Index, int Version);

internal sealed class LocalBrowserNavigationHistory
{
    private readonly List<string> _directories = [];
    private int _index = -1;
    private int _version;

    public void Record(string directory)
    {
        var fullPath = directory == LocalBrowserPath.Root ? directory : Path.GetFullPath(directory);
        if (_index >= 0 && LocalBrowserPath.AreSameDirectory(_directories[_index], fullPath)) return;

        _directories.RemoveRange(_index + 1, _directories.Count - _index - 1);
        _directories.Add(fullPath);
        _index = _directories.Count - 1;
        _version++;
    }

    public LocalBrowserHistoryTarget? GetTarget(bool forward)
    {
        var targetIndex = _index + (forward ? 1 : -1);
        return targetIndex >= 0 && targetIndex < _directories.Count
            ? new LocalBrowserHistoryTarget(_directories[targetIndex], targetIndex, _version)
            : null;
    }

    // Commit only after the target folder loads successfully. Failed or superseded
    // requests must leave both the back and forward histories intact.
    public bool TryCommit(LocalBrowserHistoryTarget target)
    {
        if (target.Version != _version || Math.Abs(target.Index - _index) != 1
            || target.Index < 0 || target.Index >= _directories.Count
            || !LocalBrowserPath.AreSameDirectory(_directories[target.Index], target.Directory)) return false;

        _index = target.Index;
        _version++;
        return true;
    }
}
