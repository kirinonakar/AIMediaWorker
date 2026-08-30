using AIMediaWorker.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIMediaWorker.Views;

/// <summary>Owns side/timeline panel visibility, persisted sizes, and splitter constraints.</summary>
internal sealed class PanelLayoutController
{
    private readonly PanelLayoutViewElements _view;
    private readonly Func<WindowLayoutSettings> _getSettings;
    private readonly Action _updateToggleIcons;

    public PanelLayoutController(PanelLayoutViewElements view, Func<WindowLayoutSettings> getSettings, Action updateToggleIcons)
    {
        _view = view;
        _getSettings = getSettings;
        _updateToggleIcons = updateToggleIcons;
    }

    public bool IsRightVisible { get; set; } = true;
    public bool IsBottomVisible { get; set; } = true;
    public bool IsStatusVisible { get; set; } = true;
    public double RightWidth { get; private set; } = 360;
    public double BottomHeight { get; private set; } = 160;

    public void Load(WindowLayoutSettings layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        IsRightVisible = layout.IsRightPanelVisible;
        IsBottomVisible = layout.IsBottomPanelVisible;
        IsStatusVisible = layout.IsStatusPanelVisible;
        RightWidth = Math.Clamp(layout.RightPanelWidth, 240, 1200);
        BottomHeight = Math.Clamp(layout.BottomPanelHeight, WindowLayoutSettings.MinimumBottomPanelHeight, 800);
    }

    public void Apply(bool persist)
    {
        _view.RightPanel.Visibility = IsRightVisible ? Visibility.Visible : Visibility.Collapsed;
        _view.RightPanelSplitter.Visibility = IsRightVisible ? Visibility.Visible : Visibility.Collapsed;
        _view.RightPanelSplitterColumn.Width = IsRightVisible ? new GridLength(6) : new GridLength(0);
        _view.RightPanelColumn.Width = IsRightVisible ? new GridLength(RightWidth) : new GridLength(0);
        _view.BottomPanel.Visibility = IsBottomVisible ? Visibility.Visible : Visibility.Collapsed;
        _view.BottomPanelSplitter.Visibility = IsBottomVisible ? Visibility.Visible : Visibility.Collapsed;
        _view.BottomPanelSplitterRow.Height = IsBottomVisible ? new GridLength(6) : new GridLength(0);
        _view.BottomPanelRow.Height = IsBottomVisible ? new GridLength(BottomHeight) : new GridLength(0);
        _view.StatusPanel.Visibility = IsStatusVisible ? Visibility.Visible : Visibility.Collapsed;
        _view.ShowRightPanelMenuItem.IsChecked = IsRightVisible;
        _view.ShowBottomPanelMenuItem.IsChecked = IsBottomVisible;
        _view.ShowStatusPanelMenuItem.IsChecked = IsStatusVisible;
        _view.RightPanelToggleButton.IsChecked = IsRightVisible;
        _view.BottomPanelToggleButton.IsChecked = IsBottomVisible;
        _view.StatusPanelToggleButton.IsChecked = IsStatusVisible;
        _updateToggleIcons();

        if (!persist) return;
        var settings = _getSettings();
        settings.IsRightPanelVisible = IsRightVisible;
        settings.IsBottomPanelVisible = IsBottomVisible;
        settings.IsStatusPanelVisible = IsStatusVisible;
        settings.RightPanelWidth = RightWidth;
        settings.BottomPanelHeight = BottomHeight;
    }

    public bool Clamp(double contentWidth, double rootHeight)
    {
        var previousRightWidth = RightWidth;
        var previousBottomHeight = BottomHeight;
        if (contentWidth > 0) RightWidth = Math.Min(RightWidth, Math.Max(240, contentWidth - 326));
        if (rootHeight > 0) BottomHeight = Math.Min(BottomHeight, Math.Max(WindowLayoutSettings.MinimumBottomPanelHeight, rootHeight - 320));
        return Math.Abs(previousRightWidth - RightWidth) > 0.1 || Math.Abs(previousBottomHeight - BottomHeight) > 0.1;
    }

    public void ResizeRight(double horizontalChange, double contentWidth, double splitterWidth, bool persist)
    {
        var maximum = Math.Max(240, contentWidth - 320 - splitterWidth);
        RightWidth = Math.Clamp(RightWidth - horizontalChange, 240, Math.Min(1200, maximum));
        _view.RightPanelColumn.Width = new GridLength(RightWidth);
        if (persist) _getSettings().RightPanelWidth = RightWidth;
    }

    public void ResizeBottom(double verticalChange, double rootHeight, bool persist)
    {
        var maximum = Math.Max(WindowLayoutSettings.MinimumBottomPanelHeight, rootHeight - 320);
        BottomHeight = Math.Clamp(BottomHeight - verticalChange, WindowLayoutSettings.MinimumBottomPanelHeight, Math.Min(800, maximum));
        _view.BottomPanelRow.Height = new GridLength(BottomHeight);
        if (persist) _getSettings().BottomPanelHeight = BottomHeight;
    }
}

internal sealed record PanelLayoutViewElements(
    FrameworkElement RightPanel,
    FrameworkElement RightPanelSplitter,
    ColumnDefinition RightPanelSplitterColumn,
    ColumnDefinition RightPanelColumn,
    FrameworkElement BottomPanel,
    FrameworkElement BottomPanelSplitter,
    RowDefinition BottomPanelSplitterRow,
    RowDefinition BottomPanelRow,
    FrameworkElement StatusPanel,
    ToggleMenuFlyoutItem ShowRightPanelMenuItem,
    ToggleMenuFlyoutItem ShowBottomPanelMenuItem,
    ToggleMenuFlyoutItem ShowStatusPanelMenuItem,
    ToggleButton RightPanelToggleButton,
    ToggleButton BottomPanelToggleButton,
    ToggleButton StatusPanelToggleButton);
