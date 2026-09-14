using AIMediaWorker.Subtitle.Writing;

namespace AIMediaWorker.Tests;

public sealed class GeneratedSubtitleAssTests
{
    [Fact]
    public void CueDefinesItsOwnBottomCenterPositionAndStyle()
    {
        var ass = GeneratedSubtitleAss.Write("자막", "Arial", 42, "#FF123456", "#80000000", 2, 45);
        Assert.StartsWith(@"{\rDefault\an2\pos(640,675)", ass);
        Assert.Contains(@"\fs42", ass);
        Assert.Contains(@"\1c&H563412&\1a&H00&", ass);
        Assert.Contains(@"\3a&H7F&", ass);
        Assert.EndsWith("자막", ass);
    }

    [Fact]
    public void TextCannotInjectPositioningTagsOrAdditionalAssEvents()
    {
        var ass = GeneratedSubtitleAss.Write("{\\pos(0,0)}literal\\N\r\nnext\rlast", "Arial", 42,
            "#FFFFFFFF", "#80000000", 2, 45);
        Assert.DoesNotContain("\r", ass);
        Assert.DoesNotContain("\n", ass);
        Assert.Contains("\\{\\\uFEFFpos(0,0)\\}", ass);
        Assert.Contains("literal\\\uFEFFN\\Nnext\\Nlast", ass);
    }
}
