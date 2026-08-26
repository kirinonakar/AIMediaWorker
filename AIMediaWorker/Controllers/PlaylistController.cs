using AIMediaWorker.Media;
using AIMediaWorker.Network;
using Microsoft.UI.Xaml.Controls;

namespace AIMediaWorker.Controllers;

/// <summary>Owns playlist entries, selection, navigation boundaries, and their UI projection.</summary>
internal sealed class PlaylistController
{
    private readonly ListView _list;
    private readonly Button _previousButton;
    private readonly Button _nextButton;
    private readonly Func<PlaylistEntry, Task> _openEntryAsync;
    private readonly List<PlaylistEntry> _entries = [];
    private int _currentIndex = -1;

    public PlaylistController(
        ListView list,
        Button previousButton,
        Button nextButton,
        Func<PlaylistEntry, Task> openEntryAsync)
    {
        _list = list;
        _previousButton = previousButton;
        _nextButton = nextButton;
        _openEntryAsync = openEntryAsync;
        RefreshView();
    }

    public void SetOpenedMedia(IMediaSource source, bool preservePlaylist)
    {
        if (preservePlaylist) return;
        _entries.Clear();
        if (source is LocalMediaSource localSource)
        {
            _entries.Add(PlaylistEntry.FromLocal(localSource.Path));
            _currentIndex = 0;
        }
        else
        {
            _currentIndex = -1;
        }
        RefreshView();
    }

    public bool ReplaceLocalFiles(IEnumerable<string> paths, string? currentPath = null)
    {
        _entries.Clear();
        _entries.AddRange(paths.Select(PlaylistEntry.FromLocal));
        _currentIndex = currentPath is null
            ? (_entries.Count > 0 ? 0 : -1)
            : _entries.FindIndex(entry => entry.Path.Equals(currentPath, StringComparison.OrdinalIgnoreCase));
        RefreshView();
        return _currentIndex >= 0;
    }

    public void ReplaceWebDavEntries(
        Guid serverId,
        IEnumerable<WebDavEntry> entries,
        IReadOnlyDictionary<string, string>? headers,
        Uri currentUri)
    {
        _entries.Clear();
        _entries.AddRange(entries.Select(entry => PlaylistEntry.FromWebDav(serverId, entry, headers)));
        _currentIndex = _entries.FindIndex(item => WebDavUri.Equals(new Uri(item.Path), currentUri));
        if (_currentIndex < 0 && _entries.Count > 0) _currentIndex = 0;
        RefreshView();
    }

    public Task OpenCurrentAsync() =>
        _currentIndex >= 0 && _currentIndex < _entries.Count
            ? _openEntryAsync(_entries[_currentIndex])
            : Task.CompletedTask;

    public Task OpenPreviousAsync() => OpenAdjacentAsync(-1);
    public Task OpenNextAsync() => OpenAdjacentAsync(1);

    public Task AutoAdvanceAsync() =>
        _currentIndex >= 0 && _currentIndex < _entries.Count - 1
            ? OpenAdjacentAsync(1)
            : Task.CompletedTask;

    public async Task OpenItemAsync(object? item)
    {
        if (item is not PlaylistEntry entry) return;
        var index = _entries.IndexOf(entry);
        if (index < 0) return;
        _currentIndex = index;
        RefreshView();
        await _openEntryAsync(entry);
    }

    public void Clear()
    {
        _entries.Clear();
        _currentIndex = -1;
        RefreshView();
    }

    private async Task OpenAdjacentAsync(int direction)
    {
        if (_entries.Count == 0) return;
        var next = _currentIndex + Math.Sign(direction);
        if (next < 0 || next >= _entries.Count) return;
        _currentIndex = next;
        RefreshView();
        await _openEntryAsync(_entries[_currentIndex]);
    }

    private void RefreshView()
    {
        _previousButton.IsEnabled = _entries.Count > 1 && _currentIndex > 0;
        _nextButton.IsEnabled = _entries.Count > 1 && _currentIndex >= 0 && _currentIndex < _entries.Count - 1;
        _list.ItemsSource = _entries.ToArray();
        _list.SelectedIndex = _currentIndex;
    }
}

internal sealed record PlaylistEntry(
    string Path,
    string DisplayName,
    IReadOnlyDictionary<string, string>? HttpHeaders = null,
    IMediaSource? MediaSource = null)
{
    public string SourceIconGlyph => MediaSource?.Kind == MediaSourceKind.WebDav ? "\uE774" : string.Empty;

    public static PlaylistEntry FromLocal(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        return new PlaylistEntry(fullPath, System.IO.Path.GetFileName(fullPath));
    }

    public static PlaylistEntry FromWebDav(Guid serverId, WebDavEntry entry, IReadOnlyDictionary<string, string>? headers) =>
        new(entry.Uri.AbsoluteUri, entry.Name, headers, new WebDavMediaSource(serverId, entry.Uri, entry.Name));
}
