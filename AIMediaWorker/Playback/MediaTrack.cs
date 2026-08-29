using System.Globalization;

namespace AIMediaWorker.Playback;

public enum MediaTrackType { Video, Audio, Subtitle, Unknown }

public sealed record MediaTrack(
    int Id,
    MediaTrackType Type,
    string? Language,
    string? Title,
    string? Codec,
    bool IsDefault,
    bool IsForced,
    bool IsSelected)
{
    public string DisplayName => $"{Id}: {Title ?? Language ?? Codec ?? Type.ToString()}";
}

public enum PlaybackState { Uninitialized, Idle, Loading, Playing, Paused, Ended, Failed, Disposed }
public enum HardwareDecoder { Auto, D3D11VA, Nvdec, Off }
public enum HdrOutputMode { Off, Auto, On }
public enum RtxVideoSuperResolutionMode { Off, Auto, On }

/// <summary>
/// Maps the app's HDR output preference to mpv's swap-chain color-space hint.
/// Auto only signals HDR when Windows and the active display expose their color
/// capabilities; On always asks the D3D11 swap chain to carry color metadata.
/// </summary>
public static class HdrOutputOptions
{
    public static string GetColorspaceHint(HdrOutputMode mode) => mode switch
    {
        HdrOutputMode.Off => "no",
        HdrOutputMode.Auto => "auto",
        HdrOutputMode.On => "yes",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

/// <summary>
/// Builds the libmpv video filter that exposes the NVIDIA RTX Video Super Resolution
/// processing extension through the Direct3D 11 video processor.
/// </summary>
public static class RtxVideoSuperResolutionFilter
{
    public const string Label = "aimedia-rtx-vsr";
    public const double DefaultScaleFactor = 2.0;

    public static string? Build(RtxVideoSuperResolutionMode mode, double scaleFactor = DefaultScaleFactor)
    {
        if (mode == RtxVideoSuperResolutionMode.Off) return null;
        if (!double.IsFinite(scaleFactor) || scaleFactor <= 1.0)
            throw new ArgumentOutOfRangeException(nameof(scaleFactor), "RTX VSR requires a scale factor greater than 1.");

        var scale = scaleFactor.ToString("0.###", CultureInfo.InvariantCulture);
        return $"@{Label}:d3d11vpp=scale={scale}:scaling-mode=nvidia";
    }
}

public sealed record PlaybackError(string Code, string Message, Exception? Exception = null);
