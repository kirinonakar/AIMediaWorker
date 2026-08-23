using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using AIMediaWorker.Settings;

namespace AIMediaWorker.Network;

public sealed record WebDavEntry(string Name, Uri Uri, bool IsCollection, long? ContentLength, DateTimeOffset? LastModified, string? ContentType)
{
    public string IconGlyph => IsCollection ? "\uE8B7" : "\uE8A5";
    public string SizeText => IsCollection || ContentLength is null ? string.Empty : FormatBytes(ContentLength.Value);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var display = (double)Math.Max(0, bytes);
        var unit = 0;
        while (display >= 1024 && unit < units.Length - 1) { display /= 1024; unit++; }
        return $"{display:0.##} {units[unit]}";
    }
}

public sealed class WebDavException(string code, string message, HttpStatusCode? statusCode = null, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class WebDavClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly WebDavCredentialStore _credentials;

    public WebDavClient(ICredentialService credentials, HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _credentials = new WebDavCredentialStore(credentials);
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All });
        _ownsClient = httpClient is null;
        _httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<WebDavEntry>> ListAsync(WebDavServerSettings server, Uri directory, CancellationToken cancellationToken = default)
    {
        var credential = ReadCredential(server);
        var root = credential.RootUri;
        EnsureWithinRoot(root, directory);
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), directory);
        request.Headers.TryAddWithoutValidation("Depth", "1");
        request.Content = new StringContent("<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:displayname/><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getcontenttype/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
        ApplyAuthentication(request, server, credential);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new WebDavException("AUTH_ERROR", "WebDAV authentication failed.", response.StatusCode);
        if ((int)response.StatusCode != 207 && !response.IsSuccessStatusCode) throw new WebDavException("NETWORK_ERROR", $"WebDAV listing failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        return ParseMultiStatus(document, directory).Where(entry => !UrisEquivalent(entry.Uri, directory)).OrderByDescending(entry => entry.IsCollection).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public HttpRequestMessage CreateMediaRequest(WebDavServerSettings server, Uri mediaUri, HttpMethod? method = null)
    {
        var credential = ReadCredential(server);
        var root = credential.RootUri;
        EnsureWithinRoot(root, mediaUri);
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, mediaUri);
        ApplyAuthentication(request, server, credential);
        return request;
    }

    public static Uri ResolveChild(Uri directory, string href)
    {
        if (!directory.IsAbsoluteUri) throw new ArgumentException("The directory URI must be absolute.", nameof(directory));
        return Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute : new Uri(directory, href);
    }

    public void Dispose() { if (_ownsClient) _httpClient.Dispose(); }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new WebDavException("NETWORK_ERROR", "The WebDAV request timed out."); }
        catch (HttpRequestException exception) { throw new WebDavException("NETWORK_ERROR", "The WebDAV server could not be reached.", exception.StatusCode, exception); }
    }

    private static void ApplyAuthentication(HttpRequestMessage request, WebDavServerSettings server, WebDavConnectionCredential credential)
    {
        if (!server.Authentication.Equals("Basic", StringComparison.OrdinalIgnoreCase)) throw new WebDavException("AUTH_ERROR", $"Unsupported WebDAV authentication mode: {server.Authentication}");
        var raw = Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Password}");
        try { request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw)); }
        finally { Array.Clear(raw); }
    }

    private WebDavConnectionCredential ReadCredential(WebDavServerSettings server) => _credentials.Read(server.Id) ?? throw new WebDavException("AUTH_ERROR", "WebDAV connection details are missing from Windows Credential Manager.");

    private static void EnsureWithinRoot(Uri root, Uri target)
    {
        if (!target.IsAbsoluteUri || !root.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase) || !root.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) || root.Port != target.Port || !target.AbsolutePath.StartsWith(root.AbsolutePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) && !UrisEquivalent(root, target))
            throw new WebDavException("NETWORK_ERROR", "The requested WebDAV URI is outside the configured server root.");
    }

    private static IReadOnlyList<WebDavEntry> ParseMultiStatus(XDocument document, Uri requestUri)
    {
        XNamespace dav = "DAV:";
        var entries = new List<WebDavEntry>();
        foreach (var response in document.Descendants(dav + "response"))
        {
            var href = response.Element(dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) continue;
            var prop = response.Descendants(dav + "prop").FirstOrDefault();
            if (prop is null) continue;
            var uri = ResolveChild(requestUri, href);
            var isCollection = prop.Element(dav + "resourcetype")?.Element(dav + "collection") is not null;
            var name = prop.Element(dav + "displayname")?.Value;
            if (string.IsNullOrWhiteSpace(name)) name = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? uri.Host);
            long? size = long.TryParse(prop.Element(dav + "getcontentlength")?.Value, out var parsedSize) ? parsedSize : null;
            DateTimeOffset? modified = DateTimeOffset.TryParse(prop.Element(dav + "getlastmodified")?.Value, out var parsedModified) ? parsedModified : null;
            entries.Add(new WebDavEntry(name, uri, isCollection, size, modified, prop.Element(dav + "getcontenttype")?.Value));
        }
        return entries;
    }

    private static bool UrisEquivalent(Uri left, Uri right) => left.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped).TrimEnd('/').Equals(right.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
