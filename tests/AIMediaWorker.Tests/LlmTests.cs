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
    public async Task TranslationProcessesSmallBatchesAndReportsEachCompletedBatch()
    {
        var calls = 0;
        var batchSizes = new List<int>();
        var completed = new List<int>();
        var provider = new FakeProvider(prompt =>
        {
            calls++;
            var input = prompt[(prompt.IndexOf("Input:\n", StringComparison.Ordinal) + "Input:\n".Length)..];
            using var document = System.Text.Json.JsonDocument.Parse(input);
            var items = document.RootElement.GetProperty("items").EnumerateArray().Select(item => new
            {
                Id = item.GetProperty("id").GetGuid(),
                Text = $"번역-{item.GetProperty("text").GetString()}"
            }).ToArray();
            batchSizes.Add(items.Length);
            return System.Text.Json.JsonSerializer.Serialize(new { items = items.Select(item => new { id = item.Id, text = item.Text }) });
        });
        var cues = Enumerable.Range(1, 18).Select(index => new SubtitleCue { StartMicroseconds = index * 1_000_000L, EndMicroseconds = (index + 1) * 1_000_000L, Text = $"cue-{index}" }).ToArray();
        var service = new LlmService(provider, "fake");

        var result = await service.TranslateAsync(cues, "Korean", batchCompleted: (batch, _) =>
        {
            completed.Add(batch.Completed);
            return Task.CompletedTask;
        });

        Assert.Equal(3, calls);
        Assert.Equal([6, 6, 6], batchSizes);
        Assert.Equal([6, 12, 18], completed);
        Assert.Equal(18, result.Count);
    }

    [Fact]
    public async Task TranslationPromptKeepsUnicodeCharactersInsteadOfEscapingThem()
    {
        string? capturedPrompt = null;
        var cue = new SubtitleCue { StartMicroseconds = 0, EndMicroseconds = 1_000_000, Text = "あれからここに来て良かった" };
        var provider = new FakeProvider(prompt =>
        {
            capturedPrompt = prompt;
            return $"<think>번역을 확인합니다.</think>{{\"items\":[{{\"id\":\"{cue.Id}\",\"text\":\"여기에 오길 잘했다\"}}]}}";
        });

        var translated = await new LlmService(provider, "fake").TranslateAsync([cue], "한국어");

        Assert.Contains("あれからここに来て良かった", capturedPrompt);
        Assert.DoesNotContain("\\u3042", capturedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("여기에 오길 잘했다", translated[cue.Id]);
    }

    [Fact]
    public async Task TranslationFallsBackToResponseOrderWhenCueIdsAreMissing()
    {
        var first = new SubtitleCue { StartMicroseconds = 0, EndMicroseconds = 1_000_000, Text = "おはよう" };
        var second = new SubtitleCue { StartMicroseconds = 1_000_000, EndMicroseconds = 2_000_000, Text = "元気？" };
        var provider = new FakeProvider(_ => "Here is the JSON:\n{\"items\":[{\"translation\":\"좋은 아침\"},{\"translation\":\"잘 지내？\"}]}\n");

        var translated = await new LlmService(provider, "fake").TranslateAsync([first, second], "Korean");

        Assert.Equal("좋은 아침", translated[first.Id]);
        Assert.Equal("잘 지내？", translated[second.Id]);
    }

    [Fact]
    public async Task TranslationKeepsUsableItemsWhenProviderOmitsOneCue()
    {
        var first = new SubtitleCue { StartMicroseconds = 0, EndMicroseconds = 1_000_000, Text = "One" };
        var second = new SubtitleCue { StartMicroseconds = 1_000_000, EndMicroseconds = 2_000_000, Text = "Two" };
        var provider = new FakeProvider(_ => $"{{\"items\":[{{\"id\":\"{second.Id}\",\"text\":\"둘\"}}]}}");

        var translated = await new LlmService(provider, "fake").TranslateAsync([first, second], "Korean");

        Assert.Single(translated);
        Assert.Equal("둘", translated[second.Id]);
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

    [Fact]
    public async Task UnslothDesktopUsesDesktopApiAddress()
    {
        Uri? requestUri = null;
        using var http = new HttpClient(new CaptureHandler(request =>
        {
            requestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") });
        }));
        using var provider = new UnslothProvider(httpClient: http);

        await provider.GetModelsAsync();

        Assert.Equal("Unsloth Desktop", provider.DisplayName);
        Assert.Equal("http://127.0.0.1:8888/v1/models", requestUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData(ThinkingLevel.Off, false, "none")]
    [InlineData(ThinkingLevel.Low, true, "low")]
    [InlineData(ThinkingLevel.Medium, true, "medium")]
    [InlineData(ThinkingLevel.High, true, "high")]
    [InlineData(ThinkingLevel.XHigh, true, "xhigh")]
    [InlineData(ThinkingLevel.Max, true, "max")]
    public async Task UnslothDesktopMapsThinkingSettings(ThinkingLevel level, bool enabled, string effort)
    {
        string? body = null;
        using var http = new HttpClient(new CaptureHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"완료\"}}]}") };
        }));
        using var provider = new UnslothProvider(httpClient: http);

        await provider.GenerateAsync("local-model", "정확하게 답하세요", "日本語を韓国語に翻訳", new LlmGenerationOptions(level));

        using var document = System.Text.Json.JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.True(provider.Capabilities.SupportsThinkingLevel);
        Assert.Equal(enabled, root.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(enabled, root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(effort, root.GetProperty("reasoning_effort").GetString());
        Assert.Contains("日本語を韓国語に翻訳", body);
        Assert.DoesNotContain("\\u65e5", body, StringComparison.OrdinalIgnoreCase);
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
