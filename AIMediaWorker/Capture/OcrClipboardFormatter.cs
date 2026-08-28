namespace AIMediaWorker.Capture;

internal static class OcrClipboardFormatter
{
    public static string Compose(string originalText, string? translatedText)
    {
        var original = originalText.Trim();
        if (string.IsNullOrWhiteSpace(translatedText)) return original;
        return $"{original}{Environment.NewLine}{Environment.NewLine}{translatedText.Trim()}";
    }
}
