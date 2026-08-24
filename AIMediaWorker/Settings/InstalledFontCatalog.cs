using System.Runtime.InteropServices;

namespace AIMediaWorker.Settings;

public static class InstalledFontCatalog
{
    private const byte DefaultCharSet = 1;
    private static readonly Lazy<Task<IReadOnlyList<string>>> CachedFonts = new(
        () => Task.Run(LoadInstalledFonts),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task<IReadOnlyList<string>> GetAsync() => CachedFonts.Value;

    private static IReadOnlyList<string> LoadInstalledFonts()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GeneralSettings.DefaultUiFontFamily
        };
        var deviceContext = GetDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero) return Sort(names);

        try
        {
            var font = new LogFont { CharacterSet = DefaultCharSet, FaceName = string.Empty };
            EnumFontFamiliesEx(deviceContext, ref font, (fontInfo, _, _, _) =>
            {
                var discovered = Marshal.PtrToStructure<LogFont>(fontInfo).FaceName?.Trim();
                if (!string.IsNullOrWhiteSpace(discovered) && !discovered.StartsWith('@')) names.Add(discovered);
                return 1;
            }, IntPtr.Zero, 0);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, deviceContext);
        }

        return Sort(names);
    }

    private static IReadOnlyList<string> Sort(IEnumerable<string> names) => names
        .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    private delegate int EnumFontFamilyCallback(IntPtr fontInfo, IntPtr textMetric, uint fontType, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        public int Height;
        public int Width;
        public int Escapement;
        public int Orientation;
        public int Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharacterSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumFontFamiliesExW")]
    private static extern int EnumFontFamiliesEx(
        IntPtr deviceContext,
        ref LogFont font,
        EnumFontFamilyCallback callback,
        IntPtr parameter,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);
}
