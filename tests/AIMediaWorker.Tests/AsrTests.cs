using AIMediaWorker.Asr;
using AIMediaWorker.Subtitle;

namespace AIMediaWorker.Tests;

public sealed class AsrTests
{
    [Fact]
    public void WorkerDirectoryOverrideRedirectsRuntimeAndModelPaths()
    {
        try
        {
            var custom = Path.Combine(Path.GetTempPath(), "AIMediaWorker.Tests", Guid.NewGuid().ToString("N"), "custom-asr");
            AsrRuntimePaths.SetWorkerDirectory(custom);
            Assert.Equal(Path.GetFullPath(custom), AsrRuntimePaths.WorkerDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "crispasr", "crispasr.dll"), AsrRuntimePaths.CrispAsrDllPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "ffmpeg", "ffmpeg.exe"), AsrRuntimePaths.FfmpegPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "models", AsrRuntimePaths.AsrModelFileName), AsrRuntimePaths.AsrModelPath);
            Assert.Equal(Path.GetFullPath(custom), AsrRuntimePaths.GetWorkerDirectory(AsrRuntimePaths.CrispAsrRuntimeDirectory));
            Assert.Equal(Path.Combine(Path.GetFullPath(custom), "models"), AsrRuntimePaths.GetModelsDirectory(AsrRuntimePaths.CrispAsrRuntimeDirectory));
        }
        finally
        {
            // Null restores the original default: the asr-worker folder beside the executable.
            AsrRuntimePaths.SetWorkerDirectory(null);
        }

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "asr-worker"), AsrRuntimePaths.WorkerDirectory);
    }

    [Fact]
    public void LiveCaptionStabilizerReturnsOnlyNewlyCommittedDelta()
    {
        var stabilizer = new LiveCaptionStabilizer(2_000_000);

        var first = stabilizer.UpdateState(LiveEvent("partial",
            Word("I", 500_000), Word("think", 1_500_000), Word("this", 2_500_000), Word("works", 3_500_000)));
        var second = stabilizer.UpdateState(LiveEvent("partial",
            Word("think", 1_500_000), Word("this", 2_500_000), Word("works", 3_500_000), Word("very", 4_500_000), Word("well.", 5_500_000)));

        Assert.Equal("I think", first.CommittedDelta);
        Assert.Equal("this works", second.CommittedDelta);
        Assert.Equal("very well.", second.UnstableText);
        Assert.Equal("I think this works very well.", second.DisplayText);
    }

    [Fact]
    public void LiveCaptionStabilizerFinalCommitsUnchangedVisibleTail()
    {
        var stabilizer = new LiveCaptionStabilizer(2_000_000);
        var partial = LiveEvent("partial", Word("hello", 500_000), Word("world", 1_500_000));
        stabilizer.UpdateState(partial);

        var final = stabilizer.UpdateState(LiveEvent("final", Word("hello", 500_000), Word("world", 1_500_000)));

        Assert.Equal("hello world", final.DisplayText);
        Assert.Equal("hello world", final.CommittedDelta);
        Assert.True(final.IsFinal);
        Assert.Empty(final.UnstableText);
    }

    [Fact]
    public void LiveCaptionStabilizerUsesFuzzyOverlapWhenTimestampsAreUnavailable()
    {
        var stabilizer = new LiveCaptionStabilizer();
        stabilizer.Update(new AsrEvent { Event = "partial", Text = "그 사람이 오늘 서울에 왔습니다" });

        var display = stabilizer.Update(new AsrEvent { Event = "partial", Text = "사람은 오늘 서울에 왔습니다 그리고 출발합니다" });

        Assert.Equal(1, CountOccurrences(display, "오늘 서울에 왔습니다"));
        Assert.EndsWith("그리고 출발합니다", display);
    }

    [Fact]
    public void LiveCaptionStabilizerIgnoresSmallAlignerBoundaryJitter()
    {
        var stabilizer = new LiveCaptionStabilizer(1_000_000);
        stabilizer.Update(LiveEvent("partial", Word("오늘", 500_000), Word("좋아요.", 1_500_000), Word("계속", 2_500_000)));

        var display = stabilizer.Update(LiveEvent("partial", Word("좋아요.", 1_650_000), Word("계속", 2_650_000), Word("갑시다.", 3_650_000)));

        Assert.Equal(1, CountOccurrences(display, "좋아요."));
    }

    [Fact]
    public void LiveCaptionStabilizerCommitsJapaneseTextForLiveTranslation()
    {
        var stabilizer = new LiveCaptionStabilizer(2_000_000, "ja");
        var source = new AsrSegment
        {
            StartMicroseconds = 0,
            EndMicroseconds = 4_000_000,
            Text = "おはよう。今日は晴れです。",
            Words =
            [
                Word("おはよう", 700_000), Word("。", 800_000),
                Word("今日", 1_500_000), Word("は", 1_700_000),
                Word("晴れ", 2_500_000), Word("です", 3_000_000), Word("。", 3_100_000)
            ]
        };

        var update = stabilizer.UpdateState(new AsrEvent { Event = "partial", Segments = [source] });

        Assert.Equal("おはよう。", update.CommittedDelta);
        Assert.Equal("今日は晴れです。", update.UnstableText);
        Assert.Equal(source.Text, update.DisplayText);
    }

    [Fact]
    public void LiveCaptionStabilizerPreservesKoreanWordSpacingWhenAlignerReturnsSubwords()
    {
        var stabilizer = new LiveCaptionStabilizer(language: "ko");
        var source = new AsrSegment
        {
            StartMicroseconds = 0,
            EndMicroseconds = 4_000_000,
            Text = "오늘 날씨가 정말 좋네요.",
            Words =
            [
                Word("오", 300_000), Word("늘", 600_000),
                Word("날", 1_000_000), Word("씨", 1_200_000), Word("가", 1_400_000),
                Word("정말", 2_200_000), Word("좋네요", 3_000_000), Word(".", 3_100_000)
            ]
        };

        var display = stabilizer.Update(new AsrEvent { Event = "partial", Segments = [source] });

        Assert.Equal(source.Text, display);
        Assert.DoesNotContain("오 늘", display, StringComparison.Ordinal);
        Assert.Contains("오늘 날씨가 정말 좋네요.", display, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveCaptionDisplaySegmenterRollsUnpunctuatedJapaneseText()
    {
        var source = string.Concat(Enumerable.Repeat("今日はとても良い天気なので公園へ散歩に行きます", 3));

        var sentences = LiveCaptionDisplaySegmenter.Split(source, "auto");

        Assert.True(sentences.Count >= 2);
        Assert.All(sentences, sentence => Assert.InRange(sentence.Length, 1, 32));
        Assert.Equal(source, string.Concat(sentences));
        Assert.DoesNotContain(sentences[0], sentences[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void LiveCaptionDisplaySegmenterPrefersJapaneseClauseAndAsrSegmentBoundaries()
    {
        var withClause = new string('あ', 20) + "、" + new string('い', 20);
        var clauseUnits = LiveCaptionDisplaySegmenter.Split(withClause, "ja");
        var firstSegment = new string('か', 8);
        var secondSegment = new string('き', 10);
        var segments = new[]
        {
            new AsrSegment { Text = firstSegment },
            new AsrSegment { Text = secondSegment }
        };

        var segmentUnits = LiveCaptionDisplaySegmenter.Split(firstSegment + secondSegment, "ja", segments);

        Assert.EndsWith("、", clauseUnits[0], StringComparison.Ordinal);
        Assert.Equal([firstSegment, secondSegment], segmentUnits);
    }

    [Fact]
    public void LiveCaptionDisplaySegmenterRecognizesJapanesePunctuationVariants()
    {
        const string source = "「本当⁉」次です｡まだ続く⁈終わり‥驚いた‼";

        var sentences = LiveCaptionDisplaySegmenter.Split(source, "ja");

        Assert.Equal(["「本当⁉」", "次です｡", "まだ続く⁈", "終わり‥", "驚いた‼"], sentences);
        Assert.True(SubtitlePunctuation.EndsSentence("「本当！？」"));
        Assert.True(SubtitlePunctuation.EndsSentence("本当⁇』"));
    }

    [Fact]
    public void LiveCaptionDisplaySegmenterDoesNotChunkUnpunctuatedEnglish()
    {
        var source = string.Join(' ', Enumerable.Repeat("continuous speech without punctuation", 4));

        var sentences = LiveCaptionDisplaySegmenter.Split(source, "en");

        Assert.Equal([source], sentences);
    }

    private static AsrWord Word(string text, long endMicroseconds) => new()
    {
        StartMicroseconds = Math.Max(0, endMicroseconds - 400_000),
        EndMicroseconds = endMicroseconds,
        Text = text
    };

    private static AsrEvent LiveEvent(string eventName, params AsrWord[] words) => new()
    {
        Event = eventName,
        Segments =
        [
            new AsrSegment
            {
                StartMicroseconds = words[0].StartMicroseconds,
                EndMicroseconds = words[^1].EndMicroseconds,
                Text = string.Join(' ', words.Select(word => word.Text)),
                Words = words
            }
        ]
    };

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}
