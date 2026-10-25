using AIMediaWorker.Playback;

namespace AIMediaWorker.Asr;

internal sealed record AsrAudioTrackSelection(string Input, int? StreamIndex)
{
    internal static AsrAudioTrackSelection Resolve(string input, IReadOnlyList<MediaTrack> tracks)
    {
        var selected = tracks.FirstOrDefault(track => track.Type == MediaTrackType.Audio && track.IsSelected);
        if (selected is null) return new(input, null);
        if (selected.IsExternal)
        {
            if (string.IsNullOrWhiteSpace(selected.ExternalFilename))
                throw new AsrWorkerException("AUDIO_TRACK_UNAVAILABLE", "The selected external audio track has no source filename.");
            return new(selected.ExternalFilename, selected.FfmpegStreamIndex is >= 0 ? selected.FfmpegStreamIndex : null);
        }
        if (selected.FfmpegStreamIndex is not >= 0)
            throw new AsrWorkerException("AUDIO_TRACK_UNAVAILABLE", "The selected audio track has no FFmpeg stream index. Reopen the media before generating subtitles.");
        return new(input, selected.FfmpegStreamIndex);
    }
}
