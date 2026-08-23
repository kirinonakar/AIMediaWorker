using System.Net.Http.Headers;
using AIMediaWorker.Network;
using AIMediaWorker.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;
using AIMediaWorker.Localization;

namespace AIMediaWorker.Views;

public sealed record RemoteMediaSelection(Guid ServerId, Uri Uri, string Name, IReadOnlyDictionary<string, string> Headers);
public sealed record RemoteFolderSelection(Guid ServerId, Uri Uri, string Name);
public sealed record RemoteDirectorySnapshot(Guid ServerId, Uri Directory, IReadOnlyList<WebDavEntry> Entries);

public sealed partial class WebDavWindow : Window
{
    private readonly SettingsService _settingsService = SettingsService.CreateDefault();
    private readonly WindowsCredentialService _credentials = new();
    private readonly WebDavClient _client;
    private AppSettings _settings = new();
    private Uri? _currentDirectory;
    private Guid? _currentServerId;
    private CancellationTokenSource? _listingCancellation;
    private readonly Guid? _initialServerId;
    private readonly Uri? _initialDirectory;
    public event EventHandler<RemoteMediaSelection>? MediaSelected;
    public event EventHandler<RemoteFolderSelection>? FolderFavoriteRequested;
    public event EventHandler<RemoteDirectorySnapshot>? DirectoryListed;

    public WebDavWindow(Guid? initialServerId = null, Uri? initialDirectory = null)
    {
        InitializeComponent();
        Title = L("WebDavWindow.Title");
        _initialServerId = initialServerId;
        _initialDirectory = initialDirectory;
        _client = new WebDavClient(_credentials);
        Closed += OnClosed;
        var handle = WindowNative.GetWindowHandle(this);
        AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle))?.Resize(new SizeInt32(1000, 700));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _settingsService.LoadAsync();
            ServerList.ItemsSource = _settings.Network.WebDavServers;
            var initialServer = _initialServerId is { } id ? _settings.Network.WebDavServers.FirstOrDefault(server => server.Id == id) : null;
            if (initialServer is not null && _initialDirectory is not null) { _currentDirectory = EnsureDirectoryUri(_initialDirectory); _currentServerId = initialServer.Id; }
            ServerList.SelectedItem = initialServer ?? _settings.Network.WebDavServers.FirstOrDefault();
        }
        catch (Exception exception) { SetStatus(L("NetworkErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }

    private async void OnServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServerList.SelectedItem is not WebDavServerSettings server || !Uri.TryCreate(server.Url, UriKind.Absolute, out var root)) return;
        if (_currentDirectory is null || _currentServerId != server.Id) _currentDirectory = EnsureDirectoryUri(root);
        _currentServerId = server.Id;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (ServerList.SelectedItem is not WebDavServerSettings server || _currentDirectory is null) return;
        _listingCancellation?.Cancel(); _listingCancellation?.Dispose(); _listingCancellation = new CancellationTokenSource();
        var operation = _listingCancellation;
        EntryList.IsEnabled = false;
        SetStatus(L("ConnectingTitle"), server.Name, InfoBarSeverity.Informational);
        try
        {
            var entries = await _client.ListAsync(server, _currentDirectory, operation.Token);
            if (operation.IsCancellationRequested) return;
            EntryList.ItemsSource = entries; PathText.Text = _currentDirectory.AbsoluteUri;
            DirectoryListed?.Invoke(this, new RemoteDirectorySnapshot(server.Id, _currentDirectory, entries));
            SetStatus(L("ConnectedTitle"), F("WebDavItemsCount", entries.Count), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (WebDavException exception) { SetStatus(exception.Code, exception.Message, exception.Code == "AUTH_ERROR" ? InfoBarSeverity.Warning : InfoBarSeverity.Error); }
        catch (Exception exception) { SetStatus(L("NetworkErrorTitle"), exception.Message, InfoBarSeverity.Error); }
        finally { if (ReferenceEquals(_listingCancellation, operation)) EntryList.IsEnabled = true; }
    }

    private async void OnEntryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WebDavEntry entry) return;
        if (entry.IsCollection) { _currentDirectory = EnsureDirectoryUri(entry.Uri); await RefreshAsync(); }
        else OpenEntry(entry);
    }
    private void OnOpenClick(object sender, RoutedEventArgs e) { if (EntryList.SelectedItem is WebDavEntry { IsCollection: false } entry) OpenEntry(entry); }
    private void OpenEntry(WebDavEntry entry)
    {
        if (ServerList.SelectedItem is not WebDavServerSettings server) return;
        using var request = _client.CreateMediaRequest(server, entry.Uri);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Headers.Authorization is AuthenticationHeaderValue authorization) headers["Authorization"] = authorization.ToString();
        MediaSelected?.Invoke(this, new RemoteMediaSelection(server.Id, entry.Uri, entry.Name, headers));
    }

    private async void OnParentClick(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not WebDavServerSettings server || _currentDirectory is null || !Uri.TryCreate(server.Url, UriKind.Absolute, out var root)) return;
        var parent = EnsureDirectoryUri(new Uri(_currentDirectory, "../"));
        var rootDirectory = EnsureDirectoryUri(root);
        if (!parent.AbsoluteUri.StartsWith(rootDirectory.AbsoluteUri, StringComparison.OrdinalIgnoreCase)) parent = rootDirectory;
        _currentDirectory = parent; await RefreshAsync();
    }
    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OnFavoriteFolderClick(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not WebDavServerSettings server || _currentDirectory is null) return;
        var displayName = $"{server.Name} — {Uri.UnescapeDataString(_currentDirectory.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? server.Name)}";
        FolderFavoriteRequested?.Invoke(this, new RemoteFolderSelection(server.Id, _currentDirectory, displayName));
        StatusBar.Title = L("FavoriteSavedTitle");
        StatusBar.Message = displayName;
        StatusBar.Severity = InfoBarSeverity.Success;
    }
    private async void OnAddServerClick(object sender, RoutedEventArgs e)
    {
        try { var server = new WebDavServerSettings(); if (await EditServerAsync(server, true)) { _settings.Network.WebDavServers.Add(server); await SaveAndRefreshServersAsync(server); } }
        catch (Exception exception) { SetStatus(L("NetworkErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }
    private async void OnEditServerClick(object sender, RoutedEventArgs e)
    {
        try { if (ServerList.SelectedItem is WebDavServerSettings server && await EditServerAsync(server, false)) await SaveAndRefreshServersAsync(server); }
        catch (Exception exception) { SetStatus(L("NetworkErrorTitle"), exception.Message, InfoBarSeverity.Error); }
    }
    private async void OnDeleteServerClick(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not WebDavServerSettings server) return;
        var confirmation = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L("DeleteWebDavServerTitle"), Content = F("DeleteWebDavServerMessage", server.Name), PrimaryButtonText = L("DeleteButtonText"), CloseButtonText = L("CancelButtonText"), DefaultButton = ContentDialogButton.Close };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        _listingCancellation?.Cancel();
        _settings.Network.WebDavServers.Remove(server); _credentials.Delete(CredentialIdentifier.ForWebDav(server.Id)); await _settingsService.SaveAsync(_settings); ServerList.ItemsSource = null; ServerList.ItemsSource = _settings.Network.WebDavServers; EntryList.ItemsSource = null; _currentDirectory = null; _currentServerId = null; PathText.Text = string.Empty;
    }

    private async Task<bool> EditServerAsync(WebDavServerSettings server, bool isNew)
    {
        var name = new TextBox { Header = L("NameHeader"), Text = server.Name };
        var url = new TextBox { Header = "URL", Text = server.Url, PlaceholderText = "https://server.example/dav/" };
        var username = new TextBox { Header = L("UsernameHeader"), Text = server.Username ?? string.Empty };
        var password = new PasswordBox { Header = L("PasswordHeader"), Password = isNew ? string.Empty : _credentials.Read(CredentialIdentifier.ForWebDav(server.Id))?.Secret ?? string.Empty };
        var panel = new StackPanel { Spacing = 8, MinWidth = 440, Children = { name, url, username, password } };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = isNew ? L("AddWebDavServerTitle") : L("EditWebDavServerTitle"), Content = panel, PrimaryButtonText = L("SaveButtonText"), CloseButtonText = L("CancelButtonText") };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        if (!Uri.TryCreate(url.Text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { StatusBar.Title = L("InvalidUrlTitle"); StatusBar.Message = L("InvalidWebDavUrlMessage"); StatusBar.Severity = InfoBarSeverity.Error; return false; }
        server.Name = string.IsNullOrWhiteSpace(name.Text) ? uri.Host : name.Text.Trim(); server.Url = EnsureDirectoryUri(uri).AbsoluteUri; server.Username = username.Text.Trim();
        _credentials.Save(CredentialIdentifier.ForWebDav(server.Id), server.Username ?? string.Empty, password.Password);
        return true;
    }

    private async Task SaveAndRefreshServersAsync(WebDavServerSettings selected)
    {
        await _settingsService.SaveAsync(_settings); ServerList.ItemsSource = null; ServerList.ItemsSource = _settings.Network.WebDavServers; _currentDirectory = null; _currentServerId = null; ServerList.SelectedItem = selected;
    }
    private static Uri EnsureDirectoryUri(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) => string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);
    private void SetStatus(string title, string message, InfoBarSeverity severity) { StatusBar.Title = title; StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true; }
    private void OnClosed(object sender, WindowEventArgs args) { _listingCancellation?.Cancel(); _listingCancellation?.Dispose(); _client.Dispose(); }
}
