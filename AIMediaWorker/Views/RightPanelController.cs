using AIMediaWorker.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIMediaWorker.Views;

internal enum RightPanelSection
{
    Explorer,
    Playlist,
    WebDav,
    Favorites,
    Subtitles
}

/// <summary>Owns navigation and visibility for the sections hosted in the right panel.</summary>
internal sealed class RightPanelController : IDisposable
{
    private readonly RightPanelViewElements _view;
    private readonly Action _ensurePanelVisible;

    public RightPanelController(RightPanelViewElements view, Action ensurePanelVisible)
    {
        _view = view;
        _ensurePanelVisible = ensurePanelVisible;
        _view.SectionList.SelectionChanged += OnSectionChanged;
        RefreshLabels();
    }

    public event EventHandler<RightPanelSection>? SectionChanged;

    public RightPanelSection CurrentSection =>
        _view.SectionList.SelectedIndex is >= 0 and <= (int)RightPanelSection.Subtitles
            ? (RightPanelSection)_view.SectionList.SelectedIndex
            : RightPanelSection.Explorer;

    public void RefreshLabels()
    {
        var selectedIndex = Math.Max(0, _view.SectionList.SelectedIndex);
        _view.SectionList.ItemsSource = new[]
        {
            new RightPanelSectionEntry("\uE8B7", L("RightPanelExplorer")),
            new RightPanelSectionEntry("\uE142", L("RightPanelPlaylist")),
            new RightPanelSectionEntry("\uE774", L("RightPanelWebDav")),
            new RightPanelSectionEntry("\uE734", L("RightPanelFavorites")),
            new RightPanelSectionEntry("\uE8C1", L("RightPanelSubtitles"))
        };
        _view.SectionList.SelectedIndex = Math.Clamp(selectedIndex, 0, (int)RightPanelSection.Subtitles);
        Apply(CurrentSection, notify: false);
    }

    public void Show(RightPanelSection section)
    {
        _ensurePanelVisible();
        _view.SectionList.SelectedIndex = (int)section;
        Apply(section, notify: true);
    }

    public void Dispose() => _view.SectionList.SelectionChanged -= OnSectionChanged;

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_view.SectionList.SelectedIndex < 0) return;
        Apply((RightPanelSection)_view.SectionList.SelectedIndex, notify: true);
    }

    private void Apply(RightPanelSection section, bool notify)
    {
        _view.SectionTitle.Text = L(section switch
        {
            RightPanelSection.Explorer => "RightPanelExplorer",
            RightPanelSection.Playlist => "RightPanelPlaylist",
            RightPanelSection.WebDav => "RightPanelWebDav",
            RightPanelSection.Favorites => "RightPanelFavorites",
            RightPanelSection.Subtitles => "RightPanelSubtitles",
            _ => "RightPanelExplorer"
        });
        _view.ExplorerSection.Visibility = VisibilityFor(section == RightPanelSection.Explorer);
        _view.PlaylistSection.Visibility = VisibilityFor(section == RightPanelSection.Playlist);
        _view.WebDavSection.Visibility = VisibilityFor(section == RightPanelSection.WebDav);
        _view.FavoritesSection.Visibility = VisibilityFor(section == RightPanelSection.Favorites);
        _view.SubtitlesSection.Visibility = VisibilityFor(section == RightPanelSection.Subtitles);
        if (notify) SectionChanged?.Invoke(this, section);
    }

    private static Visibility VisibilityFor(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    private static string L(string key) => LocalizationService.Get(key);

    private sealed record RightPanelSectionEntry(string IconGlyph, string Label);
}

internal sealed record RightPanelViewElements(
    ListView SectionList,
    FrameworkElement ExplorerSection,
    FrameworkElement PlaylistSection,
    FrameworkElement WebDavSection,
    FrameworkElement FavoritesSection,
    FrameworkElement SubtitlesSection,
    TextBlock SectionTitle);
