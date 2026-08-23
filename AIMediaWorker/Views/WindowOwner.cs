using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace AIMediaWorker.Views;

internal static class WindowOwner
{
    private const int GwlHwndParent = -8;

    public static void Attach(Window child, Window owner)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(owner);
        var childHandle = WindowNative.GetWindowHandle(child);
        var ownerHandle = WindowNative.GetWindowHandle(owner);
        Marshal.SetLastPInvokeError(0);
        var previousOwner = SetWindowLongPtr(childHandle, GwlHwndParent, ownerHandle);
        var error = Marshal.GetLastPInvokeError();
        if (previousOwner == 0 && error != 0) throw new System.ComponentModel.Win32Exception(error, "The subwindow could not be attached to the main window.");
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);
}
