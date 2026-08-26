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
    private readonly Channel<string> _translationQueue = Channel.CreateUnbounded<string>();
    private readonly List<string> _pendingSegments = [];
    private AppSettings _settings = new();
    private AppWindow? _appWindow;
    private Task? _shutdownTask;
    private Task? _translationTask;
    private CancellationTokenSource? _translationCancellation;
    private LlmService? _llm;
    private string _lastCaption = string.Empty;
    private bool _translating;
    private bool _allowClose;

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
        CloseCaptionButton.Content = L("CloseButton");
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
            await _liveAsr.StartLoopbackAsync(null, _settings.Asr.Language);
            CaptionButton.Content = L("WindowsCaptionStop.Content");
            CaptionButton.IsEnabled = true;
            TranslateButton.IsEnabled = true;
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
        var text = result.Text ?? result.Segment?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;
        _lastCaption = text;
        CaptionText.Text = text;
        CaptionText.Opacity = result.Event == "partial" ? 0.72 : 1;
        if (!_translating || result.Event == "partial") return;
        _pendingSegments.Add(text);
        if (_pendingSegments.Count < 2) return;
        var combined = string.Join('\n', _pendingSegments);
        _pendingSegments.Clear();
        CaptionText.Text = combined;
        CaptionText.Opacity = 1;
        _translationQueue.Writer.TryWrite(combined);
    });

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
            TranslateButton.Content = L("WindowsCaptionTranslateOn.Content");
            TranslationText.Visibility = Visibility.Visible;
            var cancellation = _translationCancellation ??= new CancellationTokenSource();
            _translationTask ??= Task.Run(() => TranslationLoopAsync(cancellation.Token));
            if (_pendingSegments.Count > 0)
            {
                _translationQueue.Writer.TryWrite(string.Join('\n', _pendingSegments));
                _pendingSegments.Clear();
            }
            else if (!string.IsNullOrWhiteSpace(_lastCaption))
            {
                _translationQueue.Writer.TryWrite(_lastCaption);
            }
        }
        else
        {
            _translating = false;
            _pendingSegments.Clear();
            TranslateButton.Content = L("WindowsCaptionTranslate.Content");
            TranslationText.Text = string.Empty;
            TranslationText.Visibility = Visibility.Collapsed;
        }
    }

    private async Task TranslationLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _translationQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Drain to the newest caption so translation stays real-time.
                var latest = string.Empty;
                while (_translationQueue.Reader.TryRead(out var item)) latest = item;
                if (!_translating || string.IsNullOrWhiteSpace(latest)) continue;
                await TranslateAsync(latest, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { await AppLog.WriteAsync("error", "captions", "CAPTION_TRANSLATION_ERROR", exception.Message, exception); }
    }

    private async Task TranslateAsync(string text, CancellationToken cancellationToken)
    {
        var llm = _llm;
        if (llm is null) return;
        try
        {
            var cue = new SubtitleCue { Text = text };
            var result = await llm.TranslateAsync([cue], _settings.Llm.TranslationLanguage, batchSize: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.TryGetValue(cue.Id, out var translated) && !string.IsNullOrWhiteSpace(translated))
                DispatcherQueue.TryEnqueue(() => { if (_translating) TranslationText.Text = translated; });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { await AppLog.WriteAsync("error", "captions", "CAPTION_TRANSLATION_ERROR", exception.Message, exception); }
    }

    private void ApplyCaptionAppearance()
    {
        CaptionText.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(_settings.Subtitle.FontFamily) ? SubtitleSettings.DefaultFontFamily : _settings.Subtitle.FontFamily);
        CaptionText.Foreground = new SolidColorBrush(ParseColor(_settings.Capture.CaptionTextColor, Color.FromArgb(255, 255, 255, 255)));
        CaptionText.MaxLines = Math.Clamp(_settings.Capture.CaptionMaximumLines, 1, 6);
        CaptionBackground.Background = new SolidColorBrush(ParseColor(_settings.Capture.CaptionBackgroundColor, Color.FromArgb(160, 0, 0, 0)));
    }

    // Scales the caption font with the overlay size so text always fits the window.
    private void OnCaptionAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var areaHeight = e.NewSize.Height;
        var areaWidth = e.NewSize.Width;
        if (areaHeight <= 0 || areaWidth <= 0) return;
        var baseSize = Math.Clamp(Math.Min(areaHeight * 0.22, areaWidth * 0.05), 10, 200);
        CaptionText.FontSize = baseSize;
        TranslationText.FontSize = baseSize * 0.85;
    }

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
        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
        }
        Root.ActualThemeChanged -= OnRootActualThemeChanged;
        Closed -= OnClosed;
    }
}