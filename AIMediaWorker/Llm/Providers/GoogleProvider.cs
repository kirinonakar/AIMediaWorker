using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIMediaWorker.Llm.Providers;

public sealed class GoogleProvider : ILlmProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _apiKey;

    public GoogleProvider(string apiKey, HttpClient? httpClient = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? throw new ArgumentException("A Google API key is required.", nameof(apiKey)) : apiKey;
        _httpClient = httpClient ?? new HttpClient { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/"), Timeout = TimeSpan.FromMinutes(5) };
        if (_httpClient.BaseAddress is null) _httpClient.BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/");
        _ownsClient = httpClient is null;
    }

    public string Id => "Google";
    public string DisplayName => "Google Gemini";
    public LlmProviderCapabilities Capabilities { get; } = new(true, true, true, true, true, true);

    public async Task<IReadOnlyList<LlmModel>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"models?key={Uri.EscapeDataString(_apiKey)}");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var root = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return root?["models"]?.AsArray().Select(item => item?["name"]?.GetValue<string>()).Where(name => name?.StartsWith("models/") == true).Select(name => new LlmModel(name![7..], itemDisplay(name))).ToArray() ?? [];
        static string itemDisplay(string name) => name[7..];
    }

    public async Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, LlmGenerationOptions options, CancellationToken cancellationToken = default)
    {
        var body = CreateGenerationBody(model, systemPrompt, userPrompt, options);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(_apiKey)}") { Content = JsonContent.Create(body, options: LlmJson.Options) };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var root = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return root?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>() ?? throw new LlmProviderException(Id, "Google Gemini returned no generated text.");
    }

    public async IAsyncEnumerable<string> GenerateStreamingAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        LlmGenerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = CreateGenerationBody(model, systemPrompt, userPrompt, options);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse&key={Uri.EscapeDataString(_apiKey)}")
        { Content = JsonContent.Create(body, options: LlmJson.Options) };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            JsonNode? root;
            try { root = JsonNode.Parse(line[5..].Trim()); }
            catch (JsonException) { continue; }
            var text = root?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    private static JsonObject CreateGenerationBody(string model, string systemPrompt, string userPrompt, LlmGenerationOptions options)
    {
        var generation = new JsonObject();
        if (options.Temperature is { } temperature) generation["temperature"] = temperature;
        if (options.MaximumOutputTokens is { } maximum) generation["maxOutputTokens"] = maximum;
        if (options.StructuredOutput) generation["responseMimeType"] = "application/json";
        if (CreateThinkingConfig(model, options.ThinkingLevel) is { } thinking) generation["thinkingConfig"] = thinking;
        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }) },
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = userPrompt }) }),
            ["generationConfig"] = generation
        };
    }

    public void Dispose() { if (_ownsClient) _httpClient.Dispose(); }
    private static JsonObject? CreateThinkingConfig(string model, Settings.ThinkingLevel level)
    {
        if (level == Settings.ThinkingLevel.Default) return null;
        if (model.Contains("2.5", StringComparison.OrdinalIgnoreCase))
        {
            if (level == Settings.ThinkingLevel.Off && model.Contains("pro", StringComparison.OrdinalIgnoreCase)) return null;
            var budget = level switch
            {
                Settings.ThinkingLevel.Off => 0,
                Settings.ThinkingLevel.Low => 1024,
                Settings.ThinkingLevel.Medium => 8192,
                Settings.ThinkingLevel.High => 24576,
                Settings.ThinkingLevel.XHigh or Settings.ThinkingLevel.Max => model.Contains("pro", StringComparison.OrdinalIgnoreCase) ? 32768 : 24576,
                _ => -1
            };
            return new JsonObject { ["thinkingBudget"] = budget };
        }
        if (!model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) return null;
        var wireLevel = level switch
        {
            Settings.ThinkingLevel.Off => "minimal",
            Settings.ThinkingLevel.Low => "low",
            Settings.ThinkingLevel.Medium => "medium",
            Settings.ThinkingLevel.High or Settings.ThinkingLevel.XHigh or Settings.ThinkingLevel.Max => "high",
            _ => null
        };
        return wireLevel is null ? null : new JsonObject { ["thinkingLevel"] = wireLevel };
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException exception) { throw new LlmProviderException(Id, "Google Gemini could not be reached.", exception.StatusCode is null ? null : (int)exception.StatusCode, exception); }
        if (response.IsSuccessStatusCode) return response;
        var status = (int)response.StatusCode; response.Dispose(); throw new LlmProviderException(Id, $"Google Gemini returned HTTP {status}.", status);
    }
}
