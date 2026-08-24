using System.Runtime.InteropServices;
using System.Text;

namespace AIMediaWorker.Views;

internal sealed record ScreenWindow(nint Handle, string Title)
{
    public string DisplayName => Title;
}

internal static class ScreenWindowEnumerator
{
    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    public static IReadOnlyList<ScreenWindow> GetCapturableWindows()
    {
        var shellWindow = GetShellWindow();
        var result = new List<ScreenWindow>();
        EnumWindows((window, _) =>
        {
            if (window == shellWindow || !IsWindowVisible(window) || GetWindowTextLength(window) == 0) return true;
            if (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(uint)) == 0 && cloaked != 0) return true;
            var length = GetWindowTextLength(window);
            var title = new StringBuilder(length + 1);
            _ = GetWindowText(window, title, title.Capacity);
            var value = title.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(value)) result.Add(new ScreenWindow(window, value));
            return true;
        }, nint.Zero);
        return result.OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximumCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out uint value, int valueSize);
}
