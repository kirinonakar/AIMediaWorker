using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AIMediaWorker.Settings;

namespace AIMediaWorker.Network;

public interface ICredentialService
{
    void Save(string identifier, string username, string secret);
    (string Username, string Secret)? Read(string identifier);
    bool Delete(string identifier);
}

public static class CredentialIdentifier
{
    public static string ForWebDav(Guid serverId) => $"AIMediaWorker/WebDAV/{serverId:D}";
    public static string ForLlm(string provider) => $"AIMediaWorker/LLM/{Normalize(provider)}";
    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A provider name is required.", nameof(value));
        return string.Concat(value.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')).ToLowerInvariant();
    }
}

public sealed record WebDavConnectionCredential(string Address, int Port, string Username, string Password)
{
    public Uri RootUri
    {
        get
        {
            if (!Uri.TryCreate(Address, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("The WebDAV address must be an absolute HTTPS URL.", nameof(Address));
            if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port), "The WebDAV port must be between 1 and 65535.");
            var builder = new UriBuilder(address) { Port = Port };
            var uri = builder.Uri;
            return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
        }
    }

    public static string NormalizeAddress(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("The WebDAV address must be an absolute HTTPS URL.", nameof(uri));
        return new UriBuilder(uri) { Port = -1, UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    public static bool TryParseHttpsAddress(string? value, out Uri address)
    {
        address = null!;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (!text.Contains("://", StringComparison.Ordinal)) text = $"https://{text}";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(parsed.Host)) return false;
        address = parsed;
        return true;
    }
}

public sealed class WebDavCredentialStore(ICredentialService credentials)
{
    private readonly ICredentialService _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));

    public void Save(Guid serverId, WebDavConnectionCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _ = credential.RootUri;
        _credentials.Save(CredentialIdentifier.ForWebDav(serverId), string.Empty, JsonSerializer.Serialize(credential));
    }

    public WebDavConnectionCredential? Read(Guid serverId)
    {
        var stored = _credentials.Read(CredentialIdentifier.ForWebDav(serverId));
        if (stored is null) return null;
        try
        {
            var credential = JsonSerializer.Deserialize<WebDavConnectionCredential>(stored.Value.Secret);
            if (credential is null) return null;
            _ = credential.RootUri;
            return credential;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException) { return null; }
    }

    public bool Delete(Guid serverId) => _credentials.Delete(CredentialIdentifier.ForWebDav(serverId));

    public bool MigrateLegacy(WebDavServerSettings server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (string.IsNullOrWhiteSpace(server.LegacyUrl) || !Uri.TryCreate(server.LegacyUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        var legacy = _credentials.Read(CredentialIdentifier.ForWebDav(server.Id));
        Save(server.Id, new WebDavConnectionCredential(
            WebDavConnectionCredential.NormalizeAddress(uri),
            uri.Port,
            server.LegacyUsername ?? legacy?.Username ?? string.Empty,
            legacy?.Secret ?? string.Empty));
        server.LegacyUrl = null;
        server.LegacyUsername = null;
        return true;
    }
}

public sealed class WindowsCredentialService : ICredentialService
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public void Save(string identifier, string username, string secret)
    {
        ValidateIdentifier(identifier);
        ArgumentNullException.ThrowIfNull(secret);
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        if (secretBytes.Length > 5 * 512) throw new ArgumentException("The credential secret is too large.", nameof(secret));
        var blob = Marshal.AllocCoTaskMem(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = identifier,
                CredentialBlobSize = secretBytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = username ?? string.Empty
            };
            if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not save the credential.");
        }
        finally
        {
            if (secretBytes.Length > 0) { Array.Clear(secretBytes); }
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public (string Username, string Secret)? Read(string identifier)
    {
        ValidateIdentifier(identifier);
        if (!CredRead(identifier, CredTypeGeneric, 0, out var pointer))
        {
            const int ErrorNotFound = 1168;
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new System.ComponentModel.Win32Exception(error, "Windows Credential Manager could not read the credential.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var secret = credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0 ? string.Empty : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2) ?? string.Empty;
            return (credential.UserName ?? string.Empty, secret);
        }
        finally { CredFree(pointer); }
    }

    public bool Delete(string identifier)
    {
        ValidateIdentifier(identifier);
        if (CredDelete(identifier, CredTypeGeneric, 0)) return true;
        const int ErrorNotFound = 1168;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new System.ComponentModel.Win32Exception(error, "Windows Credential Manager could not delete the credential.");
    }

    private static void ValidateIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 256) throw new ArgumentException("A valid credential identifier is required.", nameof(identifier));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref NativeCredential credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, int type, int flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint buffer);
}
