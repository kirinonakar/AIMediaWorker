using AIMediaWorker.Playback;

namespace AIMediaWorker.Tests;

public sealed class DolbyVisionCompatibilityFallbackTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(7, false)]
    [InlineData(8, true)]
    public void OnlyCompatibleBaseLayerProfilesRequireFallback(int? profile, bool expected)
    {
        Assert.Equal(expected, DolbyVisionCompatibilityFallback.IsRequired(profile));
    }

    [Fact]
    public void Profile4FilterUsesTheSdrCompatibleBaseLayerMetadata()
    {
        var filter = DolbyVisionCompatibilityFallback.BuildFilter(4);

        Assert.Equal(
            "@aimedia-dovi-compatible-base:format=dolbyvision=no:colormatrix=bt.709:colorlevels=limited:primaries=bt.709:gamma=bt.1886",
            filter);
    }

    [Fact]
    public void Profile8FilterPreservesTheTaggedCompatibleBaseLayerMetadata()
    {
        var filter = DolbyVisionCompatibilityFallback.BuildFilter(8);

        Assert.Equal("@aimedia-dovi-compatible-base:format=dolbyvision=no", filter);
    }

    [Fact]
    public void UnsupportedProfilesCannotBuildAFallback()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DolbyVisionCompatibilityFallback.BuildFilter(5));
    }
}
