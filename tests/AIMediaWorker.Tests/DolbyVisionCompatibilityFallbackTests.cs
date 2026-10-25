using AIMediaWorker.Playback;

namespace AIMediaWorker.Tests;

public sealed class DolbyVisionCompatibilityFallbackTests
{
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

}
