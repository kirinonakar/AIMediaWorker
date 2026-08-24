using AIMediaWorker.Media;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIMediaWorker.History;

public sealed record RecentMediaItem(MediaSourceKind SourceType, string DisplayName, string Location, DateTimeOffset LastOpened, long LastPlaybackPositionMicroseconds);
public sealed record FavoriteItem(MediaSourceKind SourceType, string DisplayName, string Location, bool IsFolder, DateTimeOffset Added);

public sealed class MediaHistoryService
{
    private const int MaximumRecentItems = 20;
    private readonly string _recentPath;
    private readonly string _favoritesPath;
    private readonly SemaphoreSlim _recentLock = new(1, 1);
    private readonly SemaphoreSlim _favoritesLock = new(1, 1);
    private readonly Lazy<Task> _recentLoadTask;
    private readonly Lazy<Task> _favoritesLoadTask;
    private long _recentChangeVersion;
    private long _recentSavedVersion;
    private long _favoritesChangeVersion;
    private long _favoritesSavedVersion;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public MediaHistoryService(string recentPath, string favoritesPath)
    {
        _recentPath = Path.GetFullPath(recentPath);
        _favoritesPath = Path.GetFullPath(favoritesPath);
        _recentLoadTask = new Lazy<Task>(LoadRecentCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        _favoritesLoadTask = new Lazy<Task>(LoadFavoritesCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static MediaHistoryService CreateDefault()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIMediaWorker");
        return new MediaHistoryService(
            Path.Combine(folder, "recent.json"),
            Path.Combine(folder, "favorites.json"));
    }

    public List<RecentMediaItem> Recent { get; private set; } = [];
    public List<FavoriteItem> Favorites { get; private set; } = [];

    public Task LoadRecentAsync(CancellationToken cancellationToken = default) =>
        cancellationToken.CanBeCanceled ? _recentLoadTask.Value.WaitAsync(cancellationToken) : _recentLoadTask.Value;

    public Task LoadFavoritesAsync(CancellationToken cancellationToken = default) =>
        cancellationToken.CanBeCanceled ? _favoritesLoadTask.Value.WaitAsync(cancellationToken) : _favoritesLoadTask.Value;

    public void AddRecent(IMediaSource source, long positionMicroseconds, int limit)
    {
        var key = Normalize(source.Location);
        Recent.RemoveAll(item => Normalize(item.Location) == key);
        Recent.Insert(0, new RecentMediaItem(source.Kind, source.DisplayName, source.Location, DateTimeOffset.UtcNow, Math.Max(0, positionMicroseconds)));
        var effectiveLimit = Math.Clamp(limit, 1, MaximumRecentItems);
        if (Recent.Count > effectiveLimit) Recent.RemoveRange(effectiveLimit, Recent.Count - effectiveLimit);
        Interlocked.Increment(ref _recentChangeVersion);
    }

    public bool AddFavorite(IMediaSource source, bool isFolder = false)
    {
        var key = Normalize(source.Location);
        if (Favorites.Any(item => Normalize(item.Location) == key)) return false;
        var favorite = new FavoriteItem(source.Kind, source.DisplayName, source.Location, isFolder, DateTimeOffset.UtcNow);
        var insertionIndex = isFolder ? Favorites.FindIndex(item => !item.IsFolder) : -1;
        if (insertionIndex < 0) Favorites.Add(favorite);
        else Favorites.Insert(insertionIndex, favorite);
        Interlocked.Increment(ref _favoritesChangeVersion);
        return true;
    }

    public bool RemoveFavorite(string location)
    {
        var removed = Favorites.RemoveAll(item => Normalize(item.Location) == Normalize(location)) > 0;
        if (removed) Interlocked.Increment(ref _favoritesChangeVersion);
        return removed;
    }

    public bool ReorderFavorites(IEnumerable<string> orderedLocations)
    {
        var favoritesByLocation = Favorites.ToDictionary(item => Normalize(item.Location));
        var reordered = new List<FavoriteItem>(Favorites.Count);

        foreach (var location in orderedLocations)
        {
            if (favoritesByLocation.Remove(Normalize(location), out var favorite)) reordered.Add(favorite);
        }

        reordered.AddRange(Favorites.Where(item => favoritesByLocation.ContainsKey(Normalize(item.Location))));
        reordered = PutFoldersFirst(reordered);
        if (Favorites.SequenceEqual(reordered)) return false;
        Favorites = reordered;
        Interlocked.Increment(ref _favoritesChangeVersion);
        return true;
    }

    public async Task SaveRecentAsync(CancellationToken cancellationToken = default)
    {
        await LoadRecentAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _recentChangeVersion) == Volatile.Read(ref _recentSavedVersion)) return;
        await _recentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var versionToSave = Volatile.Read(ref _recentChangeVersion);
            if (versionToSave == Volatile.Read(ref _recentSavedVersion)) return;
            var snapshot = new RecentHistoryData(Recent.ToList());
            await WriteAsync(_recentPath, snapshot, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _recentSavedVersion, versionToSave);
        }
        finally { _recentLock.Release(); }
    }

    public async Task SaveFavoritesAsync(CancellationToken cancellationToken = default)
    {
        await LoadFavoritesAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _favoritesChangeVersion) == Volatile.Read(ref _favoritesSavedVersion)) return;
        await _favoritesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var versionToSave = Volatile.Read(ref _favoritesChangeVersion);
            if (versionToSave == Volatile.Read(ref _favoritesSavedVersion)) return;
            var snapshot = new FavoritesData(Favorites.ToList());
            await WriteAsync(_favoritesPath, snapshot, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _favoritesSavedVersion, versionToSave);
        }
        finally { _favoritesLock.Release(); }
    }

    private async Task LoadRecentCoreAsync()
    {
        await _recentLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(_recentPath))
            {
                var data = await ReadAsync<RecentHistoryData>(_recentPath).ConfigureAwait(false);
                Recent = (data?.Recent ?? []).Take(MaximumRecentItems).ToList();
            }
            Interlocked.Exchange(ref _recentChangeVersion, 0);
            Interlocked.Exchange(ref _recentSavedVersion, 0);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Recent = [];
        }
        finally { _recentLock.Release(); }
    }

    private async Task LoadFavoritesCoreAsync()
    {
        await _favoritesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(_favoritesPath))
            {
                var data = await ReadAsync<FavoritesData>(_favoritesPath).ConfigureAwait(false);
                Favorites = PutFoldersFirst(data?.Favorites ?? []);
            }
            Interlocked.Exchange(ref _favoritesChangeVersion, 0);
            Interlocked.Exchange(ref _favoritesSavedVersion, 0);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Favorites = [];
        }
        finally { _favoritesLock.Release(); }
    }

    private static async Task<T?> ReadAsync<T>(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false);
    }

    private static async Task WriteAsync<T>(string path, T data, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporaryPath, path, true);
    }

    private static List<FavoriteItem> PutFoldersFirst(IEnumerable<FavoriteItem> favorites)
    {
        var items = favorites.ToList();
        return items.Where(item => item.IsFolder).Concat(items.Where(item => !item.IsFolder)).ToList();
    }

    private static string Normalize(string location) => Uri.TryCreate(location, UriKind.Absolute, out var uri) && !uri.IsFile ? uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped).TrimEnd('/').ToUpperInvariant() : Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    private sealed record RecentHistoryData(List<RecentMediaItem> Recent);
    private sealed record FavoritesData(List<FavoriteItem> Favorites);
}
