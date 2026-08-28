namespace AIMediaWorker.Capture;

internal enum OcrLanguageKind
{
    Profile,
    English,
    Korean,
    Japanese
}

internal sealed record OcrTextCandidate(string Text, OcrLanguageKind Language, bool IsProfile);

/// <summary>Selects the most plausible language-specific OCR result and fixes language-specific artifacts.</summary>
internal static class OcrTextPostProcessor
{
    public static string SelectBest(IReadOnlyList<OcrTextCandidate> candidates)
    {
        var populated = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Text))
            .ToArray();
        if (populated.Length == 0) return string.Empty;

        var profile = populated.FirstOrDefault(candidate => candidate.IsProfile);
        var eastAsianCandidates = populated
            .Select(candidate => new ScoredCandidate(candidate, CountSignificantCharacters(candidate.Text), CountNativeCharacters(candidate)))
            .Where(scored => scored.Candidate.Language is OcrLanguageKind.Korean or OcrLanguageKind.Japanese &&
                             scored.NativeCharacters >= 2 &&
                             scored.NativeCharacters * 5 >= scored.SignificantCharacters)
            .OrderByDescending(scored => scored.NativeCharacters * 6 + scored.SignificantCharacters)
            .ThenByDescending(scored => scored.Candidate.IsProfile)
            .ToArray();

        OcrTextCandidate selected;
        if (eastAsianCandidates.Length > 0)
        {
            selected = eastAsianCandidates[0].Candidate;
        }
        else if (populated
                 .Where(candidate => candidate.Language == OcrLanguageKind.English)
                 .MaxBy(candidate => CountLatinCharacters(candidate.Text)) is { } english &&
                 CountLatinCharacters(english.Text) >= 3)
        {
            // A Korean profile can misread the leading Latin "AI" as the Hangul syllable "시".
            // When there is no meaningful Korean/Japanese script evidence, trust the dedicated
            // English recognizer instead of the profile result.
            selected = english;
        }
        else
        {
            selected = profile ?? populated.MaxBy(candidate => CountSignificantCharacters(candidate.Text))!;
        }

        return selected.Language switch
        {
            OcrLanguageKind.Japanese => RemoveInsertedJapaneseSpaces(selected.Text),
            OcrLanguageKind.English => NormalizeEnglishText(selected.Text),
            _ => selected.Text
        };
    }

    public static string NormalizeEnglishText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var normalized = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\b(?:orthe)\b",
            "or the",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"\b(?:ofthe)\b",
            "of the",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"(?<=[a-z])([ \t]+)Of\b", "$1of");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+([,.;:!?])", "$1");
        return normalized;
    }

    public static string RemoveInsertedJapaneseSpaces(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var output = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character is not (' ' or '\t'))
            {
                output.Append(character);
                continue;
            }

            var previous = PreviousNonHorizontalWhitespace(text, index - 1);
            var next = NextNonHorizontalWhitespace(text, index + 1);
            if (previous is not null && next is not null &&
                (IsJapaneseCharacter(previous.Value) || IsJapaneseCharacter(next.Value)))
            {
                continue;
            }

            output.Append(character);
        }

        return output.ToString();
    }

    private static int CountSignificantCharacters(string text) => text.Count(character =>
        char.IsLetterOrDigit(character) || IsCjkIdeograph(character));

    private static int CountLatinCharacters(string text) => text.Count(IsLatinLetter);

    private static int CountNativeCharacters(OcrTextCandidate candidate) => candidate.Language switch
    {
        OcrLanguageKind.English => candidate.Text.Count(IsLatinLetter),
        OcrLanguageKind.Korean => candidate.Text.Count(IsHangulCharacter),
        OcrLanguageKind.Japanese => candidate.Text.Count(character =>
            IsJapaneseSpecificCharacter(character) || IsCjkIdeograph(character)),
        _ => 0
    };

    private static char? PreviousNonHorizontalWhitespace(string text, int index)
    {
        while (index >= 0 && text[index] is ' ' or '\t') index--;
        return index >= 0 && text[index] is not ('\r' or '\n') ? text[index] : null;
    }

    private static char? NextNonHorizontalWhitespace(string text, int index)
    {
        while (index < text.Length && text[index] is ' ' or '\t') index++;
        return index < text.Length && text[index] is not ('\r' or '\n') ? text[index] : null;
    }

    private static bool IsJapaneseCharacter(char character) =>
        IsJapaneseSpecificCharacter(character) || IsCjkIdeograph(character) ||
        character is '、' or '。' or '！' or '？' or '「' or '」' or '『' or '』' or
            '（' or '）' or '［' or '］' or '【' or '】' or '〈' or '〉' or '《' or '》' or
            '…' or '〜' or '～' or '・';

    private static bool IsJapaneseSpecificCharacter(char character) =>
        character is >= '\u3040' and <= '\u30ff' or >= '\uff66' and <= '\uff9f' or '々' or '〆' or 'ヶ';

    private static bool IsHangulCharacter(char character) =>
        character is >= '\u1100' and <= '\u11ff' or >= '\u3130' and <= '\u318f' or >= '\uac00' and <= '\ud7af';

    private static bool IsLatinLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsCjkIdeograph(char character) => character is >= '\u3400' and <= '\u9fff';

    private sealed record ScoredCandidate(OcrTextCandidate Candidate, int SignificantCharacters, int NativeCharacters);
}
