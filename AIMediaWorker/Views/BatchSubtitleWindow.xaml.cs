using AIMediaWorker.Asr;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Llm;
using AIMediaWorker.Localization;
using AIMediaWorker.Network;
using AIMediaWorker.Settings;
using AIMediaWorker.WindowsIntegration;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AIMediaWorker.Views;

public sealed partial class BatchSubtitleWindow : Window
{
    private readonly ObservableCollection<BatchSubtitleItem> _files = [];
    private readonly AppWindow _appWindow;
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private Task? _closeTask;
    private bool _allowClose;
    private bool _picking;

    public BatchSubtitleWindow(Window owner, AppSettings settings)
    {
        InitializeComponent();
        WindowOwner.Attach(this, owner);
        Title = L("BatchWindowTitle");
        Root.RequestedTheme = settings.General.Theme switch
        {
            AppTheme.Light => ElementTheme.Light, AppTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default
        };
        UiFontService.Apply(settings.General.UiFontFamily, Root);
        TranslationText.Text = F("BatchTranslationSettings", settings.Llm.Provider, settings.Llm.Model ?? "—", settings.Llm.TranslationLanguage);
        FileList.ItemsSource = _files;
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
        _appWindow.Resize(new SizeInt32(1200, 960));
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        _appWindow.Closing += OnClosing;
        Closed += OnClosed;
        UpdateControls();
    }

    private bool IsRunning => _cancellation is not null;

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (IsRunning || _picking || _closeTask is not null) return;
        _picking = true;
        UpdateControls();
        try
        {
            var picker = new FileOpenPicker();
            foreach (var extension in WindowsFileAssociationService.SupportedExtensions) picker.FileTypeFilter.Add(extension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (_closeTask is null) AddPaths(files.Select(file => file.Path));
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
        finally { _picking = false; UpdateControls(); }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = !IsRunning && !_picking && _closeTask is null && e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (IsRunning || _picking || _closeTask is not null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        _picking = true;
        UpdateControls();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (_closeTask is null) AddPaths(items.OfType<StorageFile>().Select(file => file.Path));
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
        finally { _picking = false; UpdateControls(); deferral.Complete(); }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var known = _files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rejected = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !WindowsFileAssociationService.SupportedExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            { rejected++; continue; }
            var fullPath = System.IO.Path.GetFullPath(path);
            if (known.Add(fullPath)) _files.Add(new BatchSubtitleItem(fullPath, L("BatchPending")));
        }
        StatusText.Text = rejected > 0 ? F("BatchSkippedFiles", rejected) : L("BatchReady");
        UpdateControls();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (IsRunning || _picking) return;
        if (sender is Button { DataContext: BatchSubtitleItem item }) _files.Remove(item);
        UpdateControls();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (IsRunning || _picking) return;
        _files.Clear();
        StatusText.Text = L("BatchReady");
        UpdateControls();
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (IsRunning || _picking || _closeTask is not null) return;
        var pending = _files.Where(file => !file.Completed).ToArray();
        if (pending.Length == 0) return;
        _cancellation = new CancellationTokenSource();
        var translate = TranslateCheckBox.IsChecked == true;
        foreach (var item in pending) item.SetStatus(L("BatchPending"));
        Progress.Value = 0;
        UpdateControls();
        _runTask = RunAsync(pending, translate, _cancellation.Token);
        try { await _runTask; }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _runTask = null;
            Progress.IsIndeterminate = false;
            UpdateControls();
        }
    }

    private async Task RunAsync(BatchSubtitleItem[] pending, bool translate, CancellationToken token)
    {
        var finished = 0;
        var failed = 0;
        var terminal = new HashSet<BatchSubtitleItem>();
        var byPath = pending.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        try
        {
            var settings = await SettingsService.CreateDefault().LoadAsync(token);
            TranslationText.Text = F("BatchTranslationSettings", settings.Llm.Provider, settings.Llm.Model ?? "—", settings.Llm.TranslationLanguage);
            if (translate && string.IsNullOrWhiteSpace(settings.Llm.Model)) throw new InvalidOperationException(L("LlmModelMissingMessage"));
            var provider = translate ? new LlmProviderFactory(new WindowsCredentialService()).Create(settings.Llm.Provider) : null;
            using var disposable = provider as IDisposable;
            var translator = provider is null ? null : new LlmService(provider, settings.Llm.Model!, settings.Llm.ThinkingLevel);
            await using var engine = new AsrWorkerClient();
            var progress = new InlineProgress<BatchSubtitleUpdate>(update => DispatcherQueue.TryEnqueue(() =>
            {
                if (update.Path is null)
                {
                    StatusText.Text = L("BatchLoading");
                    Progress.IsIndeterminate = true;
                    return;
                }
                var item = byPath[update.Path];
                var status = L("Batch" + update.Stage);
                if (update.Progress is { } value && update.Stage is BatchSubtitleStage.Generating or BatchSubtitleStage.Translating)
                    status += $" {Math.Clamp(value, 0, 1):P0}";
                if (update.Error is not null) status += ": " + update.Error;
                item.SetStatus(status, update.OriginalPath, update.TranslationPath);
                if (update.Stage == BatchSubtitleStage.Completed) { item.Completed = true; terminal.Add(item); finished++; }
                if (update.Stage == BatchSubtitleStage.Failed) { terminal.Add(item); finished++; failed++; }
                StatusText.Text = F("BatchRunning", finished, pending.Length, item.Name);
                Progress.IsIndeterminate = false;
                Progress.Value = 100d * finished / pending.Length;
            }));
            await new BatchSubtitleService(engine, translator).RunAsync(pending.Select(file => file.Path).ToArray(), settings, progress, token);
            // All progress callbacks are queued before this continuation on the UI dispatcher.
            StatusText.Text = F("BatchFinished", pending.Length - failed, failed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            foreach (var item in pending.Where(file => !terminal.Contains(file))) item.SetStatus(L("BatchCancelled"));
            StatusText.Text = L("BatchCancelled");
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            await AppLog.WriteAsync("error", "batch-subtitles", "BATCH_SUBTITLE_ERROR", exception.Message, exception);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = L("BatchCancelling");
    }

    private void UpdateControls()
    {
        var editable = !IsRunning && !_picking && _closeTask is null;
        AddButton.IsEnabled = editable;
        ClearButton.IsEnabled = editable && _files.Count > 0;
        TranslateCheckBox.IsEnabled = editable;
        StartButton.IsEnabled = editable && _files.Any(file => !file.Completed);
        CancelButton.IsEnabled = IsRunning && _cancellation?.IsCancellationRequested == false;
        CountText.Text = F("BatchFileCount", _files.Count);
        foreach (var item in _files) item.SetCanRemove(editable);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        await CloseAsync();
    }

    public Task CloseAsync() => _closeTask ??= CloseCoreAsync();

    private async Task CloseCoreAsync()
    {
        _cancellation?.Cancel();
        if (_runTask is { } task) await task;
        _allowClose = true;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _appWindow.Closing -= OnClosing;
        Closed -= OnClosed;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] args) => string.Format(L(key), args);
}

public sealed class BatchSubtitleItem(string path, string status) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Status { get; private set; } = status;
    public string Outputs { get; private set; } = "";
    public bool CanRemove { get; private set; } = true;
    public bool Completed { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetStatus(string value, string? original = null, string? translation = null)
    {
        Status = value;
        if (original is not null) Outputs = string.Join(Environment.NewLine, new[] { original, translation }.Where(path => path is not null));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(Outputs)));
    }

    public void SetCanRemove(bool value)
    {
        CanRemove = value;
        PropertyChanged?.Invoke(this, new(nameof(CanRemove)));
    }
}
