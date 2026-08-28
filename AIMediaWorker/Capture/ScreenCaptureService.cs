using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace AIMediaWorker.Capture;

/// <summary>High-level helpers for saving captures and extracting text from screen regions.</summary>
internal static class ScreenCaptureService
{
    /// <summary>Saves BGRA pixel data as a PNG file.</summary>
    public static async Task SavePngAsync(byte[] bgraPixels, int width, int height, string path)
    {
        var stream = new InMemoryRandomAccessStream();
        try
        {
            var writer = new DataWriter(stream);
            writer.WriteBytes(bgraPixels);
            await writer.StoreAsync().AsTask();
            writer.DetachStream();
            writer.Dispose();

            stream.Seek(0);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgraPixels);
            await encoder.FlushAsync().AsTask();

            stream.Seek(0);
            var size = (uint)stream.Size;
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(size).AsTask();
            var pngBytes = new byte[size];
            reader.ReadBytes(pngBytes);
            await File.WriteAllBytesAsync(path, pngBytes);
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>
    /// Recognizes text in BGRA pixel data using the Windows OCR engine.
    /// Returns null when no OCR engine is installed, and an empty string when nothing was recognized.
    /// </summary>
    public static async Task<string?> RecognizeTextAsync(byte[] bgraPixels, int width, int height)
    {
        var profileEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        var japaneseEngine = CreateJapaneseEngine();
        if (profileEngine is null && japaneseEngine is null) return null;

        var buffer = CryptographicBuffer.CreateFromByteArray(bgraPixels);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var profileText = profileEngine is null
            ? string.Empty
            : (await profileEngine.RecognizeAsync(bitmap).AsTask()).Text;

        if (japaneseEngine is null ||
            string.Equals(profileEngine?.RecognizerLanguage.LanguageTag, japaneseEngine.RecognizerLanguage.LanguageTag, StringComparison.OrdinalIgnoreCase))
        {
            return profileText;
        }

        var japaneseText = (await japaneseEngine.RecognizeAsync(bitmap).AsTask()).Text;
        return SelectBestRecognizedText(profileText, japaneseText);
    }

    private static OcrEngine? CreateJapaneseEngine()
    {
        var language = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(candidate =>
            candidate.LanguageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
        return language is null ? null : OcrEngine.TryCreateFromLanguage(language);
    }

    /// <summary>
    /// Uses the profile-language result for ordinary Latin/Hangul captures, but switches to the
    /// Japanese engine when it finds Japanese script (or when the profile engine found nothing).
    /// </summary>
    private static string SelectBestRecognizedText(string profileText, string japaneseText)
    {
        if (string.IsNullOrWhiteSpace(japaneseText)) return profileText;
        if (string.IsNullOrWhiteSpace(profileText)) return japaneseText;
        if (japaneseText.Any(IsJapaneseSpecificCharacter)) return japaneseText;
        if (profileText.Any(IsHangulCharacter)) return profileText;

        var japaneseCjkCount = japaneseText.Count(IsCjkIdeograph);
        var profileCjkCount = profileText.Count(IsCjkIdeograph);
        return japaneseCjkCount > 0 && profileCjkCount == 0
            ? japaneseText
            : profileText;
    }

    private static bool IsJapaneseSpecificCharacter(char character) =>
        character is >= '\u3040' and <= '\u30ff' or >= '\uff66' and <= '\uff9f' or '々' or '〆' or 'ヶ';

    private static bool IsHangulCharacter(char character) =>
        character is >= '\u1100' and <= '\u11ff' or >= '\u3130' and <= '\u318f' or >= '\uac00' and <= '\ud7af';

    private static bool IsCjkIdeograph(char character) => character is >= '\u3400' and <= '\u9fff';

    /// <summary>Returns the configured home folder, falling back to Pictures and then Documents.</summary>
    public static string ResolveHomeDirectory(string? configuredFolder)
    {
        if (!string.IsNullOrWhiteSpace(configuredFolder) && Directory.Exists(configuredFolder)) return configuredFolder!;
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (Directory.Exists(pictures)) return pictures;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents) ? documents : AppContext.BaseDirectory;
    }

    public static string BuildUniqueFilePath(string directory, string fileNameWithoutExtension, string extension)
    {
        Directory.CreateDirectory(directory);
        for (var index = 0; ; index++)
        {
            var name = index == 0
                ? $"{fileNameWithoutExtension}{extension}"
                : $"{fileNameWithoutExtension} ({index}){extension}";
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) return path;
        }
    }
}
