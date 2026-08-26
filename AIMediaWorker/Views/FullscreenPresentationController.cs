using AIMediaWorker.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace AIMediaWorker.Views;

/// <summary>
/// Owns native full-screen state and the transient chrome shown at the screen edges.
/// The window only tells this component when full-screen should change.
/// </summary>
internal sealed class FullscreenPresentationController : IDisposable
{
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly TimeSpan CursorHideDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EdgeClickDisplayDuration = TimeSpan.FromSeconds(2);

    private readonly Window _window;
    private readonly AppWindow? _appWindow;
    private readonly FullscreenViewElements _view;
    private readonly Func<double> _getRightPanelWidth;
    private readonly Action _applyWindowedPanelVisibility;
    private readonly Action _focusPlaybackSurface;
    private readonly Action<bool> _setPlaybackCursorHidden;
    private readonly Action<string> _reportError;
    private readonly DispatcherQueueTimer _hoverTimer;
    private bool _repairQueued;
    private bool _styleCaptured;
    private int _windowedStyle;
    private RectInt32? _windowBoundsBeforeFullscreen;
    private RectInt32? _workAreaBeforeFullscreen;
    private bool _wasMaximizedBeforeFullscreen;
    private DateTimeOffset _showMenuUntil;
    private DateTimeOffset _showControlsUntil;
    private DateTimeOffset _showRightPanelUntil;
    private DateTimeOffset _cursorLastMovedAt;
    private NativePoint? _lastCursorPosition;
    private bool _cursorHidden;

    public FullscreenPresentationController(
        Window window,
        AppWindow? appWindow,
        FullscreenViewElements view,
        Func<double> getRightPanelWidth,
        Action applyWindowedPanelVisibility,
        Action focusPlaybackSurface,
        Action<bool> setPlaybackCursorHidden,
        Action<string> reportError)
    {
        _window = window;
        _appWindow = appWindow;
        _view = view;
        _getRightPanelWidth = getRightPanelWidth;
        _applyWindowedPanelVisibility = applyWindowedPanelVisibility;
        _focusPlaybackSurface = focusPlaybackSurface;
        _setPlaybackCursorHidden = setPlaybackCursorHidden;
        _reportError = reportError;
        _hoverTimer = window.DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(100);
        _hoverTimer.Tick += OnHoverTick;
    }

    public bool IsFullscreen { get; private set; }
    public bool IsChanging { get; private set; }

    public void Toggle()
    {
        if (IsFullscreen) Exit();
        else Enter();
    }

    public void Enter()
    {
        if (_appWindow is null || IsFullscreen) return;
        IsChanging = true;
        try
        {
            var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
            _wasMaximizedBeforeFullscreen = _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            if (_wasMaximizedBeforeFullscreen && _appWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
            _windowBoundsBeforeFullscreen = new RectInt32(_appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);
            _workAreaBeforeFullscreen = display.WorkArea;
            IsFullscreen = true;
            HideWindowedChrome();
            ApplyFullscreenWindowStyle();
            _appWindow.MoveAndResize(display.OuterBounds);
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            ApplyFullscreenWindowStyle();
            ResetCursorIdle();
            _hoverTimer.Start();
            _focusPlaybackSurface();
        }
        catch (Exception exception)
        {
            IsFullscreen = false;
            SetCursorHidden(false);
            RestoreWindowStyle();
            RestoreWindowBounds(DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest));
            _applyWindowedPanelVisibility();
            _reportError(exception.Message);
        }
        finally { IsChanging = false; }
    }

    public void Exit()
    {
        if (_appWindow is null || !IsFullscreen) return;
        IsChanging = true;
        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
        IsFullscreen = false;
        try
        {
            _appWindow.SetPresenter(AppWindowPresenterKind.Default);
            RestoreWindowStyle();
            RestoreWindowBounds(display);
        }
        finally
        {
            _hoverTimer.Stop();
            SetCursorHidden(false);
            _lastCursorPosition = null;
            _view.MainMenuBarHost.Visibility = Visibility.Visible;
            _view.TitleBarArea.Visibility = Visibility.Visible;
            _view.PlaybackControls.Visibility = Visibility.Visible;
            _view.VideoPlaceholder.Margin = new Thickness(8, 4, 4, 4);
            _applyWindowedPanelVisibility();
            IsChanging = false;
        }
    }

    /// <returns><see langword="true"/> when the change belongs to full-screen handling.</returns>
    public bool HandleAppWindowChanged()
    {
        if (IsChanging) return true;
        if (!IsFullscreen) return false;
        ApplyFullscreenWindowStyle();
        if (_appWindow?.Presenter.Kind != AppWindowPresenterKind.FullScreen) QueueRepair();
        return true;
    }

    /// <summary>
    /// Reveals the transient full-screen chrome associated with the edge under the pointer.
    /// This is called for clicks received by the native video child window, which is outside
    /// the XAML pointer event route.
    /// </summary>
    public bool RevealPanelAtCurrentPointer()
    {
        if (!IsFullscreen || _appWindow is null || !GetCursorPos(out var cursor)) return false;

        var left = _appWindow.Position.X;
        var top = _appWindow.Position.Y;
        var right = left + _appWindow.Size.Width;
        var bottom = top + _appWindow.Size.Height;
        if (cursor.X < left || cursor.X >= right || cursor.Y < top || cursor.Y >= bottom) return false;

        var now = DateTimeOffset.UtcNow;
        var showUntil = now.Add(EdgeClickDisplayDuration);
        var revealed = false;
        if (cursor.Y <= top + 32)
        {
            _showMenuUntil = LaterOf(_showMenuUntil, showUntil);
            revealed = true;
        }
        if (cursor.Y >= bottom - 32)
        {
            _showControlsUntil = LaterOf(_showControlsUntil, showUntil);
            revealed = true;
        }
        if (cursor.X >= right - 64)
        {
            _showRightPanelUntil = LaterOf(_showRightPanelUntil, showUntil);
            revealed = true;
        }

        if (!revealed) return false;
        _cursorLastMovedAt = now;
        SetCursorHidden(false);
        ApplyTransientPanelVisibility(now);
        return true;
    }

    public void Dispose()
    {
        _hoverTimer.Stop();
        _hoverTimer.Tick -= OnHoverTick;
        SetCursorHidden(false);
    }

    private void HideWindowedChrome()
    {
        _view.MainMenuBarHost.Visibility = Visibility.Collapsed;
        _view.TitleBarArea.Visibility = Visibility.Collapsed;
        _view.PlaybackControls.Visibility = Visibility.Collapsed;
        _view.VisualizationPanel.Visibility = Visibility.Collapsed;
        _view.StatusPanel.Visibility = Visibility.Collapsed;
        _view.RightPanel.Visibility = Visibility.Collapsed;
        _view.RightPanelSplitter.Visibility = Visibility.Collapsed;
        _view.RightPanelSplitterColumn.Width = new GridLength(0);
        _view.RightPanelColumn.Width = new GridLength(0);
        _view.BottomPanelSplitter.Visibility = Visibility.Collapsed;
        _view.BottomPanelSplitterRow.Height = new GridLength(0);
        _view.BottomPanelRow.Height = new GridLength(0);
        _view.VideoPlaceholder.Margin = new Thickness(0);
    }

    private void QueueRepair()
    {
        if (_repairQueued || _appWindow is null) return;
        _repairQueued = true;
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            _repairQueued = false;
            if (!IsFullscreen || _appWindow is null) return;
            IsChanging = true;
            try
            {
                var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
                ApplyFullscreenWindowStyle();
                _appWindow.MoveAndResize(display.OuterBounds);
                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                ApplyFullscreenWindowStyle();
            }
            catch (Exception exception)
            {
                _ = AppLog.WriteAsync("error", "fullscreen", "FULLSCREEN_REPAIR_ERROR", exception.Message, exception);
            }
            finally { IsChanging = false; }
        });
    }

    private void RestoreWindowBounds(DisplayArea currentDisplay)
    {
        if (_appWindow is null || _windowBoundsBeforeFullscreen is not { } bounds) return;
        if (_workAreaBeforeFullscreen is { } previousWorkArea &&
            (previousWorkArea.X != currentDisplay.WorkArea.X || previousWorkArea.Y != currentDisplay.WorkArea.Y))
        {
            var width = Math.Min(bounds.Width, currentDisplay.WorkArea.Width);
            var height = Math.Min(bounds.Height, currentDisplay.WorkArea.Height);
            var relativeX = Math.Max(0, bounds.X - previousWorkArea.X);
            var relativeY = Math.Max(0, bounds.Y - previousWorkArea.Y);
            bounds = new RectInt32(
                currentDisplay.WorkArea.X + Math.Min(relativeX, Math.Max(0, currentDisplay.WorkArea.Width - width)),
                currentDisplay.WorkArea.Y + Math.Min(relativeY, Math.Max(0, currentDisplay.WorkArea.Height - height)),
                width,
                height);
        }
        _appWindow.MoveAndResize(bounds);
        if (_wasMaximizedBeforeFullscreen && _appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        _windowBoundsBeforeFullscreen = null;
        _workAreaBeforeFullscreen = null;
        _wasMaximizedBeforeFullscreen = false;
    }

    private void ApplyFullscreenWindowStyle()
    {
        var handle = WindowNative.GetWindowHandle(_window);
        var style = GetWindowLong(handle, GwlStyle);
        if (!_styleCaptured) { _windowedStyle = style; _styleCaptured = true; }
        var framelessStyle = style & ~(WsCaption | WsThickFrame);
        if (framelessStyle == style) return;
        SetWindowLong(handle, GwlStyle, framelessStyle);
        SetWindowPos(handle, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void RestoreWindowStyle()
    {
        if (!_styleCaptured) return;
        var handle = WindowNative.GetWindowHandle(_window);
        SetWindowLong(handle, GwlStyle, _windowedStyle);
        SetWindowPos(handle, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        _styleCaptured = false;
    }

    private void OnHoverTick(DispatcherQueueTimer sender, object args)
    {
        if (!IsFullscreen || _appWindow is null) return;
        if (!GetCursorPos(out var cursor))
        {
            SetCursorHidden(false);
            return;
        }

        var left = _appWindow.Position.X;
        var top = _appWindow.Position.Y;
        var right = left + _appWindow.Size.Width;
        var bottom = top + _appWindow.Size.Height;
        var now = DateTimeOffset.UtcNow;
        var inside = cursor.X >= left && cursor.X < right && cursor.Y >= top && cursor.Y < bottom;
        var moved = _lastCursorPosition is not { } previous || previous.X != cursor.X || previous.Y != cursor.Y;
        _lastCursorPosition = cursor;
        if (moved || !inside)
        {
            _cursorLastMovedAt = now;
            SetCursorHidden(false);
        }
        else if (now - _cursorLastMovedAt >= CursorHideDelay)
        {
            SetCursorHidden(true);
        }

        if (inside)
        {
            if (cursor.Y <= top + 32 || _view.MainMenuBarHost.Visibility == Visibility.Visible && cursor.Y <= top + 70)
                _showMenuUntil = LaterOf(_showMenuUntil, now.AddSeconds(1.5));
            if (cursor.Y >= bottom - 32 || _view.PlaybackControls.Visibility == Visibility.Visible && cursor.Y >= bottom - 150)
                _showControlsUntil = LaterOf(_showControlsUntil, now.AddSeconds(1.5));
        }
        var verticallyAligned = cursor.Y >= top && cursor.Y < bottom;
        if (verticallyAligned &&
            (cursor.X >= right - 64 && cursor.X <= right + 24 ||
             _view.RightPanel.Visibility == Visibility.Visible && cursor.X >= right - _getRightPanelWidth() - 40 && cursor.X < right))
            _showRightPanelUntil = LaterOf(_showRightPanelUntil, now.AddSeconds(1.5));

        ApplyTransientPanelVisibility(now);
    }

    private void ApplyTransientPanelVisibility(DateTimeOffset now)
    {
        var showTopChrome = now < _showMenuUntil;
        _view.TitleBarArea.Visibility = showTopChrome ? Visibility.Visible : Visibility.Collapsed;
        _view.MainMenuBarHost.Visibility = showTopChrome ? Visibility.Visible : Visibility.Collapsed;
        var showControls = now < _showControlsUntil;
        _view.PlaybackControls.Visibility = showControls ? Visibility.Visible : Visibility.Collapsed;
        _view.StatusPanel.Visibility = showControls ? Visibility.Visible : Visibility.Collapsed;
        var showRight = now < _showRightPanelUntil;
        _view.RightPanel.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        _view.RightPanelSplitter.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        _view.RightPanelSplitterColumn.Width = showRight ? new GridLength(6) : new GridLength(0);
        _view.RightPanelColumn.Width = showRight ? new GridLength(_getRightPanelWidth()) : new GridLength(0);
    }

    private static DateTimeOffset LaterOf(DateTimeOffset first, DateTimeOffset second) => first >= second ? first : second;

    private void ResetCursorIdle()
    {
        _cursorLastMovedAt = DateTimeOffset.UtcNow;
        _lastCursorPosition = GetCursorPos(out var cursor) ? cursor : null;
        SetCursorHidden(false);
    }

    private void SetCursorHidden(bool hidden)
    {
        if (_cursorHidden == hidden)
        {
            if (hidden) _setPlaybackCursorHidden(true);
            return;
        }
        _cursorHidden = hidden;
        _setPlaybackCursorHidden(hidden);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}

internal sealed record FullscreenViewElements(
    FrameworkElement MainMenuBarHost,
    FrameworkElement TitleBarArea,
    FrameworkElement PlaybackControls,
    FrameworkElement VisualizationPanel,
    FrameworkElement StatusPanel,
    FrameworkElement RightPanel,
    FrameworkElement RightPanelSplitter,
    ColumnDefinition RightPanelSplitterColumn,
    ColumnDefinition RightPanelColumn,
    FrameworkElement BottomPanelSplitter,
    RowDefinition BottomPanelSplitterRow,
    RowDefinition BottomPanelRow,
    FrameworkElement VideoPlaceholder);
