namespace AIMediaWorker.Media;

public enum MediaSourceKind { LocalFile, Http, WebDav, Camera }

public interface IMediaSource
{
    MediaSourceKind Kind { get; }
    string DisplayName { get; }
    string Location { get; }
    bool IsLive { get; }
}

public sealed record LocalMediaSource(string Path) : IMediaSource
{
    public MediaSourceKind Kind => MediaSourceKind.LocalFile;
    public string DisplayName => System.IO.Path.GetFileName(Path);
    public string Location => Path;
    public bool IsLive => false;
}

public sealed record HttpMediaSource(Uri Uri) : IMediaSource
{
    public MediaSourceKind Kind => MediaSourceKind.Http;
    public string DisplayName => Uri.Segments.LastOrDefault()?.Trim('/') is { Length: > 0 } name ? Uri.UnescapeDataString(name) : Uri.Host;
    public string Location => Uri.AbsoluteUri;
    public bool IsLive => Uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
}

public sealed record WebDavMediaSource(Guid ServerId, Uri Uri, string Name) : IMediaSource
{
    public MediaSourceKind Kind => MediaSourceKind.WebDav;
    public string DisplayName => Name;
    public string Location => Uri.AbsoluteUri;
    public bool IsLive => false;
}

public sealed record CameraMediaSource(string DeviceId, string Name) : IMediaSource
{
    public MediaSourceKind Kind => MediaSourceKind.Camera;
    public string DisplayName => Name;
    public string Location => DeviceId;
    public bool IsLive => true;
}

public static class MediaSourceFactory
{
    public static IMediaSource Parse(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return new HttpMediaSource(uri);
        if (System.IO.Path.IsPathFullyQualified(source)) return new LocalMediaSource(source);
        throw new ArgumentException("The media source must be an absolute local path or HTTP/HTTPS URL.", nameof(source));
    }
}
