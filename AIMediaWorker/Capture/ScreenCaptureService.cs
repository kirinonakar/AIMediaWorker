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
        var engines = CreateCandidateEngines();
        if (engines.Count == 0) return null;

        var buffer = CryptographicBuffer.CreateFromByteArray(bgraPixels);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var results = new List<OcrTextCandidate>(engines.Count);
        foreach (var candidate in engines)
        {
            var result = await candidate.Engine.RecognizeAsync(bitmap).AsTask();
            var textWithLineBreaks = string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
            results.Add(new OcrTextCandidate(textWithLineBreaks, candidate.Language, candidate.IsProfile));
        }

        return OcrTextPostProcessor.SelectBest(results);
    }

    private static List<OcrEngineCandidate> CreateCandidateEngines()
    {
        var candidates = new List<OcrEngineCandidate>();
        var profileEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (profileEngine is not null)
        {
            candidates.Add(new OcrEngineCandidate(
                profileEngine,
                GetLanguageKind(profileEngine.RecognizerLanguage.LanguageTag),
                true));
        }

        AddLanguageEngine(candidates, "ko", OcrLanguageKind.Korean);
        AddLanguageEngine(candidates, "ja", OcrLanguageKind.Japanese);
        return candidates;
    }

    private static void AddLanguageEngine(List<OcrEngineCandidate> candidates, string languagePrefix, OcrLanguageKind languageKind)
    {
        var language = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(candidate =>
            candidate.LanguageTag.StartsWith(languagePrefix, StringComparison.OrdinalIgnoreCase));
        if (language is null || candidates.Any(candidate =>
                string.Equals(candidate.Engine.RecognizerLanguage.LanguageTag, language.LanguageTag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var engine = OcrEngine.TryCreateFromLanguage(language);
        if (engine is not null) candidates.Add(new OcrEngineCandidate(engine, languageKind, false));
    }

    private static OcrLanguageKind GetLanguageKind(string languageTag) =>
        languageTag.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? OcrLanguageKind.Korean :
        languageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? OcrLanguageKind.Japanese :
        OcrLanguageKind.Profile;

    private sealed record OcrEngineCandidate(OcrEngine Engine, OcrLanguageKind Language, bool IsProfile);

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
