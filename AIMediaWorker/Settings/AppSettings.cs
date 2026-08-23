using AIMediaWorker.Playback;
using System.Text.Json.Serialization;

namespace AIMediaWorker.Settings;

public enum AppTheme { System, Light, Dark }
public enum AppLanguage { English, Korean, Japanese }
public enum AsrDevice { Auto, Cpu, Cuda }
public enum AsrPrecision { Auto, Float32, Float16, BFloat16, Int8 }
public enum ThinkingLevel { Default, Off, Low, Medium, High, XHigh, Max }

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public PlaybackSettings Playback { get; set; } = new();
    public SubtitleSettings Subtitle { get; set; } = new();
    public AsrSettings Asr { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public LlmSettings Llm { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public WindowLayoutSettings Window { get; set; } = new();
}

public sealed class PlaybackSettings
{
    public string Renderer { get; set; } = "gpu-next";
    public HardwareDecoder HardwareDecoder { get; set; } = HardwareDecoder.Auto;
    public RtxVideoSuperResolutionMode RtxVideoSuperResolution { get; set; } = RtxVideoSuperResolutionMode.Auto;
    public int? RtxQuality { get; set; }
    public double DefaultVolume { get; set; } = 100;
    public double PlaybackRate { get; set; } = 1;
    public double SeekIntervalSeconds { get; set; } = 5;
    public string? DefaultAudioLanguage { get; set; }
    public string? DefaultSubtitleLanguage { get; set; }
    public bool ShowSubtitles { get; set; } = true;
}

public sealed class SubtitleSettings
{
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 42;
    public string Color { get; set; } = "#FFFFFFFF";
    public string Background { get; set; } = "#80000000";
    public double Outline { get; set; } = 2;
    public int BottomMargin { get; set; } = 45;
    public string Encoding { get; set; } = "utf-8";
    public SegmentationSettings Segmentation { get; set; } = new();
}

public sealed class SegmentationSettings
{
    public double MinimumCueSeconds { get; set; } = 1;
    public double MaximumCueSeconds { get; set; } = 6;
    public int MaximumLines { get; set; } = 2;
    public int TargetCharactersPerLine { get; set; } = 24;
    public double SilenceSplitSeconds { get; set; } = 0.6;
    public double MaximumCharactersPerSecond { get; set; } = 20;
}

public sealed class AsrSettings
{
    public const string DefaultModelId = "Qwen/Qwen3-ASR-1.7B";
    public const string DefaultAlignerId = "Qwen/Qwen3-ForcedAligner-0.6B";

    public string? ModelPath { get; set; } = DefaultModelId;
    public string? AlignerPath { get; set; } = DefaultAlignerId;
    public string PythonExecutable { get; set; } = "python";
    public AsrDevice Device { get; set; } = AsrDevice.Auto;
    public AsrPrecision Precision { get; set; } = AsrPrecision.Auto;
    public string Language { get; set; } = "auto";
    public bool UseVad { get; set; } = true;
    public double ChunkDurationSeconds { get; set; } = 30;
}

public sealed class NetworkSettings
{
    public int TimeoutSeconds { get; set; } = 30;
    public string? Proxy { get; set; }
    public List<WebDavServerSettings> WebDavServers { get; set; } = [];
}

public sealed class WebDavServerSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Authentication { get; set; } = "Basic";

    // Read only for migrating settings created before connection details moved to Windows Credential Manager.
    [JsonPropertyName("Url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyUrl { get; set; }

    [JsonPropertyName("Username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyUsername { get; set; }
}

public sealed class CaptureSettings
{
    public string? CameraDeviceId { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int FrameRate { get; set; } = 30;
    public string CaptionFontFamily { get; set; } = "Segoe UI";
    public double CaptionFontSize { get; set; } = 32;
    public string CaptionTextColor { get; set; } = "#FFFFFFFF";
    public string CaptionBackgroundColor { get; set; } = "#A0000000";
    public string CaptionPosition { get; set; } = "Bottom";
    public int CaptionMaximumLines { get; set; } = 2;
}

public sealed class LlmSettings
{
    public string Provider { get; set; } = "Unsloth";
    public string? Model { get; set; }
    public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Default;
    public string TranslationLanguage { get; set; } = "English";
    public Dictionary<string, List<string>> CachedModels { get; set; } = [];
}

public sealed class GeneralSettings
{
    public AppLanguage Language { get; set; } = AppLanguage.English;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public int RecentMediaCount { get; set; } = 20;
    public bool ResumePlayback { get; set; } = true;
    public Dictionary<string, string> Shortcuts { get; set; } = ShortcutActions.CreateDefaults();
}

public sealed class WindowLayoutSettings
{
    public bool HasPlacement { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 820;
    public bool IsMaximized { get; set; }
    public bool IsRightPanelVisible { get; set; } = true;
    public bool IsBottomPanelVisible { get; set; } = true;
    public double RightPanelWidth { get; set; } = 360;
    public double BottomPanelHeight { get; set; } = 160;
}
