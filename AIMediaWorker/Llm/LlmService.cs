using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIMediaWorker.Subtitle;

namespace AIMediaWorker.Llm;

public enum SummaryKind { Short, Detailed }
public sealed record TranslationProgress(int Completed, int Total);
public sealed record TranslationBatch(IReadOnlyDictionary<Guid, string> Items, int Completed, int Total);

public sealed class LlmService(ILlmProvider provider, string model, Settings.ThinkingLevel thinkingLevel = Settings.ThinkingLevel.Default)
{
    public async Task<IReadOnlyDictionary<Guid, string>> TranslateAsync(
        IReadOnlyCollection<SubtitleCue> cues,
        string targetLanguage,
        IProgress<TranslationProgress>? progress = null,
        Func<TranslationBatch, CancellationToken, Task>? batchCompleted = null,
        int batchSize = 6,
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
            var prompt = $"Translate every item to {targetLanguage}. Preserve meaning, speaker labels, line breaks where useful, and formatting tags. Return one JSON object with an 'items' array. Every array item should contain the exact original 'id' and a 'text' string. Do not add, omit, merge, reorder, or change items. If an id cannot be copied exactly, still return every item in the original order and include its zero-based 'index'. Do not return commentary outside the JSON. Input:\n{JsonSerializer.Serialize(new { items = batch }, LlmJson.Options)}";
            var response = await provider.GenerateAsync(model, "You are a precise subtitle translator. Timestamps are managed by the application and must never appear in your output.", prompt, new LlmGenerationOptions(thinkingLevel, 0.1, true), cancellationToken);
            ParsedTranslations parsedTranslations;
            try
            {
                parsedTranslations = ParseTranslations(response);
            }
            catch (JsonException exception)
            {
                throw new LlmProviderException(provider.Id, "Translation response was not valid JSON.", innerException: exception);
            }

            var translations = ResolveTranslations(batch, parsedTranslations);
            var completedBatch = new Dictionary<Guid, string>();
            foreach (var item in batch)
            {
                // A provider can occasionally omit one cue even when the rest of the
                // batch is usable. Keep the successful translations and let the next
                // batch continue instead of failing the complete translation job.
                if (!translations.TryGetValue(item.Id, out var text)) continue;
                result[item.Id] = text;
                completedBatch[item.Id] = text;
            }
            if (completedBatch.Count == 0 && batch.Length > 0)
                throw new LlmProviderException(provider.Id, "Translation response did not contain any usable subtitle items.");
            var completed = Math.Min(offset + batch.Length, input.Length);
            if (batchCompleted is not null)
                await batchCompleted(new TranslationBatch(completedBatch, completed, input.Length), cancellationToken);
            progress?.Report(new TranslationProgress(completed, input.Length));
        }
        return result;
    }

    public async Task<string> SummarizeAsync(IReadOnlyCollection<SubtitleCue> cues, SummaryKind kind, string targetLanguage, IProgress<double>? progress = null, int chunkCharacters = 12_000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("A target language is required.", nameof(targetLanguage));
        if (chunkCharacters < 1_000) throw new ArgumentOutOfRangeException(nameof(chunkCharacters));
        var chunks = BuildTranscriptChunks(cues, chunkCharacters);
        if (chunks.Count == 0) return string.Empty;
        var partials = new List<string>(chunks.Count);
        var chunkInstruction = kind switch
        {
            SummaryKind.Short => "Create a concise but accurate intermediate summary of this transcript chunk.",
            SummaryKind.Detailed => "Create a comprehensive intermediate summary of this transcript chunk. Preserve every important name, date, number, decision, example, and relationship; do not collapse distinct facts into a vague sentence.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var chunkOptions = new LlmGenerationOptions(thinkingLevel, 0.2, MaximumOutputTokens: kind == SummaryKind.Detailed ? 2_400 : 1_200);
        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompt = $"{chunkInstruction} Write it in {targetLanguage}. Retain key names, decisions, facts, and topic transitions. Do not invent information.\n\n{chunks[index]}";
            partials.Add(await provider.GenerateAsync(model, $"You summarize audiovisual transcripts using only supplied evidence. Always write the summary in {targetLanguage}.", prompt, chunkOptions, cancellationToken).ConfigureAwait(false));
            progress?.Report((index + 1d) / (chunks.Count + 1));
        }
        var instruction = kind switch
        {
            SummaryKind.Short => "Produce a concise summary of at most five paragraphs.",
            SummaryKind.Detailed => "Produce a comprehensive detailed summary, not a brief overview. Use clear headings and sections for the overview, key points, evidence or examples, decisions, and conclusions when applicable. Preserve names, dates, numbers, and important relationships from the transcript. Do not omit relevant details or replace them with vague generalizations.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var intermediateSummaries = string.Join("\n\n---\n\n", partials);
        var finalMaterial = kind == SummaryKind.Detailed && chunks.Count == 1 ? chunks[0] : intermediateSummaries;
        var finalPrompt = $"{instruction} Write the final summary in {targetLanguage}. Return only the substantive summary; do not write an introduction such as 'Here is a detailed summary based on the provided content.' Synthesize the material below without repetition and without adding unsupported claims.\n\n{finalMaterial}";
        var finalOptions = new LlmGenerationOptions(thinkingLevel, 0.2, MaximumOutputTokens: kind == SummaryKind.Detailed ? 4_000 : 1_600);
        var final = await provider.GenerateAsync(model, $"You create faithful final summaries from the supplied transcript material. Always write the final summary in {targetLanguage}. Never return a generic acknowledgement or an introduction; return the actual summary content.", finalPrompt, finalOptions, cancellationToken).ConfigureAwait(false);
        var cleanedFinal = RemoveSummaryPreamble(final);
        if (kind == SummaryKind.Detailed && string.IsNullOrWhiteSpace(cleanedFinal))
        {
            var recoveryPrompt = $"{instruction} The previous response contained only a generic introductory sentence. Ignore it and write the actual summary from the transcript below. Return only the substantive summary in {targetLanguage}; do not describe what you are doing and do not say that this is a summary.\n\n{string.Join("\n\n---\n\n", chunks)}";
            var recovery = await provider.GenerateAsync(model, $"Write only the detailed factual summary in {targetLanguage}. Use the transcript as the source of truth and never answer with an acknowledgement.", recoveryPrompt, finalOptions, cancellationToken).ConfigureAwait(false);
            cleanedFinal = RemoveSummaryPreamble(recovery);
        }
        progress?.Report(1);
        return cleanedFinal;
    }

    private static string RemoveSummaryPreamble(string text)
    {
        var cleaned = text.Trim();
        foreach (var preamble in new[]
        {
            "제공된 내용을 바탕으로 작성한 상세 요약입니다.",
            "다음은 제공된 내용을 바탕으로 작성한 상세 요약입니다.",
            "제공된 내용을 바탕으로 한 상세 요약입니다.",
            "Here is a detailed summary based on the provided content.",
            "This is a detailed summary based on the provided content.",
            "Here is the detailed summary based on the provided content."
        })
            cleaned = cleaned.Replace(preamble, string.Empty, StringComparison.OrdinalIgnoreCase);

        return cleaned.Trim().TrimStart(':', '：', '-', '—', ' ', '\r', '\n').Trim();
    }

    private static ParsedTranslations ParseTranslations(string response)
    {
        var json = ExtractJson(response);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var itemArray, "items", "translations", "results"))
        {
            items = itemArray;
        }
        else
        {
            throw new JsonException("Translation response did not contain an items array.");
        }

        if (items.ValueKind != JsonValueKind.Array)
            throw new JsonException("Translation response items was not an array.");

        var result = new List<ParsedTranslation>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, out var text, "text", "translation", "translated_text", "translatedText", "content")) continue;
            TryGetGuid(item, out var id, "id", "cue_id", "cueId", "subtitle_id", "subtitleId");
            TryGetInt32(item, out var index, "index", "item_index", "itemIndex", "position", "number");
            result.Add(new ParsedTranslation(id, index, text));
        }
        return new ParsedTranslations(result);
    }

    private static string ExtractJson(string response)
    {
        var text = response.Trim();
        var thinkingEnd = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkingEnd >= 0) text = text[(thinkingEnd + "</think>".Length)..].Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) text = text[(firstLine + 1)..lastFence].Trim();
        }

        // Some providers prepend a short explanation even when JSON mode is
        // requested. Keep only the outermost JSON value so that the normal
        // parser can still validate the actual response.
        var objectStart = text.IndexOf('{');
        var arrayStart = text.IndexOf('[');
        var start = objectStart < 0 ? arrayStart : arrayStart < 0 ? objectStart : Math.Min(objectStart, arrayStart);
        var objectEnd = text.LastIndexOf('}');
        var arrayEnd = text.LastIndexOf(']');
        var end = Math.Max(objectEnd, arrayEnd);
        if (start >= 0 && end > start) text = text[start..(end + 1)].Trim();
        return text;
    }

    private static IReadOnlyDictionary<Guid, string> ResolveTranslations(IReadOnlyList<TranslationItem> batch, ParsedTranslations parsed)
    {
        var result = new Dictionary<Guid, string>();
        var usedItems = new HashSet<int>();
        var batchIds = batch.Select(item => item.Id).ToHashSet();

        // Exact cue ids remain the preferred mapping, even if the provider
        // changes the response order.
        for (var index = 0; index < parsed.Items.Count; index++)
        {
            var item = parsed.Items[index];
            if (item.Id is { } id && batchIds.Contains(id) && result.TryAdd(id, item.Text)) usedItems.Add(index);
        }

        // Providers sometimes return a numeric position instead of a UUID.
        var responseIndexes = parsed.Items.Where(item => item.Index.HasValue).Select(item => item.Index!.Value).ToArray();
        var oneBased = responseIndexes.Length > 0 && !responseIndexes.Contains(0) && responseIndexes.All(index => index >= 1 && index <= batch.Count);
        for (var index = 0; index < parsed.Items.Count; index++)
        {
            if (usedItems.Contains(index)) continue;
            var item = parsed.Items[index];
            if (item.Index is not { } responseIndex) continue;
            var batchIndex = oneBased ? responseIndex - 1 : responseIndex;
            if (batchIndex < 0 || batchIndex >= batch.Count) continue;
            var cueId = batch[batchIndex].Id;
            if (result.TryAdd(cueId, item.Text)) usedItems.Add(index);
        }

        // If ids are malformed or missing, the original batch order is a safe
        // fallback only when the provider returned the complete batch. For a
        // partial response, guessing would attach a valid translation to the
        // wrong cue.
        if (parsed.Items.Count == batch.Count)
        {
            for (var index = 0; index < parsed.Items.Count; index++)
            {
                if (usedItems.Contains(index)) continue;
                var cueId = batch[index].Id;
                if (result.TryAdd(cueId, parsed.Items[index].Text)) usedItems.Add(index);
            }
        }

        // A shuffled response can leave an unusable-id item at a position that
        // was already filled by an exact id. If the remaining counts agree,
        // assign only those remaining texts and cues in order.
        var remainingItems = parsed.Items
            .Select((item, index) => (item, index))
            .Where(pair => !usedItems.Contains(pair.index))
            .Select(pair => pair.item.Text)
            .ToArray();
        var remainingCueIds = batch.Select(item => item.Id).Where(id => !result.ContainsKey(id)).ToArray();
        if (remainingItems.Length == remainingCueIds.Length)
        {
            for (var index = 0; index < remainingItems.Length; index++) result[remainingCueIds[index]] = remainingItems[index];
        }

        return result;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement element, out string value, params string[] names)
    {
        if (TryGetProperty(element, out var property, names) && property.ValueKind == JsonValueKind.String && property.GetString() is { } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetGuid(JsonElement element, out Guid? value, params string[] names)
    {
        if (TryGetString(element, out var text, names) && Guid.TryParse(text, out var id))
        {
            value = id;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetInt32(JsonElement element, out int? value, params string[] names)
    {
        if (TryGetProperty(element, out var property, names))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            {
                value = number;
                return true;
            }

            if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number))
            {
                value = number;
                return true;
            }
        }

        value = null;
        return false;
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

    private sealed record ParsedTranslations(IReadOnlyList<ParsedTranslation> Items);

    private sealed record ParsedTranslation(Guid? Id, int? Index, string Text);
}
