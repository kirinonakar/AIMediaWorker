using AIMediaWorker.Playback;
using AIMediaWorker.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace AIMediaWorker.Views;

/// <summary>Owns native window placement, title-bar styling, theme, and themed chrome icons.</summary>
internal sealed class WindowChromeController
{
    private readonly AppWindow? _appWindow;
    private readonly WindowChromeViewElements _view;
    private readonly Func<PlaybackState> _getPlaybackState;
    private readonly Func<bool> _getMuted;
    private readonly Func<string> _getRepeatIconName;
    private readonly Func<bool> _getRightPanelVisible;
    private readonly Func<bool> _getBottomPanelVisible;
    private readonly Func<bool> _getStatusPanelVisible;

    public WindowChromeController(
        AppWindow? appWindow,
        WindowChromeViewElements view,
        Func<PlaybackState> getPlaybackState,
        Func<bool> getMuted,
        Func<string> getRepeatIconName,
        Func<bool> getRightPanelVisible,
        Func<bool> getBottomPanelVisible,
        Func<bool> getStatusPanelVisible)
    {
        _appWindow = appWindow;
        _view = view;
        _getPlaybackState = getPlaybackState;
        _getMuted = getMuted;
        _getRepeatIconName = getRepeatIconName;
        _getRightPanelVisible = getRightPanelVisible;
        _getBottomPanelVisible = getBottomPanelVisible;
        _getStatusPanelVisible = getStatusPanelVisible;
    }

    public void ApplySavedWindowPlacement(WindowLayoutSettings layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (_appWindow is null || !layout.HasPlacement) return;
        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var maxWidth = Math.Max(1, workArea.Width - 32);
        var maxHeight = Math.Max(1, workArea.Height - 32);
        var minWidth = Math.Min(640, maxWidth);
        var minHeight = Math.Min(420, maxHeight);
        var width = Math.Clamp(layout.Width, minWidth, maxWidth);
        var height = Math.Clamp(layout.Height, minHeight, maxHeight);
        var x = Math.Clamp(layout.X, workArea.X, workArea.X + workArea.Width - width);
        var y = Math.Clamp(layout.Y, workArea.Y, workArea.Y + workArea.Height - height);
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        if (layout.IsMaximized && _appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }

    public void ResizeToAvailableWorkArea(int preferredWidth, int preferredHeight)
    {
        if (_appWindow is null) return;
        var workArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(preferredWidth, Math.Max(1, workArea.Width - 32));
        var height = Math.Min(preferredHeight, Math.Max(1, workArea.Height - 32));
        _appWindow.Resize(new SizeInt32(width, height));
    }

    public static void CaptureWindowPlacement(AppWindow window, OverlappedPresenter presenter, WindowLayoutSettings layout)
    {
        layout.IsMaximized = presenter.State == OverlappedPresenterState.Maximized;
        if (presenter.State != OverlappedPresenterState.Restored) return;
        layout.HasPlacement = true;
        layout.X = window.Position.X;
        layout.Y = window.Position.Y;
        layout.Width = window.Size.Width;
        layout.Height = window.Size.Height;
    }

    public void ApplyTheme(AppTheme theme)
    {
        _view.Root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyTitleBarTheme(_view.Root.ActualTheme);
        UpdateIcons();
    }

    public SvgImageSource PlaybackIconSource(string name) => new()
    {
        UriSource = new Uri($"ms-appx:///Assets/Playback/{name}{(_view.Root.ActualTheme == ElementTheme.Dark ? "-dark" : string.Empty)}.svg")
    };

    public void UpdateIcons()
    {
        _view.BeginningIcon.Source = PlaybackIconSource("beginning");
        _view.PreviousIcon.Source = PlaybackIconSource("previous");
        _view.SeekBackIcon.Source = PlaybackIconSource("seek-back");
        _view.PlayPauseIcon.Source = PlaybackIconSource(_getPlaybackState() == PlaybackState.Playing ? "pause" : "play");
        _view.StopIcon.Source = PlaybackIconSource("stop");
        _view.SeekForwardIcon.Source = PlaybackIconSource("seek-forward");
        _view.NextIcon.Source = PlaybackIconSource("next");
        _view.MuteIcon.Source = PlaybackIconSource(_getMuted() ? "mute" : "volume");
        _view.RepeatIcon.Source = PlaybackIconSource(_getRepeatIconName());
        UpdatePanelToggleIcons();
    }

    public void UpdatePanelToggleIcons()
    {
        _view.BottomPanelToggleIcon.Source = PanelToggleIconSource("bottom-panel", _getBottomPanelVisible());
        _view.StatusPanelToggleIcon.Source = PanelToggleIconSource("status-panel", _getStatusPanelVisible());
        _view.RightPanelToggleIcon.Source = PanelToggleIconSource("right-panel", _getRightPanelVisible());
    }

    public void ActualThemeChanged(ElementTheme theme)
    {
        ApplyTitleBarTheme(theme);
        UpdateIcons();
    }

    public void UpdateTitleBarDragRegion()
    {
        if (_appWindow?.TitleBar is not { } titleBar) return;
        var scale = _view.Root.XamlRoot?.RasterizationScale ?? 1.0;
        var left = 0.0;
        var top = 0.0;
        var right = 0.0;
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
        {
            var border = 8 * scale;
            left = border;
            top = border;
            right = border;
        }
        var width = _view.TitleBarArea.ActualWidth * scale;
        var height = _view.TitleBarArea.ActualHeight * scale;
        var dragWidth = Math.Max(0, width - left - right - titleBar.RightInset);
        var dragHeight = Math.Max(0, height - top);
        titleBar.SetDragRectangles([new RectInt32((int)left, (int)top, (int)dragWidth, (int)dragHeight)]);
    }

    private SvgImageSource PanelToggleIconSource(string name, bool isOpen) => new()
    {
        UriSource = new Uri($"ms-appx:///Assets/Panels/{name}{(isOpen ? string.Empty : "-closed")}{(_view.Root.ActualTheme == ElementTheme.Dark ? "-dark" : string.Empty)}.svg")
    };

    private void ApplyTitleBarTheme(ElementTheme theme)
    {
        if (_appWindow?.TitleBar is not { } titleBar) return;
        var dark = theme == ElementTheme.Dark;
        var background = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 24, 24, 24);
        var inactiveForeground = dark ? Windows.UI.Color.FromArgb(255, 160, 160, 160) : Windows.UI.Color.FromArgb(255, 110, 110, 110);
        var hover = dark ? Windows.UI.Color.FromArgb(255, 58, 58, 58) : Windows.UI.Color.FromArgb(255, 224, 224, 224);
        var pressed = dark ? Windows.UI.Color.FromArgb(255, 72, 72, 72) : Windows.UI.Color.FromArgb(255, 208, 208, 208);
        _view.TitleBarArea.Background = new SolidColorBrush(background);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
    }
}

internal sealed record WindowChromeViewElements(
    FrameworkElement Root,
    Grid TitleBarArea,
    Image BeginningIcon,
    Image PreviousIcon,
    Image SeekBackIcon,
    Image PlayPauseIcon,
    Image StopIcon,
    Image SeekForwardIcon,
    Image NextIcon,
    Image MuteIcon,
    Image RepeatIcon,
    Image BottomPanelToggleIcon,
    Image StatusPanelToggleIcon,
    Image RightPanelToggleIcon);
