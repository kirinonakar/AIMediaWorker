namespace AIMediaWorker.Subtitle;

/// <summary>Shared punctuation rules for subtitle segmentation and live translation.</summary>
public static class SubtitlePunctuation
{
    public const string SentenceTerminators = ".!?。！？｡‼⁇⁈⁉…‥";
    public const string CommaCharacters = ",，、､";
    public const string ClauseTerminators = ",;:、，､；：";
    public const string ClosingCharacters = ")]}>」』】》）〕〉］｝】”’\"'";

    public static bool IsSentenceTerminator(char character) => SentenceTerminators.Contains(character);
    public static bool IsComma(char character) => CommaCharacters.Contains(character);
    public static bool IsClauseTerminator(char character) => ClauseTerminators.Contains(character);
    public static bool IsClosingCharacter(char character) => ClosingCharacters.Contains(character);

    public static bool EndsSentence(string text)
    {
        var index = text.Length - 1;
        while (index >= 0 && (char.IsWhiteSpace(text[index]) || IsClosingCharacter(text[index]))) index--;
        return index >= 0 && IsSentenceTerminator(text[index]);
    }
}
