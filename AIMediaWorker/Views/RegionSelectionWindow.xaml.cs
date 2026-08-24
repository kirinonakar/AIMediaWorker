using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace AIMediaWorker.Views;

internal sealed partial class RegionSelectionWindow : Window
{
    private readonly RectInt32 _virtualBounds;
    private readonly TaskCompletionSource<Rectangle?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NativePoint? _dragStart;
    private bool _completed;

    public RegionSelectionWindow(Window owner)
    {
        InitializeComponent();
        InstructionText.Text = L("RegionSelectionInstruction");
        WindowOwner.Attach(this, owner);
        _virtualBounds = GetVirtualScreenBounds();
        LoadScreenPreview();
        var handle = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        appWindow.MoveAndResize(_virtualBounds);
        Closed += OnClosed;
    }

    public async Task<Rectangle?> SelectAsync()
    {
        Activate();
        FocusTarget.Focus(FocusState.Programmatic);
        return await _completion.Task;
    }

    private void LoadScreenPreview()
    {
        try
        {
            using var bitmap = new Bitmap(_virtualBounds.Width, _virtualBounds.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(_virtualBounds.X, _virtualBounds.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);

            var rowBytes = checked(_virtualBounds.Width * 4);
            var pixels = new byte[checked(rowBytes * _virtualBounds.Height)];
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (var row = 0; row < bitmap.Height; row++)
                    Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * rowBytes, rowBytes);
            }
            finally { bitmap.UnlockBits(data); }

            var source = new WriteableBitmap(bitmap.Width, bitmap.Height);
            using (var stream = source.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
            source.Invalidate();
            ScreenPreview.Source = source;
        }
        catch (Exception exception)
        {
            _ = AppLog.WriteAsync("error", "screen-recording", "REGION_PREVIEW_CAPTURE_ERROR", exception.Message, exception);
        }
    }

    public static Rectangle GetPrimaryScreenBounds() => new(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!GetCursorPos(out var point)) return;
        _dragStart = point;
        SelectionSurface.CapturePointer(e.Pointer);
        UpdateSelection(point);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null || !GetCursorPos(out var point)) return;
        UpdateSelection(point);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is not { } start || !GetCursorPos(out var end)) return;
        SelectionSurface.ReleasePointerCapture(e.Pointer);
        _dragStart = null;
        var selected = Normalize(start, end);
        if (selected.Width < 2 || selected.Height < 2) { SelectionBorder.Visibility = Visibility.Collapsed; return; }
        Complete(selected);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        Complete(null);
        e.Handled = true;
    }

    private void UpdateSelection(NativePoint end)
    {
        if (_dragStart is not { } start) return;
        var selected = Normalize(start, end);
        var scaleX = SelectionSurface.ActualWidth <= 0 ? 1 : SelectionSurface.ActualWidth / _virtualBounds.Width;
        var scaleY = SelectionSurface.ActualHeight <= 0 ? 1 : SelectionSurface.ActualHeight / _virtualBounds.Height;
        SelectionBorder.Width = selected.Width * scaleX;
        SelectionBorder.Height = selected.Height * scaleY;
        SelectionBorder.Margin = new Thickness((selected.X - _virtualBounds.X) * scaleX, (selected.Y - _virtualBounds.Y) * scaleY, 0, 0);
        SelectionSizeText.Text = $"{selected.Width} × {selected.Height}";
        SelectionBorder.Visibility = Visibility.Visible;
    }

    private void Complete(Rectangle? result)
    {
        if (_completed) return;
        _completed = true;
        _completion.TrySetResult(result);
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (!_completed) { _completed = true; _completion.TrySetResult(null); }
    }

    private static Rectangle Normalize(NativePoint first, NativePoint second) => Rectangle.FromLTRB(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Max(first.X, second.X),
        Math.Max(first.Y, second.Y));

    private static RectInt32 GetVirtualScreenBounds() => new(
        GetSystemMetrics(76),
        GetSystemMetrics(77),
        Math.Max(1, GetSystemMetrics(78)),
        Math.Max(1, GetSystemMetrics(79)));

    private static string L(string key) => LocalizationService.Get(key);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
