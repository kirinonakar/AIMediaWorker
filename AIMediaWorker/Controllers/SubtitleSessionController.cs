using AIMediaWorker.Diagnostics;
using AIMediaWorker.Localization;
using AIMediaWorker.Settings;
using AIMediaWorker.Subtitle;
using AIMediaWorker.Views;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AIMediaWorker.Controllers;

/// <summary>
/// Owns the active subtitle document and its load/save/dirty-check lifecycle.
/// Presentation is notified only after a complete document transition.
/// </summary>
internal sealed class SubtitleSessionController
{
    private readonly SubtitleFileService _files = new();
    private readonly SubtitleSessionHost _host;

    public SubtitleSessionController(SubtitleSessionHost host)
    {
        _host = host;
        Document = CreateBlankDocument();
    }

    public SubtitleDocument Document { get; private set; }

    public void Bind(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var track = document.EnsureTrack();
        if (document.FilePath is null && track.Cues.Count == 0) document.MarkSaved();
        Document = document;
        _host.DocumentChanged(document);
    }

    public void ResetForMedia() => Bind(CreateBlankDocument());

    public async Task PickAndLoadAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var extension in new[] { ".srt", ".vtt", ".ass", ".ssa", ".smi" })
                picker.FileTypeFilter.Add(extension);
            InitializeWithWindow.Initialize(picker, _host.WindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await LoadAsync(file.Path);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "file-picker", "OPEN_SUBTITLE_PICKER_ERROR", exception.Message, exception);
            await _host.Dialogs.ShowMessageAsync(L("SubtitleErrorTitle"), exception.Message);
        }
    }

    public async Task<bool> LoadAsync(string path)
    {
        if (!await _host.PrepareForLoadAsync()) return false;
        try
        {
            var document = await _files.LoadAsync(path, _host.GetSettings().Subtitle.Encoding);
            Bind(document);
            _host.DocumentLoaded(document);
            return true;
        }
        catch (Exception exception)
        {
            await _host.Dialogs.ShowMessageAsync(L("SubtitleErrorTitle"), exception.Message);
            return false;
        }
    }

    public SubtitleDocument DecodeAndBind(string path, byte[] bytes)
    {
        var document = _files.DecodeAndParse(path, bytes, _host.GetSettings().Subtitle.Encoding);
        document.MarkSaved();
        Bind(document);
        _host.DocumentLoaded(document);
        return document;
    }

    public Task SaveCurrentAsync() =>
        Document.FilePath is null ? SaveAsAsync() : SaveAsync(Document.FilePath);

    public async Task SaveAsAsync()
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(_host.GetPlaybackSource() ?? "subtitles")
            };
            picker.FileTypeChoices.Add(L("SubRipFileType"), [".srt"]);
            picker.FileTypeChoices.Add(L("WebVttFileType"), [".vtt"]);
            picker.FileTypeChoices.Add(L("AssFileType"), [".ass"]);
            InitializeWithWindow.Initialize(picker, _host.WindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is not null) await SaveAsync(file.Path);
        }
        catch (Exception exception)
        {
            await AppLog.WriteAsync("error", "file-picker", "SAVE_SUBTITLE_PICKER_ERROR", exception.Message, exception);
            await _host.Dialogs.ShowMessageAsync(L("SubtitleErrorTitle"), exception.Message);
        }
    }

    public async Task SaveAsync(string path)
    {
        var track = Document.ActiveTrack;
        if (track is null) return;
        try
        {
            var settings = _host.GetSettings();
            var result = await _files.SaveAsync(
                track,
                path,
                _host.GetDisplayMode() ?? SubtitleDisplayMode.Original,
                settings.Subtitle.FontFamily,
                settings.Subtitle.Encoding);
            track.Format = result.TargetFormat;
            Document.MarkSaved(path);
            _host.SetStatus(result.HasStyleLoss ? F("StatusSavedStyleLoss", path) : F("StatusSaved", path));
        }
        catch (Exception exception)
        {
            await _host.Dialogs.ShowMessageAsync(L("SaveErrorTitle"), exception.Message);
        }
    }

    public Task<bool> ConfirmDiscardChangesAsync(string action) =>
        ConfirmSaveOrDiscardAsync(F("UnsavedChangesActionMessage", action));

    public Task<bool> ConfirmCloseAsync() =>
        ConfirmSaveOrDiscardAsync(L("UnsavedChangesCloseMessage"));

    private async Task<bool> ConfirmSaveOrDiscardAsync(string message)
    {
        if (!Document.IsDirty) return true;
        var dialog = new ContentDialog
        {
            Title = L("UnsavedChangesTitle"),
            Content = message,
            PrimaryButtonText = L("SaveButtonText"),
            SecondaryButtonText = L("DiscardButton"),
            CloseButtonText = L("CancelButtonText"),
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await _host.Dialogs.ShowAsync(dialog);
        if (result == ContentDialogResult.None) return false;
        if (result != ContentDialogResult.Primary) return true;
        await SaveCurrentAsync();
        return !Document.IsDirty;
    }

    private static SubtitleDocument CreateBlankDocument()
    {
        var document = new SubtitleDocument();
        document.EnsureTrack();
        document.MarkSaved();
        return document;
    }

    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key), arguments);
}

internal sealed record SubtitleSessionHost(
    nint WindowHandle,
    Func<AppSettings> GetSettings,
    Func<string?> GetPlaybackSource,
    Func<SubtitleDisplayMode?> GetDisplayMode,
    Func<Task<bool>> PrepareForLoadAsync,
    Action<SubtitleDocument> DocumentChanged,
    Action<SubtitleDocument> DocumentLoaded,
    Action<string> SetStatus,
    WindowDialogService Dialogs);
