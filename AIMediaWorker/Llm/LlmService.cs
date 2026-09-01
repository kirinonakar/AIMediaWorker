using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIMediaWorker.Subtitle;

namespace AIMediaWorker.Llm;

public enum SummaryKind { Short, Detailed }
public sealed record TranslationProgress(int Completed, int Total);
public sealed record TranslationBatch(IReadOnlyDictionary<Guid, string> Items, int Completed, int Total);
public sealed record VisionTranslation(string SourceText, string Translation);

public sealed class LlmService(ILlmProvider provider, string model, Settings.ThinkingLevel thinkingLevel = Settings.ThinkingLevel.Default)
{
    public async Task<VisionTranslation> RecognizeAndTranslateImageAsync(
        ReadOnlyMemory<byte> pngBytes,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (pngBytes.IsEmpty) throw new ArgumentException("Image data is required.", nameof(pngBytes));
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("A target language is required.", nameof(targetLanguage));

        var prompt = $"""
            Read every visible text element in the supplied screenshot in natural reading order, then translate it to {targetLanguage}.
            Preserve meaningful paragraph boundaries and line breaks. Do not describe the image or infer text that is not visible.
            Return one JSON object with exactly these string properties: "sourceText" and "translation".
            "sourceText" must contain the extracted original text and "translation" must contain its {targetLanguage} translation.
            """;
        var response = await provider.GenerateWithImageAsync(
            model,
            $"You are a precise visual text recognizer and translator. Return only valid JSON and translate into {targetLanguage}.",
            prompt,
            pngBytes,
            "image/png",
            new LlmGenerationOptions(thinkingLevel, 0.1, true, 4_096),
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(ExtractJson(response));
        var sourceText = document.RootElement.TryGetProperty("sourceText", out var source) ? source.GetString()?.Trim() : null;
        var translation = document.RootElement.TryGetProperty("translation", out var translated) ? translated.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(sourceText)) throw new JsonException("The visual recognition response did not contain source text.");
        if (string.IsNullOrWhiteSpace(translation)) throw new JsonException("The visual recognition response did not contain a translation.");
        return new VisionTranslation(sourceText, translation);
    }

    public async Task<string> TranslateTextAsync(
        string sourceText,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) return string.Empty;
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("A target language is required.", nameof(targetLanguage));

        var prompt = $"""
            Translate SOURCE_TEXT to {targetLanguage}.
            Preserve paragraph boundaries and line breaks where they carry meaning.
            Return only the translated text, without labels, quotes, markdown fences, commentary, or the source text.

            SOURCE_TEXT:
            {sourceText}
            """;
        var translated = await provider.GenerateAsync(
            model,
            $"You are a precise OCR text translator. Translate faithfully into {targetLanguage} and return only the translation.",
            prompt,
            new LlmGenerationOptions(thinkingLevel, 0.1, false, 2_048),
            cancellationToken).ConfigureAwait(false);
        return translated.Trim();
    }

    public async Task<string> TranslateLiveAsync(
        string stableSourceDelta,
        string sourceContext,
        string translatedPrefix,
        string targetLanguage,
        IProgress<string>? streamingProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stableSourceDelta)) return string.Empty;
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("A target language is required.", nameof(targetLanguage));

        var prompt = $"""
            Translate only NEW_STABLE_TEXT to {targetLanguage} for a live subtitle.
            CONTEXT_BEFORE is read-only context and must not be translated again.
            TRANSLATED_PREFIX is the already displayed translation for the current sentence; use it only to keep grammar and style coherent.
            Return only the translation of NEW_STABLE_TEXT. Do not add labels, quotes, explanations, or repeat prior translated text.

            CONTEXT_BEFORE:
            {sourceContext}

            TRANSLATED_PREFIX:
            {translatedPrefix}

            NEW_STABLE_TEXT:
            {stableSourceDelta}
            """;
        var builder = new StringBuilder();
        await foreach (var chunk in provider.GenerateStreamingAsync(
            model,
            "You are a low-latency simultaneous subtitle translator. Translate faithfully and concisely, using the supplied context to resolve grammar.",
            prompt,
            new LlmGenerationOptions(thinkingLevel, 0.1, false, 256),
            cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(chunk)) continue;
            builder.Append(chunk);
            streamingProgress?.Report(builder.ToString());
        }

        return builder.ToString().Trim();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> TranslateAsync(
        IReadOnlyCollection<SubtitleCue> cues,
        string targetLanguage,
        IProgress<TranslationProgress>? progress = null,
        Func<TranslationBatch, CancellationToken, Task>? batchCompleted = null,
        int batchSize = 10,
        IReadOnlyCollection<SubtitleCue>? contextCues = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("A target language is required.", nameof(targetLanguage));
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var input = cues.OrderBy(cue => cue.StartMicroseconds).ThenBy(cue => cue.EndMicroseconds).ToArray();
        var timeline = BuildTranslationTimeline(input, contextCues);
        var timelineIndexes = timeline.Select((cue, index) => (cue.Id, index)).ToDictionary(item => item.Id, item => item.index);
        var result = new Dictionary<Guid, string>();
        const int maximumTranslationAttempts = 3;
        for (var offset = 0; offset < input.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchLength = SelectTranslationBatchLength(input, offset, batchSize);
            var batchCues = input.Skip(offset).Take(batchLength).ToArray();
            var batch = batchCues.Select((cue, index) => new TranslationItem(
                cue.Id,
                index,
                timelineIndexes[cue.Id] + 1,
                cue.Text)).ToArray();
            var firstTimelineIndex = timelineIndexes[batchCues[0].Id];
            var lastTimelineIndex = timelineIndexes[batchCues[^1].Id];
            var contextBefore = BuildTranslationContext(
                timeline,
                Math.Max(0, firstTimelineIndex - TranslationContextCueCount),
                firstTimelineIndex,
                result);
            var contextAfter = BuildTranslationContext(
                timeline,
                lastTimelineIndex + 1,
                Math.Min(timeline.Length, lastTimelineIndex + 1 + TranslationContextCueCount),
                result,
                includeTranslation: false);
            var payload = JsonSerializer.Serialize(new
            {
                contextBefore,
                targetItems = batch,
                contextAfter
            }, LlmJson.Options);
            var prompt = $"""
                Translate TARGET_ITEMS to {targetLanguage} as concise, natural subtitles.
                READ_ONLY_CONTEXT_BEFORE and READ_ONLY_CONTEXT_AFTER exist only to resolve speakers, pronouns, omitted subjects, relationships, terminology, honorifics, and tone. Never translate or output a context item.
                A translation in READ_ONLY_CONTEXT_BEFORE is already accepted. Keep its names, terms, honorifics, and speaking style consistent unless the source clearly requires otherwise.
                Preserve meaning, speaker labels, useful line breaks, and formatting tags in every target item.
                Return one JSON object with an 'items' array containing exactly {batch.Length} TARGET_ITEMS. Each output item must contain the exact target 'id' and its translated 'text'. Do not add, omit, merge, split, or reorder target items.
                If an id cannot be copied exactly, still return every target item in its original order with its zero-based 'index'. Return no markdown or commentary outside the JSON.

                Payload:
                {payload}
                """;
            ParsedTranslations? parsedTranslations = null;
            JsonException? parseException = null;
            for (var attempt = 1; attempt <= maximumTranslationAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestPrompt = attempt == 1
                    ? prompt
                    : $"{prompt}\n\nThe previous response was invalid JSON. Retry this translation and return only one valid JSON object with the required items array.";
                var response = await provider.GenerateAsync(model, "You are a precise subtitle translator. Timestamps are managed by the application and must never appear in your output.", requestPrompt, new LlmGenerationOptions(thinkingLevel, 0.1, true), cancellationToken);
                try
                {
                    parsedTranslations = ParseTranslations(response);
                    break;
                }
                catch (JsonException exception)
                {
                    parseException = exception;
                    if (attempt < maximumTranslationAttempts)
                        await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }
            if (parsedTranslations is null)
                throw new LlmProviderException(provider.Id, "Translation response was not valid JSON.", innerException: parseException);

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
            offset += batch.Length;
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
        var finalPrompt = $"{instruction} Write the final summary in {targetLanguage}. Return only the substantive summary; do not write an introduction such as 'Here is a detailed summary based on the provided content.' A title alone is incomplete: after every heading, include the corresponding explanatory paragraphs, facts, examples, and conclusions. Synthesize the intermediate summaries below without repetition and without adding unsupported claims.\n\n{intermediateSummaries}";
        var finalOptions = new LlmGenerationOptions(thinkingLevel, 0.2, MaximumOutputTokens: kind == SummaryKind.Detailed ? 4_000 : 1_600);
        var final = await provider.GenerateAsync(model, $"You create faithful final summaries from the supplied transcript material. Always write the final summary in {targetLanguage}. Never return a generic acknowledgement or an introduction; return the actual summary content.", finalPrompt, finalOptions, cancellationToken).ConfigureAwait(false);
        var cleanedFinal = CleanSummaryResponse(final);
        if (kind == SummaryKind.Detailed && IsInsufficientDetailedSummary(cleanedFinal))
        {
            var recoveryMaterial = intermediateSummaries;
            if (IsInsufficientDetailedSummary(CleanSummaryResponse(recoveryMaterial)))
                recoveryMaterial = string.Join("\n\n---\n\n", chunks);
            var recoveryPrompt = $"{instruction} The previous response was unusable because it contained only a heading or a generic introduction. Ignore it and write the actual factual summary now. A heading by itself is not a valid answer: include the substantive paragraphs, facts, examples, and conclusions supported by the source. Return only the substantive summary in {targetLanguage}; do not describe what you are doing and do not say that this is a summary.\n\n{recoveryMaterial}";
            var recovery = await provider.GenerateAsync(model, $"Write only the detailed factual summary in {targetLanguage}. Use the transcript as the source of truth and never answer with an acknowledgement.", recoveryPrompt, finalOptions, cancellationToken).ConfigureAwait(false);
            cleanedFinal = CleanSummaryResponse(recovery);
            if (IsInsufficientDetailedSummary(cleanedFinal))
            {
                var intermediateFallback = CleanSummaryResponse(intermediateSummaries);
                if (!IsInsufficientDetailedSummary(intermediateFallback)) cleanedFinal = intermediateFallback;
            }
        }
        progress?.Report(1);
        return cleanedFinal;
    }

    private static string CleanSummaryResponse(string text)
    {
        var cleaned = text.Trim();
        var thinkingEnd = cleaned.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkingEnd >= 0) cleaned = cleaned[(thinkingEnd + "</think>".Length)..].Trim();
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

    private static bool IsInsufficientDetailedSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 1 && IsMarkdownHeading(lines[0]);
    }

    private static bool IsMarkdownHeading(string line)
    {
        var trimmed = line.Trim();
        var hashCount = 0;
        while (hashCount < trimmed.Length && trimmed[hashCount] == '#') hashCount++;
        return hashCount is >= 1 and <= 6 && hashCount < trimmed.Length && char.IsWhiteSpace(trimmed[hashCount]);
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

    private const int TranslationContextCueCount = 5;
    private const int MaximumTranslationSourceTokens = 1_500;
    private const long SceneBreakMicroseconds = 3_000_000;

    private static SubtitleCue[] BuildTranslationTimeline(
        IReadOnlyCollection<SubtitleCue> targets,
        IReadOnlyCollection<SubtitleCue>? contextCues)
    {
        var cuesById = new Dictionary<Guid, SubtitleCue>();
        if (contextCues is not null)
        {
            foreach (var cue in contextCues) cuesById[cue.Id] = cue;
        }
        foreach (var cue in targets) cuesById[cue.Id] = cue;
        return cuesById.Values
            .OrderBy(cue => cue.StartMicroseconds)
            .ThenBy(cue => cue.EndMicroseconds)
            .ToArray();
    }

    private static TranslationContextItem[] BuildTranslationContext(
        IReadOnlyList<SubtitleCue> timeline,
        int start,
        int end,
        IReadOnlyDictionary<Guid, string> currentTranslations,
        bool includeTranslation = true)
    {
        var context = new List<TranslationContextItem>(Math.Max(0, end - start));
        for (var index = start; index < end; index++)
        {
            var cue = timeline[index];
            string? translation = null;
            if (includeTranslation)
            {
                if (!currentTranslations.TryGetValue(cue.Id, out translation)) translation = cue.TranslatedText;
                if (string.IsNullOrWhiteSpace(translation)) translation = null;
            }
            context.Add(new TranslationContextItem(index + 1, cue.Text, translation));
        }
        return context.ToArray();
    }

    private static int SelectTranslationBatchLength(IReadOnlyList<SubtitleCue> cues, int offset, int targetSize)
    {
        var remaining = cues.Count - offset;
        if (remaining <= 0) return 0;

        var variation = targetSize >= 8 ? 2 : Math.Min(1, targetSize - 1);
        var minimumSize = Math.Max(1, targetSize - variation);
        var maximumSize = Math.Min(remaining, targetSize + variation);
        var allowedSize = 0;
        var estimatedTokens = 0;
        while (allowedSize < maximumSize)
        {
            var nextTokens = EstimateSourceTokens(cues[offset + allowedSize].Text);
            if (allowedSize > 0 && estimatedTokens + nextTokens > MaximumTranslationSourceTokens) break;
            estimatedTokens += nextTokens;
            allowedSize++;
        }
        if (allowedSize == 0) allowedSize = 1;
        if (remaining <= allowedSize || allowedSize < minimumSize) return allowedSize;

        var preferredSize = Math.Min(targetSize, allowedSize);
        var bestSize = preferredSize;
        var bestScore = int.MinValue;
        for (var size = minimumSize; size <= allowedSize; size++)
        {
            var current = cues[offset + size - 1];
            var next = cues[offset + size];
            var sceneBreak = next.StartMicroseconds - current.EndMicroseconds >= SceneBreakMicroseconds;
            var sentenceEnd = EndsSentence(current.Text);
            if (!sceneBreak && !sentenceEnd) continue;

            var score = (sceneBreak ? 1_000 : 0) + (sentenceEnd ? 100 : 0) - Math.Abs(size - targetSize) * 10;
            if (score > bestScore)
            {
                bestScore = score;
                bestSize = size;
            }
        }
        return bestSize;
    }

    private static int EstimateSourceTokens(string text)
    {
        double tokens = 0;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character)) tokens += 0.05;
            else if (character <= 0x7f) tokens += 0.25;
            else tokens += 0.8;
        }
        return Math.Max(1, (int)Math.Ceiling(tokens));
    }

    private static bool EndsSentence(string text)
    {
        var end = text.Length - 1;
        while (end >= 0)
        {
            while (end >= 0 && (char.IsWhiteSpace(text[end]) || "\"'”’」』】）》）]}".Contains(text[end]))) end--;
            if (end >= 0 && text[end] == '>')
            {
                var tagStart = text.LastIndexOf('<', end);
                if (tagStart >= 0) { end = tagStart - 1; continue; }
            }
            if (end >= 0 && text[end] == '}')
            {
                var tagStart = text.LastIndexOf('{', end);
                if (tagStart >= 0) { end = tagStart - 1; continue; }
            }
            break;
        }
        return end >= 0 && ".!?。！？…".Contains(text[end]);
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
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("cue_number")] int CueNumber,
        [property: JsonPropertyName("text")] string Text);

    private sealed record TranslationContextItem(
        [property: JsonPropertyName("cue_number")] int CueNumber,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("translation")] string? Translation);

    private sealed record ParsedTranslations(IReadOnlyList<ParsedTranslation> Items);

    private sealed record ParsedTranslation(Guid? Id, int? Index, string Text);
}
