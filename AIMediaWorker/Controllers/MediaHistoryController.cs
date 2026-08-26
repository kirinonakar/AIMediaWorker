using System.Collections.ObjectModel;
using AIMediaWorker.History;
using AIMediaWorker.Localization;
using AIMediaWorker.Media;
using AIMediaWorker.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIMediaWorker.Controllers;

/// <summary>Owns persistence and UI projection for recent media and favorites.</summary>
internal sealed class MediaHistoryController
{
    private readonly ListView _favoriteList;
    private readonly TextBlock _favoritesEmptyText;
    private readonly MenuFlyoutSubItem _recentMenu;
    private readonly MediaHistoryHost _host;
    private readonly MediaHistoryService _history = MediaHistoryService.CreateDefault();
    private readonly ObservableCollection<FavoriteListEntry> _favoriteEntries = [];

    public MediaHistoryController(
        ListView favoriteList,
        TextBlock favoritesEmptyText,
        MenuFlyoutSubItem recentMenu,
        MediaHistoryHost host)
    {
        _favoriteList = favoriteList;
        _favoritesEmptyText = favoritesEmptyText;
        _recentMenu = recentMenu;
        _host = host;
    }

    public async Task LoadRecentAsync()
    {
        await _history.LoadRecentAsync();
        RebuildRecentMenu();
    }

    public async Task LoadFavoritesAsync()
    {
        await _history.LoadFavoritesAsync();
        RefreshFavoritesList();
    }

    public void RefreshFavoritesList()
    {
        if (!ReferenceEquals(_favoriteList.ItemsSource, _favoriteEntries))
            _favoriteList.ItemsSource = _favoriteEntries;
        _favoriteEntries.Clear();
        foreach (var item in _history.Favorites)
            _favoriteEntries.Add(new FavoriteListEntry(item, L("RemoveFavoriteButton")));
        _favoritesEmptyText.Visibility = _favoriteEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task PrepareForMediaOpenAsync()
    {
        await _history.LoadRecentAsync();
        RememberCurrentPosition();
    }

    public void MediaOpened(IMediaSource source) => _ = SaveHistoryAfterOpenAsync(source);

    public async Task AddFavoriteAsync(IMediaSource source, bool isFolder)
    {
        await _history.LoadFavoritesAsync();
        if (!_history.AddFavorite(source, isFolder)) return;
        await _history.SaveFavoritesAsync();
        RefreshFavoritesList();
        _host.SetStatus(isFolder ? F("StatusAddedFavoriteFolder", source.DisplayName) : L("StatusAddedFavorite"));
    }

    public async Task ReorderFavoritesAsync()
    {
        await _history.LoadFavoritesAsync();
        if (!_history.ReorderFavorites(_favoriteEntries.Select(entry => entry.Item.Location))) return;
        RefreshFavoritesList();
        await _history.SaveFavoritesAsync();
    }

    public async Task OpenFavoriteItemAsync(object? item)
    {
        if (item is FavoriteListEntry entry) await _host.OpenFavoriteAsync(entry.Item);
    }

    public async Task RemoveFavoriteAsync(object sender)
    {
        if (sender is not FrameworkElement { DataContext: FavoriteListEntry entry }) return;
        await _history.LoadFavoritesAsync();
        if (!_history.RemoveFavorite(entry.Item.Location)) return;
        await _history.SaveFavoritesAsync();
        RefreshFavoritesList();
    }

    public async Task SaveAsync()
    {
        await _history.LoadRecentAsync();
        RememberCurrentPosition();
        await _history.SaveRecentAsync();
    }

    private async Task SaveHistoryAfterOpenAsync(IMediaSource source)
    {
        try
        {
            await _history.LoadRecentAsync();
            _history.AddRecent(source, 0, _host.GetSettings().General.RecentMediaCount);
            await _history.SaveRecentAsync();
            RebuildRecentMenu();
        }
        catch (Exception exception)
        {
            await _host.LogHistoryErrorAsync(exception);
        }
    }

    private void RememberCurrentPosition()
    {
        if (_host.GetCurrentMediaSource() is not { } source) return;
        _history.AddRecent(source, _host.GetPlaybackPositionMicroseconds(), _host.GetSettings().General.RecentMediaCount);
    }

    private void RebuildRecentMenu()
    {
        _recentMenu.Items.Clear();
        foreach (var recent in _history.Recent.Take(20))
        {
            var item = new MenuFlyoutItem { Text = recent.DisplayName, Tag = recent };
            item.Click += async (_, _) => await _host.OpenRecentAsync(recent);
            _recentMenu.Items.Add(item);
        }
        if (_recentMenu.Items.Count == 0)
            _recentMenu.Items.Add(new MenuFlyoutItem { Text = L("NoRecentMediaText"), IsEnabled = false });
    }

    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);
}

internal sealed record MediaHistoryHost(
    Func<AppSettings> GetSettings,
    Func<IMediaSource?> GetCurrentMediaSource,
    Func<long> GetPlaybackPositionMicroseconds,
    Func<FavoriteItem, Task> OpenFavoriteAsync,
    Func<RecentMediaItem, Task> OpenRecentAsync,
    Action<string> SetStatus,
    Func<Exception, Task> LogHistoryErrorAsync);

internal sealed record FavoriteListEntry(FavoriteItem Item, string RemoveLabel)
{
    public string DisplayName => Item.DisplayName;
    public string Location => Item.Location;
    public string SourceIconGlyph => Item.SourceType == MediaSourceKind.WebDav ? "\uE774" : string.Empty;
    public string IconGlyph => Item.IsFolder ? "\uE8B7" : "\uE8A5";
}
