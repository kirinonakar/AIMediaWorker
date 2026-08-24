using AIMediaWorker.Network;
using AIMediaWorker.Playback;
using AIMediaWorker.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Asr;
using AIMediaWorker.Llm;
using AIMediaWorker.Localization;
using AIMediaWorker.WindowsIntegration;
using System.ComponentModel;

namespace AIMediaWorker.Views;

public sealed partial class SettingsWindow : Window
{
    private const int FixedWidth = 1500;
    private const int FixedHeight = 1080;

    public IReadOnlyList<string> CaptionPositions { get; } = ["Top", "Center", "Bottom"];
    private readonly SettingsService _service = SettingsService.CreateDefault();
    private readonly WindowsCredentialService _credentials = new();
    private readonly WindowsFileAssociationService _fileAssociations = new();
    private AppSettings _settings = new();
    private AppWindow? _appWindow;
    private CancellationTokenSource? _asrInstallCancellation;
    private readonly HashSet<ListView> _loadedFontLists = [];
    private readonly HashSet<ListView> _loadingFontLists = [];
    private bool _settingFontSelection;
    public Array Languages { get; } = Enum.GetValues<AppLanguage>();
    public Array Themes { get; } = Enum.GetValues<AppTheme>();
    public Array HardwareDecoders { get; } = Enum.GetValues<HardwareDecoder>();
    public Array RtxModes { get; } = Enum.GetValues<RtxVideoSuperResolutionMode>();
    public Array AsrDevices { get; } = Enum.GetValues<AsrDevice>();
    public Array Precisions { get; } = Enum.GetValues<AsrPrecision>();
    public Array ThinkingLevels { get; } = Enum.GetValues<ThinkingLevel>();
    public IReadOnlyList<FileAssociationOption> AssociationOptions { get; } = WindowsFileAssociationService.SupportedExtensions.Select(extension => new FileAssociationOption(extension)).ToArray();
    public string[] Providers { get; } = ["Unsloth Desktop", "Google", "OllamaCloud", "OpenCodeGo", "OpenCodeZen"];
    public string GeneralHeading { get; } = L("GeneralExpander.Header");
    public string PlaybackHeading { get; } = L("PlaybackExpander.Header");
    public string PlaybackAdvancedHeading { get; } = L("PlaybackAdvancedExpander.Header");
    public string SubtitleHeading { get; } = L("SubtitleExpander.Header");
    public string SubtitleAdvancedHeading { get; } = L("SubtitleAdvancedExpander.Header");
    public string AsrHeading { get; } = L("AsrExpander.Header");
    public string AsrProcessingHeading { get; } = L("AsrProcessingExpander.Header");
    public string NetworkHeading { get; } = L("NetworkExpander.Header");
    public string CaptureHeading { get; } = L("CaptureExpander.Header");
    public string LlmHeading { get; } = L("LlmExpander.Header");
    public event EventHandler<AppSettings>? SettingsSaved;
    public event EventHandler? DllUnloadRequested;
    public event EventHandler? RestartRequested;

    public SettingsWindow(Window owner)
    {
        InitializeComponent();
        Title = L("SettingsWindow.Title");
        WindowOwner.Attach(this, owner);
        var handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        CenterOnOwnerDisplay(owner);
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        SettingsSectionList.SelectionChanged += OnSettingsSectionChanged;
        SettingsSectionList.SelectedIndex = 0;
        ThemeCombo.SelectionChanged += OnThemeComboChanged;
        Root.ActualThemeChanged += OnRootActualThemeChanged;
        Closed += (_, _) => _asrInstallCancellation?.Cancel();
    }

    private void CenterOnOwnerDisplay(Window owner)
    {
        if (_appWindow is null) return;

        var ownerHandle = WindowNative.GetWindowHandle(owner);
        var ownerWindowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(ownerHandle);
        var workArea = DisplayArea.GetFromWindowId(ownerWindowId, DisplayAreaFallback.Nearest).WorkArea;
        const int width = FixedWidth;
        const int height = FixedHeight;
        var x = workArea.X + (workArea.Width - width) / 2;
        var y = workArea.Y + (workArea.Height - height) / 2;

        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void OnSettingsSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedIndex = Math.Clamp(SettingsSectionList.SelectedIndex, 0, 7);
        FrameworkElement[] sections = [GeneralSection, AssociationsSection, PlaybackSection, SubtitleSection, AsrSection, NetworkSection, CaptureSection, LlmSection];
        for (var index = 0; index < sections.Length; index++) sections[index].Visibility = index == selectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnThemeComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is AppTheme theme) ApplyTheme(theme);
    }

    private void ApplyTheme(AppTheme theme)
    {
        Root.RequestedTheme = theme switch { AppTheme.Light => ElementTheme.Light, AppTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyTitleBarTheme(Root.ActualTheme);
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTitleBarTheme(sender.ActualTheme);
    }

    private void ApplyTitleBarTheme(ElementTheme theme)
    {
        if (_appWindow?.TitleBar is not { } titleBar) return;
        var dark = theme == ElementTheme.Dark;
        var background = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 24, 24, 24);
        var inactiveForeground = dark ? Windows.UI.Color.FromArgb(255, 160, 160, 160) : Windows.UI.Color.FromArgb(255, 110, 110, 110);
        var hover = dark ? Windows.UI.Color.FromArgb(255, 58, 58, 58) : Windows.UI.Color.FromArgb(255, 224, 224, 224);
        var pressed = dark ? Windows.UI.Color.FromArgb(255, 72, 72, 72) : Windows.UI.Color.FromArgb(255, 208, 208, 208);
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = await _service.LoadAsync();
        ApplyTheme(_settings.General.Theme);
        LanguageCombo.SelectedItem = _settings.General.Language; ThemeCombo.SelectedItem = _settings.General.Theme; UiFontButton.Content = _settings.General.UiFontFamily; RecentCountBox.Value = _settings.General.RecentMediaCount; ResumeCheck.IsChecked = _settings.General.ResumePlayback; DefaultFolderBox.Text = _settings.General.DefaultFolder ?? string.Empty;
        HardwareCombo.SelectedItem = _settings.Playback.HardwareDecoder; RtxCombo.SelectedItem = _settings.Playback.RtxVideoSuperResolution; RtxQualityBox.Value = _settings.Playback.RtxQuality ?? 0; VolumeBox.Value = _settings.Playback.DefaultVolume; SeekBox.Value = _settings.Playback.SeekIntervalSeconds;
        RendererBox.Text = _settings.Playback.Renderer; AudioLanguageBox.Text = _settings.Playback.DefaultAudioLanguage ?? string.Empty; SubtitleLanguageBox.Text = _settings.Playback.DefaultSubtitleLanguage ?? string.Empty;
        SubtitleFontButton.Content = _settings.Subtitle.FontFamily; SubtitleSizeBox.Value = _settings.Subtitle.FontSize; CueDurationBox.Value = _settings.Subtitle.Segmentation.MaximumCueSeconds; SubtitleColorBox.Text = _settings.Subtitle.Color; SubtitleBackgroundBox.Text = _settings.Subtitle.Background; OutlineBox.Value = _settings.Subtitle.Outline; BottomMarginBox.Value = _settings.Subtitle.BottomMargin; EncodingBox.Text = _settings.Subtitle.Encoding; MinCueDurationBox.Value = _settings.Subtitle.Segmentation.MinimumCueSeconds; MaxLinesBox.Value = _settings.Subtitle.Segmentation.MaximumLines; TargetCharsBox.Value = _settings.Subtitle.Segmentation.TargetCharactersPerLine; SilenceSplitBox.Value = _settings.Subtitle.Segmentation.SilenceSplitSeconds; MaximumCpsBox.Value = _settings.Subtitle.Segmentation.MaximumCharactersPerSecond;
        AsrModelBox.Text = AsrSettings.DefaultModelId; AlignerBox.Text = AsrSettings.DefaultAlignerId; PythonBox.Text = AsrRuntimePaths.PythonExecutable; AsrModelFolderBox.Text = AsrRuntimePaths.ModelsDirectory; AsrDeviceCombo.SelectedItem = _settings.Asr.Device; PrecisionCombo.SelectedItem = _settings.Asr.Precision; VadCheck.IsChecked = _settings.Asr.UseVad; AsrLanguageBox.Text = _settings.Asr.Language; ChunkDurationBox.Value = _settings.Asr.ChunkDurationSeconds;
        NetworkTimeoutBox.Value = _settings.Network.TimeoutSeconds; ProxyBox.Text = _settings.Network.Proxy ?? string.Empty; CameraIdBox.Text = _settings.Capture.CameraDeviceId ?? string.Empty; MicrophoneIdBox.Text = _settings.Capture.MicrophoneDeviceId ?? string.Empty; CaptureWidthBox.Value = _settings.Capture.Width; CaptureHeightBox.Value = _settings.Capture.Height; CaptureFpsBox.Value = _settings.Capture.FrameRate; CaptionSizeBox.Value = _settings.Capture.CaptionFontSize; CaptionTextColorBox.Text = _settings.Capture.CaptionTextColor; CaptionBackgroundBox.Text = _settings.Capture.CaptionBackgroundColor; CaptionPositionCombo.SelectedItem = _settings.Capture.CaptionPosition; CaptionLinesBox.Value = _settings.Capture.CaptionMaximumLines;
        ProviderCombo.SelectedItem = _settings.Llm.Provider.Equals("Unsloth", StringComparison.OrdinalIgnoreCase) ? "Unsloth Desktop" : _settings.Llm.Provider; ModelBox.Text = _settings.Llm.Model ?? string.Empty; ThinkingCombo.SelectedItem = _settings.Llm.ThinkingLevel; TranslationLanguageBox.Text = _settings.Llm.TranslationLanguage;
        var rtx = new GraphicsCapabilityService().DetectRtxVideoSuperResolution();
        RtxStatusText.Text = rtx.Status;
        if (!rtx.IsSupported) { RtxCombo.SelectedItem = RtxVideoSuperResolutionMode.Off; RtxCombo.IsEnabled = false; RtxQualityBox.IsEnabled = false; }
        var isPackaged = _fileAssociations.IsPackaged;
        var registeredExtensions = isPackaged
            ? AssociationOptions.Select(option => option.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : AssociationOptions.Where(option => _fileAssociations.IsRegistered(option.Extension)).Select(option => option.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!isPackaged && registeredExtensions.Count > 0)
            foreach (var option in AssociationOptions) option.IsSelected = registeredExtensions.Contains(option.Extension);
        RegisterAssociationsButton.IsEnabled = !isPackaged;
        UnregisterAndUnloadButton.IsEnabled = !isPackaged;
        AssociationOptionsList.IsEnabled = !isPackaged;
        AssociationStateText.Text = isPackaged
            ? L("FileAssociationsPackagedStatus")
            : registeredExtensions.Count > 0
                ? L("FileAssociationsRegisteredStatus")
                : L("FileAssociationsNotRegisteredStatus");
    }

    private async void OnFontFlyoutOpening(object sender, object e)
    {
        var isUiFont = ReferenceEquals(sender, UiFontFlyout);
        var list = isUiFont ? UiFontList : SubtitleFontList;
        var progress = isUiFont ? UiFontProgress : SubtitleFontProgress;
        var selectedFont = (isUiFont ? UiFontButton.Content : SubtitleFontButton.Content)?.ToString()
            ?? (isUiFont ? GeneralSettings.DefaultUiFontFamily : SubtitleSettings.DefaultFontFamily);
        if (_loadedFontLists.Contains(list) || !_loadingFontLists.Add(list)) return;

        progress.IsActive = true;
        progress.Visibility = Visibility.Visible;
        list.Visibility = Visibility.Collapsed;
        try
        {
            var fonts = await InstalledFontCatalog.GetAsync();
            list.ItemsSource = fonts;
            _settingFontSelection = true;
            list.SelectedItem = fonts.FirstOrDefault(font => string.Equals(font, selectedFont, StringComparison.OrdinalIgnoreCase));
            _settingFontSelection = false;
            list.Visibility = Visibility.Visible;
            progress.IsActive = false;
            progress.Visibility = Visibility.Collapsed;
            _loadedFontLists.Add(list);
            if (list.SelectedItem is not null)
            {
                list.UpdateLayout();
                list.ScrollIntoView(list.SelectedItem);
            }
        }
        catch (Exception exception)
        {
            progress.IsActive = false;
            StatusBar.Title = L("SettingsErrorTitle");
            StatusBar.Message = exception.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
        finally
        {
            _settingFontSelection = false;
            _loadingFontLists.Remove(list);
        }
    }

    private void OnFontListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingFontSelection || sender is not ListView { SelectedItem: string selectedFont } list) return;
        if (ReferenceEquals(list, UiFontList))
        {
            UiFontButton.Content = selectedFont;
            UiFontFlyout.Hide();
        }
        else
        {
            SubtitleFontButton.Content = selectedFont;
            SubtitleFontFlyout.Hide();
        }
    }

    private void OnRegisterAssociationsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _fileAssociations.Register(GetSelectedAssociationExtensions());
            AssociationStateText.Text = L("FileAssociationsRegisteredStatus");
            StatusBar.Title = L("FileAssociationsRegisteredTitle");
            StatusBar.Message = L("FileAssociationsRegisteredMessage");
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            StatusBar.Title = L("FileAssociationsErrorTitle");
            StatusBar.Message = exception.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
    }

    private async void OnUnregisterAndUnloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var confirmation = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.ActualTheme,
                Title = L("FileAssociationsUnregisterTitle"),
                Content = L("FileAssociationsUnregisterConfirmation"),
                PrimaryButtonText = L("UnregisterAndExitButton"),
                CloseButtonText = L("CancelButtonText"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

            _fileAssociations.Unregister();
            foreach (var option in AssociationOptions) option.IsSelected = false;
            AssociationStateText.Text = L("FileAssociationsNotRegisteredStatus");
            StatusBar.Title = L("FileAssociationsUnregisteredTitle");
            StatusBar.Message = L("FileAssociationsUnregisteredMessage");
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.IsOpen = true;

            DllUnloadRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            StatusBar.Title = L("FileAssociationsErrorTitle");
            StatusBar.Message = exception.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
    }

    private async void OnOpenDefaultAppsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_fileAssociations.IsPackaged) _fileAssociations.Register(GetSelectedAssociationExtensions());
            if (!await Windows.System.Launcher.LaunchUriAsync(_fileAssociations.GetDefaultAppsSettingsUri()))
                throw new InvalidOperationException(L("DefaultAppsOpenFailedMessage"));
        }
        catch (Exception exception)
        {
            StatusBar.Title = L("FileAssociationsErrorTitle");
            StatusBar.Message = exception.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
    }

    private string[] GetSelectedAssociationExtensions()
    {
        var selected = AssociationOptions.Where(option => option.IsSelected).Select(option => option.Extension).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException(L("FileAssociationsSelectionRequiredMessage"));
        return selected;
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        var provider = ProviderCombo.SelectedItem?.ToString();
        ApiKeyBox.Password = provider is null ? string.Empty : _credentials.Read(CredentialIdentifier.ForLlm(provider))?.Secret
            ?? (provider == "Unsloth Desktop" ? _credentials.Read(CredentialIdentifier.ForLlm("Unsloth"))?.Secret : null)
            ?? string.Empty;
        ThinkingCombo.IsEnabled = provider is "Unsloth Desktop" or "Google" or "OllamaCloud" or "OpenCodeGo" or "OpenCodeZen";
    }

    private async void OnInstallAsrClick(object sender, RoutedEventArgs e)
    {
        if (_asrInstallCancellation is not null) return;
        _asrInstallCancellation = new CancellationTokenSource();
        InstallAsrButton.IsEnabled = false;
        AsrInstallProgressBar.Visibility = Visibility.Visible;
        AsrInstallProgressBar.IsIndeterminate = true;
        AsrInstallStatusText.Text = L("AsrInstallStartingMessage");
        try
        {
            var progress = new Progress<AsrInstallationProgress>(UpdateAsrInstallationProgress);
            await new AsrInstallationService().InstallAsync(progress, _asrInstallCancellation.Token);
            PythonBox.Text = AsrRuntimePaths.PythonExecutable;
            AsrModelFolderBox.Text = AsrRuntimePaths.ModelsDirectory;
            _settings.Asr.PythonExecutable = AsrRuntimePaths.PythonExecutable;
            _settings.Asr.ModelPath = AsrSettings.DefaultModelId;
            _settings.Asr.AlignerPath = AsrSettings.DefaultAlignerId;
            await _service.SaveAsync(_settings);
            AsrInstallProgressBar.IsIndeterminate = false;
            AsrInstallProgressBar.Value = 100;
            AsrInstallStatusText.Text = L("AsrInstallCompleteMessage");
            StatusBar.Title = L("AsrInstallCompleteTitle");
            StatusBar.Message = F("AsrInstallCompleteDetail", AsrRuntimePaths.WorkerDirectory);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            AsrInstallStatusText.Text = L("AsrInstallCancelledMessage");
        }
        catch (Exception exception)
        {
            AsrInstallProgressBar.IsIndeterminate = false;
            AsrInstallStatusText.Text = L("AsrInstallFailedMessage");
            StatusBar.Title = L("AsrInstallFailedTitle");
            StatusBar.Message = exception.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
        finally
        {
            _asrInstallCancellation.Dispose();
            _asrInstallCancellation = null;
            InstallAsrButton.IsEnabled = true;
        }
    }

    private void UpdateAsrInstallationProgress(AsrInstallationProgress update)
    {
        if (update.Progress is { } value)
        {
            AsrInstallProgressBar.IsIndeterminate = false;
            AsrInstallProgressBar.Value = value * 100;
        }
        else
        {
            AsrInstallProgressBar.IsIndeterminate = true;
        }

        var detail = update.TotalBytes > 0
            ? $"{FormatBytes(update.DownloadedBytes)} / {FormatBytes(update.TotalBytes)}"
            : update.Message;
        AsrInstallStatusText.Text = update.Stage switch
        {
            "environment" => F("AsrInstallEnvironmentMessage", detail),
            "environment-skipped" => L("AsrInstallEnvironmentSkippedMessage"),
            "environment-complete" => L("AsrInstallEnvironmentCompleteMessage"),
            "requirements" => F("AsrInstallRequirementsMessage", detail),
            "requirements-skipped" => L("AsrInstallRequirementsSkippedMessage"),
            "requirements-complete" => L("AsrInstallRequirementsCompleteMessage"),
            "asr" => F("AsrInstallModelMessage", update.Message, detail),
            "aligner" => F("AsrInstallModelMessage", update.Message, detail),
            "complete" => L("AsrInstallCompleteMessage"),
            _ => detail
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private async void OnSyncModelsClick(object sender, RoutedEventArgs e)
    {
        var providerId = ProviderCombo.SelectedItem?.ToString() ?? "Unsloth Desktop";
        try
        {
            var provider = new LlmProviderFactory(_credentials).Create(providerId, string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? null : ApiKeyBox.Password);
            using var disposable = provider as IDisposable;
            var models = await provider.GetModelsAsync();
            if (models.Count == 0) throw new InvalidOperationException(L("ProviderNoModelsMessage"));
            _settings.Llm.CachedModels[providerId] = models.Select(model => model.Id).ToList();
            var list = new ListView { ItemsSource = models, DisplayMemberPath = "Id", SelectionMode = ListViewSelectionMode.Single, MinWidth = 420, MinHeight = 320 };
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = F("ProviderModelsTitle", provider.DisplayName), Content = list, PrimaryButtonText = L("UseSelectedButton"), CloseButtonText = L("CloseButton") };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is LlmModel selected) ModelBox.Text = selected.Id;
            await _service.SaveAsync(_settings);
            LocalizationService.Apply(_settings.General.Language);
        }
        catch (Exception exception)
        {
            if (_settings.Llm.CachedModels.TryGetValue(providerId, out var cached) && cached.Count > 0)
            {
                var list = new ListView { ItemsSource = cached, SelectionMode = ListViewSelectionMode.Single, MinWidth = 420, MinHeight = 280 };
                var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = L("CachedModelsTitle"), Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = F("ModelSyncCachedMessage", exception.Message), TextWrapping = TextWrapping.Wrap }, list } }, PrimaryButtonText = L("UseSelectedButton"), CloseButtonText = L("CloseButton") };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is string selected) ModelBox.Text = selected;
            }
            else { StatusBar.Title = L("ModelSyncFailedTitle"); StatusBar.Message = F("ModelSyncManualMessage", exception.Message); StatusBar.Severity = InfoBarSeverity.Warning; StatusBar.IsOpen = true; }
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MinCueDurationBox.Value > CueDurationBox.Value) throw new InvalidOperationException(L("CueDurationValidationMessage"));
            if (string.IsNullOrWhiteSpace(PythonBox.Text)) throw new InvalidOperationException(L("PythonRequiredMessage"));
            _ = EncodingBox.Text.Trim().Equals("utf-8", StringComparison.OrdinalIgnoreCase) ? new System.Text.UTF8Encoding(false, true) : System.Text.Encoding.GetEncoding(EncodingBox.Text.Trim());
            var previousLanguage = _settings.General.Language;
            _settings.General.Language = (AppLanguage)(LanguageCombo.SelectedItem ?? AppLanguage.Default); _settings.General.Theme = (AppTheme)(ThemeCombo.SelectedItem ?? AppTheme.System); _settings.General.UiFontFamily = UiFontButton.Content?.ToString() ?? GeneralSettings.DefaultUiFontFamily; _settings.General.RecentMediaCount = (int)RecentCountBox.Value; _settings.General.ResumePlayback = ResumeCheck.IsChecked == true; _settings.General.DefaultFolder = EmptyToNull(DefaultFolderBox.Text);
            _settings.Playback.HardwareDecoder = (HardwareDecoder)(HardwareCombo.SelectedItem ?? HardwareDecoder.Auto); _settings.Playback.RtxVideoSuperResolution = (RtxVideoSuperResolutionMode)(RtxCombo.SelectedItem ?? RtxVideoSuperResolutionMode.Auto); _settings.Playback.RtxQuality = RtxQualityBox.Value <= 0 ? null : (int)RtxQualityBox.Value; _settings.Playback.DefaultVolume = VolumeBox.Value; _settings.Playback.SeekIntervalSeconds = SeekBox.Value; _settings.Playback.Renderer = RendererBox.Text.Trim(); _settings.Playback.DefaultAudioLanguage = EmptyToNull(AudioLanguageBox.Text); _settings.Playback.DefaultSubtitleLanguage = EmptyToNull(SubtitleLanguageBox.Text);
            _settings.Subtitle.FontFamily = SubtitleFontButton.Content?.ToString() ?? SubtitleSettings.DefaultFontFamily; _settings.Subtitle.FontSize = SubtitleSizeBox.Value; _settings.Subtitle.Segmentation.MaximumCueSeconds = CueDurationBox.Value; _settings.Subtitle.Color = SubtitleColorBox.Text; _settings.Subtitle.Background = SubtitleBackgroundBox.Text; _settings.Subtitle.Outline = OutlineBox.Value; _settings.Subtitle.BottomMargin = (int)BottomMarginBox.Value; _settings.Subtitle.Encoding = EncodingBox.Text; _settings.Subtitle.Segmentation.MinimumCueSeconds = MinCueDurationBox.Value; _settings.Subtitle.Segmentation.MaximumLines = (int)MaxLinesBox.Value; _settings.Subtitle.Segmentation.TargetCharactersPerLine = (int)TargetCharsBox.Value; _settings.Subtitle.Segmentation.SilenceSplitSeconds = SilenceSplitBox.Value; _settings.Subtitle.Segmentation.MaximumCharactersPerSecond = MaximumCpsBox.Value;
            _settings.Asr.ModelPath = AsrSettings.DefaultModelId; _settings.Asr.AlignerPath = AsrSettings.DefaultAlignerId; _settings.Asr.PythonExecutable = AsrRuntimePaths.PythonExecutable; _settings.Asr.Device = (AsrDevice)(AsrDeviceCombo.SelectedItem ?? AsrDevice.Auto); _settings.Asr.Precision = (AsrPrecision)(PrecisionCombo.SelectedItem ?? AsrPrecision.Auto); _settings.Asr.UseVad = VadCheck.IsChecked == true; _settings.Asr.Language = AsrLanguageBox.Text.Trim(); _settings.Asr.ChunkDurationSeconds = ChunkDurationBox.Value;
            _settings.Network.TimeoutSeconds = (int)NetworkTimeoutBox.Value; _settings.Network.Proxy = EmptyToNull(ProxyBox.Text); _settings.Capture.CameraDeviceId = EmptyToNull(CameraIdBox.Text); _settings.Capture.MicrophoneDeviceId = EmptyToNull(MicrophoneIdBox.Text); _settings.Capture.Width = (int)CaptureWidthBox.Value; _settings.Capture.Height = (int)CaptureHeightBox.Value; _settings.Capture.FrameRate = (int)CaptureFpsBox.Value; _settings.Capture.CaptionFontSize = CaptionSizeBox.Value; _settings.Capture.CaptionTextColor = CaptionTextColorBox.Text.Trim(); _settings.Capture.CaptionBackgroundColor = CaptionBackgroundBox.Text.Trim(); _settings.Capture.CaptionPosition = CaptionPositionCombo.SelectedItem?.ToString() ?? "Bottom"; _settings.Capture.CaptionMaximumLines = (int)CaptionLinesBox.Value;
            _settings.Llm.Provider = ProviderCombo.SelectedItem?.ToString() ?? "Unsloth Desktop"; _settings.Llm.Model = EmptyToNull(ModelBox.Text); _settings.Llm.ThinkingLevel = (ThinkingLevel)(ThinkingCombo.SelectedItem ?? ThinkingLevel.Default); _settings.Llm.TranslationLanguage = TranslationLanguageBox.Text.Trim();
            if (!string.IsNullOrEmpty(ApiKeyBox.Password)) _credentials.Save(CredentialIdentifier.ForLlm(_settings.Llm.Provider), _settings.Llm.Provider, ApiKeyBox.Password);
            await _service.SaveAsync(_settings);
            SettingsSaved?.Invoke(this, _settings);
            if (_settings.General.Language != previousLanguage)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    RequestedTheme = Root.ActualTheme,
                    Title = L("LanguageRestartTitle"),
                    Content = L("LanguageRestartMessage"),
                    PrimaryButtonText = L("RestartNowButton"),
                    CloseButtonText = L("LaterButton")
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    RestartRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
            Close();
        }
        catch (Exception exception) { StatusBar.Title = L("SettingsErrorTitle"); StatusBar.Message = exception.Message; StatusBar.Severity = InfoBarSeverity.Error; StatusBar.IsOpen = true; }
    }

    private async void OnBrowseDefaultFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) DefaultFolderBox.Text = folder.Path;
        }
        catch (Exception exception) { StatusBar.Title = L("SettingsErrorTitle"); StatusBar.Message = exception.Message; StatusBar.Severity = InfoBarSeverity.Error; StatusBar.IsOpen = true; }
    }

    private void OnClearDefaultFolderClick(object sender, RoutedEventArgs e) => DefaultFolderBox.Text = string.Empty;

    private void OnClearApiKeyClick(object sender, RoutedEventArgs e)
    {
        var provider = ProviderCombo.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(provider)) return;
        _credentials.Delete(CredentialIdentifier.ForLlm(provider));
        ApiKeyBox.Password = string.Empty;
        StatusBar.Title = L("CredentialRemovedTitle"); StatusBar.Message = F("CredentialRemovedMessage", provider); StatusBar.Severity = InfoBarSeverity.Success; StatusBar.IsOpen = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) => string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);
}

public sealed class FileAssociationOption : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public FileAssociationOption(string extension) => Extension = extension;
    public string Extension { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}