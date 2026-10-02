using AIMediaWorker.Localization;
using AIMediaWorker.Media;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.System;

namespace AIMediaWorker.Views;

public sealed partial class LocalMediaBrowserView : UserControl
{
    private BrowserEntry[] _entries = [];
    private BrowserEntry[]? _searchEntries;
    private CancellationTokenSource? _searchCancellation;
    private EntrySortMode _sortMode;
    private int _navigationVersion;
    private IReadOnlyList<LocalBrowserBreadcrumb> _breadcrumbs = [];
    private readonly List<Button> _breadcrumbButtons = [];
    private readonly LocalBrowserNavigationHistory _navigationHistory = new();
    private bool _historyNavigationInProgress;

    public LocalMediaBrowserView()
    {
        InitializeComponent();
        AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnBrowserPointerPressed), handledEventsToo: true);
        var regexSearchTooltip = LocalizationService.Get("RegexSearchTooltip");
        ToolTipService.SetToolTip(RegexSearchToggle, regexSearchTooltip);
        AutomationProperties.SetName(RegexSearchToggle, regexSearchTooltip);
        CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    public event EventHandler? ChooseFolderRequested;
    public event EventHandler<LocalMediaBrowserEntryEventArgs>? MediaRequested;
    public event EventHandler<LocalMediaBrowserEntryEventArgs>? FavoriteRequested;
    public event EventHandler<LocalMediaBrowserErrorEventArgs>? ErrorOccurred;

    public string? DefaultDirectory { get; set; }
    public string CurrentDirectory { get; private set; }
    public string? LoadedDirectory { get; private set; }

    public Task InitializeAsync() => NavigateAsync(ResolveDefaultDirectory(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)));

    public Task NavigateAsync(string directory, string? selectedPath = null) => NavigateCoreAsync(directory, selectedPath);

    private async Task NavigateCoreAsync(string directory, string? selectedPath = null, LocalBrowserHistoryTarget? historyTarget = null)
    {
        ClearSearch();
        var navigationVersion = Interlocked.Increment(ref _navigationVersion);
        try
        {
            if (directory != LocalBrowserPath.Root && (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)))
            {
                if (historyTarget is not null) throw new DirectoryNotFoundException(directory);
                directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!Directory.Exists(directory)) return;
            }

            var entries = await Task.Run(() => EnumerateEntries(directory, selectedPath));
            if (navigationVersion != Volatile.Read(ref _navigationVersion)) return;
            if (historyTarget is not null && !_navigationHistory.TryCommit(historyTarget)) return;
            if (!AreSameDirectory(directory, CurrentDirectory)) FilterBox.Text = string.Empty;
            CurrentDirectory = directory == LocalBrowserPath.Root ? directory : Path.GetFullPath(directory);
            if (historyTarget is null) _navigationHistory.Record(CurrentDirectory);
            LoadedDirectory = directory == LocalBrowserPath.Root ? null : CurrentDirectory;
            _entries = entries;
            UpdateBreadcrumbs();
            ApplyEntryView();
            if (selectedPath is not null) SelectPath(selectedPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ErrorOccurred?.Invoke(this, new LocalMediaBrowserErrorEventArgs(exception));
        }
    }

    public void PrepareForOpenedFile(string fullPath)
    {
        Interlocked.Increment(ref _navigationVersion);
        if (Path.GetDirectoryName(fullPath) is not { } directory) return;
        if (AreSameDirectory(directory, CurrentDirectory))
        {
            SelectPath(fullPath);
            return;
        }

        CurrentDirectory = Path.GetFullPath(directory);
        _navigationHistory.Record(CurrentDirectory);
        LoadedDirectory = null;
        ClearSearch();
        FilterBox.Text = string.Empty;
        _entries = [];
        UpdateBreadcrumbs();
        ApplyEntryView();
    }

    public async Task SynchronizeOpenedFileAsync(string fullPath)
    {
        if (Path.GetDirectoryName(fullPath) is not { } directory) return;
        if (LoadedDirectory is not null && AreSameDirectory(directory, LoadedDirectory))
        {
            SelectPath(fullPath);
            return;
        }

        await NavigateAsync(directory, fullPath);
    }

    public IReadOnlyList<string>? GetLoadedMediaPaths(string directory) =>
        LoadedDirectory is not null && AreSameDirectory(directory, LoadedDirectory)
            ? _entries.Where(entry => !entry.IsDirectory).Select(entry => entry.Path).ToArray()
            : null;

    public void SelectPath(string path)
    {
        if (EntryList.ItemsSource is not IEnumerable<BrowserEntry> entries) return;
        var fullPath = Path.GetFullPath(path);
        var selectedEntry = entries.FirstOrDefault(item => item.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        if (selectedEntry is null) return;
        EntryList.SelectedItem = selectedEntry;
        EntryList.ScrollIntoView(selectedEntry);
    }

    public static bool AreSameDirectory(string first, string second) => LocalBrowserPath.AreSameDirectory(first, second);

    private static BrowserEntry[] EnumerateEntries(string directory, string? selectedPath)
    {
        if (directory == LocalBrowserPath.Root)
            return DriveInfo.GetDrives().Select(drive => BrowserEntry.FromDrive(drive.Name)).ToArray();

        const int maximumEntries = 5000;
        var result = new List<BrowserEntry>();
        foreach (var path in Directory.EnumerateDirectories(directory).Take(maximumEntries).OrderBy(Path.GetFileName, WindowsFileNameComparer.Instance))
        {
            try { result.Add(BrowserEntry.FromDirectory(path)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        var remaining = Math.Max(0, maximumEntries - result.Count);
        foreach (var path in Directory.EnumerateFiles(directory).Where(MediaFileClassifier.IsPlayable).Take(remaining).OrderBy(Path.GetFileName, WindowsFileNameComparer.Instance))
        {
            try { result.Add(BrowserEntry.FromFile(path)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        if (selectedPath is not null && File.Exists(selectedPath) && !result.Any(item => item.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase)))
        {
            try { result.Add(BrowserEntry.FromFile(selectedPath)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return result.ToArray();
    }

    private string ResolveDefaultDirectory(string fallback) =>
        !string.IsNullOrWhiteSpace(DefaultDirectory) && Directory.Exists(DefaultDirectory) ? DefaultDirectory : fallback;

    private async void OnHomeClick(object sender, RoutedEventArgs e) =>
        await NavigateAsync(ResolveDefaultDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    private async void OnParentClick(object sender, RoutedEventArgs e)
    {
        var parent = LocalBrowserPath.GetParent(CurrentDirectory);
        if (parent is not null) await NavigateAsync(parent);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await NavigateAsync(CurrentDirectory);
    private void OnChooseFolderClick(object sender, RoutedEventArgs e) => ChooseFolderRequested?.Invoke(this, EventArgs.Empty);

    private async void OnEntryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BrowserEntry entry) return;
        if (entry.IsDirectory) await NavigateAsync(entry.Path);
        else MediaRequested?.Invoke(this, new LocalMediaBrowserEntryEventArgs(entry.Path, false));
    }

    private async void OnBrowserPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var updateKind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (updateKind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return;
        e.Handled = true;
        if (_historyNavigationInProgress) return;

        var target = _navigationHistory.GetTarget(forward: updateKind == PointerUpdateKind.XButton2Pressed);
        if (target is null) return;

        _historyNavigationInProgress = true;
        try
        {
            await NavigateCoreAsync(target.Directory, historyTarget: target);
        }
        finally
        {
            _historyNavigationInProgress = false;
        }
    }

    private void UpdateBreadcrumbs()
    {
        BreadcrumbOverflowButton.Flyout?.Hide();
        _breadcrumbs = LocalBrowserPath.GetBreadcrumbs(CurrentDirectory);
        _breadcrumbButtons.Clear();
        BreadcrumbItems.Children.Clear();
        for (var index = 0; index < _breadcrumbs.Count; index++)
        {
            var entry = _breadcrumbs[index];
            var content = new Grid { ColumnSpacing = 4 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(new TextBlock { Text = entry.Label, TextTrimming = TextTrimming.CharacterEllipsis });
            if (index < _breadcrumbs.Count - 1)
            {
                var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(chevron, 1);
                content.Children.Add(chevron);
            }
            var button = new Button { Content = content, Style = (Style)Resources["BreadcrumbNavigationButtonStyle"] };
            ToolTipService.SetToolTip(button, entry.Path);
            AutomationProperties.SetName(button, entry.Label);
            button.Click += async (_, _) =>
            {
                if (!AreSameDirectory(entry.Path, CurrentDirectory)) await NavigateAsync(entry.Path);
            };
            _breadcrumbButtons.Add(button);
            BreadcrumbItems.Children.Add(button);
        }
        UpdateBreadcrumbLayout();

        var canSearch = CurrentDirectory != LocalBrowserPath.Root;
        SearchBox.IsEnabled = canSearch;
        SearchButton.IsEnabled = canSearch;
        RegexSearchToggle.IsEnabled = canSearch;
    }

    private void OnBreadcrumbSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBreadcrumbLayout();

    private void UpdateBreadcrumbLayout()
    {
        if (_breadcrumbButtons.Count == 0 || BreadcrumbHost.ActualWidth <= 0) return;
        var widths = new double[_breadcrumbButtons.Count];
        for (var index = 0; index < _breadcrumbButtons.Count; index++)
        {
            var button = _breadcrumbButtons[index];
            button.Visibility = Visibility.Visible;
            button.MaxWidth = double.PositiveInfinity;
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widths[index] = button.DesiredSize.Width + BreadcrumbItems.Spacing;
        }
        BreadcrumbOverflowButton.Visibility = Visibility.Visible;
        BreadcrumbOverflowButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var overflowWidth = BreadcrumbOverflowButton.DesiredSize.Width + BreadcrumbHost.ColumnSpacing;
        var hiddenCount = LocalBrowserPath.GetHiddenBreadcrumbCount(widths, BreadcrumbHost.ActualWidth + BreadcrumbItems.Spacing, overflowWidth);
        var flyout = new MenuFlyout();
        for (var index = 0; index < hiddenCount; index++)
        {
            _breadcrumbButtons[index].Visibility = Visibility.Collapsed;
            var entry = _breadcrumbs[index];
            var item = new MenuFlyoutItem { Text = entry.Label };
            ToolTipService.SetToolTip(item, entry.Path);
            item.Click += async (_, _) => await NavigateAsync(entry.Path);
            flyout.Items.Add(item);
        }
        BreadcrumbOverflowButton.Flyout = flyout;
        BreadcrumbOverflowButton.Visibility = hiddenCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        var precedingWidth = widths.Skip(hiddenCount).Take(widths.Length - hiddenCount - 1).Sum();
        _breadcrumbButtons[^1].MaxWidth = Math.Max(0, BreadcrumbHost.ActualWidth - precedingWidth - (hiddenCount > 0 ? overflowWidth : 0));
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e) => ApplyEntryView();

    private async void OnSearchClick(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private async void OnRegexSearchClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) await SearchAsync();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text)) ClearSearch(clearQuery: false);
    }

    private async Task SearchAsync()
    {
        if (CurrentDirectory == LocalBrowserPath.Root) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            ClearSearch();
            return;
        }

        var previous = _searchCancellation;
        _searchCancellation = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
        var operation = _searchCancellation;
        SearchButton.IsEnabled = false;
        SearchProgressRing.IsActive = true;
        SearchStatusText.Text = LocalizationService.Get("SearchInProgress");
        try
        {
            var results = await LocalMediaSearchService.SearchAsync(CurrentDirectory, query, RegexSearchToggle.IsChecked == true, operation.Token);
            if (operation.IsCancellationRequested) return;
            _searchEntries = results.Select(result => result.IsDirectory
                ? BrowserEntry.FromDirectory(result.Path, result.RelativePath)
                : BrowserEntry.FromFile(result.Path, result.RelativePath)).ToArray();
            ApplyEntryView();
            SearchStatusText.Text = Format("SearchResultsFormat", _searchEntries.Length);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            _searchEntries = [];
            ApplyEntryView();
            SearchStatusText.Text = Format("SearchInvalidPatternFormat", exception.Message);
        }
        catch (Exception exception)
        {
            _searchEntries = [];
            ApplyEntryView();
            ErrorOccurred?.Invoke(this, new LocalMediaBrowserErrorEventArgs(exception));
            SearchStatusText.Text = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, operation))
            {
                SearchButton.IsEnabled = CurrentDirectory != LocalBrowserPath.Root;
                SearchProgressRing.IsActive = false;
            }
        }
    }

    private void ClearSearch(bool clearQuery = true)
    {
        var operation = _searchCancellation;
        _searchCancellation = null;
        operation?.Cancel();
        operation?.Dispose();
        _searchEntries = null;
        if (clearQuery && SearchBox.Text.Length > 0) SearchBox.Text = string.Empty;
        SearchStatusText.Text = string.Empty;
        SearchButton.IsEnabled = CurrentDirectory != LocalBrowserPath.Root;
        SearchProgressRing.IsActive = false;
        ApplyEntryView();
    }

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
        var selectedPath = (EntryList.SelectedItem as BrowserEntry)?.Path;
        var filter = FilterBox.Text.Trim();
        var source = _searchEntries ?? _entries;
        IEnumerable<BrowserEntry> filtered = string.IsNullOrEmpty(filter)
            ? source
            : source.Where(entry => entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        filtered = _sortMode switch
        {
            EntrySortMode.Newest => filtered.OrderByDescending(entry => entry.IsDirectory).ThenByDescending(entry => entry.LastModified).ThenBy(entry => entry.Name, WindowsFileNameComparer.Instance),
            EntrySortMode.Oldest => filtered.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.LastModified).ThenBy(entry => entry.Name, WindowsFileNameComparer.Instance),
            _ => filtered.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, WindowsFileNameComparer.Instance)
        };
        var view = filtered.ToArray();
        EntryList.ItemsSource = view;
        if (selectedPath is not null) EntryList.SelectedItem = view.FirstOrDefault(entry => entry.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
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

    private void OnEntryRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserEntry entry }) EntryList.SelectedItem = entry;
    }

    private void OnAddFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is BrowserEntry entry)
            FavoriteRequested?.Invoke(this, new LocalMediaBrowserEntryEventArgs(entry.Path, entry.IsDirectory));
    }

    private enum EntrySortMode { Name, Newest, Oldest }

    private static string Format(string key, params object[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizationService.Get(key), arguments);

    private sealed record BrowserEntry(string Path, bool IsDirectory, long? Length, DateTime LastModified, string? SearchRelativePath = null)
    {
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        public string DisplayName => SearchRelativePath ?? Name;
        public string IconGlyph => IsDirectory ? "\uE8B7" : MediaFileClassifier.GetFileIconGlyph(Path);
        public string Details => IsDirectory || Length is null ? string.Empty : FormatBytes(Length.Value);

        public static BrowserEntry FromDrive(string path) => new(path, true, null, DateTime.MinValue, path);

        public static BrowserEntry FromDirectory(string path, string? searchRelativePath = null)
        {
            var info = new DirectoryInfo(path);
            return new BrowserEntry(path, true, null, info.LastWriteTimeUtc, searchRelativePath);
        }

        public static BrowserEntry FromFile(string path, string? searchRelativePath = null)
        {
            var info = new FileInfo(path);
            return new BrowserEntry(path, false, info.Length, info.LastWriteTimeUtc, searchRelativePath);
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            var display = (double)Math.Max(0, bytes);
            var unit = 0;
            while (display >= 1024 && unit < units.Length - 1) { display /= 1024; unit++; }
            return $"{display:0.##} {units[unit]}";
        }
    }
}

public sealed class LocalMediaBrowserEntryEventArgs(string path, bool isDirectory) : EventArgs
{
    public string Path { get; } = path;
    public bool IsDirectory { get; } = isDirectory;
}

public sealed class LocalMediaBrowserErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
