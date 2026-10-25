namespace AIMediaWorker.Asr;

/// <summary>Retains one unfinished Japanese cue so a tail in the next audio chunk can join it.</summary>
internal sealed class AsrSubtitleStreamSegmenter(AsrSegmentationOptions? options)
{
    private AsrSegment? _pending;

    public IReadOnlyList<AsrSegment> Append(IReadOnlyList<AsrSegment> segments)
    {
        // An empty (e.g. VAD-gated) chunk is a real pause, so release the tail.
        if (segments.Count == 0) return Flush();

        var input = _pending is { } pending ? new[] { pending }.Concat(segments).ToArray() : segments;
        var result = AsrSubtitleSegmenter.Segment(input, options);
        _pending = null;
        if (options is not null && result.Count > 0 &&
            AsrSubtitleSegmenter.HasUnfinishedJapaneseText(result[^1].Text))
        {
            _pending = result[^1];
            return result.Take(result.Count - 1).ToArray();
        }
        return result;
    }

    public IReadOnlyList<AsrSegment> Flush()
    {
        var result = _pending is { } pending ? new[] { pending } : [];
        _pending = null;
        return result;
    }
}
