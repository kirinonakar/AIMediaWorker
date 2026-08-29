using AIMediaWorker.Playback;
using AIMediaWorker.Settings;

namespace AIMediaWorker.Tests;

public sealed class HdrOutputTests
{
    [Theory]
    [InlineData(HdrOutputMode.Off, "no")]
    [InlineData(HdrOutputMode.Auto, "auto")]
    [InlineData(HdrOutputMode.On, "yes")]
    public void OutputModeMapsToMpvColorspaceHint(HdrOutputMode mode, string expected)
    {
        Assert.Equal(expected, HdrOutputOptions.GetColorspaceHint(mode));
    }

    [Fact]
    public void AutomaticHdrOutputIsEnabledByDefault()
    {
        Assert.Equal(HdrOutputMode.Auto, new PlaybackSettings().HdrOutput);
    }

    [Fact]
    public void InvalidOutputModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HdrOutputOptions.GetColorspaceHint((HdrOutputMode)999));
    }
}
