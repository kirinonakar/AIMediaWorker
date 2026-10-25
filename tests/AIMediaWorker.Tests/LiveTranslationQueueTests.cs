using AIMediaWorker.Llm;

namespace AIMediaWorker.Tests;

public sealed class LiveTranslationQueueTests
{
    [Fact]
    public async Task OverloadMergesPendingDeltasInOrderAndUsesNewestRevision()
    {
        var queue = new LiveTranslationQueue();
        for (var i = 1; i <= 9; i++) queue.Enqueue(Request(i));
        Assert.Equal(1, queue.Count);
        var item = await queue.ReadAsync(CancellationToken.None);
        Assert.Equal("1 2 3 4 5 6 7 8 9", item.Text);
        Assert.Equal(9, item.Sequence);
        Assert.Equal(9, item.DisplayRevision);
        Assert.True(item.CompletesSentence);
        Assert.Equal("before 1", item.SourceContext);
    }

    [Fact]
    public async Task SlowConsumerMergesStaleWorkWithoutRequeuingAlreadyProcessedSentence()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new LiveTranslationQueue(() => now);
        queue.Enqueue(Request(1));
        var processed = await queue.ReadAsync(CancellationToken.None);
        queue.Enqueue(Request(2));
        queue.Enqueue(Request(3));
        now += TimeSpan.FromSeconds(9);
        var merged = await queue.ReadAsync(CancellationToken.None);
        Assert.Equal("1", processed.Text);
        Assert.Equal("2 3", merged.Text);
        Assert.DoesNotContain("1", merged.Text);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task StaleProducerMergesNewestTextAndBoundsTotalPendingMemory()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new LiveTranslationQueue(() => now);
        queue.Enqueue(Request(1));
        now += TimeSpan.FromSeconds(9);
        queue.Enqueue(Request(2));
        Assert.Equal("1 2", (await queue.ReadAsync(CancellationToken.None)).Text);
        for (var i = 0; i < 1000; i++)
        {
            queue.Enqueue(Request(i) with { Text = new string('x', 1000) + " latest", SourceContext = new string('y', 2000) });
            Assert.InRange(queue.Count, 1, LiveTranslationQueue.MaximumRequests);
        }
        var latest = await queue.ReadAsync(CancellationToken.None);
        Assert.InRange(latest.Text.Length, 1, LiveTranslationQueue.MaximumTextCharacters);
        Assert.InRange(latest.SourceContext.Length, 0, 480);
        Assert.EndsWith("latest", latest.Text);
    }

    [Fact]
    public async Task RestartClearsOldGenerationAndReaderWaitIsCancellable()
    {
        var queue = new LiveTranslationQueue();
        queue.Enqueue(Request(1));
        queue.Clear();
        queue.Enqueue(Request(2) with { Generation = 2 });
        Assert.Equal(2, (await queue.ReadAsync(CancellationToken.None)).Generation);
        queue.Enqueue(Request(3));
        queue.Enqueue(Request(4) with { Generation = 2 });
        Assert.Equal("4", (await queue.ReadAsync(CancellationToken.None)).Text);
        using var cancellation = new CancellationTokenSource();
        var waiting = queue.ReadAsync(cancellation.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    private static LiveTranslationRequest Request(int sequence) =>
        new(sequence.ToString(), $"before {sequence}", 1, sequence, sequence, sequence % 3 == 0);
}
