using AIMediaWorker.Network;
using AIMediaWorker.Playback;
using AIMediaWorker.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Llm;
using AIMediaWorker.Localization;

namespace AIMediaWorker.Views;

public sealed partial class SettingsWindow : Window
{
    public IReadOnlyList<string> CaptionPositions { get; } = ["Top", "Center", "Bottom"];
    private readonly SettingsService _service = SettingsService.CreateDefault();
    private readonly WindowsCredentialService _credentials = new();
    private AppSettings _settings = new();
    public Array Languages { get; } = Enum.GetValues<AppLanguage>();
    public Array Themes { get; } = Enum.GetValues<AppTheme>();
    public Array HardwareDecoders { get; } = Enum.GetValues<HardwareDecoder>();
    public Array RtxModes { get; } = Enum.GetValues<RtxVideoSuperResolutionMode>();
    public Array AsrDevices { get; } = Enum.GetValues<AsrDevice>();
    public Array Precisions { get; } = Enum.GetValues<AsrPrecision>();
    public Array ThinkingLevels { get; } = Enum.GetValues<ThinkingLevel>();
    public string[] Providers { get; } = ["Unsloth", "Google", "OllamaCloud", "OpenCodeGo", "OpenCodeZen"];
    public event EventHandler<AppSettings>? SettingsSaved;

    public SettingsWindow()
    {
        InitializeComponent();
        var handle = WindowNative.GetWindowHandle(this);
        AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle))?.Resize(new SizeInt32(760, 820));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = await _service.LoadAsync();
        LanguageCombo.SelectedItem = _settings.General.Language; ThemeCombo.SelectedItem = _settings.General.Theme; RecentCountBox.Value = _settings.General.RecentMediaCount; ResumeCheck.IsChecked = _settings.General.ResumePlayback;
        HardwareCombo.SelectedItem = _settings.Playback.HardwareDecoder; RtxCombo.SelectedItem = _settings.Playback.RtxVideoSuperResolution; RtxQualityBox.Value = _settings.Playback.RtxQuality ?? 0; VolumeBox.Value = _settings.Playback.DefaultVolume; SeekBox.Value = _settings.Playback.SeekIntervalSeconds;
        RendererBox.Text = _settings.Playback.Renderer; AudioLanguageBox.Text = _settings.Playback.DefaultAudioLanguage ?? string.Empty; SubtitleLanguageBox.Text = _settings.Playback.DefaultSubtitleLanguage ?? string.Empty;
        SubtitleFontBox.Text = _settings.Subtitle.FontFamily; SubtitleSizeBox.Value = _settings.Subtitle.FontSize; CueDurationBox.Value = _settings.Subtitle.Segmentation.MaximumCueSeconds; SubtitleColorBox.Text = _settings.Subtitle.Color; SubtitleBackgroundBox.Text = _settings.Subtitle.Background; OutlineBox.Value = _settings.Subtitle.Outline; BottomMarginBox.Value = _settings.Subtitle.BottomMargin; EncodingBox.Text = _settings.Subtitle.Encoding; MinCueDurationBox.Value = _settings.Subtitle.Segmentation.MinimumCueSeconds; MaxLinesBox.Value = _settings.Subtitle.Segmentation.MaximumLines; TargetCharsBox.Value = _settings.Subtitle.Segmentation.TargetCharactersPerLine; SilenceSplitBox.Value = _settings.Subtitle.Segmentation.SilenceSplitSeconds; MaximumCpsBox.Value = _settings.Subtitle.Segmentation.MaximumCharactersPerSecond;
        AsrModelBox.Text = _settings.Asr.ModelPath ?? string.Empty; AlignerBox.Text = _settings.Asr.AlignerPath ?? string.Empty; PythonBox.Text = _settings.Asr.PythonExecutable; AsrDeviceCombo.SelectedItem = _settings.Asr.Device; PrecisionCombo.SelectedItem = _settings.Asr.Precision; VadCheck.IsChecked = _settings.Asr.UseVad; AsrLanguageBox.Text = _settings.Asr.Language; ChunkDurationBox.Value = _settings.Asr.ChunkDurationSeconds;
        NetworkTimeoutBox.Value = _settings.Network.TimeoutSeconds; ProxyBox.Text = _settings.Network.Proxy ?? string.Empty; CameraIdBox.Text = _settings.Capture.CameraDeviceId ?? string.Empty; MicrophoneIdBox.Text = _settings.Capture.MicrophoneDeviceId ?? string.Empty; CaptureWidthBox.Value = _settings.Capture.Width; CaptureHeightBox.Value = _settings.Capture.Height; CaptureFpsBox.Value = _settings.Capture.FrameRate; CaptionFontBox.Text = _settings.Capture.CaptionFontFamily; CaptionSizeBox.Value = _settings.Capture.CaptionFontSize; CaptionTextColorBox.Text = _settings.Capture.CaptionTextColor; CaptionBackgroundBox.Text = _settings.Capture.CaptionBackgroundColor; CaptionPositionCombo.SelectedItem = _settings.Capture.CaptionPosition; CaptionLinesBox.Value = _settings.Capture.CaptionMaximumLines;
        ProviderCombo.SelectedItem = _settings.Llm.Provider; ModelBox.Text = _settings.Llm.Model ?? string.Empty; ThinkingCombo.SelectedItem = _settings.Llm.ThinkingLevel; TranslationLanguageBox.Text = _settings.Llm.TranslationLanguage;
        var rtx = new GraphicsCapabilityService().DetectRtxVideoSuperResolution();
        RtxStatusText.Text = rtx.Status;
        if (!rtx.IsSupported) { RtxCombo.SelectedItem = RtxVideoSuperResolutionMode.Off; RtxCombo.IsEnabled = false; RtxQualityBox.IsEnabled = false; }
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        var provider = ProviderCombo.SelectedItem?.ToString();
        ApiKeyBox.Password = provider is null ? string.Empty : _credentials.Read(CredentialIdentifier.ForLlm(provider))?.Secret ?? string.Empty;
        ThinkingCombo.IsEnabled = provider is "Google" or "OllamaCloud" or "OpenCodeGo" or "OpenCodeZen";
    }

    private async void OnSyncModelsClick(object sender, RoutedEventArgs e)
    {
        var providerId = ProviderCombo.SelectedItem?.ToString() ?? "Unsloth";
        try
        {
            var provider = new LlmProviderFactory(_credentials).Create(providerId, string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? null : ApiKeyBox.Password);
            using var disposable = provider as IDisposable;
            var models = await provider.GetModelsAsync();
            if (models.Count == 0) throw new InvalidOperationException(L("ProviderNoModelsMessage"));
            _settings.Llm.CachedModels[providerId] = models.Select(model => model.Id).ToList();
            var list = new ListView { ItemsSource = models, DisplayMemberPath = "Id", SelectionMode = ListViewSelectionMode.Single, MinWidth = 420, MinHeight = 320 };
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = F("ProviderModelsTitle", provider.DisplayName), Content = list, PrimaryButtonText = L("UseSelectedButton"), CloseButtonText = L("CloseButton") };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is LlmModel selected) ModelBox.Text = selected.Id;
            await _service.SaveAsync(_settings);
            LocalizationService.Apply(_settings.General.Language);
        }
        catch (Exception exception)
        {
            if (_settings.Llm.CachedModels.TryGetValue(providerId, out var cached) && cached.Count > 0)
            {
                var list = new ListView { ItemsSource = cached, SelectionMode = ListViewSelectionMode.Single, MinWidth = 420, MinHeight = 280 };
                var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L("CachedModelsTitle"), Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = F("ModelSyncCachedMessage", exception.Message), TextWrapping = TextWrapping.Wrap }, list } }, PrimaryButtonText = L("UseSelectedButton"), CloseButtonText = L("CloseButton") };
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
            _settings.General.Language = (AppLanguage)(LanguageCombo.SelectedItem ?? AppLanguage.English); _settings.General.Theme = (AppTheme)(ThemeCombo.SelectedItem ?? AppTheme.System); _settings.General.RecentMediaCount = (int)RecentCountBox.Value; _settings.General.ResumePlayback = ResumeCheck.IsChecked == true;
            _settings.Playback.HardwareDecoder = (HardwareDecoder)(HardwareCombo.SelectedItem ?? HardwareDecoder.Auto); _settings.Playback.RtxVideoSuperResolution = (RtxVideoSuperResolutionMode)(RtxCombo.SelectedItem ?? RtxVideoSuperResolutionMode.Auto); _settings.Playback.RtxQuality = RtxQualityBox.Value <= 0 ? null : (int)RtxQualityBox.Value; _settings.Playback.DefaultVolume = VolumeBox.Value; _settings.Playback.SeekIntervalSeconds = SeekBox.Value; _settings.Playback.Renderer = RendererBox.Text.Trim(); _settings.Playback.DefaultAudioLanguage = EmptyToNull(AudioLanguageBox.Text); _settings.Playback.DefaultSubtitleLanguage = EmptyToNull(SubtitleLanguageBox.Text);
            _settings.Subtitle.FontFamily = SubtitleFontBox.Text; _settings.Subtitle.FontSize = SubtitleSizeBox.Value; _settings.Subtitle.Segmentation.MaximumCueSeconds = CueDurationBox.Value; _settings.Subtitle.Color = SubtitleColorBox.Text; _settings.Subtitle.Background = SubtitleBackgroundBox.Text; _settings.Subtitle.Outline = OutlineBox.Value; _settings.Subtitle.BottomMargin = (int)BottomMarginBox.Value; _settings.Subtitle.Encoding = EncodingBox.Text; _settings.Subtitle.Segmentation.MinimumCueSeconds = MinCueDurationBox.Value; _settings.Subtitle.Segmentation.MaximumLines = (int)MaxLinesBox.Value; _settings.Subtitle.Segmentation.TargetCharactersPerLine = (int)TargetCharsBox.Value; _settings.Subtitle.Segmentation.SilenceSplitSeconds = SilenceSplitBox.Value; _settings.Subtitle.Segmentation.MaximumCharactersPerSecond = MaximumCpsBox.Value;
            _settings.Asr.ModelPath = EmptyToNull(AsrModelBox.Text); _settings.Asr.AlignerPath = EmptyToNull(AlignerBox.Text); _settings.Asr.PythonExecutable = PythonBox.Text.Trim(); _settings.Asr.Device = (AsrDevice)(AsrDeviceCombo.SelectedItem ?? AsrDevice.Auto); _settings.Asr.Precision = (AsrPrecision)(PrecisionCombo.SelectedItem ?? AsrPrecision.Auto); _settings.Asr.UseVad = VadCheck.IsChecked == true; _settings.Asr.Language = AsrLanguageBox.Text.Trim(); _settings.Asr.ChunkDurationSeconds = ChunkDurationBox.Value;
            _settings.Network.TimeoutSeconds = (int)NetworkTimeoutBox.Value; _settings.Network.Proxy = EmptyToNull(ProxyBox.Text); _settings.Capture.CameraDeviceId = EmptyToNull(CameraIdBox.Text); _settings.Capture.MicrophoneDeviceId = EmptyToNull(MicrophoneIdBox.Text); _settings.Capture.Width = (int)CaptureWidthBox.Value; _settings.Capture.Height = (int)CaptureHeightBox.Value; _settings.Capture.FrameRate = (int)CaptureFpsBox.Value; _settings.Capture.CaptionFontFamily = CaptionFontBox.Text.Trim(); _settings.Capture.CaptionFontSize = CaptionSizeBox.Value; _settings.Capture.CaptionTextColor = CaptionTextColorBox.Text.Trim(); _settings.Capture.CaptionBackgroundColor = CaptionBackgroundBox.Text.Trim(); _settings.Capture.CaptionPosition = CaptionPositionCombo.SelectedItem?.ToString() ?? "Bottom"; _settings.Capture.CaptionMaximumLines = (int)CaptionLinesBox.Value;
            _settings.Llm.Provider = ProviderCombo.SelectedItem?.ToString() ?? "Unsloth"; _settings.Llm.Model = EmptyToNull(ModelBox.Text); _settings.Llm.ThinkingLevel = (ThinkingLevel)(ThinkingCombo.SelectedItem ?? ThinkingLevel.Default); _settings.Llm.TranslationLanguage = TranslationLanguageBox.Text.Trim();
            if (!string.IsNullOrEmpty(ApiKeyBox.Password)) _credentials.Save(CredentialIdentifier.ForLlm(_settings.Llm.Provider), _settings.Llm.Provider, ApiKeyBox.Password);
            await _service.SaveAsync(_settings);
            SettingsSaved?.Invoke(this, _settings);
            Close();
        }
        catch (Exception exception) { StatusBar.Title = L("SettingsErrorTitle"); StatusBar.Message = exception.Message; StatusBar.Severity = InfoBarSeverity.Error; StatusBar.IsOpen = true; }
    }

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
