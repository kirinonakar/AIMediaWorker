using System.Runtime.InteropServices;

namespace AIMediaWorker.Asr;

/// <summary>
/// Minimal C# projection of the prebuilt CrispASR C ABI shipped under
/// asr-worker/crispasr. The WinUI process owns the native sessions;
/// no helper process is started.
/// </summary>
internal static class CrispAsrNative
{
    private const string LibraryName = "crispasr";
    private static readonly object LoadLock = new();
    private static nint _library;
    private static string? _runtimeDirectory;

    [DllImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string? pathName);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint crispasr_session_open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        int nThreads);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void crispasr_session_close(nint session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint crispasr_session_transcribe_lang(
        nint session,
        [In] float[] pcm,
        int nSamples,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? language);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int crispasr_session_result_n_segments(nint result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint crispasr_session_result_segment_text(nint result, int index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long crispasr_session_result_segment_t0(nint result, int index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long crispasr_session_result_segment_t1(nint result, int index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int crispasr_session_result_n_words(nint result, int segmentIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint crispasr_session_result_word_text(nint result, int segmentIndex, int wordIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long crispasr_session_result_word_t0(nint result, int segmentIndex, int wordIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long crispasr_session_result_word_t1(nint result, int segmentIndex, int wordIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern float crispasr_session_result_word_p(nint result, int segmentIndex, int wordIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void crispasr_session_result_free(nint result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint crispasr_align_words_abi(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string alignerModel,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string transcript,
        [In] float[] samples,
        int nSamples,
        long timeOffsetCentiseconds,
        int nThreads);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int crispasr_align_result_n_words(nint result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint crispasr_align_result_word_text(nint result, int index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long crispasr_align_result_word_t0(nint result, int index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long crispasr_align_result_word_t1(nint result, int index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void crispasr_align_result_free(nint result);

    public static string? LoadedRuntimeDirectory
    {
        get
        {
            lock (LoadLock) return _runtimeDirectory;
        }
    }

    public static void EnsureLoaded(string runtimeDirectory)
    {
        var fullDirectory = Path.GetFullPath(runtimeDirectory);
        var libraryPath = Path.Combine(fullDirectory, "crispasr.dll");
        if (!File.Exists(libraryPath))
            throw new FileNotFoundException("The CrispASR native runtime was not found.", libraryPath);

        lock (LoadLock)
        {
            if (_library != 0)
            {
                if (!string.Equals(_runtimeDirectory, fullDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"CrispASR is already loaded from '{_runtimeDirectory}'.");
                return;
            }

            if (!SetDllDirectory(fullDirectory))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the CrispASR DLL directory.");
            if (!NativeLibrary.TryLoad(libraryPath, out _library) || _library == 0)
                throw new DllNotFoundException($"Could not load the CrispASR native runtime: {libraryPath}");

            _runtimeDirectory = fullDirectory;
        }
    }

    public static nint OpenSession(string modelPath, int nThreads)
    {
        // Let CrispASR inspect general.architecture. This is the same path as
        // its supported C# binding and avoids passing a CLI-only alias such as
        // qwen3-1.7b to the native dispatcher.
        var session = crispasr_session_open(modelPath, nThreads);
        if (session == 0) throw new InvalidOperationException($"CrispASR could not open the Qwen3 session for '{modelPath}'.");
        return session;
    }

    public static void CloseSession(nint session)
    {
        if (session != 0) crispasr_session_close(session);
    }

    public static NativeSegment[] Transcribe(nint session, float[] samples, string? language)
    {
        var result = crispasr_session_transcribe_lang(session, samples, samples.Length, language);
        if (result == 0) throw new InvalidOperationException("CrispASR transcription failed.");

        try
        {
            var segmentCount = Math.Max(0, crispasr_session_result_n_segments(result));
            var segments = new NativeSegment[segmentCount];
            for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                var wordCount = Math.Max(0, crispasr_session_result_n_words(result, segmentIndex));
                var words = new NativeWord[wordCount];
                for (var wordIndex = 0; wordIndex < wordCount; wordIndex++)
                {
                    words[wordIndex] = new NativeWord(
                        Utf8(crispasr_session_result_word_text(result, segmentIndex, wordIndex)),
                        crispasr_session_result_word_t0(result, segmentIndex, wordIndex),
                        crispasr_session_result_word_t1(result, segmentIndex, wordIndex),
                        crispasr_session_result_word_p(result, segmentIndex, wordIndex));
                }

                segments[segmentIndex] = new NativeSegment(
                    Utf8(crispasr_session_result_segment_text(result, segmentIndex)),
                    crispasr_session_result_segment_t0(result, segmentIndex),
                    crispasr_session_result_segment_t1(result, segmentIndex),
                    words);
            }

            return segments;
        }
        finally
        {
            crispasr_session_result_free(result);
        }
    }

    public static NativeWord[] AlignWords(string alignerModel, string transcript, float[] samples, int nThreads)
    {
        var result = crispasr_align_words_abi(alignerModel, transcript, samples, samples.Length, 0, nThreads);
        if (result == 0) throw new InvalidOperationException("CrispASR forced alignment failed.");

        try
        {
            var count = Math.Max(0, crispasr_align_result_n_words(result));
            var words = new NativeWord[count];
            for (var index = 0; index < count; index++)
            {
                words[index] = new NativeWord(
                    Utf8(crispasr_align_result_word_text(result, index)),
                    crispasr_align_result_word_t0(result, index),
                    crispasr_align_result_word_t1(result, index),
                    null);
            }
            return words;
        }
        finally
        {
            crispasr_align_result_free(result);
        }
    }

    private static string Utf8(nint value) => value == 0 ? string.Empty : Marshal.PtrToStringUTF8(value) ?? string.Empty;

    public readonly record struct NativeWord(string Text, long StartCentiseconds, long EndCentiseconds, float? Probability);
    public readonly record struct NativeSegment(string Text, long StartCentiseconds, long EndCentiseconds, NativeWord[] Words);
}
