using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIMediaWorker.Asr;

public enum AsrWorkerState { NotStarted, Starting, Ready, Busy, Failed, Stopping }

public sealed record AsrSegmentationOptions(double MinimumCueSeconds, double MaximumCueSeconds, int MaximumLines, int TargetCharactersPerLine, double SilenceSplitSeconds, double MaximumCharactersPerSecond);

public sealed record AsrRequest
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("command")] public required string Command { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement> Arguments { get; init; } = [];

    public static AsrRequest Create(string command, object? arguments = null, string? id = null)
    {
        var values = arguments is null
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(arguments, AsrJson.Options), AsrJson.Options) ?? [];
        return new AsrRequest { Id = id ?? $"job-{Guid.NewGuid():N}", Command = command, Arguments = values };
    }
}

public sealed record AsrSegment
{
    [JsonPropertyName("start_us")] public long StartMicroseconds { get; init; }
    [JsonPropertyName("end_us")] public long EndMicroseconds { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
    [JsonPropertyName("confidence")] public double? Confidence { get; init; }
    [JsonPropertyName("words")] public IReadOnlyList<AsrWord>? Words { get; init; }
}

public sealed record AsrWord
{
    [JsonPropertyName("start_us")] public long StartMicroseconds { get; init; }
    [JsonPropertyName("end_us")] public long EndMicroseconds { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
}

public sealed record AsrEvent
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("event")] public required string Event { get; init; }
    [JsonPropertyName("progress")] public double? Progress { get; init; }
    [JsonPropertyName("model_progress")] public double? ModelProgress { get; init; }
    [JsonPropertyName("stage")] public string? Stage { get; init; }
    [JsonPropertyName("downloaded_bytes")] public long? DownloadedBytes { get; init; }
    [JsonPropertyName("total_bytes")] public long? TotalBytes { get; init; }
    [JsonPropertyName("segment")] public AsrSegment? Segment { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("start_us")] public long? StartMicroseconds { get; init; }
    [JsonPropertyName("end_us")] public long? EndMicroseconds { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Data { get; init; }
}

public static class AsrJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(AsrRequest request) => JsonSerializer.Serialize(request, Options);
    public static AsrEvent DeserializeEvent(string json) => JsonSerializer.Deserialize<AsrEvent>(json, Options) ?? throw new JsonException("The ASR worker returned an empty event.");
}
