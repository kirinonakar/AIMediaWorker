using AIMediaWorker.Media;
using AIMediaWorker.Network;
using AIMediaWorker.Settings;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AIMediaWorker.Tests;

public sealed class WebDavCertificateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidCertificateRequiresConnectDecisionBeforeSendingCredentials(bool connect)
    {
        await using var server = new TlsWebDavServer();
        var credentials = new MemoryCredentials();
        var settings = new WebDavServerSettings();
        new WebDavCredentialStore(credentials).Save(settings.Id,
            new WebDavConnectionCredential(server.RootUri.GetLeftPart(UriPartial.Path), server.RootUri.Port, "user", "password"));
        var prompt = new TaskCompletionSource<WebDavCertificateWarning>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptCount = 0;
        using var client = new WebDavClient(credentials, confirmCertificateAsync: warning =>
        {
            Interlocked.Increment(ref promptCount);
            prompt.TrySetResult(warning);
            return decision.Task;
        });

        var listing = client.ListAsync(settings, server.RootUri);
        if (await Task.WhenAny(listing, prompt.Task).WaitAsync(TimeSpan.FromSeconds(15)) == listing) await listing;
        var warning = await prompt.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(listing.IsCompleted);
        Assert.Equal(0, server.RequestCount);
        Assert.Equal(server.RootUri.Authority, warning.ServerUri.Authority);
        Assert.True(warning.Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.True(warning.Errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(server.Certificate.GetCertHashString(HashAlgorithmName.SHA256), warning.Fingerprint);
        decision.SetResult(connect);

        if (!connect)
        {
            var exception = await Assert.ThrowsAsync<WebDavException>(() => listing);
            Assert.Equal("CERTIFICATE_REJECTED", exception.Code);
            Assert.Equal(0, server.RequestCount);
            return;
        }

        Assert.Single(await listing);
        await client.CheckConnectionAsync(settings, new Uri(server.RootUri, "test.mp4"));
        Assert.Equal("subtitle", Encoding.UTF8.GetString(await client.DownloadAsync(settings, new Uri(server.RootUri, "test.srt"))));
        var path = await new RemoteMediaDownloadService().DownloadAsync(new Uri(server.RootUri, "test.mp4").AbsoluteUri,
            new Dictionary<string, string> { ["Authorization"] = "Basic dXNlcjpwYXNzd29yZA==" }, null, 15,
            CancellationToken.None, client.CreateHttpHandler());
        try { Assert.Equal("subtitle", await File.ReadAllTextAsync(path)); }
        finally { File.Delete(path); }
        Assert.Equal(1, promptCount);
        Assert.Equal(4, server.RequestCount);
        Assert.All(server.Requests, request => Assert.Contains("Authorization: Basic dXNlcjpwYXNzd29yZA==", request));
    }

    [Fact]
    public void ApprovalIsScopedToCertificateAndAuthorityAndValidCertificatesNeedNoPrompt()
    {
        using var first = CreateCertificate();
        using var second = CreateCertificate();
        var count = 0;
        var connect = true;
        using var client = new WebDavClient(new MemoryCredentials(), confirmCertificateAsync: _ =>
        {
            count++;
            return Task.FromResult(connect);
        });
        using var handler = client.CreateHttpHandler();
        var validate = handler.ServerCertificateCustomValidationCallback!;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:8443/root/");
        Assert.True(validate(request, first, null, SslPolicyErrors.None));
        Assert.Equal(0, count);
        Assert.True(validate(request, first, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(validate(request, first, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(1, count);
        Assert.True(validate(request, second, null, SslPolicyErrors.RemoteCertificateChainErrors));
        using var other = new HttpRequestMessage(HttpMethod.Get, "https://localhost:9443/root/");
        Assert.True(validate(other, first, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(3, count);

        connect = false;
        using var rejected = new HttpRequestMessage(HttpMethod.Get, "https://localhost:10443/root/");
        Assert.False(validate(rejected, first, null, SslPolicyErrors.RemoteCertificateChainErrors));
        connect = true;
        Assert.False(validate(rejected, first, null, SslPolicyErrors.RemoteCertificateChainErrors));
        client.ResetRejectedCertificates();
        Assert.True(validate(rejected, first, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(5, count);
    }

    [Fact]
    public void InvalidCertificateIsRejectedWithoutConfirmationHandler()
    {
        using var client = new WebDavClient(new MemoryCredentials());
        using var handler = client.CreateHttpHandler();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost/root/");
        Assert.False(handler.ServerCertificateCustomValidationCallback!(request, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public async Task OrdinaryRemoteDownloadStillRejectsInvalidCertificates()
    {
        await using var server = new TlsWebDavServer();
        await Assert.ThrowsAsync<HttpRequestException>(() => new RemoteMediaDownloadService().DownloadAsync(
            new Uri(server.RootUri, "test.mp4").AbsoluteUri, new Dictionary<string, string>(), null, 15, CancellationToken.None));
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task ConcurrentCertificateChecksShareOnePendingDecision()
    {
        using var certificate = CreateCertificate();
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var client = new WebDavClient(new MemoryCredentials(), confirmCertificateAsync: _ =>
        {
            Interlocked.Increment(ref count);
            prompt.TrySetResult();
            return decision.Task;
        });
        using var handler = client.CreateHttpHandler();
        var checks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost/root/");
            return handler.ServerCertificateCustomValidationCallback!(request, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);
        })).ToArray();
        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.DoesNotContain(checks, check => check.IsCompleted);
        decision.SetResult(true);
        Assert.All(await Task.WhenAll(checks).WaitAsync(TimeSpan.FromSeconds(15)), accepted => Assert.True(accepted));
        Assert.Equal(1, count);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=different.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        // Schannel needs an imported private key for the local TLS server.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private sealed class TlsWebDavServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public X509Certificate2 Certificate { get; } = CreateCertificate();
        public Uri RootUri { get; }
        public List<string> Requests { get; } = [];
        public int RequestCount => Requests.Count;

        public TlsWebDavServer()
        {
            _listener.Start();
            RootUri = new Uri($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/root/");
            _loop = RunAsync();
        }

        private async Task RunAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var tls = new SslStream(connection.GetStream());
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = Certificate,
                        EnabledSslProtocols = SslProtocols.Tls12
                    }, _stop.Token);
                    using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                    var request = new StringBuilder();
                    var firstLine = await reader.ReadLineAsync(_stop.Token);
                    if (firstLine is null) continue;
                    request.AppendLine(firstLine);
                    var contentLength = 0;
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                    {
                        request.AppendLine(line);
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            contentLength = int.Parse(line[15..].Trim());
                    }
                    if (contentLength > 0)
                        await reader.ReadBlockAsync(new char[contentLength], _stop.Token);
                    Requests.Add(request.ToString());
                    var isListing = firstLine.StartsWith("PROPFIND ", StringComparison.Ordinal);
                    var body = isListing
                        ? "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/root/test.srt</d:href><d:propstat><d:prop><d:displayname>test.srt</d:displayname></d:prop></d:propstat></d:response></d:multistatus>"
                        : "subtitle";
                    var response = Encoding.UTF8.GetBytes($"HTTP/1.1 {(isListing ? "207 Multi-Status" : "200 OK")}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                    await tls.WriteAsync(response, _stop.Token);
                }
                catch (Exception exception) when (exception is AuthenticationException or IOException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; }
            catch (OperationCanceledException) { }
            Certificate.Dispose();
            _stop.Dispose();
        }
    }

    private sealed class MemoryCredentials : ICredentialService
    {
        private readonly Dictionary<string, (string Username, string Secret)> _values = [];
        public void Save(string identifier, string username, string secret) => _values[identifier] = (username, secret);
        public (string Username, string Secret)? Read(string identifier) => _values.TryGetValue(identifier, out var value) ? value : null;
        public bool Delete(string identifier) => _values.Remove(identifier);
    }
}
