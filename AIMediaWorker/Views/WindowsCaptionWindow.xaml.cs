using AIMediaWorker.Asr;
using AIMediaWorker.Capture;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Llm;
using AIMediaWorker.Localization;
using AIMediaWorker.Network;
using AIMediaWorker.Settings;
using AIMediaWorker.Subtitle;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Threading.Channels;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace AIMediaWorker.Views;

/// <summary>
/// Small always-on-top overlay that captions the system's output audio in real
/// time and optionally translates each finished caption. Closing the overlay
/// returns to the main window.
/// </summary>
public sealed partial class WindowsCaptionWindow : Window
{
    private readonly AsrWorkerClient _asr = new();
    private readonly AudioCaptureService _audio = new();
    private readonly LiveAsrController _liveAsr;
    private readonly LiveCaptionStabilizer _captionStabilizer = new();
    private readonly Channel<TranslationRequest> _translationQueue = Channel.CreateUnbounded<TranslationRequest>();
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private readonly DispatcherQueueTimer _pendingFlushTimer;
    private const int MaximumDisplayedTranslationHistoryItems = 2;
    private readonly List<string> _captionPreviousItems = [];
    private readonly List<string> _translationPreviousItems = [];
    private int _pendingTranslationUpdates;
    private AppSettings _settings = new();
    private AppWindow? _appWindow;
    private Task? _shutdownTask;
    private Task? _translationTask;
    private CancellationTokenSource? _translationCancellation;
    private LlmService? _llm;
    private string _lastCaptionForTranslation = string.Empty;
    private string _latestCaptionText = string.Empty;
    private string _latestTranslationText = string.Empty;
    private long _captionSentenceId;
    private long _latestTranslationSentenceId = -1;
    private bool _translating;
    private bool _translationOnly;
    private bool _showPrevious = true;
    private int _previousSentenceCount = 2;
    private bool _initializingControls;
    private bool _allowClose;

    private sealed record TranslationRequest(string Text, long SentenceId);

    public WindowsCaptionWindow(Window owner)
    {
        InitializeComponent();
        Title = L("WindowsCaptionWindow.Title");
        AppTitleText.Text = Title;
        ExtendsContentIntoTitleBar = true;
        WindowOwner.Attach(this, owner);
        _liveAsr = new LiveAsrController(_audio, _asr);
        _liveAsr.CaptionReceived += OnCaptionReceived;
        _liveAsr.Failed += OnLiveFailed;
        CaptionButton.Content = L("WindowsCaptionStart.Content");
        TranslateButton.Content = L("WindowsCaptionTranslate.Content");
        TranslationOnlyButton.Content = L("WindowsCaptionTranslationOnly.Content");
        ShowPreviousButton.Content = L("WindowsCaptionShowPrevious.Content");
        FontSizeLabel.Text = L("WindowsCaptionFontSize.Text");
        CloseCaptionButton.Content = L("CloseButton");
        _pendingFlushTimer = DispatcherQueue.CreateTimer();
        _pendingFlushTimer.Interval = TimeSpan.FromSeconds(4);
        _pendingFlushTimer.Tick += OnPendingFlushTick;
        Closed += OnClosed;
        var handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        if (_appWindow is not null)
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
            _appWindow.Resize(new SizeInt32(1360, 384));
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
            }
            _appWindow.Closing += OnAppWindowClosing;
            _appWindow.Changed += OnAppWindowChanged;
        }
        Root.ActualThemeChanged += OnRootActualThemeChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = await SettingsService.CreateDefault().LoadAsync();
        UiFontService.Apply(_settings.General.UiFontFamily, Root);
        ApplyTheme(_settings.General.Theme);
        _initializingControls = true;
        _translationOnly = _settings.Capture.WindowsCaptionTranslationOnly;
        _showPrevious = _settings.Capture.WindowsCaptionShowPrevious;
        _previousSentenceCount = Math.Clamp(_settings.Capture.WindowsCaptionPreviousSentenceCount, 1, 2);
        TranslationOnlyButton.IsChecked = _translationOnly;
        ShowPreviousButton.IsChecked = _showPrevious;
        PreviousSentenceCountCombo.ItemsSource = new[] { 1, 2 };
        PreviousSentenceCountCombo.SelectedItem = _previousSentenceCount;
        PreviousSentenceCountCombo.IsEnabled = _showPrevious;
        CaptionFontSizeBox.Value = Math.Clamp(_settings.Capture.CaptionFontSize, 16, 72);
        _initializingControls = false;
        ApplyCaptionAppearance();
        UpdateTitleBarDragRegion();
    }

    private void ApplyTheme(AppTheme theme)
    {
        Root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyTitleBarTheme(Root.ActualTheme);
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args) => ApplyTitleBarTheme(sender.ActualTheme);

    private void ApplyTitleBarTheme(ElementTheme theme)
    {
        if (_appWindow?.TitleBar is not { } titleBar) return;
        var dark = theme == ElementTheme.Dark;
        var background = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 24, 24, 24);
        var inactiveForeground = dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 110, 110, 110);
        var hover = dark ? Color.FromArgb(255, 58, 58, 58) : Color.FromArgb(255, 224, 224, 224);
        var pressed = dark ? Color.FromArgb(255, 72, 72, 72) : Color.FromArgb(255, 208, 208, 208);
        AppTitleBarArea.Background = new SolidColorBrush(background);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
    }

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarDragRegion();

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => UpdateTitleBarDragRegion();

    private void UpdateTitleBarDragRegion()
    {
        if (_appWindow?.TitleBar is not { } titleBar) return;
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var width = AppTitleBarArea.ActualWidth * scale;
        var height = AppTitleBarArea.ActualHeight * scale;
        var dragWidth = Math.Max(0, width - titleBar.RightInset);
        var dragHeight = Math.Max(0, height);
        titleBar.SetDragRectangles([new RectInt32(0, 0, (int)dragWidth, (int)dragHeight)]);
    }

    private async void OnCaptionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_liveAsr.IsRunning)
            {
                CaptionButton.IsEnabled = false;
                await _liveAsr.StopAsync();
                CaptionButton.Content = L("WindowsCaptionStart.Content");
                CaptionButton.IsEnabled = true;
                SetStatus(L("WindowsCaptionStopped.Title"), L("WindowsCaptionStopped.Message"), InfoBarSeverity.Informational);
                return;
            }
            if (!File.Exists(AsrRuntimePaths.CrispAsrDllPath))
            {
                SetStatus(L("AsrInstallRequiredTitle"), L("AsrInstallRequiredMessage"), InfoBarSeverity.Warning);
                return;
            }
            CaptionButton.IsEnabled = false;
            var runtimeDirectory = AsrRuntimePaths.GetCrispAsrRuntimeDirectory(_settings.Asr.CrispAsrRuntimeDirectory);
            await _asr.StartAsync(runtimeDirectory);
            var acceptingLoadProgress = true;
            var loadProgress = new Progress<AsrEvent>(update => { if (acceptingLoadProgress) UpdateAsrModelProgress(update); });
            try { await _asr.LoadModelAsync(_settings.Asr.ModelPath ?? AsrSettings.DefaultModelId, _settings.Asr.AlignerPath, _settings.Asr.Device.ToString(), _settings.Asr.Precision.ToString(), loadProgress); }
            finally { acceptingLoadProgress = false; }
            ClearCaptionHistory();
            await _liveAsr.StartLoopbackAsync(null, _settings.Asr.Language);
            CaptionButton.Content = L("WindowsCaptionStop.Content");
            CaptionButton.IsEnabled = true;
            TranslateButton.IsEnabled = true;
            TranslationOnlyButton.IsEnabled = true;
            SetStatus(L("WindowsCaptionListening.Title"), L("WindowsCaptionListening.Message"), InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            CaptionButton.IsEnabled = true;
            SetStatus(L("WindowsCaptionErrorTitle"), exception.Message, InfoBarSeverity.Error);
        }
    }

    private void UpdateAsrModelProgress(AsrEvent update)
    {
        if (update.Stage == "download" && update.Progress is { } progress)
        {
            var model = update.Message ?? "Qwen3-ASR";
            var modelProgress = update.ModelProgress ?? progress;
            var message = update.TotalBytes is > 0 && update.DownloadedBytes is { } downloaded
                ? F("StatusDownloadingAsrModel", model, modelProgress, FormatDownloadSize(downloaded), FormatDownloadSize(update.TotalBytes.Value))
                : F("StatusPreparingAsrDownload", model);
            SetStatus(L("StatusLoadingAsr"), message, InfoBarSeverity.Informational);
        }
        else
        {
            var message = update.ElapsedSeconds is > 0 ? $"{L("StatusLoadingAsr")} ({update.ElapsedSeconds}s)" : L("StatusLoadingAsr");
            SetStatus(L("StatusLoadingAsr"), message, InfoBarSeverity.Informational);
        }
    }

    private static string FormatDownloadSize(long bytes) => bytes >= 1_073_741_824
        ? $"{bytes / 1_073_741_824d:0.00} GB"
        : $"{bytes / 1_048_576d:0.0} MB";

    private void OnCaptionReceived(object? sender, AsrEvent result) => DispatcherQueue.TryEnqueue(() =>
    {
        var text = NormalizeHistoryText(_captionStabilizer.Update(result));
        if (string.IsNullOrWhiteSpace(text) || !UpdateCaptionSentences(text)) return;
        _lastCaptionForTranslation = _latestCaptionText;
        UpdateCaptionFontSize();
        if (!_translating) return;
        // The live ASR emits rolling "partial" updates and a "final" only when
        // the stream stops, so batch every two updates into one request.
        _pendingTranslationUpdates++;
        if (result.Event != "partial" || _pendingTranslationUpdates >= 2)
        {
            _pendingTranslationUpdates = 0;
            _pendingFlushTimer.Stop();
            if (!string.IsNullOrWhiteSpace(_lastCaptionForTranslation))
                _translationQueue.Writer.TryWrite(new TranslationRequest(_lastCaptionForTranslation, _captionSentenceId));
        }
        else
        {
            _pendingFlushTimer.Start();
        }
    });

    private void OnPendingFlushTick(DispatcherQueueTimer sender, object args)
    {
        if (!_translating || _pendingTranslationUpdates == 0) return;
        _pendingTranslationUpdates = 0;
        if (!string.IsNullOrWhiteSpace(_latestCaptionText))
        {
            _lastCaptionForTranslation = _latestCaptionText;
            _translationQueue.Writer.TryWrite(new TranslationRequest(_lastCaptionForTranslation, _captionSentenceId));
        }
    }

    private void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        if (TranslateButton.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(_settings.Llm.Model))
            {
                TranslateButton.IsChecked = false;
                SetStatus(L("LlmModelMissingTitle"), L("LlmModelMissingMessage"), InfoBarSeverity.Warning);
                return;
            }
            try
            {
                _llm ??= new LlmService(new LlmProviderFactory(new WindowsCredentialService()).Create(_settings.Llm.Provider), _settings.Llm.Model, _settings.Llm.ThinkingLevel);
            }
            catch (Exception exception)
            {
                TranslateButton.IsChecked = false;
                SetStatus(L("WindowsCaptionErrorTitle"), exception.Message, InfoBarSeverity.Error);
                return;
            }
            _translating = true;
            TranslationOnlyButton.IsEnabled = true;
            TranslateButton.Content = L("WindowsCaptionTranslateOn.Content");
            RebuildHistoryText();
            UpdateCaptionFontSize();
            var cancellation = _translationCancellation ??= new CancellationTokenSource();
            _translationTask ??= Task.Run(() => TranslationLoopAsync(cancellation.Token));
            if (!string.IsNullOrWhiteSpace(_latestCaptionText))
                _translationQueue.Writer.TryWrite(new TranslationRequest(_latestCaptionText, _captionSentenceId));
        }
        else
        {
            _translating = false;
            _pendingTranslationUpdates = 0;
            _pendingFlushTimer.Stop();
            TranslateButton.Content = L("WindowsCaptionTranslate.Content");
            TranslationOnlyButton.IsEnabled = false;
            ClearTranslationHistory();
            UpdateCaptionFontSize();
        }
    }

    private async Task TranslationLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var firstItem in _translationQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Drain to the newest caption so translation stays real-time.
                var latest = firstItem;
                while (_translationQueue.Reader.TryRead(out var queuedItem)) latest = queuedItem;
                if (!_translating || string.IsNullOrWhiteSpace(latest.Text)) continue;
                await TranslateAsync(latest, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { await AppLog.WriteAsync("error", "captions", "CAPTION_TRANSLATION_ERROR", exception.Message, exception); }
    }

    private async Task TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        var llm = _llm;
        if (llm is null) return;
        try
        {
            var cue = new SubtitleCue { Text = request.Text };
            var result = await llm.TranslateAsync([cue], _settings.Llm.TranslationLanguage, batchSize: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.TryGetValue(cue.Id, out var translated) && !string.IsNullOrWhiteSpace(translated))
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_translating || request.SentenceId != _captionSentenceId ||
                        !string.Equals(request.Text, _latestCaptionText, StringComparison.Ordinal)) return;
                    SetLatestTranslation(translated);
                    UpdateCaptionFontSize();
                });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "captions", "CAPTION_TRANSLATION_ERROR", exception.Message, exception);
            DispatcherQueue.TryEnqueue(() => SetStatus(L("WindowsCaptionErrorTitle"), exception.Message, InfoBarSeverity.Error));
        }
    }

    private void ApplyCaptionAppearance()
    {
        var fontFamily = new FontFamily(string.IsNullOrWhiteSpace(_settings.Subtitle.FontFamily) ? SubtitleSettings.DefaultFontFamily : _settings.Subtitle.FontFamily);
        var captionColor = new SolidColorBrush(ParseColor(_settings.Capture.CaptionTextColor, Color.FromArgb(255, 255, 255, 255)));
        var captionPreviousColor = new SolidColorBrush(WithOpacity(captionColor.Color, 0.48));
        var translationColor = new SolidColorBrush(Color.FromArgb(255, 255, 224, 176));
        var translationPreviousColor = new SolidColorBrush(WithOpacity(translationColor.Color, 0.48));
        CaptionPreviousText.FontFamily = fontFamily;
        CaptionPreviousText.Foreground = captionPreviousColor;
        CaptionPreviousText.MaxLines = 0;
        CaptionLatestText.FontFamily = fontFamily;
        CaptionLatestText.Foreground = captionColor;
        CaptionLatestText.MaxLines = 0;
        TranslationPreviousText.FontFamily = fontFamily;
        TranslationPreviousText.Foreground = translationPreviousColor;
        TranslationPreviousText.MaxLines = 0;
        TranslationLatestText.FontFamily = fontFamily;
        TranslationLatestText.Foreground = translationColor;
        RebuildHistoryText();
        CaptionBackground.Background = new SolidColorBrush(ParseColor(_settings.Capture.CaptionBackgroundColor, Color.FromArgb(160, 0, 0, 0)));
    }

    private void OnCaptionAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCaptionFontSize();
    }

    private void UpdateCaptionFontSize()
    {
        var fontSize = Math.Clamp(_settings.Capture.CaptionFontSize, 16, 72);
        CaptionPreviousText.FontSize = fontSize;
        CaptionLatestText.FontSize = fontSize;
        TranslationPreviousText.FontSize = fontSize;
        TranslationLatestText.FontSize = fontSize;
    }

    private bool UpdateCaptionSentences(string text)
    {
        var sentences = SplitSentences(text);
        if (sentences.Count == 0) return false;

        var latest = sentences[^1];
        var previous = sentences.Count > 1
            ? sentences.Take(sentences.Count - 1).TakeLast(2).ToArray()
            : [];
        var latestChanged = !string.Equals(latest, _latestCaptionText, StringComparison.Ordinal);
        var previousChanged = !_captionPreviousItems.SequenceEqual(previous, StringComparer.Ordinal);
        if (!latestChanged && !previousChanged) return false;

        // A new sentence starts only after the previous latest sentence has
        // ended. Partial ASR updates therefore replace the latest text instead
        // of creating extra history entries.
        var startsNewSentence = latestChanged && !string.IsNullOrWhiteSpace(_latestCaptionText) &&
            IsSentenceTerminator(_latestCaptionText[^1]);
        if (startsNewSentence)
        {
            _captionSentenceId++;
            MoveLatestTranslationToPrevious();
        }

        _captionPreviousItems.Clear();
        _captionPreviousItems.AddRange(previous);
        _latestCaptionText = latest;
        RebuildHistoryText();
        return true;
    }

    private static IReadOnlyList<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var builder = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            builder.Append(character);
            if (!IsSentenceTerminator(character)) continue;
            var sentence = builder.ToString().Trim();
            if (sentence.Length > 0) sentences.Add(sentence);
            builder.Clear();
        }

        var remainder = builder.ToString().Trim();
        if (remainder.Length > 0) sentences.Add(remainder);
        return sentences;
    }

    private static bool IsSentenceTerminator(char character) => ".!?。！？…".Contains(character);

    private void SetLatestTranslation(string translated)
    {
        var current = NormalizeHistoryText(translated);
        if (string.IsNullOrWhiteSpace(current)) return;

        if (_latestTranslationSentenceId != _captionSentenceId &&
            !string.IsNullOrWhiteSpace(_latestTranslationText))
        {
            AddTranslationToPrevious(_latestTranslationText);
        }

        _latestTranslationSentenceId = _captionSentenceId;
        _latestTranslationText = current;
        RebuildHistoryText();
    }

    private void MoveLatestTranslationToPrevious()
    {
        if (string.IsNullOrWhiteSpace(_latestTranslationText)) return;
        AddTranslationToPrevious(_latestTranslationText);
        _latestTranslationText = string.Empty;
        _latestTranslationSentenceId = -1;
    }

    private void AddTranslationToPrevious(string text)
    {
        var current = NormalizeHistoryText(text);
        if (string.IsNullOrWhiteSpace(current) ||
            _translationPreviousItems.Contains(current, StringComparer.OrdinalIgnoreCase)) return;
        _translationPreviousItems.Add(current);
        while (_translationPreviousItems.Count > MaximumDisplayedTranslationHistoryItems)
            _translationPreviousItems.RemoveAt(0);
    }

    private void RebuildHistoryText()
    {
        var visiblePreviousCaptions = _showPrevious ? _captionPreviousItems.TakeLast(_previousSentenceCount) : [];
        var visiblePreviousTranslations = _showPrevious ? _translationPreviousItems.TakeLast(_previousSentenceCount) : [];
        var hideOriginal = _translating && _translationOnly;
        CaptionPreviousText.Text = string.Join(" ", visiblePreviousCaptions);
        CaptionLatestText.Text = _latestCaptionText;
        CaptionPreviousText.Visibility = hideOriginal || string.IsNullOrWhiteSpace(CaptionPreviousText.Text) ? Visibility.Collapsed : Visibility.Visible;
        CaptionLatestText.Visibility = hideOriginal || string.IsNullOrWhiteSpace(CaptionLatestText.Text) ? Visibility.Collapsed : Visibility.Visible;

        TranslationPreviousText.Text = string.Join(" ", visiblePreviousTranslations);
        TranslationLatestText.Text = _latestTranslationText;
        TranslationPreviousText.Visibility = _translating && !string.IsNullOrWhiteSpace(TranslationPreviousText.Text) ? Visibility.Visible : Visibility.Collapsed;
        TranslationLatestText.Visibility = _translating && !string.IsNullOrWhiteSpace(TranslationLatestText.Text)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string NormalizeHistoryText(string text) =>
        string.Join(' ', text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));


    private void ClearCaptionHistory()
    {
        CaptionPreviousText.Text = string.Empty;
        CaptionLatestText.Text = string.Empty;
        _captionPreviousItems.Clear();
        _latestCaptionText = string.Empty;
        _lastCaptionForTranslation = string.Empty;
        _captionStabilizer.Reset();
        _captionSentenceId++;
        ClearTranslationHistory();
        TranslationPreviousText.Visibility = Visibility.Collapsed;
        TranslationLatestText.Visibility = Visibility.Collapsed;
    }

    private void ClearTranslationHistory()
    {
        TranslationPreviousText.Text = string.Empty;
        TranslationLatestText.Text = string.Empty;
        _translationPreviousItems.Clear();
        _latestTranslationText = string.Empty;
        _latestTranslationSentenceId = -1;
        TranslationPreviousText.Visibility = Visibility.Collapsed;
        TranslationLatestText.Visibility = Visibility.Collapsed;
    }

    private async void OnCaptionFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializingControls || double.IsNaN(args.NewValue)) return;
        _settings.Capture.CaptionFontSize = Math.Clamp(args.NewValue, 16, 72);
        UpdateCaptionFontSize();
        await SaveCaptionPreferencesAsync();
    }

    private async void OnTranslationOnlyClick(object sender, RoutedEventArgs e)
    {
        _translationOnly = TranslationOnlyButton.IsChecked == true;
        _settings.Capture.WindowsCaptionTranslationOnly = _translationOnly;
        RebuildHistoryText();
        await SaveCaptionPreferencesAsync();
    }

    private async void OnShowPreviousClick(object sender, RoutedEventArgs e)
    {
        _showPrevious = ShowPreviousButton.IsChecked == true;
        _settings.Capture.WindowsCaptionShowPrevious = _showPrevious;
        PreviousSentenceCountCombo.IsEnabled = _showPrevious;
        RebuildHistoryText();
        await SaveCaptionPreferencesAsync();
    }

    private async void OnPreviousSentenceCountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingControls || PreviousSentenceCountCombo.SelectedItem is not int count) return;
        _previousSentenceCount = Math.Clamp(count, 1, 2);
        _settings.Capture.WindowsCaptionPreviousSentenceCount = _previousSentenceCount;
        RebuildHistoryText();
        await SaveCaptionPreferencesAsync();
    }

    private async Task SaveCaptionPreferencesAsync()
    {
        await _settingsSaveGate.WaitAsync();
        try { await SettingsService.CreateDefault().SaveAsync(_settings); }
        catch (Exception exception) { await AppLog.WriteAsync("warning", "captions", "CAPTION_SETTINGS_SAVE_ERROR", exception.Message, exception); }
        finally { _settingsSaveGate.Release(); }
    }

    private static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(color.A * opacity, 1, 255), color.R, color.G, color.B);

    private static Color ParseColor(string value, Color fallback)
    {
        var text = value.Trim().TrimStart('#');
        if (text.Length == 6) text = "FF" + text;
        return text.Length == 8 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var argb)
            ? Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)
            : fallback;
    }

    private void OnLiveFailed(object? sender, Exception exception) => DispatcherQueue.TryEnqueue(() => SetStatus(L("WindowsCaptionErrorTitle"), exception.Message, InfoBarSeverity.Error));

    private void SetStatus(string title, string message, InfoBarSeverity severity) { StatusBar.Title = title; StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true; }
    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) => string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseCaptionButton.IsEnabled = false;
        await CloseAsync();
    }

    public async Task CloseAsync()
    {
        _shutdownTask ??= ShutdownAsync();
        await _shutdownTask;
        if (_allowClose) return;
        _allowClose = true;
        Close();
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        await CloseAsync();
    }

    private async Task ShutdownAsync()
    {
        _translating = false;
        _translationCancellation?.Cancel();
        if (_translationTask is not null) { try { await _translationTask.ConfigureAwait(false); } catch { } }
        _translationCancellation?.Dispose(); _translationCancellation = null; _translationTask = null;
        try { await _liveAsr.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "captions", "CAPTION_ASR_SHUTDOWN_ERROR", exception.Message, exception); }
        try { await _asr.DisposeAsync(); }
        catch (Exception exception) { await AppLog.WriteAsync("error", "captions", "CAPTION_ASR_SHUTDOWN_ERROR", exception.Message, exception); }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _pendingFlushTimer.Stop();
        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
        }
        Root.ActualThemeChanged -= OnRootActualThemeChanged;
        Closed -= OnClosed;
    }
}
