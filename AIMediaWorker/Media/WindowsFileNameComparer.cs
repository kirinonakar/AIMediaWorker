using System.Runtime.InteropServices;

namespace AIMediaWorker.Media;

/// <summary>Compares file names using the same logical ordering as the Windows shell.</summary>
public sealed class WindowsFileNameComparer : IComparer<string?>
{
    public static WindowsFileNameComparer Instance { get; } = new();

    private WindowsFileNameComparer() { }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        return OperatingSystem.IsWindows()
            ? StrCmpLogicalW(x, y)
            : StringComparer.CurrentCultureIgnoreCase.Compare(x, y);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
