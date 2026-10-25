using AIMediaWorker.Playback;

namespace AIMediaWorker.Tests;

public sealed class RtxVideoSuperResolutionTests
{
    [Theory]
    [InlineData(RtxVideoSuperResolutionMode.Auto, null, "bt.1886", true)]
    [InlineData(RtxVideoSuperResolutionMode.Auto, 8, "pq", false)]
    [InlineData(RtxVideoSuperResolutionMode.Auto, null, "pq", false)]
    [InlineData(RtxVideoSuperResolutionMode.Auto, null, "hlg", false)]
    [InlineData(RtxVideoSuperResolutionMode.On, 8, "pq", true)]
    [InlineData(RtxVideoSuperResolutionMode.Off, null, "bt.1886", false)]
    public void AutomaticModePreservesHdrAndDolbyVisionColorMetadata(
        RtxVideoSuperResolutionMode mode,
        int? dolbyVisionProfile,
        string? transferFunction,
        bool expected)
    {
        Assert.Equal(expected, RtxVideoSuperResolutionFilter.ShouldApply(mode, dolbyVisionProfile, transferFunction));
    }
}
