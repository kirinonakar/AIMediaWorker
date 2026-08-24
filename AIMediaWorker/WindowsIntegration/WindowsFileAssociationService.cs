using Microsoft.Win32;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace AIMediaWorker.WindowsIntegration;

/// <summary>
/// Registers the unpackaged build as a supported Windows file handler.
/// Packaged builds use Package.appxmanifest instead.
/// </summary>
public sealed class WindowsFileAssociationService
{
    public const string ApplicationName = "AIMediaWorker";

    public static IReadOnlyList<string> SupportedExtensions { get; } =
    [
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".m2ts",
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus"
    ];

    public bool IsPackaged
    {
        get
        {
            try
            {
                _ = Package.Current.Id.Name;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    public void Register(IEnumerable<string> extensions)
    {
        if (IsPackaged) return;

        var selectedExtensions = extensions
            .Select(NormalizeExtension)
            .Where(SupportedExtensions.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedExtensions.Length == 0) throw new ArgumentException("At least one supported extension is required.", nameof(extensions));

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)) throw new InvalidOperationException("The application executable path is unavailable.");

        var executableName = Path.GetFileName(executablePath);
        var icon = $"{executablePath},0";
        var openCommand = $"\"{executablePath}\" \"%1\"";

        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes", writable: true)
            ?? throw new InvalidOperationException("Unable to open the current user's file association registry.");
        using var application = classes.CreateSubKey($@"Applications\{executableName}", writable: true);
        application?.SetValue("FriendlyAppName", ApplicationName);
        application?.SetValue("ApplicationDescription", "Play video and audio files with AIMediaWorker.");
        application?.CreateSubKey("DefaultIcon")?.SetValue(null, icon);
        application?.CreateSubKey(@"shell\open\command")?.SetValue(null, openCommand);

        using var supportedTypes = application?.CreateSubKey("SupportedTypes", writable: true);
        using var capabilities = Registry.CurrentUser.CreateSubKey($@"Software\{ApplicationName}\Capabilities", writable: true);
        capabilities?.SetValue("ApplicationName", ApplicationName);
        capabilities?.SetValue("ApplicationDescription", "Play video and audio files with AIMediaWorker.");
        capabilities?.SetValue("ApplicationIcon", icon);
        using var fileAssociations = capabilities?.CreateSubKey("FileAssociations", writable: true);

        foreach (var extension in SupportedExtensions.Except(selectedExtensions, StringComparer.OrdinalIgnoreCase))
        {
            var progId = GetProgId(extension);
            supportedTypes?.DeleteValue(extension, throwOnMissingValue: false);
            fileAssociations?.DeleteValue(extension, throwOnMissingValue: false);
            using (var openWithProgIds = classes.OpenSubKey($@"{extension}\OpenWithProgids", writable: true))
                openWithProgIds?.DeleteValue(progId, throwOnMissingValue: false);
            classes.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
        }

        foreach (var extension in selectedExtensions)
        {
            var progId = GetProgId(extension);
            supportedTypes?.SetValue(extension, string.Empty);
            fileAssociations?.SetValue(extension, progId);

            using var progIdKey = classes.CreateSubKey(progId, writable: true);
            progIdKey?.SetValue(null, $"{ApplicationName} {extension.TrimStart('.').ToUpperInvariant()} media");
            progIdKey?.SetValue("FriendlyTypeName", $"{ApplicationName} {extension.TrimStart('.').ToUpperInvariant()} media");
            progIdKey?.CreateSubKey("DefaultIcon")?.SetValue(null, icon);
            progIdKey?.CreateSubKey(@"shell\open\command")?.SetValue(null, openCommand);

            using var openWithProgIds = classes.CreateSubKey($@"{extension}\OpenWithProgids", writable: true);
            openWithProgIds?.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using var registeredApplications = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications", writable: true);
        registeredApplications?.SetValue(ApplicationName, $@"Software\{ApplicationName}\Capabilities");
        NotifyAssociationChanged();
    }

    public bool IsRegistered(string extension)
    {
        if (IsPackaged) return SupportedExtensions.Contains(NormalizeExtension(extension));
        using var fileAssociations = Registry.CurrentUser.OpenSubKey($@"Software\{ApplicationName}\Capabilities\FileAssociations");
        return fileAssociations?.GetValue(NormalizeExtension(extension)) is string;
    }

    public Uri GetDefaultAppsSettingsUri()
    {
        if (IsPackaged)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                try
                {
                    var appUserModelId = AppInfo.Current.AppUserModelId;
                    if (!string.IsNullOrWhiteSpace(appUserModelId))
                        return new Uri($"ms-settings:defaultapps?registeredAUMID={Uri.EscapeDataString(appUserModelId)}");
                }
                catch (Exception exception) when (exception is InvalidOperationException or COMException)
                {
                }
            }

            return new Uri("ms-settings:defaultapps");
        }

        return new Uri($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(ApplicationName)}");
    }

    private static string NormalizeExtension(string extension)
    {
        var normalized = extension.Trim().ToLowerInvariant();
        return normalized.StartsWith('.') ? normalized : $".{normalized}";
    }

    private static string GetProgId(string extension) => $"{ApplicationName}{NormalizeExtension(extension)}";

    private static void NotifyAssociationChanged() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
