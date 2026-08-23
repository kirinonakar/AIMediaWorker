using System.Runtime.InteropServices;
using System.Text;

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
