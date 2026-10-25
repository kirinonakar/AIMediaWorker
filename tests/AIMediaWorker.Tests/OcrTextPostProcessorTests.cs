using AIMediaWorker.Capture;

namespace AIMediaWorker.Tests;

public sealed class OcrTextPostProcessorTests
{
    [Fact]
    public void SelectBestAutomaticallySelectsKoreanResult()
    {
        var result = OcrTextPostProcessor.SelectBest([
            new OcrTextCandidate("gibberish", OcrLanguageKind.Profile, true),
            new OcrTextCandidate("한글 인식 결과", OcrLanguageKind.Korean, false),
            new OcrTextCandidate("韓国語", OcrLanguageKind.Japanese, false)
        ]);

        Assert.Equal("한글 인식 결과", result);
    }

    [Fact]
    public void SelectBestAutomaticallySelectsJapaneseAndRemovesInsertedSpaces()
    {
        var result = OcrTextPostProcessor.SelectBest([
            new OcrTextCandidate("unreadable", OcrLanguageKind.Profile, true),
            new OcrTextCandidate("한글오인식", OcrLanguageKind.Korean, false),
            new OcrTextCandidate("これ は OCR です", OcrLanguageKind.Japanese, false)
        ]);

        Assert.Equal("これはOCRです", result);
    }

    [Fact]
    public void JapaneseSpacingCleanupPreservesLatinSpacesAndLineBreaks()
    {
        var result = OcrTextPostProcessor.RemoveInsertedJapaneseSpaces("AI Media Worker の 結果\r\n次 の 行");

        Assert.Equal("AI Media Workerの結果\r\n次の行", result);
    }

    [Fact]
    public void SelectBestUsesExplicitEnglishEngineInsteadOfKoreanProfileForLatinText()
    {
        const string noisyProfile = "ln terms Of equity, AI will either be the greatest equalizer ever invented or\r\n" +
                                    "the harms caused by artlficial intelligence, including those wh0 lose their";
        const string englishResult = "In terms of equity, AI will either be the greatest equalizer ever invented, or\r\n" +
                                     "the harms caused by artificial intelligence, including those who lose their";

        var result = OcrTextPostProcessor.SelectBest([
            new OcrTextCandidate(noisyProfile, OcrLanguageKind.Korean, true),
            new OcrTextCandidate(englishResult, OcrLanguageKind.English, false),
            new OcrTextCandidate("In terms of equity AI", OcrLanguageKind.Japanese, false)
        ]);

        Assert.Equal(englishResult, result);
    }

    [Fact]
    public void SelectBestNormalizesPredominantlyEnglishTextEvenWhenOnlyKoreanProfileIsAvailable()
    {
        var result = OcrTextPostProcessor.SelectBest([
            new OcrTextCandidate(
                "시 will either be the greatest equalizer ever invented, orthe\r\n" +
                "worst source Of inJl-lStice.",
                OcrLanguageKind.Korean,
                true)
        ]);

        Assert.Equal(
            "AI will either be the greatest equalizer ever invented, or the\r\n" +
            "worst source of injustice.",
            result);
    }
}
