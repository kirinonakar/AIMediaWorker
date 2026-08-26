using AIMediaWorker.Diagnostics;
using AIMediaWorker.History;
using AIMediaWorker.Localization;
using AIMediaWorker.Media;
using AIMediaWorker.Network;
using AIMediaWorker.Settings;
using AIMediaWorker.Subtitle.Parsing;
using AIMediaWorker.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AIMediaWorker.Controllers;

/// <summary>
/// Coordinates local/WebDAV discovery with the dedicated playlist and history controllers.
/// </summary>
internal sealed class MediaNavigationController : IDisposable
{
    private readonly MediaNavigationViewElements _view;
    private readonly MediaNavigationHost _host;
    private readonly WindowsCredentialService _windowsCredentials = new();
    private readonly WebDavCredentialStore _webDavCredentials;
    private readonly WebDavClient _webDavClient;
    private readonly MediaHistoryController _history;
    private readonly PlaylistController _playlist;
    private PendingPostOpenWork? _pendingPostOpenWork;
    private CancellationTokenSource? _postOpenCancellation;
    private bool _disposed;

    public MediaNavigationController(MediaNavigationViewElements view, MediaNavigationHost host)
    {
        _view = view;
        _host = host;
        _webDavCredentials = new WebDavCredentialStore(_windowsCredentials);
        _webDavClient = new WebDavClient(
            _windowsCredentials,
            timeout: TimeSpan.FromSeconds(_host.GetSettings().Network.TimeoutSeconds));
        _playlist = new PlaylistController(
            _view.PlaylistList,
            _view.PreviousButton,
            _view.NextButton,
            OpenPlaylistEntryAsync);
        _history = new MediaHistoryController(
            _view.FavoriteList,
            _view.FavoritesEmptyText,
            _view.RecentMenu,
            new MediaHistoryHost(
                _host.GetSettings,
                _host.GetCurrentMediaSource,
                _host.GetPlaybackPositionMicroseconds,
                location => FindWebDavServerForLocation(location)?.Name,
                OpenFavoriteAsync,
                OpenRecentAsync,
                _host.SetStatus,
                exception => AppLog.WriteAsync("error", "history", "HISTORY_SAVE_AFTER_OPEN_ERROR", exception.Message, exception)));

        _view.MediaBrowser.DefaultDirectory = _host.GetSettings().General.DefaultFolder;
        _view.MediaBrowser.ChooseFolderRequested += OnChooseFolderRequested;
        _view.MediaBrowser.MediaRequested += OnLocalMediaRequested;
        _view.MediaBrowser.FavoriteRequested += OnLocalFavoriteRequested;
        _view.MediaBrowser.ErrorOccurred += OnLocalBrowserError;

        _view.WebDavBrowser.Configure(_webDavClient, _webDavCredentials);
        _view.WebDavBrowser.AddServerRequested += OnAddWebDavServerRequested;
        _view.WebDavBrowser.DeleteServerRequested += OnDeleteWebDavServerRequested;
        _view.WebDavBrowser.EntryRequested += OnWebDavEntryRequested;
        _view.WebDavBrowser.FavoriteRequested += OnWebDavFavoriteRequested;
        RefreshWebDavServers();
    }

    public Task InitializeBrowserAsync() => _view.MediaBrowser.InitializeAsync();
    public Task LoadRecentAsync() => _history.LoadRecentAsync();

    public async Task LoadFavoritesAsync()
    {
        try { await _history.LoadFavoritesAsync(); }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "favorites", "FAVORITES_LOAD_ERROR", exception.Message, exception);
        }
    }

    public void ApplySettings()
    {
        _view.MediaBrowser.DefaultDirectory = _host.GetSettings().General.DefaultFolder;
        RefreshWebDavServers();
        _history.RefreshFavoritesList();
    }

    public Task PrepareForMediaOpenAsync() => _history.PrepareForMediaOpenAsync();

    public void MediaOpened(IMediaSource source, bool preservePlaylist, bool showInExplorer)
    {
        _playlist.SetOpenedMedia(source, preservePlaylist);
        if (source is WebDavMediaSource webDavSource)
            _view.WebDavBrowser.SelectEntry(webDavSource.ServerId, webDavSource.Uri);
        _history.MediaOpened(source);
        QueuePostOpenWork(source.Location, source as LocalMediaSource, !preservePlaylist, showInExplorer);
    }

    public void NotifyFirstFrameReady()
    {
        if (_pendingPostOpenWork is not { } work ||
            !string.Equals(_host.GetPlaybackSource(), work.Source, StringComparison.OrdinalIgnoreCase)) return;
        _pendingPostOpenWork = null;
        _ = RunPostOpenWorkAsync(work);
    }

    public async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        if (files.Length == 1 && MediaFileClassifier.IsSubtitle(files[0]))
        {
            await _host.LoadLocalSubtitleAsync(files[0]);
            return;
        }

        var mediaFiles = files.Where(path => !MediaFileClassifier.IsSubtitle(path)).ToArray();
        if (!_playlist.ReplaceLocalFiles(mediaFiles)) return;
        await _playlist.OpenCurrentAsync();
    }

    public Task OpenPreviousAsync() => _playlist.OpenPreviousAsync();
    public Task OpenNextAsync() => _playlist.OpenNextAsync();
    public Task AutoAdvanceAsync() => _playlist.AutoAdvanceAsync();
    public void ClearPlaylist() => _playlist.Clear();
    public Task OpenPlaylistItemAsync(object? item) => _playlist.OpenItemAsync(item);
    public Task ReorderFavoritesAsync() => _history.ReorderFavoritesAsync();
    public Task OpenFavoriteItemAsync(object? item) => _history.OpenFavoriteItemAsync(item);
    public Task RemoveFavoriteAsync(object sender) => _history.RemoveFavoriteAsync(sender);
    public Task SaveHistoryAsync() => _history.SaveAsync();

    public void CancelPendingWork()
    {
        _postOpenCancellation?.Cancel();
        _view.WebDavBrowser.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.MediaBrowser.ChooseFolderRequested -= OnChooseFolderRequested;
        _view.MediaBrowser.MediaRequested -= OnLocalMediaRequested;
        _view.MediaBrowser.FavoriteRequested -= OnLocalFavoriteRequested;
        _view.MediaBrowser.ErrorOccurred -= OnLocalBrowserError;
        _view.WebDavBrowser.AddServerRequested -= OnAddWebDavServerRequested;
        _view.WebDavBrowser.DeleteServerRequested -= OnDeleteWebDavServerRequested;
        _view.WebDavBrowser.EntryRequested -= OnWebDavEntryRequested;
        _view.WebDavBrowser.FavoriteRequested -= OnWebDavFavoriteRequested;
        _postOpenCancellation?.Cancel();
        _postOpenCancellation?.Dispose();
        _postOpenCancellation = null;
        _webDavClient.Dispose();
    }

    private async Task OpenPlaylistEntryAsync(PlaylistEntry entry)
    {
        await _host.OpenMediaAsync(new MediaOpenRequest(entry.Path, entry.HttpHeaders, entry.MediaSource, PreservePlaylist: true));
        if (entry.MediaSource is WebDavMediaSource webDavSource && IsCurrentMedia(webDavSource))
            await TryLoadMatchingWebDavSmiAsync(webDavSource);
    }

    private void QueuePostOpenWork(string source, LocalMediaSource? localSource, bool populateSiblingPlaylist, bool showInExplorer)
    {
        _postOpenCancellation?.Cancel();
        _postOpenCancellation?.Dispose();
        _postOpenCancellation = new CancellationTokenSource();
        var localPath = localSource is null ? null : Path.GetFullPath(localSource.Path);
        if (localPath is not null)
        {
            if (showInExplorer) _host.ShowPanel(RightPanelSection.Explorer);
            _view.MediaBrowser.PrepareForOpenedFile(localPath);
        }
        _pendingPostOpenWork = new PendingPostOpenWork(source, localPath, populateSiblingPlaylist, _postOpenCancellation.Token);
    }

    private async Task RunPostOpenWorkAsync(PendingPostOpenWork work)
    {
        try
        {
            await Task.Delay(250, work.CancellationToken);
            if (!string.Equals(_host.GetPlaybackSource(), work.Source, StringComparison.OrdinalIgnoreCase)) return;
            if (work.LocalPath is not { } fullPath) return;
            await SynchronizeBrowserAsync(fullPath);
            if (work.PopulateSiblingPlaylist) await PopulateSiblingPlaylistAsync(fullPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "post-open", "POST_OPEN_WORK_ERROR", exception.Message, exception);
        }
    }

    private async Task SynchronizeBrowserAsync(string fullPath)
    {
        try
        {
            await _view.MediaBrowser.SynchronizeOpenedFileAsync(fullPath);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "browser", "BROWSER_SYNC_AFTER_OPEN_ERROR", exception.Message, exception);
        }
    }

    private async Task PopulateSiblingPlaylistAsync(string currentPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(currentPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null) return;
            var siblings = _view.MediaBrowser.GetLoadedMediaPaths(directory)?.ToArray()
                ?? await Task.Run(() => Directory.EnumerateFiles(directory)
                    .Where(MediaFileClassifier.IsPlayable)
                    .OrderBy(Path.GetFileName, WindowsFileNameComparer.Instance)
                    .Take(5000)
                    .Select(Path.GetFullPath)
                    .ToArray());
            if (!string.Equals(_host.GetPlaybackSource(), fullPath, StringComparison.OrdinalIgnoreCase)) return;
            _playlist.ReplaceLocalFiles(siblings, fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private async void OnChooseFolderRequested(object? sender, EventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_view.Owner));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) await _view.MediaBrowser.NavigateAsync(folder.Path);
        }
        catch (Exception exception)
        {
            await _host.ShowMessageAsync(L("FolderUnavailableTitle"), exception.Message);
        }
    }

    private async void OnLocalMediaRequested(object? sender, LocalMediaBrowserEntryEventArgs e) =>
        await _host.OpenMediaAsync(new MediaOpenRequest(e.Path));

    private async void OnLocalFavoriteRequested(object? sender, LocalMediaBrowserEntryEventArgs e) =>
        await _history.AddFavoriteAsync(new LocalMediaSource(e.Path), e.IsDirectory);

    private void OnLocalBrowserError(object? sender, LocalMediaBrowserErrorEventArgs e) =>
        _host.SetStatus(e.Exception.Message);

    private async void OnAddWebDavServerRequested(object? sender, EventArgs e)
    {
        try
        {
            var name = CreateWebDavTextBox(L("NameHeader"), string.Empty);
            var address = CreateWebDavTextBox(L("AddressHeader"), string.Empty, "https://server.example/dav/");
            var port = new NumberBox
            {
                Header = L("PortHeader"), Value = 443, Minimum = 1, Maximum = 65535,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden
            };
            var username = CreateWebDavTextBox(L("UsernameHeader"), string.Empty);
            var password = new PasswordBox { Header = L("PasswordHeader") };
            var validation = new TextBlock
            {
                Foreground = ThemeBrush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(255, 196, 43, 28)),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            var panel = new StackPanel { Spacing = 12, Width = 440, Children = { name, address, port, username, password, validation } };
            var dialog = new ContentDialog
            {
                Title = L("AddWebDavServerTitle"), Content = panel, PrimaryButtonText = L("SaveButtonText"),
                CloseButtonText = L("CancelButtonText"), DefaultButton = ContentDialogButton.Primary
            };
            Uri? parsedAddress = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (Uri.TryCreate(address.Text.Trim(), UriKind.Absolute, out parsedAddress) &&
                    parsedAddress.Scheme is "http" or "https" &&
                    !double.IsNaN(port.Value) && port.Value % 1 == 0 && port.Value is >= 1 and <= 65535) return;
                args.Cancel = true;
                validation.Text = L("InvalidWebDavAddressMessage");
                validation.Visibility = Visibility.Visible;
            };
            if (await _host.ShowDialogAsync(dialog) != ContentDialogResult.Primary || parsedAddress is null) return;

            var server = new WebDavServerSettings
            {
                Name = string.IsNullOrWhiteSpace(name.Text) ? parsedAddress.Host : name.Text.Trim()
            };
            _webDavCredentials.Save(server.Id, new WebDavConnectionCredential(
                WebDavConnectionCredential.NormalizeAddress(parsedAddress),
                (int)port.Value,
                username.Text.Trim(),
                password.Password));
            _host.GetSettings().Network.WebDavServers.Add(server);
            await SettingsService.CreateDefault().SaveAsync(_host.GetSettings());
            _host.ShowPanel(RightPanelSection.WebDav);
            RefreshWebDavServers(server);
            await _view.WebDavBrowser.ConnectAsync(server);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "webdav", "WEBDAV_ADD_ERROR", exception.Message, exception);
            _view.WebDavBrowser.SetStatus(exception.Message);
        }
    }

    private async void OnDeleteWebDavServerRequested(object? sender, WebDavServerEventArgs e)
    {
        var server = e.Server;
        var dialog = new ContentDialog
        {
            Title = L("DeleteWebDavServerTitle"),
            Content = new TextBlock { Text = F("DeleteWebDavServerMessage", server.Name), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = L("DeleteButtonText"),
            CloseButtonText = L("CancelButtonText"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await _host.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        try
        {
            _view.WebDavBrowser.Disconnect(server.Id);
            _host.GetSettings().Network.WebDavServers.RemoveAll(candidate => candidate.Id == server.Id);
            _webDavCredentials.Delete(server.Id);
            await SettingsService.CreateDefault().SaveAsync(_host.GetSettings());
            RefreshWebDavServers();
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "webdav", "WEBDAV_DELETE_ERROR", exception.Message, exception);
            _view.WebDavBrowser.SetStatus(exception.Message);
        }
    }

    private async void OnWebDavEntryRequested(object? sender, WebDavEntryEventArgs e)
    {
        if (MediaFileClassifier.IsSubtitle(Uri.UnescapeDataString(e.Entry.Uri.AbsolutePath)))
        {
            await LoadWebDavSubtitleAsync(e.Server, e.Entry, confirmChanges: true, showSubtitlePanel: true);
            return;
        }
        await OpenWebDavMediaAsync(e.Server, e.Entry, e.Siblings);
    }

    private async void OnWebDavFavoriteRequested(object? sender, WebDavEntryEventArgs e) =>
        await _history.AddFavoriteAsync(new WebDavMediaSource(e.Server.Id, e.Entry.Uri, e.Entry.Name), e.Entry.IsCollection);

    private async Task LoadWebDavSubtitleAsync(
        WebDavServerSettings server,
        WebDavEntry entry,
        bool confirmChanges,
        bool showSubtitlePanel,
        Uri? expectedMediaUri = null)
    {
        if (confirmChanges && !await _host.PrepareSubtitleLoadAsync()) return;
        try
        {
            var bytes = await _webDavClient.DownloadAsync(server, entry.Uri);
            if (expectedMediaUri is not null && !IsCurrentMedia(server.Id, expectedMediaUri)) return;
            var path = Uri.UnescapeDataString(entry.Uri.AbsolutePath);
            await _host.ApplyDownloadedSubtitleAsync(new DownloadedWebDavSubtitle(path, bytes, showSubtitlePanel));
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync(
                "error", "webdav",
                exception is WebDavException webDavException ? webDavException.Code : "WEBDAV_SUBTITLE_ERROR",
                exception.Message, exception);
            await _host.ShowMessageAsync(L("SubtitleErrorTitle"), exception.Message);
        }
    }

    private async Task TryLoadMatchingWebDavSmiAsync(WebDavMediaSource mediaSource)
    {
        try
        {
            var server = _host.GetSettings().Network.WebDavServers.FirstOrDefault(candidate => candidate.Id == mediaSource.ServerId);
            if (server is null) return;
            var directory = WebDavUri.AsDirectory(new Uri(mediaSource.Uri, "."));
            IReadOnlyList<WebDavEntry> entries = _view.WebDavBrowser.TryGetEntries(mediaSource.ServerId, directory, out var displayedEntries)
                ? displayedEntries
                : await _webDavClient.ListAsync(server, directory);
            if (!IsCurrentMedia(mediaSource)) return;
            var sidecar = entries.FirstOrDefault(candidate =>
                !candidate.IsCollection && SmiParser.IsSidecarFor(mediaSource.DisplayName, candidate.Name));
            if (sidecar is not null)
                await LoadWebDavSubtitleAsync(server, sidecar, confirmChanges: false, showSubtitlePanel: false, expectedMediaUri: mediaSource.Uri);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync(
                "warning", "webdav",
                exception is WebDavException webDavException ? webDavException.Code : "WEBDAV_SIDECAR_SUBTITLE_ERROR",
                exception.Message, exception);
        }
    }

    private async Task OpenWebDavMediaAsync(WebDavServerSettings server, WebDavEntry entry, IReadOnlyList<WebDavEntry>? siblings = null)
    {
        using var request = _webDavClient.CreateMediaRequest(server, entry.Uri);
        IReadOnlyDictionary<string, string>? headers = request.Headers.Authorization is { } authorization
            ? new Dictionary<string, string> { ["Authorization"] = authorization.ToString() }
            : null;
        if (siblings is null)
        {
            try
            {
                siblings = await _webDavClient.ListAsync(server, new Uri(entry.Uri, "."));
                _view.WebDavBrowser.Synchronize(server, entry.Uri, siblings);
            }
            catch (Exception exception)
            {
                await AppLog.WriteAsync("warning", "webdav", "WEBDAV_SIBLING_LIST_ERROR", exception.Message, exception);
            }
        }

        var mediaEntries = (siblings ?? []).Where(IsPlayableWebDavEntry).ToList();
        if (!mediaEntries.Any(candidate => WebDavUri.Equals(candidate.Uri, entry.Uri))) mediaEntries.Add(entry);
        _playlist.ReplaceWebDavEntries(server.Id, mediaEntries, headers, entry.Uri);
        await _playlist.OpenCurrentAsync();
    }

    private async Task OpenFavoriteAsync(FavoriteItem favorite)
    {
        if (favorite.IsFolder)
        {
            if (favorite.SourceType == MediaSourceKind.WebDav)
            {
                var server = FindWebDavServerForLocation(favorite.Location);
                if (server is null)
                {
                    await _host.ShowMessageAsync(L("WebDavServerMissingTitle"), L("FavoriteServerMissingMessage"));
                    return;
                }
                _host.ShowPanel(RightPanelSection.WebDav);
                await _view.WebDavBrowser.ConnectAsync(server, new Uri(favorite.Location));
                return;
            }
            if (!Directory.Exists(favorite.Location))
            {
                await _host.ShowMessageAsync(L("FolderUnavailableTitle"), favorite.Location);
                return;
            }
            _host.ShowPanel(RightPanelSection.Explorer);
            await _view.MediaBrowser.NavigateAsync(favorite.Location);
            return;
        }
        await OpenRecentAsync(new RecentMediaItem(favorite.SourceType, favorite.DisplayName, favorite.Location, favorite.Added, 0));
    }

    private async Task OpenRecentAsync(RecentMediaItem recent)
    {
        if (recent.SourceType == MediaSourceKind.WebDav)
        {
            var server = FindWebDavServerForLocation(recent.Location);
            if (server is null)
            {
                await _host.ShowMessageAsync(L("WebDavServerMissingTitle"), L("RecentServerMissingMessage"));
                return;
            }
            await OpenWebDavMediaAsync(server, new WebDavEntry(recent.DisplayName, new Uri(recent.Location), false, null, null, null));
        }
        else
        {
            await _host.OpenMediaAsync(new MediaOpenRequest(recent.Location, MediaSource: MediaSourceFactory.Parse(recent.Location)));
        }

        if (_host.GetSettings().General.ResumePlayback && recent.LastPlaybackPositionMicroseconds > 0 &&
            await _host.WaitForFirstFrameAsync(recent.Location))
        {
            _host.ResumePlayback(TimeSpan.FromTicks(recent.LastPlaybackPositionMicroseconds * 10));
        }
    }

    private void RefreshWebDavServers(WebDavServerSettings? selected = null) =>
        _view.WebDavBrowser.SetServers(_host.GetSettings().Network.WebDavServers, selected);

    private WebDavServerSettings? FindWebDavServerForLocation(string location)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var target)) return null;
        WebDavServerSettings? bestMatch = null;
        var bestPathLength = -1;
        foreach (var server in _host.GetSettings().Network.WebDavServers)
        {
            WebDavConnectionCredential? credential;
            try { credential = _webDavCredentials.Read(server.Id); }
            catch (Exception) { continue; }
            if (credential is null) continue;
            var root = credential.RootUri;
            if (!root.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !root.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) ||
                root.Port != target.Port ||
                !target.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (root.AbsolutePath.Length <= bestPathLength) continue;
            bestMatch = server;
            bestPathLength = root.AbsolutePath.Length;
        }
        return bestMatch;
    }

    private bool IsCurrentMedia(WebDavMediaSource source) => IsCurrentMedia(source.ServerId, source.Uri);

    private bool IsCurrentMedia(Guid serverId, Uri uri) =>
        _host.GetCurrentMediaSource() is WebDavMediaSource current &&
        current.ServerId == serverId && WebDavUri.Equals(current.Uri, uri);

    private static bool IsPlayableWebDavEntry(WebDavEntry entry) =>
        !entry.IsCollection &&
        (MediaFileClassifier.IsPlayable(Uri.UnescapeDataString(entry.Uri.AbsolutePath)) ||
         entry.ContentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
         entry.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true);

    private static TextBox CreateWebDavTextBox(string header, string text, string? placeholder = null) => new()
    {
        Header = header,
        Text = text,
        PlaceholderText = placeholder ?? string.Empty,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false
    };

    private static Brush ThemeBrush(string resourceKey, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(resourceKey, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);

    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);

    private sealed record PendingPostOpenWork(
        string Source,
        string? LocalPath,
        bool PopulateSiblingPlaylist,
        CancellationToken CancellationToken);
}

internal sealed record MediaNavigationViewElements(
    Window Owner,
    LocalMediaBrowserView MediaBrowser,
    WebDavBrowserView WebDavBrowser,
    ListView PlaylistList,
    ListView FavoriteList,
    TextBlock FavoritesEmptyText,
    MenuFlyoutSubItem RecentMenu,
    Button PreviousButton,
    Button NextButton);

internal sealed record MediaNavigationHost(
    Func<AppSettings> GetSettings,
    Func<IMediaSource?> GetCurrentMediaSource,
    Func<string?> GetPlaybackSource,
    Func<long> GetPlaybackPositionMicroseconds,
    Func<MediaOpenRequest, Task> OpenMediaAsync,
    Func<string, Task> LoadLocalSubtitleAsync,
    Func<Task<bool>> PrepareSubtitleLoadAsync,
    Func<DownloadedWebDavSubtitle, Task> ApplyDownloadedSubtitleAsync,
    Func<string, Task<bool>> WaitForFirstFrameAsync,
    Action<TimeSpan> ResumePlayback,
    Action<RightPanelSection> ShowPanel,
    Action<string> SetStatus,
    Func<string, string, Task> ShowMessageAsync,
    Func<ContentDialog, Task<ContentDialogResult>> ShowDialogAsync);

internal sealed record MediaOpenRequest(
    string Source,
    IReadOnlyDictionary<string, string>? HttpHeaders = null,
    IMediaSource? MediaSource = null,
    bool PreservePlaylist = false);

internal sealed record DownloadedWebDavSubtitle(string Path, byte[] Bytes, bool ShowSubtitlePanel);
