using AIMediaWorker.Asr;

namespace AIMediaWorker.Tests;

public sealed class AsrSegmentationTests
{
    private static readonly AsrSegmentationOptions DefaultOptions = new(1, 6, 2, 24, 0.6, 20);

    [Theory]
    [InlineData(0)]
    [InlineData(700_000)]
    public void JapaneseInflectionIsNotSplitByReadingSpeedOrAlignmentGap(long gap)
    {
        var source = new AsrSegment
        {
            Text = "それに、朝より悪くなってる。",
            EndMicroseconds = 1_700_000 + gap,
            Words =
            [
                new AsrWord { Text = "それに", EndMicroseconds = 300_000 },
                new AsrWord { Text = "朝より", StartMicroseconds = 300_000, EndMicroseconds = 800_000 },
                new AsrWord { Text = "悪くな", StartMicroseconds = 800_000, EndMicroseconds = 1_300_000 },
                new AsrWord { Text = "って", StartMicroseconds = 1_300_000 + gap, EndMicroseconds = 1_500_000 + gap },
                new AsrWord { Text = "る", StartMicroseconds = 1_500_000 + gap, EndMicroseconds = 1_700_000 + gap }
            ]
        };

        var cue = Assert.Single(AsrSubtitleSegmenter.Segment([source], DefaultOptions with { MaximumCharactersPerSecond = 5 }));

        Assert.Equal(source.Text, cue.Text);
        Assert.Equal(source.EndMicroseconds, cue.EndMicroseconds);
        Assert.Equal(source.Words, cue.Words);
    }

    [Theory]
    [InlineData("る。")]
    [InlineData("る")]
    public void JapaneseNativeInflectionFragmentsAreJoinedWithTimingAndWordsPreserved(string ending)
    {
        var fragments = JapaneseFragments(ending);

        var cue = Assert.Single(AsrSubtitleSegmenter.Segment(fragments, DefaultOptions));

        Assert.Equal("それに、朝より悪くなって" + ending, cue.Text);
        Assert.Equal(fragments[0].StartMicroseconds, cue.StartMicroseconds);
        Assert.Equal(fragments[^1].EndMicroseconds, cue.EndMicroseconds);
        Assert.Equal(fragments.SelectMany(fragment => fragment.Words!), cue.Words!);
        Assert.Equal(0.8, cue.Confidence!.Value, 8);
    }

    [Theory]
    [InlineData("前の文。", "って", 1_400_000)]
    [InlineData("それに、朝より悪くな", "って", 3_000_000)]
    [InlineData("それに、朝より悪くな", "別の文。", 1_400_000)]
    [InlineData("English", "る。", 1_400_000)]
    public void ContinuationRepairPreservesSentenceEndsLongPausesAndOtherText(string previous, string next, long nextStart)
    {
        AsrSegment[] source =
        [
            new() { Text = previous, EndMicroseconds = 1_300_000 },
            new() { Text = next, StartMicroseconds = nextStart, EndMicroseconds = nextStart + 300_000 }
        ];

        var result = AsrSubtitleSegmenter.Segment(source, DefaultOptions);

        Assert.Equal([previous, next], result.Select(cue => cue.Text));
    }

    [Fact]
    public void JapaneseContinuationRepairRespectsCharacterAndDurationLimits()
    {
        var fragments = JapaneseFragments("る。");
        var characterLimited = AsrSubtitleSegmenter.Segment(fragments,
            DefaultOptions with { MaximumLines = 1, TargetCharactersPerLine = 11 });
        var durationLimited = AsrSubtitleSegmenter.Segment(fragments,
            DefaultOptions with { MaximumCueSeconds = 1.5 });

        Assert.True(characterLimited.Count > 1);
        Assert.All(characterLimited, cue => Assert.InRange(cue.Text.Length, 1, 11));
        Assert.True(durationLimited.Count > 1);
        Assert.All(durationLimited, cue => Assert.InRange(cue.EndMicroseconds - cue.StartMicroseconds, 1, 1_500_000));
        Assert.Equal("それに、朝より悪くなってる。", string.Concat(characterLimited.Select(cue => cue.Text)));
        Assert.Equal("それに、朝より悪くなってる。", string.Concat(durationLimited.Select(cue => cue.Text)));
    }

    [Fact]
    public void JapaneseInflectionCanContinueAcrossAudioChunks()
    {
        var fragments = JapaneseFragments("る。");
        var segmenter = new AsrSubtitleStreamSegmenter(DefaultOptions);

        Assert.Empty(segmenter.Append([fragments[0]]));
        Assert.Empty(segmenter.Append([fragments[1]]));
        var cue = Assert.Single(segmenter.Append([fragments[2]]));

        Assert.Equal("それに、朝より悪くなってる。", cue.Text);
        Assert.Equal(fragments[0].StartMicroseconds, cue.StartMicroseconds);
        Assert.Equal(fragments[^1].EndMicroseconds, cue.EndMicroseconds);
        Assert.Equal(fragments.SelectMany(fragment => fragment.Words!), cue.Words!);
        Assert.Empty(segmenter.Flush());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingJapaneseCueIsEmittedAtEndOfFileOrSilentChunk(bool silence)
    {
        var source = JapaneseFragments("る。")[0];
        var segmenter = new AsrSubtitleStreamSegmenter(DefaultOptions);
        Assert.Empty(segmenter.Append([source]));

        var result = silence ? segmenter.Append([]) : segmenter.Flush();

        Assert.Equal(source.Text, Assert.Single(result).Text);
        Assert.Empty(segmenter.Flush());
    }

    [Fact]
    public void StreamingEmitsCompletedCuesImmediatelyAndFlushesUnrelatedPendingCue()
    {
        var segmenter = new AsrSubtitleStreamSegmenter(DefaultOptions);
        var complete = new AsrSegment { Text = "おはよう。", EndMicroseconds = 1_000_000 };
        var pending = JapaneseFragments("る。")[0];

        Assert.Equal(complete.Text, Assert.Single(segmenter.Append([complete])).Text);
        Assert.Empty(segmenter.Append([pending]));
        var next = complete with { Text = "次の文です。", StartMicroseconds = 2_000_000, EndMicroseconds = 3_000_000 };
        Assert.Equal([pending.Text, next.Text], segmenter.Append([next]).Select(cue => cue.Text));
        Assert.Empty(segmenter.Flush());
    }

    private static AsrSegment[] JapaneseFragments(string ending) =>
        new[] { ("それに、朝より悪くな", 0L, 1_300_000L), ("って", 1_400_000L, 1_600_000L), (ending, 1_650_000L, 1_800_000L) }
            .Select(fragment => new AsrSegment
            {
                Text = fragment.Item1, StartMicroseconds = fragment.Item2, EndMicroseconds = fragment.Item3,
                Confidence = 0.8,
                Words = [new AsrWord { Text = fragment.Item1, StartMicroseconds = fragment.Item2, EndMicroseconds = fragment.Item3 }]
            }).ToArray();

    [Theory]
    [InlineData("AI와  LLM을 함께 사용합니다")]
    [InlineData("Hello, world! 다음 문장입니다.")]
    public void CharacterTimestampsPreserveTranscriptSpacing(string text)
    {
        var words = text.Where(character => !char.IsWhiteSpace(character))
            .Select((character, index) => new AsrWord
            {
                Text = character.ToString(),
                StartMicroseconds = index * 100_000L,
                EndMicroseconds = (index + 1) * 100_000L
            }).ToArray();
        var source = new AsrSegment
        {
            Text = text, Words = words, EndMicroseconds = words[^1].EndMicroseconds
        };

        var result = AsrSubtitleSegmenter.Segment([source], new AsrSegmentationOptions(1, 20, 2, 100, 0.6, 100));

        Assert.Equal(text, string.Join(" ", result.Select(cue => cue.Text)));
        Assert.Equal(words[0].StartMicroseconds, result[0].StartMicroseconds);
        Assert.Equal(words[^1].EndMicroseconds, result[^1].EndMicroseconds);
    }

    [Fact]
    public void UnmappableTokensPreserveUnpunctuatedTranscript()
    {
        var source = new AsrSegment
        {
            Text = "원래 띄어쓰기 유지", EndMicroseconds = 3_000_000,
            Words = [new AsrWord { Text = "<token>", EndMicroseconds = 3_000_000 }]
        };

        var result = AsrSubtitleSegmenter.Segment([source], new AsrSegmentationOptions(1, 6, 2, 42, 0.6, 20));

        Assert.Equal(source.Text, Assert.Single(result).Text);
    }

    [Fact]
    public void JapaneseSentencePunctuationSplitsOneLongAsrSegment()
    {
        var source = new AsrSegment
        {
            StartMicroseconds = 0,
            EndMicroseconds = 12_000_000,
            Text = "おはよう。あれ、おはようリフルエ。ねえ聞いた？",
            Words =
            [
                new AsrWord { StartMicroseconds = 0, EndMicroseconds = 1_000_000, Text = "おはよう" },
                new AsrWord { StartMicroseconds = 1_100_000, EndMicroseconds = 2_800_000, Text = "あれ" },
                new AsrWord { StartMicroseconds = 2_900_000, EndMicroseconds = 4_700_000, Text = "おはようリフルエ" },
                new AsrWord { StartMicroseconds = 5_000_000, EndMicroseconds = 6_500_000, Text = "ねえ聞いた" }
            ]
        };

        var result = AsrSubtitleSegmenter.Segment([source], new AsrSegmentationOptions(1, 6, 2, 24, 0.6, 20));

        Assert.Equal(3, result.Count);
        Assert.Equal("おはよう。", result[0].Text);
        Assert.Equal("あれ、おはようリフルエ。", result[1].Text);
        Assert.Equal("ねえ聞いた？", result[2].Text);
    }

    [Fact]
    public void SegmentWithoutWordTimestampsStillSplitsJapaneseText()
    {
        var source = new AsrSegment
        {
            StartMicroseconds = 0,
            EndMicroseconds = 9_000_000,
            Text = "金星ドベルグが？今回は勇者様も一緒らしいよ。あいつ暇なのか。"
        };

        var result = AsrSubtitleSegmenter.Segment([source], new AsrSegmentationOptions(1, 6, 2, 24, 0.6, 20));

        Assert.Equal(3, result.Count);
        Assert.Equal("金星ドベルグが？", result[0].Text);
        Assert.Equal("今回は勇者様も一緒らしいよ。", result[1].Text);
        Assert.Equal("あいつ暇なのか。", result[2].Text);
    }

    [Fact]
    public void MaximumCueLengthSplitsUnpunctuatedWords()
    {
        var source = new AsrSegment
        {
            StartMicroseconds = 0,
            EndMicroseconds = 8_000_000,
            Text = "abcdefghij klmnopqrst uvwxyz",
            Words =
            [
                new AsrWord { StartMicroseconds = 0, EndMicroseconds = 2_000_000, Text = "abcdefghij" },
                new AsrWord { StartMicroseconds = 2_100_000, EndMicroseconds = 4_000_000, Text = "klmnopqrst" },
                new AsrWord { StartMicroseconds = 4_100_000, EndMicroseconds = 6_000_000, Text = "uvwxyz" }
            ]
        };

        var result = AsrSubtitleSegmenter.Segment([source], new AsrSegmentationOptions(1, 6, 1, 10, 0.6, 20));

        Assert.True(result.Count >= 2);
        Assert.All(result, cue => Assert.True(cue.Text.Length <= 20));
    }

    [Fact]
    public void JapaneseSmallKanaFragmentIsMergedWithPreviousCue()
    {
        var result = AsrSubtitleSegmenter.Segment(
        [
            new AsrSegment
            {
                StartMicroseconds = 101_600_000,
                EndMicroseconds = 102_960_000,
                Text = "もっと強くならなくち"
            },
            new AsrSegment
            {
                StartMicroseconds = 113_040_000,
                EndMicroseconds = 113_290_000,
                Text = "ゃ。"
            }
        ], new AsrSegmentationOptions(1, 20, 2, 42, 0.6, 20));

        var cue = Assert.Single(result);
        Assert.Equal("もっと強くならなくちゃ。", cue.Text);
        Assert.Equal(101_600_000, cue.StartMicroseconds);
        Assert.Equal(113_290_000, cue.EndMicroseconds);
    }

}
