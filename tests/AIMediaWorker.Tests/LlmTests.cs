using AIMediaWorker.Llm;
using AIMediaWorker.Subtitle;
using AIMediaWorker.Llm.Providers;
using AIMediaWorker.Settings;
using System.Net;

namespace AIMediaWorker.Tests;

public sealed class LlmTests
{
    [Fact]
    public async Task TranslationMapsByIdWithoutChangingTimestamps()
    {
        var first = new SubtitleCue { StartMicroseconds = 1_000_000, EndMicroseconds = 2_000_000, Text = "One" };
        var second = new SubtitleCue { StartMicroseconds = 3_000_000, EndMicroseconds = 4_000_000, Text = "Two" };
        var provider = new FakeProvider(prompt => $"{{\"items\":[{{\"id\":\"{second.Id}\",\"text\":\"둘\"}},{{\"id\":\"{first.Id}\",\"text\":\"하나\"}}]}}");
        var service = new LlmService(provider, "fake");
        var result = await service.TranslateAsync([first, second], "Korean");
        Assert.Equal("하나", result[first.Id]);
        Assert.Equal("둘", result[second.Id]);
        Assert.Equal(1_000_000, first.StartMicroseconds);
        Assert.Equal(4_000_000, second.EndMicroseconds);
    }

    [Fact]
    public async Task SummaryChunksLongTranscriptsAndRunsFinalPass()
    {
        var calls = 0;
        var provider = new FakeProvider(_ => $"summary {++calls}");
        var cues = Enumerable.Range(0, 30).Select(index => new SubtitleCue { StartMicroseconds = index * 1_000_000L, EndMicroseconds = (index + 1) * 1_000_000L, Text = new string('x', 100) }).ToArray();
        var service = new LlmService(provider, "fake");
        var result = await service.SummarizeAsync(cues, SummaryKind.Short, chunkCharacters: 1000);
        Assert.True(calls >= 4);
        Assert.StartsWith("summary", result);
    }

    [Fact]
    public async Task GoogleMapsThinkingLevelOnlyForSupportedModelFamilies()
    {
        string? body = null;
        using var http = new HttpClient(new CaptureHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]}}]}") };
        }));
        using var provider = new GoogleProvider("test-key", http);

        await provider.GenerateAsync("gemini-3-flash", "system", "user", new LlmGenerationOptions(ThinkingLevel.Low), CancellationToken.None);

        Assert.Contains("\"thinkingLevel\":\"low\"", body);
    }

    private sealed class FakeProvider(Func<string, string> generate) : ILlmProvider
    {
        public string Id => "fake";
        public string DisplayName => "Fake";
        public LlmProviderCapabilities Capabilities => new(false, false, false, false, true, true);
        public Task<IReadOnlyList<LlmModel>> GetModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LlmModel>>([]);
        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, LlmGenerationOptions options, CancellationToken cancellationToken = default) => Task.FromResult(generate(userPrompt));
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
