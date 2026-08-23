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

public sealed partial class WebDavWindow : Window
{
    private readonly SettingsService _settingsService = SettingsService.CreateDefault();
    private readonly WindowsCredentialService _credentials = new();
    private readonly WebDavClient _client;
    private AppSettings _settings = new();
    private Uri? _currentDirectory;
    private CancellationTokenSource? _listingCancellation;
    private readonly Guid? _initialServerId;
    private readonly Uri? _initialDirectory;
    public event EventHandler<RemoteMediaSelection>? MediaSelected;
    public event EventHandler<RemoteFolderSelection>? FolderFavoriteRequested;

    public WebDavWindow(Guid? initialServerId = null, Uri? initialDirectory = null)
    {
        InitializeComponent();
        _initialServerId = initialServerId;
        _initialDirectory = initialDirectory;
        _client = new WebDavClient(_credentials);
        Closed += OnClosed;
        var handle = WindowNative.GetWindowHandle(this);
        AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle))?.Resize(new SizeInt32(1000, 700));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsService.LoadAsync();
        ServerList.ItemsSource = _settings.Network.WebDavServers;
        var initialServer = _initialServerId is { } id ? _settings.Network.WebDavServers.FirstOrDefault(server => server.Id == id) : null;
        if (initialServer is not null && _initialDirectory is not null) _currentDirectory = EnsureDirectoryUri(_initialDirectory);
        ServerList.SelectedItem = initialServer ?? _settings.Network.WebDavServers.FirstOrDefault();
    }

    private async void OnServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServerList.SelectedItem is not WebDavServerSettings server || !Uri.TryCreate(server.Url, UriKind.Absolute, out var root)) return;
        if (_currentDirectory is null || _initialServerId != server.Id) _currentDirectory = EnsureDirectoryUri(root);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (ServerList.SelectedItem is not WebDavServerSettings server || _currentDirectory is null) return;
        _listingCancellation?.Cancel(); _listingCancellation?.Dispose(); _listingCancellation = new CancellationTokenSource();
        StatusBar.Title = L("ConnectingTitle"); StatusBar.Message = server.Name; StatusBar.Severity = InfoBarSeverity.Informational;
        try
        {
            var entries = await _client.ListAsync(server, _currentDirectory, _listingCancellation.Token);
            EntryList.ItemsSource = entries; PathText.Text = _currentDirectory.AbsoluteUri;
            StatusBar.Title = L("ConnectedTitle"); StatusBar.Message = F("WebDavItemsCount", entries.Count); StatusBar.Severity = InfoBarSeverity.Success;
        }
        catch (OperationCanceledException) { }
        catch (WebDavException exception) { StatusBar.Title = exception.Code; StatusBar.Message = exception.Message; StatusBar.Severity = exception.Code == "AUTH_ERROR" ? InfoBarSeverity.Warning : InfoBarSeverity.Error; }
        catch (Exception exception) { StatusBar.Title = L("NetworkErrorTitle"); StatusBar.Message = exception.Message; StatusBar.Severity = InfoBarSeverity.Error; }
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
    private async void OnAddServerClick(object sender, RoutedEventArgs e) { var server = new WebDavServerSettings(); if (await EditServerAsync(server, true)) { _settings.Network.WebDavServers.Add(server); await SaveAndRefreshServersAsync(server); } }
    private async void OnEditServerClick(object sender, RoutedEventArgs e) { if (ServerList.SelectedItem is WebDavServerSettings server && await EditServerAsync(server, false)) await SaveAndRefreshServersAsync(server); }
    private async void OnDeleteServerClick(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not WebDavServerSettings server) return;
        var confirmation = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L("DeleteWebDavServerTitle"), Content = F("DeleteWebDavServerMessage", server.Name), PrimaryButtonText = L("DeleteButtonText"), CloseButtonText = L("CancelButtonText"), DefaultButton = ContentDialogButton.Close };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        _settings.Network.WebDavServers.Remove(server); _credentials.Delete(CredentialIdentifier.ForWebDav(server.Id)); await _settingsService.SaveAsync(_settings); ServerList.ItemsSource = null; ServerList.ItemsSource = _settings.Network.WebDavServers; EntryList.ItemsSource = null;
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
        await _settingsService.SaveAsync(_settings); ServerList.ItemsSource = null; ServerList.ItemsSource = _settings.Network.WebDavServers; ServerList.SelectedItem = selected;
    }
    private static Uri EnsureDirectoryUri(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) => string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);
    private void OnClosed(object sender, WindowEventArgs args) { _listingCancellation?.Cancel(); _listingCancellation?.Dispose(); _client.Dispose(); }
}
