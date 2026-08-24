using System.Text;

namespace AIMediaWorker.Subtitle.Parsing;

public static class SubtitleTextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LittleEndianPreamble = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianPreamble = [0xFE, 0xFF];

    static SubtitleTextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Decode(byte[] bytes, Encoding fallbackEncoding, bool detectKorean = false)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(fallbackEncoding);

        if (bytes.AsSpan().StartsWith(Utf8Preamble)) return StrictUtf8.GetString(bytes, Utf8Preamble.Length, bytes.Length - Utf8Preamble.Length);
        if (bytes.AsSpan().StartsWith(Utf16LittleEndianPreamble)) return Encoding.Unicode.GetString(bytes, Utf16LittleEndianPreamble.Length, bytes.Length - Utf16LittleEndianPreamble.Length);
        if (bytes.AsSpan().StartsWith(Utf16BigEndianPreamble)) return Encoding.BigEndianUnicode.GetString(bytes, Utf16BigEndianPreamble.Length, bytes.Length - Utf16BigEndianPreamble.Length);

        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            if (detectKorean)
            {
                // CP949 is a superset of EUC-KR and covers the legacy Korean SAMI files
                // commonly found alongside older media.
                var korean = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                try { return korean.GetString(bytes); }
                catch (DecoderFallbackException) { }
            }

            return fallbackEncoding.GetString(bytes);
        }
    }
}
