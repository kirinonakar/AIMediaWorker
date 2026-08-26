using AIMediaWorker.Media;
using System.Text;

namespace AIMediaWorker.Tests;

public sealed class AudioTagReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AIMediaWorker.Tests", Guid.NewGuid().ToString("N"));

    public AudioTagReaderTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void ReadArtworkReturnsEmbeddedFrontCover()
    {
        var path = Path.Combine(_folder, "tagged.wav");
        var expected = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        CreateWaveFile(path);

        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Pictures =
            [new TagLib.Picture(new TagLib.ByteVector(expected))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/png"
            }];
            file.Save();
        }

        var artwork = AudioTagReader.ReadArtwork(path);

        Assert.NotNull(artwork);
        Assert.Equal(expected, artwork.Bytes);
        Assert.Equal("image/png", artwork.MimeType);
    }

    [Fact]
    public void ReadArtworkDoesNotThrowForMissingFile()
    {
        var artwork = AudioTagReader.ReadArtwork(Path.Combine(_folder, "missing.mp3"));

        Assert.Null(artwork);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static void CreateWaveFile(string path)
    {
        const int dataSize = 800;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
    }
}
