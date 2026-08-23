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

public sealed record PlaybackError(string Code, string Message, Exception? Exception = null);
