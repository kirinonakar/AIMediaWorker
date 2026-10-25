using AIMediaWorker.Asr;

namespace AIMediaWorker.Tests;

public sealed class AsrSegmentationTests
{
    private static readonly AsrSegmentationOptions DefaultOptions = new(1, 6, 2, 24, 0.6, 20);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DemonstrativePrefixRepairsReportedEightSecondAlignmentGap(bool acrossChunks, bool withWords)
    {
        AsrSegment[] source =
        [
            new() { Text = "こ", StartMicroseconds = 780_720_000, EndMicroseconds = 780_800_000, Confidence = 0.9 },
            new() { Text = "の先、あの娘がどうなるかなんてわからない。", StartMicroseconds = 789_680_000, EndMicroseconds = 795_040_000, Confidence = 0.8 }
        ];
        if (withWords)
            source = source.Select(segment => segment with
            {
                Words = segment.Text.Select((character, index) => new AsrWord
                {
                    Text = character.ToString(),
                    StartMicroseconds = segment.StartMicroseconds + (segment.EndMicroseconds - segment.StartMicroseconds) * index / segment.Text.Length,
                    EndMicroseconds = segment.StartMicroseconds + (segment.EndMicroseconds - segment.StartMicroseconds) * (index + 1) / segment.Text.Length
                }).ToArray()
            }).ToArray();
        var stream = new AsrSubtitleStreamSegmenter(DefaultOptions);
        if (acrossChunks) Assert.Empty(stream.Append([source[0]]));

        var result = acrossChunks ? stream.Append([source[1]]) : AsrSubtitleSegmenter.Segment(source, DefaultOptions);

        var cue = Assert.Single(result);
        Assert.Equal("この先、あの娘がどうなるかなんてわからない。", cue.Text);
        Assert.Equal(789_600_000, cue.StartMicroseconds);
        Assert.Equal(795_040_000, cue.EndMicroseconds);
        Assert.InRange(cue.Confidence!.Value, 0.8, 0.9);
        if (withWords)
        {
            Assert.Equal(789_600_000, cue.Words![0].StartMicroseconds);
            Assert.Equal(789_680_000, cue.Words[0].EndMicroseconds);
            Assert.Equal(source[1].Words, cue.Words.Skip(1));
        }
        var repeated = Assert.Single(AsrSubtitleSegmenter.Segment([cue], DefaultOptions));
        Assert.Equal(cue.Text, repeated.Text);
        Assert.Equal(cue.StartMicroseconds, repeated.StartMicroseconds);
        Assert.Equal(cue.EndMicroseconds, repeated.EndMicroseconds);
        Assert.Empty(stream.Flush());
    }

    [Fact]
    public void CharacterAlignedDemonstrativeDoesNotSplitAtReportedGap()
    {
        const string text = "この先、あの娘がどうなるかなんてわからない。";
        var words = text.Select((character, index) => new AsrWord
        {
            Text = character.ToString(),
            StartMicroseconds = index == 0 ? 780_720_000 : 789_680_000 + 5_360_000L * (index - 1) / (text.Length - 1),
            EndMicroseconds = index == 0 ? 780_800_000 : 789_680_000 + 5_360_000L * index / (text.Length - 1)
        }).ToArray();
        var source = new AsrSegment { Text = text, StartMicroseconds = 780_720_000, EndMicroseconds = 795_040_000, Words = words };

        var cue = Assert.Single(AsrSubtitleSegmenter.Segment([source], DefaultOptions));

        Assert.Equal(text, cue.Text);
        Assert.Equal(789_600_000, cue.StartMicroseconds);
        Assert.Equal(source.EndMicroseconds, cue.EndMicroseconds);
        Assert.Equal(789_600_000, cue.Words![0].StartMicroseconds);
        Assert.Equal(789_680_000, cue.Words[0].EndMicroseconds);
        Assert.Equal(words.Skip(1), cue.Words.Skip(1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReanchoredPrefixRemainsCorrectWhenOnlyOneSideHasWordTimestamps(bool prefixAligned)
    {
        var prefix = new AsrSegment { Text = "こ", StartMicroseconds = 780_720_000, EndMicroseconds = 780_800_000 };
        var body = new AsrSegment { Text = "の先、あの娘がどうなるかなんてわからない。", StartMicroseconds = 789_680_000, EndMicroseconds = 795_040_000 };
        if (prefixAligned) prefix = prefix with { Words = [new() { Text = prefix.Text, StartMicroseconds = prefix.StartMicroseconds, EndMicroseconds = prefix.EndMicroseconds }] };
        else body = body with { Words = [new() { Text = body.Text, StartMicroseconds = body.StartMicroseconds, EndMicroseconds = body.EndMicroseconds }] };

        var cue = Assert.Single(AsrSubtitleSegmenter.Segment([prefix, body], DefaultOptions));
        var repeated = Assert.Single(AsrSubtitleSegmenter.Segment([cue], DefaultOptions));

        Assert.Equal(789_600_000, cue.StartMicroseconds);
        Assert.Equal(cue.Text, repeated.Text);
        Assert.Equal(cue.StartMicroseconds, repeated.StartMicroseconds);
        Assert.Equal(cue.EndMicroseconds, repeated.EndMicroseconds);
        Assert.All(repeated.Words!, word => Assert.InRange(word.StartMicroseconds, cue.StartMicroseconds, cue.EndMicroseconds - 1));
    }

    [Theory]
    [InlineData("そ", "の先です。")]
    [InlineData("あ", "の娘です。")]
    [InlineData("ど", "ちらに行く？")]
    [InlineData("こ", "っちです。")]
    public void LongGapPrefixRepairRecognizesDemonstrativeWords(string prefix, string next)
    {
        var result = AsrSubtitleSegmenter.Segment(
        [
            new() { Text = prefix, StartMicroseconds = 780_720_000, EndMicroseconds = 780_800_000 },
            new() { Text = next, StartMicroseconds = 789_680_000, EndMicroseconds = 795_040_000 }
        ], DefaultOptions);

        Assert.Equal(prefix + next, Assert.Single(result).Text);
    }

    [Theory]
    [InlineData("こ。", "の先です。", 80_000, 8_960_000)]
    [InlineData("え", "の先です。", 80_000, 8_960_000)]
    [InlineData("こ", "なら別の話です。", 80_000, 8_960_000)]
    [InlineData("こ", "の先です。", 80_000, 31_080_000)]
    [InlineData("こ", "の先です。", 1_500_000, 8_960_000)]
    public void LongGapPrefixRepairRejectsUnrelatedTextAndUnboundedGaps(string prefix, string next, long prefixEnd, long nextStart)
    {
        var result = AsrSubtitleSegmenter.Segment(
        [
            new() { Text = prefix, EndMicroseconds = prefixEnd },
            new() { Text = next, StartMicroseconds = nextStart, EndMicroseconds = nextStart + 5_360_000 }
        ], DefaultOptions);

        Assert.Equal([prefix, next], result.Select(cue => cue.Text));
    }

    [Fact]
    public void LongGapPrefixRepairRetainsConfiguredCharacterLimit()
    {
        const string next = "の先、あの娘がどうなるかなんてわからない。";
        var result = AsrSubtitleSegmenter.Segment(
        [
            new() { Text = "こ", StartMicroseconds = 780_720_000, EndMicroseconds = 780_800_000 },
            new() { Text = next, StartMicroseconds = 789_680_000, EndMicroseconds = 795_040_000 }
        ], DefaultOptions with { MaximumLines = 1, TargetCharactersPerLine = next.Length });

        Assert.Equal(["こ", next], result.Select(cue => cue.Text));
    }

    [Theory]
    [InlineData("ズブズブ沈んでいけばい", "い。", false)]
    [InlineData("ズブズブ沈んでいけばい", "い。", true)]
    [InlineData("この先、あの娘がどうなるかなんてわから", "ない。", false)]
    [InlineData("この先、あの娘がどうなるかなんてわから", "ない。", true)]
    public void JapaneseSentenceFinalKanaTailStaysWithSentenceAtDurationLimit(string prefix, string tail, bool acrossChunks)
    {
        AsrSegment[] source =
        [
            new() { Text = prefix, EndMicroseconds = 6_000_000 },
            new() { Text = tail, StartMicroseconds = 6_050_000, EndMicroseconds = 6_350_000 }
        ];
        var stream = new AsrSubtitleStreamSegmenter(DefaultOptions);
        if (acrossChunks) Assert.Empty(stream.Append([source[0]]));

        var result = acrossChunks ? stream.Append([source[1]]) : AsrSubtitleSegmenter.Segment(source, DefaultOptions);

        var cue = Assert.Single(result);
        Assert.Equal(prefix + tail, cue.Text);
        Assert.Equal(0, cue.StartMicroseconds);
        Assert.Equal(6_350_000, cue.EndMicroseconds);
        Assert.Empty(stream.Flush());
    }

    [Theory]
    [InlineData("ズブズブ沈んでいけばい", "い。")]
    [InlineData("この先、あの娘がどうなるかなんてわから", "ない。")]
    public void CharacterAlignmentDoesNotLeaveJapaneseSentenceFinalKanaOnItsOwn(string prefix, string tail)
    {
        var words = prefix.Select((character, index) => new AsrWord
        {
            Text = character.ToString(), StartMicroseconds = 6_000_000L * index / prefix.Length,
            EndMicroseconds = 6_000_000L * (index + 1) / prefix.Length
        }).Concat(tail.Select((character, index) => new AsrWord
        {
            Text = character.ToString(), StartMicroseconds = 6_050_000L + 300_000L * index / tail.Length,
            EndMicroseconds = 6_050_000L + 300_000L * (index + 1) / tail.Length
        })).ToArray();
        var source = new AsrSegment { Text = prefix + tail, EndMicroseconds = 6_350_000, Words = words, Confidence = 0.8 };

        var cue = Assert.Single(AsrSubtitleSegmenter.Segment([source], DefaultOptions));

        Assert.Equal(source.Text, cue.Text);
        Assert.Equal(words, cue.Words);
        Assert.Equal(0, cue.StartMicroseconds);
        Assert.Equal(source.EndMicroseconds, cue.EndMicroseconds);
        Assert.Equal(source.Confidence, cue.Confidence);
    }

    [Theory]
    [InlineData("ズブズブ沈んでいけばいい。")]
    [InlineData("この先、あの娘がどうなるかなんてわからない。")]
    public void JapaneseSentenceWithoutAlignmentStaysWholeNearDurationLimit(string text)
    {
        var source = new AsrSegment { Text = text, EndMicroseconds = 6_350_000 };

        var cue = Assert.Single(AsrSubtitleSegmenter.Segment([source], DefaultOptions));

        Assert.Equal(text, cue.Text);
        Assert.Equal(0, cue.StartMicroseconds);
        Assert.Equal(source.EndMicroseconds, cue.EndMicroseconds);
    }

    [Theory]
    [InlineData("ズブズブ沈んでいけばい", "い。")]
    [InlineData("この先、あの娘がどうなるかなんてわから", "ない。")]
    public void ShortFinalKanaTailCanJoinAcrossNoisyAlignmentGap(string prefix, string tail)
    {
        var result = AsrSubtitleSegmenter.Segment(
        [
            new() { Text = prefix, EndMicroseconds = 3_000_000 },
            new() { Text = tail, StartMicroseconds = 4_300_000, EndMicroseconds = 4_600_000 }
        ], DefaultOptions);

        Assert.Equal(prefix + tail, Assert.Single(result).Text);
    }

    [Theory]
    [InlineData(7_000_000, 1)]
    [InlineData(7_010_000, 2)]
    public void SentenceFinalKanaCompletionAllowanceIsBounded(long end, int expectedCount)
    {
        const string prefix = "この先、あの娘がどうなるかなんてわから";
        var result = AsrSubtitleSegmenter.Segment(
        [
            new() { Text = prefix, EndMicroseconds = 6_000_000 },
            new() { Text = "ない。", StartMicroseconds = 6_050_000, EndMicroseconds = end }
        ], DefaultOptions);

        Assert.Equal(expectedCount, result.Count);
        Assert.Equal(prefix + "ない。", string.Concat(result.Select(cue => cue.Text)));
    }

    [Fact]
    public void SentenceFinalKanaRepairStillRespectsCharacterLimitsAndCompletedSentences()
    {
        AsrSegment[] source =
        [
            new() { Text = "この先、あの娘がどうなるかなんてわから", EndMicroseconds = 6_000_000 },
            new() { Text = "ない。", StartMicroseconds = 6_050_000, EndMicroseconds = 6_350_000 }
        ];
        var limited = AsrSubtitleSegmenter.Segment(source,
            DefaultOptions with { MaximumLines = 1, TargetCharactersPerLine = source[0].Text.Length });
        var completed = AsrSubtitleSegmenter.Segment(
            [source[0] with { Text = "前の文は完結。" }, source[1]], DefaultOptions);

        Assert.Equal(source.Select(cue => cue.Text), limited.Select(cue => cue.Text));
        Assert.Equal(["前の文は完結。", "ない。"], completed.Select(cue => cue.Text));
    }

    [Theory]
    [InlineData("ズブズブ沈んでいけばいい。", 8_000_000)]
    [InlineData("This sentence is too long to remain in a single cue.", 6_350_000)]
    public void CompletionAllowanceDoesNotKeepOverlongOrNonJapaneseSentencesWhole(string text, long end)
    {
        var result = AsrSubtitleSegmenter.Segment([new() { Text = text, EndMicroseconds = end }], DefaultOptions);

        Assert.True(result.Count > 1);
        Assert.Equal(text, string.Join(text.Contains(' ') ? " " : "", result.Select(cue => cue.Text)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void JapaneseSingleKanaPrefixJoinsFollowingSentence(bool acrossChunks, bool withWords)
    {
        AsrSegment[] source =
        [
            new() { Text = "こ", StartMicroseconds = 100_000, EndMicroseconds = 250_000 },
            new() { Text = "の先、あの娘がどうなるかなんてわからない。", StartMicroseconds = 300_000, EndMicroseconds = 4_500_000 }
        ];
        if (withWords)
            source = source.Select(segment => segment with
            {
                Words = [new() { Text = segment.Text, StartMicroseconds = segment.StartMicroseconds, EndMicroseconds = segment.EndMicroseconds }]
            }).ToArray();
        var stream = new AsrSubtitleStreamSegmenter(DefaultOptions);
        if (acrossChunks) Assert.Empty(stream.Append([source[0]]));

        var result = acrossChunks ? stream.Append([source[1]]) : AsrSubtitleSegmenter.Segment(source, DefaultOptions);

        var cue = Assert.Single(result);
        Assert.Equal("この先、あの娘がどうなるかなんてわからない。", cue.Text);
        Assert.Equal(source[0].StartMicroseconds, cue.StartMicroseconds);
        Assert.Equal(source[^1].EndMicroseconds, cue.EndMicroseconds);
        if (withWords) Assert.Equal(source.SelectMany(segment => segment.Words!), cue.Words!);
        Assert.Empty(stream.Flush());
    }

    [Fact]
    public void JapaneseSingleKanaPrefixPrefersFollowingWordOverPreviousCue()
    {
        AsrSegment[] source =
        [
            new() { Text = "前の話", EndMicroseconds = 1_000_000 },
            new() { Text = "こ", StartMicroseconds = 1_100_000, EndMicroseconds = 1_250_000 },
            new() { Text = "の先、あの娘がどうなるかなんてわからない。", StartMicroseconds = 1_300_000, EndMicroseconds = 4_500_000 }
        ];

        var result = AsrSubtitleSegmenter.Segment(source, DefaultOptions);

        Assert.Equal(["前の話", "この先、あの娘がどうなるかなんてわからない。"], result.Select(cue => cue.Text));
    }

    [Fact]
    public void JapaneseSingleKanaPrefixDirectionIsDeferredAcrossThreeChunks()
    {
        var stream = new AsrSubtitleStreamSegmenter(DefaultOptions);
        Assert.Empty(stream.Append([new() { Text = "前の話", EndMicroseconds = 1_000_000 }]));
        Assert.Empty(stream.Append([new() { Text = "こ", StartMicroseconds = 1_100_000, EndMicroseconds = 1_250_000 }]));

        var result = stream.Append([new() { Text = "の先、あの娘がどうなるかなんてわからない。", StartMicroseconds = 1_300_000, EndMicroseconds = 4_500_000 }]);

        Assert.Equal(["前の話", "この先、あの娘がどうなるかなんてわからない。"], result.Select(cue => cue.Text));
        Assert.Empty(stream.Flush());
    }

    [Fact]
    public void DeferredUnpunctuatedVerbTailIsJoinedWhenStreamFinishes()
    {
        var stream = new AsrSubtitleStreamSegmenter(DefaultOptions);
        Assert.Empty(stream.Append([new() { Text = "分かって", EndMicroseconds = 1_000_000 }]));
        Assert.Empty(stream.Append([new() { Text = "る", StartMicroseconds = 1_100_000, EndMicroseconds = 1_300_000 }]));

        Assert.Equal("分かってる", Assert.Single(stream.Flush()).Text);
        Assert.Empty(stream.Flush());
    }

    [Theory]
    [InlineData(1_300_000, 2_500_000, 2_700_000, false)]
    [InlineData(1_300_000, 2_500_000, 2_700_000, true)]
    [InlineData(6_000_000, 6_100_000, 6_300_000, false)]
    [InlineData(6_000_000, 6_100_000, 6_300_000, true)]
    public void JapaneseTeFormTailSurvivesAlignmentGapOrDurationBoundary(long firstEnd, long nextStart, long nextEnd, bool acrossChunks)
    {
        AsrSegment[] source =
        [
            new() { Text = "分かって", EndMicroseconds = firstEnd, Confidence = 0.8,
                Words = [new() { Text = "分かって", EndMicroseconds = firstEnd }] },
            new() { Text = "る。", StartMicroseconds = nextStart, EndMicroseconds = nextEnd, Confidence = 0.8,
                Words = [new() { Text = "る。", StartMicroseconds = nextStart, EndMicroseconds = nextEnd }] }
        ];
        var stream = new AsrSubtitleStreamSegmenter(DefaultOptions);
        if (acrossChunks) Assert.Empty(stream.Append([source[0]]));

        var result = acrossChunks ? stream.Append([source[1]]) : AsrSubtitleSegmenter.Segment(source, DefaultOptions);

        var cue = Assert.Single(result);
        Assert.Equal("分かってる。", cue.Text);
        Assert.Equal(0, cue.StartMicroseconds);
        Assert.Equal(nextEnd, cue.EndMicroseconds);
        Assert.Equal(source.SelectMany(segment => segment.Words!), cue.Words!);
        Assert.Equal(0.8, cue.Confidence!.Value, 8);
        Assert.Empty(stream.Flush());
    }

    [Theory]
    [InlineData("こ。", "の先です。", 300_000)]
    [InlineData("え", "の先です。", 2_000_000)]
    [InlineData("こ", "English.", 300_000)]
    public void JapanesePrefixRepairPreservesCompleteCuesLongPausesAndOtherLanguages(string prefix, string next, long nextStart)
    {
        var result = AsrSubtitleSegmenter.Segment(
        [
            new() { Text = prefix, EndMicroseconds = 250_000 },
            new() { Text = next, StartMicroseconds = nextStart, EndMicroseconds = nextStart + 1_000_000 }
        ], DefaultOptions);

        Assert.Equal([prefix, next], result.Select(cue => cue.Text));
    }

    [Theory]
    [InlineData(6_000_000, 6_500_000, 7_500_000, 24)]
    [InlineData(1_300_000, 8_000_000, 8_200_000, 24)]
    [InlineData(1_300_000, 1_400_000, 1_600_000, 5)]
    public void TeFormRepairStillBoundsDurationGapAndCharacterCount(long firstEnd, long nextStart, long nextEnd, int maximumCharacters)
    {
        var result = AsrSubtitleSegmenter.Segment(
        [
            new() { Text = "分かって", EndMicroseconds = firstEnd },
            new() { Text = "る。", StartMicroseconds = nextStart, EndMicroseconds = nextEnd }
        ], DefaultOptions with { MaximumLines = 1, TargetCharactersPerLine = maximumCharacters });

        Assert.Equal(["分かって", "る。"], result.Select(cue => cue.Text));
    }

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
