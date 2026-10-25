namespace AIMediaWorker.Asr;

/// <summary>Retains an unfinished Japanese cue and an ambiguous single-kana prefix for the next chunk.</summary>
internal sealed class AsrSubtitleStreamSegmenter(AsrSegmentationOptions? options)
{
    private IReadOnlyList<AsrSegment> _pending = [];

    public IReadOnlyList<AsrSegment> Append(IReadOnlyList<AsrSegment> segments)
    {
        // An empty (e.g. VAD-gated) chunk is a real pause, so release the tail.
        if (segments.Count == 0) return Flush();

        var input = _pending.Concat(segments).ToArray();
        // Until the following text arrives, こ may start この rather than finish
        // the previous cue. Keep it separate so backward repair cannot consume it.
        var deferPrefix = options is not null && input.Length > 1 &&
            AsrSubtitleSegmenter.IsJapanesePrefixFragmentText(input[^1].Text);
        var result = AsrSubtitleSegmenter.Segment(deferPrefix ? input[..^1] : input, options);
        var committedCount = result.Count;
        if (options is not null && committedCount > 0 &&
            AsrSubtitleSegmenter.HasUnfinishedJapaneseText(result[^1].Text)) committedCount--;
        _pending = deferPrefix
            ? result.Skip(committedCount).Append(input[^1]).ToArray()
            : result.Skip(committedCount).ToArray();
        return result.Take(committedCount).ToArray();
    }

    public IReadOnlyList<AsrSegment> Flush()
    {
        var result = AsrSubtitleSegmenter.Segment(_pending, options);
        _pending = [];
        return result;
    }
}
