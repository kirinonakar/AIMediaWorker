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
public enum RtxVideoSuperResolutionMode { Off, Auto, On }

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
