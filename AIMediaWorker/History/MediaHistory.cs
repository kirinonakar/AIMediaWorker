using AIMediaWorker.Media;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIMediaWorker.History;

public sealed record RecentMediaItem(MediaSourceKind SourceType, string DisplayName, string Location, DateTimeOffset LastOpened, long LastPlaybackPositionMicroseconds);
public sealed record FavoriteItem(MediaSourceKind SourceType, string DisplayName, string Location, bool IsFolder, DateTimeOffset Added);

public sealed class MediaHistoryService
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public MediaHistoryService(string path) => _path = Path.GetFullPath(path);
    public List<RecentMediaItem> Recent { get; private set; } = [];
    public List<FavoriteItem> Favorites { get; private set; } = [];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return;
            await using var stream = File.OpenRead(_path);
            var data = await JsonSerializer.DeserializeAsync<HistoryData>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            Recent = data?.Recent ?? [];
            Favorites = data?.Favorites ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Recent = []; Favorites = [];
        }
        finally { _lock.Release(); }
    }

    public void AddRecent(IMediaSource source, long positionMicroseconds, int limit)
    {
        var key = Normalize(source.Location);
        Recent.RemoveAll(item => Normalize(item.Location) == key);
        Recent.Insert(0, new RecentMediaItem(source.Kind, source.DisplayName, source.Location, DateTimeOffset.UtcNow, Math.Max(0, positionMicroseconds)));
        if (Recent.Count > Math.Max(1, limit)) Recent.RemoveRange(Math.Max(1, limit), Recent.Count - Math.Max(1, limit));
    }

    public void AddFavorite(IMediaSource source, bool isFolder = false)
    {
        var key = Normalize(source.Location);
        if (Favorites.Any(item => Normalize(item.Location) == key)) return;
        Favorites.Add(new FavoriteItem(source.Kind, source.DisplayName, source.Location, isFolder, DateTimeOffset.UtcNow));
    }

    public bool RemoveFavorite(string location) => Favorites.RemoveAll(item => Normalize(item.Location) == Normalize(location)) > 0;

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, new HistoryData(Recent, Favorites), JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, true);
        }
        finally { _lock.Release(); }
    }

    private static string Normalize(string location) => Uri.TryCreate(location, UriKind.Absolute, out var uri) && !uri.IsFile ? uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped).TrimEnd('/').ToUpperInvariant() : Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    private sealed record HistoryData(List<RecentMediaItem> Recent, List<FavoriteItem> Favorites);
}
