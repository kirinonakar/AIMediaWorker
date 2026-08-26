using AIMediaWorker.Localization;
using AIMediaWorker.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIMediaWorker.Views;

/// <summary>Serializes modal dialogs and coordinates them with the native video child window.</summary>
internal sealed class WindowDialogService
{
    private readonly FrameworkElement _root;
    private readonly Func<NativeVideoHost?> _getVideoHost;
    private readonly SemaphoreSlim _dialogLock = new(1, 1);

    public WindowDialogService(FrameworkElement root, Func<NativeVideoHost?> getVideoHost)
    {
        _root = root;
        _getVideoHost = getVideoHost;
    }

    public ContentDialog Create(string title, object content, string primaryText) => Prepare(new ContentDialog
    {
        Title = title,
        Content = content,
        PrimaryButtonText = primaryText,
        CloseButtonText = LocalizationService.Get("CancelButtonText"),
        DefaultButton = ContentDialogButton.Primary
    });

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        Prepare(dialog);
        await _dialogLock.WaitAsync();
        var videoHost = _getVideoHost();
        var restoreVideo = videoHost?.IsVisible == true;
        try
        {
            if (restoreVideo) videoHost!.SetVisible(false);
            return await dialog.ShowAsync();
        }
        finally
        {
            if (restoreVideo) _getVideoHost()?.SetVisible(true);
            _dialogLock.Release();
        }
    }

    public Task ShowMessageAsync(string title, string message) => ShowAsync(new ContentDialog
    {
        Title = title,
        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        CloseButtonText = LocalizationService.Get("OkButton")
    });

    private ContentDialog Prepare(ContentDialog dialog)
    {
        dialog.XamlRoot ??= _root.XamlRoot;
        dialog.RequestedTheme = _root.ActualTheme;
        return dialog;
    }
}
