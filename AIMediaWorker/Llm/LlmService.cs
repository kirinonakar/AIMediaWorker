using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIMediaWorker.Subtitle;

namespace AIMediaWorker.Llm;

public enum SummaryKind { Short, Detailed, Chapters }
public sealed record TranslationProgress(int Completed, int Total);
public sealed record TranslationBatch(IReadOnlyDictionary<Guid, string> Items, int Completed, int Total);

public sealed class LlmService(ILlmProvider provider, string model, Settings.ThinkingLevel thinkingLevel = Settings.ThinkingLevel.Default)
{
    public async Task<IReadOnlyDictionary<Guid, string>> TranslateAsync(
        IReadOnlyCollection<SubtitleCue> cues,
        string targetLanguage,
        IProgress<TranslationProgress>? progress = null,
        Func<TranslationBatch, CancellationToken, Task>? batchCompleted = null,
        int batchSize = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("A target language is required.", nameof(targetLanguage));
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var input = cues.ToArray();
        var result = new Dictionary<Guid, string>();
        for (var offset = 0; offset < input.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = input.Skip(offset).Take(batchSize).Select(cue => new TranslationItem(cue.Id, cue.Text)).ToArray();
            var prompt = $"Translate every item to {targetLanguage}. Preserve meaning, speaker labels, line breaks where useful, and formatting tags. Return one JSON object with an 'items' array. Every array item must contain exactly the original 'id' and a 'text' string. Do not add, omit, merge, reorder, or change ids. Input:\n{JsonSerializer.Serialize(new { items = batch })}";
            var response = await provider.GenerateAsync(model, "You are a precise subtitle translator. Timestamps are managed by the application and must never appear in your output.", prompt, new LlmGenerationOptions(thinkingLevel, 0.1, true), cancellationToken);
            var translations = ParseTranslations(response);
            var completedBatch = new Dictionary<Guid, string>();
            foreach (var item in batch)
            {
                if (!translations.TryGetValue(item.Id, out var text)) throw new LlmProviderException(provider.Id, $"Translation response did not contain cue {item.Id}.");
                result[item.Id] = text;
                completedBatch[item.Id] = text;
            }
            var completed = Math.Min(offset + batch.Length, input.Length);
            if (batchCompleted is not null)
                await batchCompleted(new TranslationBatch(completedBatch, completed, input.Length), cancellationToken);
            progress?.Report(new TranslationProgress(completed, input.Length));
        }
        return result;
    }

    public async Task<string> SummarizeAsync(IReadOnlyCollection<SubtitleCue> cues, SummaryKind kind, IProgress<double>? progress = null, int chunkCharacters = 12_000, CancellationToken cancellationToken = default)
    {
        if (chunkCharacters < 1_000) throw new ArgumentOutOfRangeException(nameof(chunkCharacters));
        var chunks = BuildTranscriptChunks(cues, chunkCharacters);
        if (chunks.Count == 0) return string.Empty;
        var partials = new List<string>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompt = $"Summarize this transcript chunk accurately. Retain key names, decisions, facts, and topic transitions. Do not invent information.\n\n{chunks[index]}";
            partials.Add(await provider.GenerateAsync(model, "You summarize audiovisual transcripts using only supplied evidence.", prompt, new LlmGenerationOptions(thinkingLevel, 0.2), cancellationToken).ConfigureAwait(false));
            progress?.Report((index + 1d) / (chunks.Count + 1));
        }
        var instruction = kind switch
        {
            SummaryKind.Short => "Produce a concise summary of at most five paragraphs.",
            SummaryKind.Detailed => "Produce a detailed, well-structured summary with key points, evidence, and conclusions.",
            SummaryKind.Chapters => "Produce chronological chapters with timestamp ranges, descriptive titles, and a short topic summary for each chapter.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var finalPrompt = $"{instruction} Synthesize the chunk summaries below without repetition and without adding unsupported claims.\n\n{string.Join("\n\n---\n\n", partials)}";
        var final = await provider.GenerateAsync(model, "You create faithful final summaries from intermediate transcript summaries.", finalPrompt, new LlmGenerationOptions(thinkingLevel, 0.2), cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
        return final;
    }

    private static IReadOnlyDictionary<Guid, string> ParseTranslations(string response)
    {
        var json = ExtractJson(response);
        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement : document.RootElement.GetProperty("items");
        var result = new Dictionary<Guid, string>();
        foreach (var item in items.EnumerateArray())
        {
            if (!Guid.TryParse(item.GetProperty("id").GetString(), out var id)) continue;
            if (item.GetProperty("text").GetString() is { } text) result[id] = text;
        }
        return result;
    }

    private static string ExtractJson(string response)
    {
        var text = response.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) text = text[(firstLine + 1)..lastFence].Trim();
        }
        return text;
    }

    private static List<string> BuildTranscriptChunks(IEnumerable<SubtitleCue> cues, int maximumCharacters)
    {
        var chunks = new List<string>();
        var builder = new StringBuilder();
        foreach (var cue in cues.OrderBy(cue => cue.StartMicroseconds))
        {
            var line = $"[{SubtitleTime.FormatVtt(cue.StartMicroseconds)}] {cue.Text.Replace('\n', ' ')}";
            if (builder.Length > 0 && builder.Length + line.Length + 1 > maximumCharacters)
            {
                chunks.Add(builder.ToString()); builder.Clear();
            }
            builder.AppendLine(line);
        }
        if (builder.Length > 0) chunks.Add(builder.ToString());
        return chunks;
    }

    private sealed record TranslationItem(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("text")] string Text);
}
