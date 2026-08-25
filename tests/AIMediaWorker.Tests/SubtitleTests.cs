using AIMediaWorker.Subtitle;
using AIMediaWorker.Subtitle.Editing;
using AIMediaWorker.Subtitle.Parsing;
using AIMediaWorker.Subtitle.Writing;
using System.Text;

namespace AIMediaWorker.Tests;

public sealed class SubtitleTests
{
    [Fact]
    public void SrtRoundTripPreservesTimesAndText()
    {
        const string input = "1\r\n00:00:01,250 --> 00:00:03,500\r\nHello\r\nworld\r\n\r\n2\r\n00:01:00,000 --> 00:01:01,100\r\nEnd\r\n";
        var document = SrtParser.Parse(input);
        Assert.Equal(2, document.ActiveTrack!.Cues.Count);
        Assert.Equal(1_250_000, document.ActiveTrack.Cues[0].StartMicroseconds);
        Assert.Equal("Hello\nworld", document.ActiveTrack.Cues[0].Text);
        var reparsed = SrtParser.Parse(SrtWriter.Write(document.ActiveTrack));
        Assert.Equal(document.ActiveTrack.Cues.Select(c => (c.StartMicroseconds, c.EndMicroseconds, c.Text)), reparsed.ActiveTrack!.Cues.Select(c => (c.StartMicroseconds, c.EndMicroseconds, c.Text)));
    }

    [Fact]
    public async Task SrtFileUsesUtf8AndPreservesKoreanText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aimw-utf8-{Guid.NewGuid():N}.srt");
        var track = new SubtitleTrack { Format = "srt" };
        track.Cues.Add(new SubtitleCue { StartMicroseconds = 0, EndMicroseconds = 1_000_000, Text = "안녕하세요, 한글 자막입니다." });
        try
        {
            await SrtWriter.WriteFileAsync(track, path);
            var bytes = await File.ReadAllBytesAsync(path);
            var text = new UTF8Encoding(false, true).GetString(bytes);

            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            Assert.Contains("안녕하세요, 한글 자막입니다.", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VttParsesIdentifiersAndSettings()
    {
        var document = VttParser.Parse("WEBVTT\n\nintro\n00:00:01.000 --> 00:00:02.500 align:start\nHi\n");
        Assert.Single(document.ActiveTrack!.Cues);
        Assert.Equal(2_500_000, document.ActiveTrack.Cues[0].EndMicroseconds);
    }

    [Fact]
    public void SmiParsesSyncBlocksMarkupAndHtmlEntities()
    {
        const string input = "<SAMI><BODY><SYNC Start=1000><P Class=KRCC>첫 줄<br>둘째 &amp; 줄<SYNC Start='3500'><P Class=KRCC>&nbsp;<SYNC Start=5000><P>마지막</BODY></SAMI>";

        var document = SmiParser.Parse(input);

        Assert.Equal(2, document.ActiveTrack!.Cues.Count);
        Assert.Equal((1_000_000, 3_500_000, "첫 줄\n둘째 & 줄"), (document.ActiveTrack.Cues[0].StartMicroseconds, document.ActiveTrack.Cues[0].EndMicroseconds, document.ActiveTrack.Cues[0].Text));
        Assert.Equal((5_000_000, 7_000_000, "마지막"), (document.ActiveTrack.Cues[1].StartMicroseconds, document.ActiveTrack.Cues[1].EndMicroseconds, document.ActiveTrack.Cues[1].Text));
        Assert.Equal("smi", document.ActiveTrack.Format);
    }

    [Fact]
    public void SubtitleDecoderAutomaticallyDetectsEucKrSmi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var eucKr = Encoding.GetEncoding("euc-kr");
        var bytes = eucKr.GetBytes("<SAMI><SYNC Start=0><P>한글 자막입니다");

        var decoded = SubtitleTextDecoder.Decode(bytes, new UTF8Encoding(false, true), detectKorean: true);

        Assert.Contains("한글 자막입니다", decoded);
    }

    [Fact]
    public void SubtitleDecoderUsesConfiguredFallbackForNonSmiText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var eucKr = Encoding.GetEncoding("euc-kr", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var bytes = eucKr.GetBytes("설정된 인코딩");

        var decoded = SubtitleTextDecoder.Decode(bytes, eucKr);

        Assert.Equal("설정된 인코딩", decoded);
    }

    [Theory]
    [InlineData("Movie.mkv", "movie.SMI", true)]
    [InlineData("movie.mp4", "movie.en.smi", false)]
    [InlineData("movie.mp4", "movie.srt", false)]
    public void SmiSidecarMatchingRequiresTheSameBaseName(string mediaName, string subtitleName, bool expected)
    {
        Assert.Equal(expected, SmiParser.IsSidecarFor(mediaName, subtitleName));
    }

    [Fact]
    public void AssRoundTripKeepsBasicStyle()
    {
        var track = new SubtitleTrack { Format = "ass" };
        track.Cues.Add(new SubtitleCue { StartMicroseconds = 1_000_000, EndMicroseconds = 2_500_000, Text = "A\nB", Style = "Default" });
        var parsed = AssParser.Parse(AssWriter.Write(track));
        Assert.Equal("A" + Environment.NewLine + "B", parsed.ActiveTrack!.Cues[0].Text);
        Assert.Equal("Default", parsed.ActiveTrack.Cues[0].Style);
    }

    [Fact]
    public void AssOverlayKeepsIndependentCueTimings()
    {
        var track = new SubtitleTrack { Format = "ass" };
        track.Cues.Add(new SubtitleCue { StartMicroseconds = 1_000_000, EndMicroseconds = 2_000_000, Text = "First" });
        track.Cues.Add(new SubtitleCue { StartMicroseconds = 4_000_000, EndMicroseconds = 5_000_000, Text = "Second" });

        var parsed = AssParser.Parse(AssWriter.Write(track));

        Assert.Equal(2, parsed.ActiveTrack!.Cues.Count);
        Assert.Equal((1_000_000, 2_000_000, "First"), (parsed.ActiveTrack.Cues[0].StartMicroseconds, parsed.ActiveTrack.Cues[0].EndMicroseconds, parsed.ActiveTrack.Cues[0].Text));
        Assert.Equal((4_000_000, 5_000_000, "Second"), (parsed.ActiveTrack.Cues[1].StartMicroseconds, parsed.ActiveTrack.Cues[1].EndMicroseconds, parsed.ActiveTrack.Cues[1].Text));
    }

    [Fact]
    public void AssOverlayPreservesKoreanText()
    {
        var track = new SubtitleTrack { Format = "ass" };
        track.Cues.Add(new SubtitleCue { StartMicroseconds = 0, EndMicroseconds = 2_000_000, Text = "자동 생성된 한글 자막입니다." });

        var content = AssWriter.Write(track);
        var parsed = AssParser.Parse(content);

        Assert.Contains("Style: Default,Noto Sans CJK JP,", content);
        Assert.Contains("자동 생성된 한글 자막입니다.", content);
        Assert.Equal("자동 생성된 한글 자막입니다.", parsed.ActiveTrack!.Cues[0].Text);
    }

    [Fact]
    public void AssOverlayUsesConfiguredFontFamily()
    {
        var track = new SubtitleTrack { Format = "ass" };

        var content = AssWriter.Write(track, "Noto Sans KR");

        Assert.Contains("Style: Default,Noto Sans KR,", content);
        Assert.DoesNotContain("Style: Default,Noto Sans CJK JP,", content);
    }

    [Fact]
    public void AssRoundTripPreservesNativeStyleDefinitions()
    {
        const string ass = "[Script Info]\nScriptType: v4.00+\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour\nStyle: Sign,Comic Sans MS,33,&H00ABCDEF\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Sign,,0,0,0,,Hello";
        var document = AssParser.Parse(ass);
        var output = AssWriter.Write(document.ActiveTrack!);
        Assert.Contains("Style: Sign,Comic Sans MS,33,&H00ABCDEF", output);
        Assert.Contains("Dialogue: 0,0:00:01.00,0:00:02.00,Sign", output);
    }

    [Fact]
    public void DurationSetterUsesExactMicroseconds()
    {
        var cue = new SubtitleCue { StartMicroseconds = 1_000_001, EndMicroseconds = 2_000_001 };
        cue.DurationMicroseconds = 1_234_567;
        Assert.Equal(2_234_568, cue.EndMicroseconds);
    }

    [Fact]
    public void SplitMergeAndUndoAreIncremental()
    {
        var document = new SubtitleDocument();
        var track = document.EnsureTrack();
        var cue = new SubtitleCue { StartMicroseconds = 0, EndMicroseconds = 4_000_000, Text = "Hello world" };
        track.Cues.Add(cue);
        var history = new SubtitleCommandHistory();
        history.Execute(new SplitSubtitleCommand(document, track.Cues, cue, 2_000_000, 5));
        Assert.Equal(2, track.Cues.Count);
        history.Undo();
        Assert.Single(track.Cues);
        Assert.Equal("Hello world", cue.Text);
        history.Redo();
        history.Execute(new MergeSubtitleCommand(document, track.Cues, track.Cues[0], track.Cues[1]));
        Assert.Single(track.Cues);
        Assert.Equal("Hello world", track.Cues[0].Text);
    }

    [Fact]
    public void BatchShiftMovesAllCuesAndUndoRestoresTheirOriginalTimes()
    {
        var document = new SubtitleDocument();
        var track = document.EnsureTrack();
        track.Cues.Add(new SubtitleCue { StartMicroseconds = 1_000_000, EndMicroseconds = 2_000_000, Text = "First" });
        track.Cues.Add(new SubtitleCue { StartMicroseconds = 4_000_000, EndMicroseconds = 5_000_000, Text = "Second" });
        var command = new BatchShiftCommand(document, track.Cues.ToArray(), 750_000);

        command.Execute();

        Assert.Equal([(1_750_000L, 2_750_000L), (4_750_000L, 5_750_000L)], track.Cues.Select(cue => (cue.StartMicroseconds, cue.EndMicroseconds)));

        command.Undo();

        Assert.Equal([(1_000_000L, 2_000_000L), (4_000_000L, 5_000_000L)], track.Cues.Select(cue => (cue.StartMicroseconds, cue.EndMicroseconds)));
    }

    [Fact]
    public void DisplayModesKeepOriginalTextAndSelectTranslationIndependently()
    {
        var cue = new SubtitleCue { Text = "おはよう", TranslatedText = "좋은 아침" };

        Assert.Equal("おはよう", cue.GetDisplayText(SubtitleDisplayMode.Original));
        Assert.Equal("좋은 아침", cue.GetDisplayText(SubtitleDisplayMode.Translation));
        Assert.Equal("おはよう\n좋은 아침", cue.GetDisplayText(SubtitleDisplayMode.OriginalAndTranslation));

        var document = new SubtitleDocument();
        var command = new SetSubtitleTranslationCommand(document, cue, "안녕하세요");
        command.Execute();
        Assert.Equal("おはよう", cue.Text);
        Assert.Equal("안녕하세요", cue.TranslatedText);
        command.Undo();
        Assert.Equal("좋은 아침", cue.TranslatedText);
    }

    [Fact]
    public void SubtitleWritersPersistTheSelectedTranslationDisplayMode()
    {
        var track = new SubtitleTrack { Format = "srt" };
        track.Cues.Add(new SubtitleCue
        {
            StartMicroseconds = 0,
            EndMicroseconds = 1_000_000,
            Text = "Hello",
            TranslatedText = "안녕하세요"
        });

        Assert.Contains("안녕하세요", SrtWriter.Write(track, SubtitleDisplayMode.Translation));
        Assert.Contains("안녕하세요", VttWriter.Write(track, SubtitleDisplayMode.Translation));
        Assert.Contains("안녕하세요", AssWriter.Write(track, SubtitleDisplayMode.Translation));
        Assert.DoesNotContain("Hello", SrtWriter.Write(track, SubtitleDisplayMode.Translation));
    }
}
