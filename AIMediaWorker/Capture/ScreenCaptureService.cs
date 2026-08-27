using Windows.Graphics.Imaging;
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
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;

        var buffer = CryptographicBuffer.CreateFromByteArray(bgraPixels);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap).AsTask();
        return result.Text;
    }

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
