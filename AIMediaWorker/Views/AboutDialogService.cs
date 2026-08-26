using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AIMediaWorker.Views;

internal sealed class AboutDialogService
{
    private readonly WindowDialogService _dialogs;

    public AboutDialogService(WindowDialogService dialogs) => _dialogs = dialogs;

    public async Task ShowAsync()
    {
        try
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            header.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/app.png")),
                Width = 64,
                Height = 64,
                VerticalAlignment = VerticalAlignment.Center
            });
            var nameVersion = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            nameVersion.Children.Add(new TextBlock { Text = "AIMediaWorker", FontSize = 20, FontWeight = FontWeights.SemiBold });
            nameVersion.Children.Add(new TextBlock { Text = Format("AboutVersion", GetAppVersion()), Opacity = 0.7 });
            header.Children.Add(nameVersion);

            var github = new HyperlinkButton
            {
                Content = "https://github.com/kirinonakar/AIMediaWorker",
                HorizontalAlignment = HorizontalAlignment.Left
            };
            github.Click += async (_, _) =>
            {
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/kirinonakar/AIMediaWorker")); }
                catch (Exception exception) { await AppLog.WriteAsync("error", "about", "OPEN_GITHUB_ERROR", exception.Message, exception); }
            };

            var licenses = new Expander
            {
                Header = L("AboutThirdPartyLicenses"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer
                {
                    MaxHeight = 220,
                    Content = new TextBlock
                    {
                        Text = ThirdPartyLicensesText,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        Opacity = 0.85
                    }
                }
            };

            var content = new StackPanel { Spacing = 12, Width = 440 };
            content.Children.Add(header);
            content.Children.Add(github);
            content.Children.Add(licenses);
            await _dialogs.ShowAsync(new ContentDialog
            {
                Title = L("AboutTitle"),
                Content = content,
                CloseButtonText = L("CloseButton")
            });
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "about", "ABOUT_DIALOG_ERROR", exception.Message, exception);
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
        }
    }

    private static string L(string key) => LocalizationService.Get(key);
    private static string Format(string key, params object[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);

    private const string ThirdPartyLicensesText =
        "NAudio 2.2.1 — MIT License\nhttps://github.com/naudio/NAudio\n\n" +
        "Windows App SDK 2.4.0 — MIT License\nhttps://github.com/microsoft/WindowsAppSDK\n\n" +
        "System.Security.Cryptography.ProtectedData 10.0.0 — MIT License\nhttps://www.nuget.org/packages/System.Security.Cryptography.ProtectedData\n\n" +
        "libmpv / mpv — GPLv2+ (build-dependent)\nhttps://github.com/mpv-player/mpv\n\n" +
        "FFmpeg — LGPLv2.1+ (build-dependent)\nhttps://ffmpeg.org\n\n" +
        "Silero VAD — MIT License\nhttps://github.com/snakers4/silero-vad\n\n" +
        "Qwen3-ASR — Apache License 2.0\nhttps://github.com/QwenLM/Qwen3-ASR";
}
