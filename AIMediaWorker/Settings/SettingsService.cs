using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIMediaWorker.Settings;

public sealed class SettingsService
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsService(string path) => _path = Path.GetFullPath(path);

    public static SettingsService CreateDefault()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIMediaWorker");
        return new SettingsService(Path.Combine(folder, "settings.json"));
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Normalize(await JsonSerializer.DeserializeAsync<AppSettings>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            TryPreserveCorruptFile();
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporaryPath, _path, true);
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            var backup = $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(_path, backup, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        var loadedSchemaVersion = settings.SchemaVersion;
        settings.Playback ??= new PlaybackSettings();
        settings.Subtitle ??= new SubtitleSettings();
        if (loadedSchemaVersion < 3 && string.Equals(settings.Subtitle.FontFamily?.Trim(), "Segoe UI", StringComparison.OrdinalIgnoreCase))
            settings.Subtitle.FontFamily = SubtitleSettings.DefaultFontFamily;
        settings.Subtitle.FontFamily = string.IsNullOrWhiteSpace(settings.Subtitle.FontFamily) ? SubtitleSettings.DefaultFontFamily : settings.Subtitle.FontFamily.Trim();
        settings.Subtitle.Segmentation ??= new SegmentationSettings();
        settings.Asr ??= new AsrSettings();
        settings.Asr.ModelPath = string.IsNullOrWhiteSpace(settings.Asr.ModelPath) ? AsrSettings.DefaultModelId : settings.Asr.ModelPath.Trim();
        settings.Asr.AlignerPath = string.IsNullOrWhiteSpace(settings.Asr.AlignerPath) ? AsrSettings.DefaultAlignerId : settings.Asr.AlignerPath.Trim();
        settings.Network ??= new NetworkSettings();
        settings.Network.WebDavServers ??= [];
        settings.Capture ??= new CaptureSettings();
        settings.Llm ??= new LlmSettings();
        if (string.Equals(settings.Llm.Provider, "Unsloth", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(settings.Llm.Provider)) settings.Llm.Provider = "Unsloth Desktop";
        settings.Llm.CachedModels ??= [];
        settings.General ??= new GeneralSettings();
        settings.Window ??= new WindowLayoutSettings();
        settings.General.Shortcuts ??= [];
        if (settings.General.Shortcuts.TryGetValue(ShortcutActions.PreviousMedia, out var previousMedia) &&
            settings.General.Shortcuts.TryGetValue(ShortcutActions.NextMedia, out var nextMedia) &&
            string.Equals(previousMedia, "Ctrl+F", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(nextMedia, "Ctrl+B", StringComparison.OrdinalIgnoreCase))
        {
            settings.General.Shortcuts[ShortcutActions.PreviousMedia] = "Ctrl+B";
            settings.General.Shortcuts[ShortcutActions.NextMedia] = "Ctrl+F";
        }
        foreach (var item in ShortcutActions.CreateDefaults()) settings.General.Shortcuts.TryAdd(item.Key, item.Value);
        settings.General.RecentMediaCount = Math.Clamp(settings.General.RecentMediaCount, 1, 20);
        settings.Network.TimeoutSeconds = Math.Clamp(settings.Network.TimeoutSeconds, 5, 300);
        settings.Playback.SeekIntervalSeconds = Math.Clamp(settings.Playback.SeekIntervalSeconds, 1, 60);
        settings.Window.Width = Math.Clamp(settings.Window.Width, 640, 7680);
        settings.Window.Height = Math.Clamp(settings.Window.Height, 420, 4320);
        settings.Window.RightPanelWidth = Math.Clamp(settings.Window.RightPanelWidth, 240, 1200);
        settings.Window.BottomPanelHeight = Math.Clamp(settings.Window.BottomPanelHeight, 100, 800);
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return settings;
    }
}
