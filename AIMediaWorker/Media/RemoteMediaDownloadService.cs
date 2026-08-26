using System.Net;

namespace AIMediaWorker.Media;

/// <summary>Downloads authenticated remote media to an owned temporary file for ASR.</summary>
public sealed class RemoteMediaDownloadService
{
    public async Task<string> DownloadAsync(
        string source,
        IReadOnlyDictionary<string, string> headers,
        string? proxy,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        if (Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri))
        {
            handler.Proxy = new WebProxy(proxyUri);
            handler.UseProxy = true;
        }
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var headerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerCancellation.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 300)));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCancellation.Token);
        response.EnsureSuccessStatusCode();

        var extension = Path.GetExtension(new Uri(source).AbsolutePath);
        if (extension.Length is 0 or > 12 || extension.Any(character => !char.IsLetterOrDigit(character) && character != '.')) extension = ".media";
        var path = Path.Combine(Path.GetTempPath(), $"aimw-asr-{Guid.NewGuid():N}{extension}");
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
    }
}
