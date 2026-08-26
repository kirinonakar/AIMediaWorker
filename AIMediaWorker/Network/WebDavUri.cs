namespace AIMediaWorker.Network;

public static class WebDavUri
{
    public static Uri AsDirectory(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    public static bool Equals(Uri left, Uri right) =>
        left.AbsoluteUri.Equals(right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
}
