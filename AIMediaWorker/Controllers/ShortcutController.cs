using AIMediaWorker.Localization;
using AIMediaWorker.Settings;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace AIMediaWorker.Controllers;

/// <summary>Owns keyboard gesture routing and the accelerator/tool-tip projection for those gestures.</summary>
internal sealed class ShortcutController : IDisposable
{
    private const int OemLeftBracket = 0xDB;
    private const int OemBackslash = 0xDC;
    private const int OemRightBracket = 0xDD;

    private readonly ShortcutViewElements _view;
    private readonly ShortcutControllerHost _host;
    private readonly KeyEventHandler _previewKeyDownHandler;
    private readonly KeyEventHandler _keyDownHandler;
    private bool _disposed;

    public ShortcutController(ShortcutViewElements view, ShortcutControllerHost host)
    {
        _view = view;
        _host = host;
        _previewKeyDownHandler = OnPreviewKeyDown;
        _keyDownHandler = OnKeyDown;
        _view.Root.AddHandler(UIElement.PreviewKeyDownEvent, _previewKeyDownHandler, true);
        _view.Root.AddHandler(UIElement.KeyDownEvent, _keyDownHandler, true);
    }

    public void RefreshHints()
    {
        string Shortcut(string action) =>
            _host.GetSettings().General.Shortcuts.TryGetValue(action, out var gesture) ? gesture : string.Empty;
        static string Combine(params string[] gestures) => string.Join(" / ", gestures
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        _view.SaveSubtitleMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.SaveSubtitle);
        _view.SaveSubtitleAsMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.SaveSubtitleAs);
        _view.ExitMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.CloseWindow);
        _view.PlayPauseMenuItem.KeyboardAcceleratorTextOverride = Combine(Shortcut(ShortcutActions.PlayPause), Shortcut(ShortcutActions.PlayPauseAlternate));
        _view.SetAbStartMenuItem.KeyboardAcceleratorTextOverride = "[";
        _view.SetAbEndMenuItem.KeyboardAcceleratorTextOverride = "]";
        _view.ClearAbMenuItem.KeyboardAcceleratorTextOverride = "\\";
        _view.DeleteCueMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.DeleteCue);
        _view.PreviousSubtitleMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.PreviousSubtitle);
        _view.NextSubtitleMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.NextSubtitle);
        _view.UndoMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.Undo);
        _view.RedoMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.Redo);
        _view.SubtitleVisibilityMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.ToggleSubtitles);
        _view.ShowBottomPanelMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.ToggleTimelinePanel);
        _view.ShowRightPanelMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.ToggleSidePanel);
        _view.ShowStatusPanelMenuItem.KeyboardAcceleratorTextOverride = Shortcut(ShortcutActions.ToggleStatusPanel);
        _view.FullscreenMenuItem.KeyboardAcceleratorTextOverride = $"{Combine(Shortcut(ShortcutActions.Fullscreen), "Enter", "F", "F11")} · Esc";

        ToolTipService.SetToolTip(_view.BottomPanelToggleButton, F("TooltipToggleBottomPanel", Shortcut(ShortcutActions.ToggleTimelinePanel)));
        ToolTipService.SetToolTip(_view.RightPanelToggleButton, F("TooltipToggleRightPanel", Shortcut(ShortcutActions.ToggleSidePanel)));
        ToolTipService.SetToolTip(_view.StatusPanelToggleButton, F("TooltipToggleStatusPanel", Shortcut(ShortcutActions.ToggleStatusPanel)));
        AutomationProperties.SetName(_view.BottomPanelToggleButton, L("ShowBottomPanel.Text"));
        AutomationProperties.SetName(_view.RightPanelToggleButton, L("ShowRightPanel.Text"));
        AutomationProperties.SetName(_view.StatusPanelToggleButton, L("ShowStatusPanel.Text"));
        ToolTipService.SetToolTip(_view.PlayPauseButton, $"{L("PlayPause.Text")} ({Combine(Shortcut(ShortcutActions.PlayPause), Shortcut(ShortcutActions.PlayPauseAlternate))})");
        ToolTipService.SetToolTip(_view.BeginningButton, L("TooltipBeginning"));
        ToolTipService.SetToolTip(_view.PreviousButton, F("TooltipPreviousMedia", Shortcut(ShortcutActions.PreviousMedia)));
        ToolTipService.SetToolTip(_view.NextButton, F("TooltipNextMedia", Shortcut(ShortcutActions.NextMedia)));
        ToolTipService.SetToolTip(_view.SeekBackButton, F("TooltipSeekBackward", _host.GetSettings().Playback.SeekIntervalSeconds, Shortcut(ShortcutActions.SeekBackward)));
        ToolTipService.SetToolTip(_view.SeekForwardButton, F("TooltipSeekForward", _host.GetSettings().Playback.SeekIntervalSeconds, Shortcut(ShortcutActions.SeekForward)));
        ToolTipService.SetToolTip(_view.StopButton, L("Stop.Text"));
        ToolTipService.SetToolTip(_view.MuteButton, L("TooltipMute"));
        ToolTipService.SetToolTip(_view.VolumeSlider, L("TooltipVolume"));
        ToolTipService.SetToolTip(_view.PositionSlider, F("TooltipPosition", Shortcut(ShortcutActions.PlayFromBeginning)));
        ToolTipService.SetToolTip(_view.SubtitleList, F("TooltipSubtitleNavigation", Shortcut(ShortcutActions.PreviousSubtitle), Shortcut(ShortcutActions.NextSubtitle)));
        _host.RefreshRepeatToolTip();
        ToolTipService.SetToolTip(_view.CloseButton, F("TooltipClose", Shortcut(ShortcutActions.CloseWindow)));
        RefreshFullscreenState();
    }

    public void ToggleFullscreen()
    {
        try { _host.ToggleFullscreen(); }
        finally { RefreshFullscreenState(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Root.RemoveHandler(UIElement.PreviewKeyDownEvent, _previewKeyDownHandler);
        _view.Root.RemoveHandler(UIElement.KeyDownEvent, _keyDownHandler);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Space || e.OriginalSource is TextBox or PasswordBox) return;
        var (ctrl, shift, alt) = ModifierState();
        if (!Is(ShortcutActions.PlayPause, e.Key.ToString(), ctrl, shift, alt)) return;
        e.Handled = true;
        _host.FocusPlaybackSurface();
        _host.TogglePause();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space && e.Handled) return;
        var (ctrl, shift, alt) = ModifierState();
        var key = e.Key.ToString();
        var isTextInput = e.OriginalSource is TextBox or PasswordBox;

        if (e.Key == Windows.System.VirtualKey.Escape && _host.IsFullscreen())
        {
            _host.ExitFullscreen();
            RefreshFullscreenState();
            e.Handled = true;
            return;
        }
        if (ctrl && shift && !alt && e.Key == Windows.System.VirtualKey.N) { _host.PlayFromBeginning(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.F or Windows.System.VirtualKey.F11) { ToggleFullscreen(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.M) { _host.ToggleMute(); e.Handled = true; return; }
        if (_view.PlaybackFocusTarget.FocusState != FocusState.Unfocused && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Up) { _host.AdjustVolume(5); e.Handled = true; return; }
        if (_view.PlaybackFocusTarget.FocusState != FocusState.Unfocused && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Down) { _host.AdjustVolume(-5); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Back) { _host.PlayFromAbStartOrBeginning(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.Home) { _host.GoToBeginning(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && e.Key == Windows.System.VirtualKey.End) { _host.SeekToEnd(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && (int)e.Key == OemLeftBracket) { _host.SetAbStart(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && (int)e.Key == OemRightBracket) { _host.SetAbEnd(); e.Handled = true; return; }
        if (!isTextInput && !ctrl && !shift && !alt && (int)e.Key == OemBackslash) { _host.ClearAb(); e.Handled = true; return; }

        bool Matches(string action) => Is(action, key, ctrl, shift, alt);
        var save = Matches(ShortcutActions.SaveSubtitle);
        var saveAs = Matches(ShortcutActions.SaveSubtitleAs);
        var close = Matches(ShortcutActions.CloseWindow);
        var alternatePause = Matches(ShortcutActions.PlayPauseAlternate);
        var playFromBeginning = Matches(ShortcutActions.PlayFromBeginning);
        var previousMedia = Matches(ShortcutActions.PreviousMedia);
        var nextMedia = Matches(ShortcutActions.NextMedia);
        var toggleTimelinePanel = Matches(ShortcutActions.ToggleTimelinePanel);
        var toggleSidePanel = Matches(ShortcutActions.ToggleSidePanel);
        var toggleStatusPanel = Matches(ShortcutActions.ToggleStatusPanel);
        if (isTextInput && !save && !saveAs && !close && !alternatePause && !playFromBeginning && !previousMedia && !nextMedia && !toggleTimelinePanel && !toggleSidePanel && !toggleStatusPanel) return;

        if (close) _host.Close();
        else if (saveAs) _ = _host.SaveSubtitleAsAsync();
        else if (save) _ = _host.SaveSubtitleAsync();
        else if (playFromBeginning) _host.PlayFromBeginning();
        else if (Matches(ShortcutActions.PlayPause) || alternatePause) _host.TogglePause();
        else if (previousMedia) _ = _host.OpenPreviousAsync();
        else if (nextMedia) _ = _host.OpenNextAsync();
        else if (Matches(ShortcutActions.PreviousSubtitle)) _host.SelectRelativeCue(-1);
        else if (Matches(ShortcutActions.NextSubtitle)) _host.SelectRelativeCue(1);
        else if (Matches(ShortcutActions.SeekBackward)) _host.SeekBackward();
        else if (Matches(ShortcutActions.SeekForward)) _host.SeekForward();
        else if (Matches(ShortcutActions.Undo)) _host.Undo();
        else if (Matches(ShortcutActions.Redo)) _host.Redo();
        else if (Matches(ShortcutActions.DeleteCue)) _host.DeleteCue();
        else if (Matches(ShortcutActions.Fullscreen)) ToggleFullscreen();
        else if (Matches(ShortcutActions.ToggleSubtitles)) _host.ToggleSubtitles();
        else if (toggleTimelinePanel) _host.ToggleTimelinePanel();
        else if (toggleSidePanel) _host.ToggleSidePanel();
        else if (toggleStatusPanel) _host.ToggleStatusPanel();
        else return;
        e.Handled = true;
    }

    private bool Is(string action, string key, bool ctrl, bool shift, bool alt) =>
        _host.GetSettings().General.Shortcuts.TryGetValue(action, out var gesture) &&
        ShortcutGesture.Matches(gesture, key, ctrl, shift, alt);

    private void RefreshFullscreenState()
    {
        var fullscreen = _host.IsFullscreen();
        _view.FullscreenButton.IsChecked = fullscreen;
        _view.FullscreenButtonIcon.Glyph = fullscreen ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(_view.FullscreenButton, L(fullscreen ? "TooltipExitFullscreen" : "TooltipEnterFullscreen"));
    }

    private static (bool Ctrl, bool Shift, bool Alt) ModifierState() =>
    (
        IsDown(Windows.System.VirtualKey.Control),
        IsDown(Windows.System.VirtualKey.Shift),
        IsDown(Windows.System.VirtualKey.Menu)
    );

    private static bool IsDown(Windows.System.VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);
}

internal sealed record ShortcutControllerHost(
    Func<AppSettings> GetSettings,
    Func<bool> IsFullscreen,
    Action ExitFullscreen,
    Action ToggleFullscreen,
    Action FocusPlaybackSurface,
    Action Close,
    Func<Task> SaveSubtitleAsync,
    Func<Task> SaveSubtitleAsAsync,
    Action PlayFromBeginning,
    Action PlayFromAbStartOrBeginning,
    Action TogglePause,
    Func<Task> OpenPreviousAsync,
    Func<Task> OpenNextAsync,
    Action<int> SelectRelativeCue,
    Action SeekBackward,
    Action SeekForward,
    Action Undo,
    Action Redo,
    Action DeleteCue,
    Action ToggleSubtitles,
    Action ToggleTimelinePanel,
    Action ToggleSidePanel,
    Action ToggleStatusPanel,
    Action ToggleMute,
    Action<double> AdjustVolume,
    Action GoToBeginning,
    Action SeekToEnd,
    Action SetAbStart,
    Action SetAbEnd,
    Action ClearAb,
    Action RefreshRepeatToolTip);

internal sealed record ShortcutViewElements(
    FrameworkElement Root,
    Control PlaybackFocusTarget,
    MenuFlyoutItem SaveSubtitleMenuItem,
    MenuFlyoutItem SaveSubtitleAsMenuItem,
    MenuFlyoutItem ExitMenuItem,
    MenuFlyoutItem PlayPauseMenuItem,
    MenuFlyoutItem SetAbStartMenuItem,
    MenuFlyoutItem SetAbEndMenuItem,
    MenuFlyoutItem ClearAbMenuItem,
    MenuFlyoutItem DeleteCueMenuItem,
    MenuFlyoutItem PreviousSubtitleMenuItem,
    MenuFlyoutItem NextSubtitleMenuItem,
    MenuFlyoutItem UndoMenuItem,
    MenuFlyoutItem RedoMenuItem,
    ToggleMenuFlyoutItem SubtitleVisibilityMenuItem,
    ToggleMenuFlyoutItem ShowBottomPanelMenuItem,
    ToggleMenuFlyoutItem ShowRightPanelMenuItem,
    ToggleMenuFlyoutItem ShowStatusPanelMenuItem,
    MenuFlyoutItem FullscreenMenuItem,
    ToggleButton BottomPanelToggleButton,
    ToggleButton RightPanelToggleButton,
    ToggleButton StatusPanelToggleButton,
    Button PlayPauseButton,
    Button BeginningButton,
    Button PreviousButton,
    Button NextButton,
    Button SeekBackButton,
    Button SeekForwardButton,
    Button StopButton,
    Button MuteButton,
    Slider VolumeSlider,
    Slider PositionSlider,
    ListView SubtitleList,
    Button CloseButton,
    ToggleButton FullscreenButton,
    FontIcon FullscreenButtonIcon);
