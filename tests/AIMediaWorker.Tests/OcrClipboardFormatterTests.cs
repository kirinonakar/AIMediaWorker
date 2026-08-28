using AIMediaWorker.Capture;

namespace AIMediaWorker.Tests;

public sealed class OcrClipboardFormatterTests
{
    [Fact]
    public void ComposeKeepsOriginalAndTranslationSeparatedByBlankLine()
    {
        var result = OcrClipboardFormatter.Compose("original\r\ntext", "번역\r\n문장");

        Assert.Equal(
            $"original\r\ntext{Environment.NewLine}{Environment.NewLine}번역\r\n문장",
            result);
    }

    [Fact]
    public void ComposeReturnsOnlyOriginalWhenTranslationIsDisabled()
    {
        Assert.Equal("original", OcrClipboardFormatter.Compose(" original ", null));
    }
}
