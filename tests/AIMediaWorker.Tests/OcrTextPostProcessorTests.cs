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
    public void SelectBestPreservesKoreanWordSpaces()
    {
        var result = OcrTextPostProcessor.SelectBest([
            new OcrTextCandidate("첫 번째 줄\r\n두 번째 줄", OcrLanguageKind.Korean, false)
        ]);

        Assert.Equal("첫 번째 줄\r\n두 번째 줄", result);
    }

    [Fact]
    public void JapaneseSpacingCleanupPreservesLatinSpacesAndLineBreaks()
    {
        var result = OcrTextPostProcessor.RemoveInsertedJapaneseSpaces("AI Media Worker の 結果\r\n次 の 行");

        Assert.Equal("AI Media Workerの結果\r\n次の行", result);
    }

    [Fact]
    public void SelectBestKeepsProfileResultForOrdinaryEnglish()
    {
        var result = OcrTextPostProcessor.SelectBest([
            new OcrTextCandidate("hello world", OcrLanguageKind.English, true),
            new OcrTextCandidate("hello wor1d", OcrLanguageKind.Korean, false),
            new OcrTextCandidate("hello worid", OcrLanguageKind.Japanese, false)
        ]);

        Assert.Equal("hello world", result);
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
}
