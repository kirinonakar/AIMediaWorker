using AIMediaWorker.Subtitle;
using AIMediaWorker.Subtitle.Editing;
using AIMediaWorker.Subtitle.Parsing;
using AIMediaWorker.Subtitle.Writing;

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
    public void VttParsesIdentifiersAndSettings()
    {
        var document = VttParser.Parse("WEBVTT\n\nintro\n00:00:01.000 --> 00:00:02.500 align:start\nHi\n");
        Assert.Single(document.ActiveTrack!.Cues);
        Assert.Equal(2_500_000, document.ActiveTrack.Cues[0].EndMicroseconds);
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
}
