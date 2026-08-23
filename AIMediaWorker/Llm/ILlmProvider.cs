using AIMediaWorker.Settings;

namespace AIMediaWorker.Llm;

public sealed record LlmProviderCapabilities(
    bool SupportsModelList,
    bool SupportsStreaming,
    bool SupportsThinkingLevel,
    bool SupportsTemperature,
    bool SupportsSystemPrompt,
    bool SupportsStructuredOutput);

public sealed record LlmGenerationOptions(
    ThinkingLevel ThinkingLevel = ThinkingLevel.Default,
    double? Temperature = null,
    bool StructuredOutput = false,
    int? MaximumOutputTokens = null);

public sealed record LlmModel(string Id, string? DisplayName = null);

public interface ILlmProvider
{
    string Id { get; }
    string DisplayName { get; }
    LlmProviderCapabilities Capabilities { get; }
    Task<IReadOnlyList<LlmModel>> GetModelsAsync(CancellationToken cancellationToken = default);
    Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, LlmGenerationOptions options, CancellationToken cancellationToken = default);
}

public sealed class LlmProviderException(string provider, string message, int? statusCode = null, Exception? innerException = null) : Exception(message, innerException)
{
    public string Provider { get; } = provider;
    public int? StatusCode { get; } = statusCode;
}
