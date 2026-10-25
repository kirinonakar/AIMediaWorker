using AIMediaWorker.Asr;

namespace AIMediaWorker.Tests;

public sealed class AsrTimestampTests
{
    private const long ChunkStart = 730_000_000; // 700 s seek + 30 s already decoded.

    [Fact]
    public void NativeWordTimesAreChunkRelativeAndDoNotAddSegmentOffsetTwice()
    {
        var source = NativeSegment();

        var cue = Assert.Single(Map(source));

        Assert.Equal(735_000_000, cue.StartMicroseconds);
        Assert.Equal(737_000_000, cue.EndMicroseconds);
        Assert.Equal(735_500_000, cue.Words![0].StartMicroseconds);
        Assert.Equal(736_000_000, cue.Words[0].EndMicroseconds);
        Assert.Equal(736_100_000, cue.Words[1].StartMicroseconds);
    }

    [Fact]
    public void ForcedAlignerTimesUseActualCroppedBufferOrigin()
    {
        var samples = Enumerable.Range(0, 160_000).Select(index => (float)index).ToArray();
        var cue = Assert.Single(AsrWorkerClient.MapSegments([NativeSegment()], samples, 480_000, 700_000_000,
            null, 2, null, (text, crop, threads) =>
            {
                Assert.Equal("hello world", text);
                Assert.Equal(2, threads);
                Assert.Equal(32_000, crop.Length);
                Assert.Equal(80_000f, crop[0]);
                Assert.Equal(111_999f, crop[^1]);
                return [new("hello", 50, 100, null), new("world", 110, 150, null)];
            }));

        Assert.Equal(735_500_000, cue.Words![0].StartMicroseconds);
        Assert.Equal(736_500_000, cue.Words[^1].EndMicroseconds);
    }

    [Fact]
    public void InvalidCropFallsBackToFullChunkWithMatchingTimeOrigin()
    {
        var source = NativeSegment() with { StartCentiseconds = 500, EndCentiseconds = 400 };
        var cue = Assert.Single(Map(source, (_, crop, _) =>
        {
            Assert.Equal(160_000, crop.Length);
            return [new("hello world", 50, 100, null)];
        }));

        Assert.Equal(ChunkStart, cue.StartMicroseconds);
        Assert.Equal(ChunkStart + 10_000_000, cue.EndMicroseconds);
        Assert.Equal(ChunkStart + 500_000, cue.Words![0].StartMicroseconds);
    }

    [Fact]
    public void MissingNativeWordTimestampsUseSegmentFallbackInsteadOfTenMillisecondCues()
    {
        var source = NativeSegment() with { Words = [new("hello", -1, -1, null), new("world", -1, -1, null)] };

        var cue = Assert.Single(Map(source));

        Assert.Equal(735_000_000, cue.Words![0].StartMicroseconds);
        Assert.Equal(736_000_000, cue.Words[0].EndMicroseconds);
        Assert.Equal(736_000_000, cue.Words[1].StartMicroseconds);
        Assert.Equal(737_000_000, cue.Words[1].EndMicroseconds);
        Assert.Equal("hello world", string.Join(' ', cue.Words.Select(word => word.Text)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InvalidAlignmentFallsBackToValidNativeTiming(int invalidCase)
    {
        var logged = new List<Exception>();
        var cue = Assert.Single(Map(NativeSegment(), (_, _, _) => invalidCase switch
        {
            0 => [new("hello", -1, -1, null)],
            1 => [new("hello", 500, 600, null)],
            2 => [new("hello", 100, 150, null), new("world", 10, 20, null)],
            3 => [new("hello", 0, 1, null), new("world", 0, 1, null)],
            _ => throw new InvalidOperationException("alignment failed")
        }, logged.Add));

        Assert.Single(logged);
        Assert.Equal(735_500_000, cue.Words![0].StartMicroseconds);
        Assert.Equal(736_500_000, cue.Words[^1].EndMicroseconds);
        Assert.All(cue.Words, word => Assert.True(word.EndMicroseconds > word.StartMicroseconds));
    }

    [Fact]
    public void PartialFinalChunkClampsCentisecondRoundingToActualAudioEnd()
    {
        var source = new CrispAsrNative.NativeSegment("短い", 0, 1, [new("短い", 0, 1, null)]);

        var cue = Assert.Single(AsrWorkerClient.MapSegments([source], new float[20], 0, 0, null, 1, null));

        Assert.Equal(0, cue.StartMicroseconds);
        Assert.Equal(1_250, cue.EndMicroseconds);
        Assert.Equal(1_250, cue.Words![0].EndMicroseconds);
    }

    [Fact]
    public void OversizedNativeTimesCannotOverflowOrEscapeChunk()
    {
        var source = NativeSegment() with
        {
            StartCentiseconds = long.MaxValue, EndCentiseconds = long.MaxValue,
            Words = [new("hello world", long.MaxValue, long.MaxValue, null)]
        };

        var cue = Assert.Single(Map(source));

        Assert.Equal(ChunkStart, cue.StartMicroseconds);
        Assert.Equal(ChunkStart + 10_000_000, cue.EndMicroseconds);
        Assert.All(cue.Words!, word =>
        {
            Assert.InRange(word.StartMicroseconds, cue.StartMicroseconds, cue.EndMicroseconds - 1);
            Assert.InRange(word.EndMicroseconds, word.StartMicroseconds + 1, cue.EndMicroseconds);
        });
    }

    [Fact]
    public void SmallAlignmentJitterIsMonotonicAndRoundingStaysWithinAudio()
    {
        var words = AsrTimestampMapper.MapWords(
            [new("a", 0, 100, null), new("b", 99, 99, null), new("c", 100, 201, null)], 0, 0, 2_000_000)!;

        Assert.Equal([0L, 990_000, 1_000_000], words.Select(word => word.StartMicroseconds));
        Assert.Equal([1_000_000L, 1_000_000, 2_000_000], words.Select(word => word.EndMicroseconds));
    }

    [Fact]
    public void EmptyAudioDoesNotInvokeAlignerOrEmitOutOfRangeSegment()
    {
        Assert.Empty(AsrWorkerClient.MapSegments([NativeSegment()], [], 0, 0, null, 1, null,
            (_, _, _) => throw new InvalidOperationException("must not run")));
    }

    private static CrispAsrNative.NativeSegment NativeSegment() => new("hello world", 500, 700,
        [new("hello", 550, 600, 0.9f), new("world", 610, 650, 0.9f)]);

    private static IReadOnlyList<AsrSegment> Map(CrispAsrNative.NativeSegment source,
        Func<string, float[], int, CrispAsrNative.NativeWord[]>? aligner = null, Action<Exception>? log = null) =>
        AsrWorkerClient.MapSegments([source], new float[160_000], 480_000, 700_000_000, null, 2, log, aligner);
}
