using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using AIMediaWorker.Media;
using AIMediaWorker.Network;
using AIMediaWorker.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AIMediaWorker.Views;

public sealed partial class WebDavBrowserView : UserControl
{
    private WebDavClient? _client;
    private WebDavCredentialStore? _credentials;
    private IReadOnlyList<WebDavServerSettings> _servers = [];
    private WebDavEntry[] _entries = [];
    private CancellationTokenSource? _listingCancellation;
    private EntrySortMode _sortMode;

    public WebDavBrowserView()
    {
        InitializeComponent();
        UpdateBreadcrumbs();
    }

    public event EventHandler? AddServerRequested;
    public event EventHandler<WebDavServerEventArgs>? DeleteServerRequested;
    public event EventHandler<WebDavEntryEventArgs>? EntryRequested;
    public event EventHandler<WebDavEntryEventArgs>? FavoriteRequested;

    public Guid? CurrentServerId { get; private set; }
    public Uri? CurrentDirectory { get; private set; }
    public IReadOnlyList<WebDavEntry> Entries => _entries;

    public void Configure(WebDavClient client, WebDavCredentialStore credentials)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public void SetServers(IReadOnlyList<WebDavServerSettings> servers, WebDavServerSettings? selected = null)
    {
        _servers = servers;
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = servers;
        EmptyServersText.Visibility = servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected is not null) ServerList.SelectedItem = selected;
        if (CurrentDirectory is null) UpdateBreadcrumbs();
    }

    public async Task ConnectAsync(WebDavServerSettings server, Uri? directory = null)
    {
        EnsureConfigured();
        var credential = _credentials!.Read(server.Id);
        if (credential is null)
        {
            ClearActiveConnection();
            ConnectionStatusText.Text = LocalizationService.Get("WebDavCredentialMissingMessage");
            return;
        }

        var targetDirectory = WebDavUri.AsDirectory(directory ?? credential.RootUri);
        var changedDirectory = CurrentServerId != server.Id || CurrentDirectory is null || !WebDavUri.Equals(CurrentDirectory, targetDirectory);
        CurrentServerId = server.Id;
        CurrentDirectory = targetDirectory;
        if (changedDirectory) FilterBox.Text = string.Empty;
        ServerList.SelectedItem = server;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        EnsureConfigured();
        if (CurrentServerId is not { } serverId || CurrentDirectory is null) return;
        var server = _servers.FirstOrDefault(candidate => candidate.Id == serverId);
        if (server is null) return;

        _listingCancellation?.Cancel();
        _listingCancellation?.Dispose();
        _listingCancellation = new CancellationTokenSource();
        var operation = _listingCancellation;
        SetBusy(true);
        UpdateBreadcrumbs(server);
        ConnectionStatusText.Text = Format("WebDavConnectingMessage", server.Name);
        try
        {
            var entries = await _client!.ListAsync(server, CurrentDirectory, operation.Token);
            if (operation.IsCancellationRequested) return;
            _entries = entries.ToArray();
            ApplyEntryView();
            ConnectionStatusText.Text = Format("WebDavConnectedMessage", server.Name, entries.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (operation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "webdav", exception is WebDavException webDavException ? webDavException.Code : "WEBDAV_LIST_ERROR", exception.Message, exception);
            _entries = [];
            ApplyEntryView();
            ConnectionStatusText.Text = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_listingCancellation, operation)) SetBusy(false);
        }
    }

    public void Disconnect(Guid serverId)
    {
        if (CurrentServerId != serverId) return;
        ClearActiveConnection();
        ServerList.SelectedItem = null;
    }

    public void Synchronize(WebDavServerSettings server, Uri mediaUri, IReadOnlyList<WebDavEntry> entries)
    {
        var directory = WebDavUri.AsDirectory(new Uri(mediaUri, "."));
        var changedDirectory = CurrentServerId != server.Id || CurrentDirectory is null || !WebDavUri.Equals(CurrentDirectory, directory);
        CurrentServerId = server.Id;
        CurrentDirectory = directory;
        ServerList.SelectedItem = server;
        if (changedDirectory) FilterBox.Text = string.Empty;
        _entries = entries.ToArray();
        UpdateBreadcrumbs(server);
        ApplyEntryView();
    }

    public bool TryGetEntries(Guid serverId, Uri directory, out IReadOnlyList<WebDavEntry> entries)
    {
        if (CurrentServerId == serverId && CurrentDirectory is not null && WebDavUri.Equals(CurrentDirectory, directory))
        {
            entries = _entries;
            return true;
        }
        entries = [];
        return false;
    }

    public void SelectEntry(Guid serverId, Uri uri)
    {
        if (CurrentServerId != serverId || EntryList.ItemsSource is not IEnumerable<WebDavEntry> entries) return;
        var selectedEntry = entries.FirstOrDefault(entry => WebDavUri.Equals(entry.Uri, uri));
        if (selectedEntry is null) return;
        EntryList.SelectedItem = selectedEntry;
        EntryList.ScrollIntoView(selectedEntry);
    }

    public void SetStatus(string status) => ConnectionStatusText.Text = status;

    public void Cancel()
    {
        var cancellation = _listingCancellation;
        _listingCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void ClearActiveConnection()
    {
        Cancel();
        CurrentServerId = null;
        CurrentDirectory = null;
        _entries = [];
        EntryList.SelectedItem = null;
        SetBusy(false);
        ParentButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        ConnectionStatusText.Text = string.Empty;
        ApplyEntryView();
        UpdateBreadcrumbs();
    }

    private void SetBusy(bool busy)
    {
        ProgressRing.IsActive = busy;
        EntryList.IsEnabled = !busy;
        ParentButton.IsEnabled = !busy && CurrentDirectory is not null;
        RefreshButton.IsEnabled = !busy && CurrentDirectory is not null;
    }

    private async void OnServerClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WebDavServerSettings server) await ConnectAsync(server);
    }

    private void OnAddServerClick(object sender, RoutedEventArgs e) => AddServerRequested?.Invoke(this, EventArgs.Empty);

    private void OnDeleteServerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WebDavServerSettings server })
            DeleteServerRequested?.Invoke(this, new WebDavServerEventArgs(server));
    }

    private void OnDeleteServerButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var label = LocalizationService.Get("DeleteButtonText");
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
    }

    private async void OnParentClick(object sender, RoutedEventArgs e)
    {
        if (CurrentServerId is not { } serverId || CurrentDirectory is null) return;
        var credential = _credentials?.Read(serverId);
        if (credential is null) return;
        var root = WebDavUri.AsDirectory(credential.RootUri);
        var parent = WebDavUri.AsDirectory(new Uri(CurrentDirectory, "../"));
        if (!parent.AbsoluteUri.StartsWith(root.AbsoluteUri, StringComparison.OrdinalIgnoreCase)) parent = root;
        if (!WebDavUri.Equals(parent, CurrentDirectory)) FilterBox.Text = string.Empty;
        CurrentDirectory = parent;
        await RefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnBreadcrumbItemClick(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs e)
    {
        if (e.Item is not WebDavBreadcrumbEntry entry || entry.Uri is null || entry.Uri == CurrentDirectory) return;
        if (CurrentDirectory is null || !WebDavUri.Equals(entry.Uri, CurrentDirectory)) FilterBox.Text = string.Empty;
        CurrentDirectory = entry.Uri;
        await RefreshAsync();
    }

    private void UpdateBreadcrumbs(WebDavServerSettings? server = null)
    {
        if (server is null || CurrentDirectory is null || CurrentServerId is not { } serverId)
        {
            BreadcrumbBar.ItemsSource = new[] { new WebDavBreadcrumbEntry(LocalizationService.Get("WebDavSelectServerMessage"), null) };
            return;
        }

        var credential = _credentials?.Read(serverId);
        if (credential is null)
        {
            BreadcrumbBar.ItemsSource = new[] { new WebDavBreadcrumbEntry(server.Name, null) };
            return;
        }

        var root = WebDavUri.AsDirectory(credential.RootUri);
        var current = WebDavUri.AsDirectory(CurrentDirectory);
        var entries = new List<WebDavBreadcrumbEntry> { new(server.Name, root) };
        if (current.AbsoluteUri.StartsWith(root.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = root.MakeRelativeUri(current).OriginalString;
            var accumulatedPath = string.Empty;
            foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                accumulatedPath += segment + "/";
                entries.Add(new WebDavBreadcrumbEntry(Uri.UnescapeDataString(segment), WebDavUri.AsDirectory(new Uri(root, accumulatedPath))));
            }
        }
        BreadcrumbBar.ItemsSource = entries;
    }

    private async void OnEntryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WebDavEntry entry || CurrentServerId is not { } serverId) return;
        if (entry.IsCollection)
        {
            var targetDirectory = WebDavUri.AsDirectory(entry.Uri);
            if (CurrentDirectory is null || !WebDavUri.Equals(targetDirectory, CurrentDirectory)) FilterBox.Text = string.Empty;
            CurrentDirectory = targetDirectory;
            await RefreshAsync();
            return;
        }
        var server = _servers.FirstOrDefault(candidate => candidate.Id == serverId);
        if (server is not null)
            EntryRequested?.Invoke(this, new WebDavEntryEventArgs(server, entry, (EntryList.ItemsSource as IEnumerable<WebDavEntry>)?.ToArray() ?? []));
    }

    private void OnEntryRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WebDavEntry entry }) EntryList.SelectedItem = entry;
    }

    private void OnAddFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not WebDavEntry entry || CurrentServerId is not { } serverId) return;
        var server = _servers.FirstOrDefault(candidate => candidate.Id == serverId);
        if (server is not null) FavoriteRequested?.Invoke(this, new WebDavEntryEventArgs(server, entry, []));
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e) => ApplyEntryView();

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        _sortMode = _sortMode switch
        {
            EntrySortMode.Name => EntrySortMode.Newest,
            EntrySortMode.Newest => EntrySortMode.Oldest,
            _ => EntrySortMode.Name
        };
        ApplyEntryView();
    }

    private void ApplyEntryView()
    {
        var selectedUri = (EntryList.SelectedItem as WebDavEntry)?.Uri;
        var filter = FilterBox.Text.Trim();
        IEnumerable<WebDavEntry> filtered = string.IsNullOrEmpty(filter)
            ? _entries
            : _entries.Where(entry => entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        filtered = _sortMode switch
        {
            EntrySortMode.Newest => filtered.OrderByDescending(entry => entry.IsCollection).ThenBy(entry => entry.LastModified is null).ThenByDescending(entry => entry.LastModified).ThenBy(entry => entry.Name, WindowsFileNameComparer.Instance),
            EntrySortMode.Oldest => filtered.OrderByDescending(entry => entry.IsCollection).ThenBy(entry => entry.LastModified is null).ThenBy(entry => entry.LastModified).ThenBy(entry => entry.Name, WindowsFileNameComparer.Instance),
            _ => filtered.OrderByDescending(entry => entry.IsCollection).ThenBy(entry => entry.Name, WindowsFileNameComparer.Instance)
        };
        var view = filtered.ToArray();
        EntryList.ItemsSource = view;
        if (selectedUri is not null) EntryList.SelectedItem = view.FirstOrDefault(entry => WebDavUri.Equals(entry.Uri, selectedUri));
        UpdateSortButton();
    }

    private void UpdateSortButton()
    {
        SortButton.Label = LocalizationService.Get(_sortMode switch
        {
            EntrySortMode.Newest => "SortNewest",
            EntrySortMode.Oldest => "SortOldest",
            _ => "SortName"
        });
        SortIcon.Glyph = _sortMode switch
        {
            EntrySortMode.Newest => "\uE74B",
            EntrySortMode.Oldest => "\uE74A",
            _ => "\uE8CB"
        };
    }

    private void EnsureConfigured()
    {
        if (_client is null || _credentials is null) throw new InvalidOperationException("The WebDAV browser has not been configured.");
    }

    private static string Format(string key, params object[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizationService.Get(key), arguments);

    private sealed record WebDavBreadcrumbEntry(string Label, Uri? Uri);
    private enum EntrySortMode { Name, Newest, Oldest }
}

public sealed class WebDavServerEventArgs(WebDavServerSettings server) : EventArgs
{
    public WebDavServerSettings Server { get; } = server;
}

public sealed class WebDavEntryEventArgs(WebDavServerSettings server, WebDavEntry entry, IReadOnlyList<WebDavEntry> siblings) : EventArgs
{
    public WebDavServerSettings Server { get; } = server;
    public WebDavEntry Entry { get; } = entry;
    public IReadOnlyList<WebDavEntry> Siblings { get; } = siblings;
}
