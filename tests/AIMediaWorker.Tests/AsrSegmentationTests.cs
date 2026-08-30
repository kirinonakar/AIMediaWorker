using AIMediaWorker.Asr;

namespace AIMediaWorker.Tests;

public sealed class AsrSegmentationTests
{
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

    [Fact]
    public void StandaloneJapanesePunctuationIsMergedWithPreviousCue()
    {
        var result = AsrSubtitleSegmenter.Segment(
        [
            new AsrSegment
            {
                StartMicroseconds = 113_280_000,
                EndMicroseconds = 116_640_000,
                Text = "ルーデスなんて、当然エリスの妄想に違いないわ"
            },
            new AsrSegment
            {
                StartMicroseconds = 118_640_000,
                EndMicroseconds = 118_650_000,
                Text = " 。"
            }
        ], new AsrSegmentationOptions(1, 20, 2, 42, 0.6, 20));

        var cue = Assert.Single(result);
        Assert.Equal("ルーデスなんて、当然エリスの妄想に違いないわ。", cue.Text);
        Assert.Equal(118_650_000, cue.EndMicroseconds);
    }
}
