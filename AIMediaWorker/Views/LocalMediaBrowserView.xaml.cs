using AIMediaWorker.Localization;
using AIMediaWorker.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Text.RegularExpressions;
using Windows.System;

namespace AIMediaWorker.Views;

public sealed partial class LocalMediaBrowserView : UserControl
{
    private BrowserEntry[] _entries = [];
    private BrowserEntry[]? _searchEntries;
    private CancellationTokenSource? _searchCancellation;
    private EntrySortMode _sortMode;
    private int _navigationVersion;

    public LocalMediaBrowserView()
    {
        InitializeComponent();
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

    public async Task NavigateAsync(string directory, string? selectedPath = null)
    {
        ClearSearch();
        var navigationVersion = Interlocked.Increment(ref _navigationVersion);
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!Directory.Exists(directory)) return;
            }

            var entries = await Task.Run(() => EnumerateEntries(directory, selectedPath));
            if (navigationVersion != Volatile.Read(ref _navigationVersion)) return;
            if (!AreSameDirectory(directory, CurrentDirectory)) FilterBox.Text = string.Empty;
            CurrentDirectory = Path.GetFullPath(directory);
            LoadedDirectory = CurrentDirectory;
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

    public static bool AreSameDirectory(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static BrowserEntry[] EnumerateEntries(string directory, string? selectedPath)
    {
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
        var parent = Directory.GetParent(CurrentDirectory);
        if (parent is not null) await NavigateAsync(parent.FullName);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await NavigateAsync(CurrentDirectory);
    private void OnChooseFolderClick(object sender, RoutedEventArgs e) => ChooseFolderRequested?.Invoke(this, EventArgs.Empty);

    private async void OnEntryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BrowserEntry entry) return;
        if (entry.IsDirectory) await NavigateAsync(entry.Path);
        else MediaRequested?.Invoke(this, new LocalMediaBrowserEntryEventArgs(entry.Path, false));
    }

    private async void OnBreadcrumbItemClick(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs e)
    {
        if (e.Item is BrowserBreadcrumbEntry entry && !AreSameDirectory(entry.Path, CurrentDirectory)) await NavigateAsync(entry.Path);
    }

    private void UpdateBreadcrumbs()
    {
        var fullPath = Path.GetFullPath(CurrentDirectory);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            BreadcrumbBar.ItemsSource = new[] { new BrowserBreadcrumbEntry(fullPath, fullPath) };
            return;
        }

        var entries = new List<BrowserBreadcrumbEntry> { new(root, root) };
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath != ".")
        {
            var accumulatedPath = root;
            foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.IsNullOrEmpty(segment)) continue;
                accumulatedPath = Path.Combine(accumulatedPath, segment);
                entries.Add(new BrowserBreadcrumbEntry(segment, accumulatedPath));
            }
        }
        BreadcrumbBar.ItemsSource = entries;
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
                SearchButton.IsEnabled = true;
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
        SearchButton.IsEnabled = true;
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

    private sealed record BrowserBreadcrumbEntry(string Label, string Path);
    private enum EntrySortMode { Name, Newest, Oldest }

    private static string Format(string key, params object[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizationService.Get(key), arguments);

    private sealed record BrowserEntry(string Path, bool IsDirectory, long? Length, DateTime LastModified, string? SearchRelativePath = null)
    {
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        public string DisplayName => SearchRelativePath ?? Name;
        public string IconGlyph => IsDirectory ? "\uE8B7" : MediaFileClassifier.GetFileIconGlyph(Path);
        public string Details => IsDirectory || Length is null ? string.Empty : FormatBytes(Length.Value);

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
