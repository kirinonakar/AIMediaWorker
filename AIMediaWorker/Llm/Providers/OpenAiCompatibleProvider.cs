using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIMediaWorker.Llm.Providers;

public class OpenAiCompatibleProvider : ILlmProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string? _apiKey;
    private readonly Uri _baseUri;

    public OpenAiCompatibleProvider(string id, string displayName, Uri baseUri, string? apiKey, LlmProviderCapabilities capabilities, HttpClient? httpClient = null)
    {
        Id = id; DisplayName = displayName; _baseUri = EnsureTrailingSlash(baseUri); _apiKey = apiKey; Capabilities = capabilities;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) }; _ownsClient = httpClient is null;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public LlmProviderCapabilities Capabilities { get; }

    public virtual async Task<IReadOnlyList<LlmModel>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SupportsModelList) return [];
        using var request = CreateRequest(HttpMethod.Get, "models");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => new LlmModel(id!)).ToArray();
    }

    public virtual async Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, LlmGenerationOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model id is required.", nameof(model));
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }),
            ["stream"] = false
        };
        if (Capabilities.SupportsTemperature && options.Temperature is { } temperature) body["temperature"] = temperature;
        if (options.MaximumOutputTokens is { } maximum) body["max_tokens"] = maximum;
        if (Capabilities.SupportsStructuredOutput && options.StructuredOutput) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        ConfigureThinking(body, options);
        using var request = CreateRequest(HttpMethod.Post, "chat/completions");
        request.Content = JsonContent.Create(body, options: LlmJson.Options);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var root = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new LlmProviderException(Id, "The provider returned an empty response.");
        return root["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? throw new LlmProviderException(Id, "The provider response did not contain generated text.");
    }

    public void Dispose() { if (_ownsClient) _httpClient.Dispose(); GC.SuppressFinalize(this); }

    protected virtual void ConfigureThinking(JsonObject body, LlmGenerationOptions options)
    {
        if (!Capabilities.SupportsThinkingLevel || options.ThinkingLevel == Settings.ThinkingLevel.Default) return;
        body["reasoning_effort"] = options.ThinkingLevel switch
        {
            Settings.ThinkingLevel.Off => "none",
            Settings.ThinkingLevel.XHigh or Settings.ThinkingLevel.Max => "high",
            _ => options.ThinkingLevel.ToString().ToLowerInvariant()
        };
    }

    protected HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath));
        if (!string.IsNullOrWhiteSpace(_apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException exception) { throw new LlmProviderException(Id, $"{DisplayName} could not be reached.", exception.StatusCode is null ? null : (int)exception.StatusCode, exception); }
        if (response.IsSuccessStatusCode) return response;
        var status = (int)response.StatusCode;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.Dispose();
        throw new LlmProviderException(Id, $"{DisplayName} returned HTTP {status}: {Sanitize(detail)}", status);
    }

    private static string Sanitize(string message) => message.Length <= 800 ? message : message[..800] + "…";
    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}

public sealed class UnslothProvider(string? apiKey = null, Uri? baseUri = null, HttpClient? httpClient = null) : OpenAiCompatibleProvider(
    "Unsloth Desktop", "Unsloth Desktop", baseUri ?? new Uri("http://127.0.0.1:8888/v1/"), apiKey,
    new(true, true, true, true, true, true), httpClient)
{
    protected override void ConfigureThinking(JsonObject body, LlmGenerationOptions options)
    {
        if (options.ThinkingLevel == Settings.ThinkingLevel.Default) return;
        var enabled = options.ThinkingLevel != Settings.ThinkingLevel.Off;
        body["enable_thinking"] = enabled;
        body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = enabled };
        body["reasoning_effort"] = options.ThinkingLevel switch
        {
            Settings.ThinkingLevel.Off => "none",
            Settings.ThinkingLevel.Low => "low",
            Settings.ThinkingLevel.Medium => "medium",
            Settings.ThinkingLevel.High => "high",
            Settings.ThinkingLevel.XHigh => "xhigh",
            Settings.ThinkingLevel.Max => "max",
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
    }
}

public sealed class OllamaCloudProvider(string apiKey, HttpClient? httpClient = null) : OpenAiCompatibleProvider(
    "OllamaCloud", "Ollama Cloud", new Uri("https://ollama.com/v1/"), apiKey,
    new(true, true, true, true, true, true), httpClient);

public sealed class OpenCodeZenProvider(string apiKey, HttpClient? httpClient = null) : OpenAiCompatibleProvider(
    "OpenCodeZen", "OpenCode Zen", new Uri("https://opencode.ai/zen/v1/"), apiKey,
    new(true, true, true, true, true, true), httpClient);

public sealed class OpenCodeGoProvider(string apiKey, HttpClient? httpClient = null) : OpenAiCompatibleProvider(
    "OpenCodeGo", "OpenCode Go", new Uri("https://opencode.ai/zen/go/v1/"), apiKey,
    new(true, true, true, true, true, true), httpClient);

public sealed class OllamaProvider(string? apiKey = null, Uri? baseUri = null, HttpClient? httpClient = null) : OpenAiCompatibleProvider(
    "Ollama", "Ollama", baseUri ?? new Uri("http://localhost:11434/v1/"), apiKey,
    new(true, true, false, true, true, true), httpClient);

public sealed class LmStudioProvider(string? apiKey = null, Uri? baseUri = null, HttpClient? httpClient = null) : OpenAiCompatibleProvider(
    "LM Studio", "LM Studio", baseUri ?? new Uri("http://localhost:1234/v1/"), apiKey,
    new(true, true, false, true, true, true), httpClient);
