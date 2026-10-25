using System.Diagnostics;
using System.Runtime.InteropServices;
using AIMediaWorker.Asr;
using AIMediaWorker.Playback;

namespace AIMediaWorker.Tests;

public sealed class AsrStreamingTests
{
    [Fact]
    public async Task ReadsShortPipeReadsAndFinalChunkWithoutLosingSamples()
    {
        float[] samples = [0, 0.1f, -0.2f, 1, -1, 0.5f, 0.25f];
        using var pcm = new ShortReadStream(MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
        var chunks = new List<float[]>();
        await foreach (var chunk in AsrAudioDecoder.ReadChunksAsync(pcm, 3)) chunks.Add(chunk);
        Assert.Equal([3, 3, 1], chunks.Select(chunk => chunk.Length));
        Assert.Equal(samples, chunks.SelectMany(chunk => chunk));
    }

    [Fact]
    public async Task StreamingSeekFallbackDiscardsPrefixAcrossChunks()
    {
        float[] samples = [1, 2, 3, 4, 5, 6, 7];
        using var pcm = new ShortReadStream(MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
        var received = new List<float>();
        await foreach (var chunk in AsrAudioDecoder.ReadChunksAsync(pcm, 3, skipSamples: 4)) received.AddRange(chunk);
        Assert.Equal([5f, 6f, 7f], received);
    }

    [Fact]
    public async Task LongInputIsReadLazilyWithOneBoundedBuffer()
    {
        using var pcm = new VirtualPcmStream();
        await using var chunks = AsrAudioDecoder.ReadChunksAsync(pcm, 480_000).GetAsyncEnumerator();
        Assert.True(await chunks.MoveNextAsync());
        Assert.Equal(480_000, chunks.Current.Length);
        Assert.Equal(1_920_000, pcm.BytesRead);
        Assert.True(pcm.BytesRead < pcm.Length);
    }

    [Fact]
    public async Task RejectsIncompleteFloatAndHonorsCancellation()
    {
        using var pcm = new MemoryStream(new byte[7]);
        await using var chunks = AsrAudioDecoder.ReadChunksAsync(pcm, 3).GetAsyncEnumerator();
        var error = await Assert.ThrowsAsync<AsrWorkerException>(() => chunks.MoveNextAsync().AsTask());
        Assert.Equal("AUDIO_DECODE_ERROR", error.Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var longPcm = new VirtualPcmStream();
        await using var cancelled = AsrAudioDecoder.ReadChunksAsync(longPcm, 10, cancellationToken: cancellation.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.MoveNextAsync().AsTask());
        Assert.Equal(0, longPcm.BytesRead);
    }

    [Theory]
    [InlineData("Auto", true)]
    [InlineData("Cpu", false)]
    public void NativeSessionOptionsHonorSupportedDevice(string device, bool useGpu)
    {
        Assert.Equal(useGpu, CrispAsrNative.ResolveUseGpu(device, "Auto"));
        Assert.Equal(48, Marshal.SizeOf<CrispAsrNative.OpenParams>());
    }

    [Fact]
    public void RejectsUnsupportedExplicitConfiguration()
    {
        Assert.Equal("UNSUPPORTED_PRECISION", Assert.Throws<AsrWorkerException>(() => CrispAsrNative.ResolveUseGpu("auto", "float16")).Code);
        Assert.Equal("UNSUPPORTED_DEVICE", Assert.Throws<AsrWorkerException>(() => CrispAsrNative.ResolveUseGpu("cuda", "auto")).Code);
    }

    [Fact]
    public void MapsSelectedMpvTrackToAbsoluteFfmpegStreamIndex()
    {
        MediaTrack first = new(1, MediaTrackType.Audio, "en", null, null, true, false, false) { FfmpegStreamIndex = 1 };
        MediaTrack selected = new(2, MediaTrackType.Audio, "ja", null, null, false, false, true) { FfmpegStreamIndex = 3 };
        var audio = AsrAudioTrackSelection.Resolve("movie.mkv", [first, selected]);
        Assert.Equal("movie.mkv", audio.Input);
        Assert.Equal(3, audio.StreamIndex);
        var arguments = AsrAudioDecoder.BuildArguments(audio.Input, 12_345_678, true, audio.StreamIndex).ToList();
        Assert.Equal("0:3", arguments[arguments.IndexOf("-map") + 1]);
        Assert.Equal("12.345678", arguments[arguments.IndexOf("-ss") + 1]);
        Assert.True(arguments.IndexOf("-ss") > arguments.IndexOf("-i"));
        Assert.DoesNotContain("0:a:0?", arguments);
    }

    [Fact]
    public void ExternalAudioUsesItsOwnInputAndMissingMappingFailsExplicitly()
    {
        MediaTrack selected = new(5, MediaTrackType.Audio, "ja", null, null, false, false, true)
        {
            IsExternal = true, ExternalFilename = "japanese.mka", FfmpegStreamIndex = 0
        };
        Assert.Equal(new AsrAudioTrackSelection("japanese.mka", 0), AsrAudioTrackSelection.Resolve("movie.mkv", [selected]));
        var error = Assert.Throws<AsrWorkerException>(() => AsrAudioTrackSelection.Resolve("movie.mkv", [selected with { IsExternal = false, FfmpegStreamIndex = null }]));
        Assert.Equal("AUDIO_TRACK_UNAVAILABLE", error.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FfmpegStreamsSelectedAudioSeeksAndCleansUpAfterCancellation()
    {
        // This integration test is optional on machines without FFmpeg, like the native-runtime tests.
        if (!await CanRunFfmpegAsync()) return;
        var input = Path.Combine(Path.GetTempPath(), $"aimedia-asr-{Guid.NewGuid():N}.mka");
        try
        {
            await RunFfmpegAsync(["-y", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000", "-t", "12",
                "-map", "0:a", "-map", "1:a", "-c:a", "pcm_f32le", input]);
            var samples = 0;
            double energy = 0;
            await foreach (var chunk in AsrAudioDecoder.DecodeAsync("ffmpeg", input, 2_000_000, 16_000, 1))
            {
                samples += chunk.Length;
                energy += chunk.Sum(value => (double)value * value);
                Assert.InRange(chunk.Length, 1, 16_000);
            }
            Assert.InRange(samples, 159_990, 160_010);
            Assert.True(energy / samples > 0.001); // Second track is a tone; first is silence.

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using (var chunks = AsrAudioDecoder.DecodeAsync("ffmpeg", input, 0, 16_000, 0, cancellation.Token).GetAsyncEnumerator())
            {
                Assert.True(await chunks.MoveNextAsync());
                Assert.All(chunks.Current, value => Assert.Equal(0, value));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chunks.MoveNextAsync().AsTask());
            }
            // Early disposal, without cancelling, must terminate a decoder blocked on stdout too.
            var early = AsrAudioDecoder.DecodeAsync("ffmpeg", input, 0, 16_000, 0).GetAsyncEnumerator();
            Assert.True(await early.MoveNextAsync());
            await early.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            await using var invalid = AsrAudioDecoder.DecodeAsync("ffmpeg", input, 0, 16_000, 99).GetAsyncEnumerator();
            Assert.Equal("AUDIO_DECODE_ERROR", (await Assert.ThrowsAsync<AsrWorkerException>(() => invalid.MoveNextAsync().AsTask())).Code);
        }
        finally { File.Delete(input); }
    }

    private static async Task<bool> CanRunFfmpegAsync()
    {
        try { await RunFfmpegAsync(["-version"]); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static async Task RunFfmpegAsync(string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        Assert.True(process.ExitCode == 0, await error);
    }

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(7, buffer.Length)], cancellationToken);
    }

    private sealed class VirtualPcmStream : Stream
    {
        public long BytesRead { get; private set; }
        public override long Length => 64L * 1024 * 1024 * 1024;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, Length - BytesRead);
            buffer.Span[..count].Clear();
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
