using AIMediaWorker.Playback;

namespace AIMediaWorker.Tests;

public sealed class RtxVideoSuperResolutionTests
{
    [Fact]
    public void OffDoesNotCreateAFilter()
    {
        Assert.Null(RtxVideoSuperResolutionFilter.Build(RtxVideoSuperResolutionMode.Off));
    }

    [Theory]
    [InlineData(RtxVideoSuperResolutionMode.Auto)]
    [InlineData(RtxVideoSuperResolutionMode.On)]
    public void EnabledModesCreateTheNvidiaD3D11Filter(RtxVideoSuperResolutionMode mode)
    {
        var filter = RtxVideoSuperResolutionFilter.Build(mode);

        Assert.Equal("@aimedia-rtx-vsr:d3d11vpp=scale=2:scaling-mode=nvidia", filter);
    }

    [Fact]
    public void FilterUsesInvariantScaleFormatting()
    {
        var filter = RtxVideoSuperResolutionFilter.Build(RtxVideoSuperResolutionMode.On, 1.5);

        Assert.Equal("@aimedia-rtx-vsr:d3d11vpp=scale=1.5:scaling-mode=nvidia", filter);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FilterRejectsNonUpscalingFactors(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RtxVideoSuperResolutionFilter.Build(RtxVideoSuperResolutionMode.On, scale));
    }

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
