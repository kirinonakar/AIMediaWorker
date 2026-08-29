using AIMediaWorker.Diagnostics;
using AIMediaWorker.Media;
using AIMediaWorker.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace AIMediaWorker.Views;

/// <summary>Owns the audio-only playback surface, embedded artwork, and tag status.</summary>
internal sealed class AudioPresentationController : IDisposable
{
    private readonly AudioPresentationViewElements _view;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<NativeVideoHost?> _getVideoHost;
    private readonly Action<string> _setStatus;
    private CancellationTokenSource? _artworkCancellation;
    private int _artworkRequestId;
    private string? _currentAudioPath;
    private bool _disposed;

    public AudioPresentationController(
        AudioPresentationViewElements view,
        DispatcherQueue dispatcherQueue,
        Func<NativeVideoHost?> getVideoHost,
        Action<string> setStatus)
    {
        _view = view;
        _dispatcherQueue = dispatcherQueue;
        _getVideoHost = getVideoHost;
        _setStatus = setStatus;
    }

    public string? StatusText { get; private set; }

    public void BeginMediaOpen(string source)
    {
        ResetArtwork();
        StatusText = null;
        _currentAudioPath = IsLocalAudioPath(source) ? Path.GetFullPath(source) : null;
        ApplyPresentation(_currentAudioPath is not null);
    }

    public void CompleteMediaOpen(IMediaSource source)
    {
        ResetArtwork();
        StatusText = null;
        _currentAudioPath = source is LocalMediaSource local && MediaFileClassifier.IsAudio(local.Path)
            ? Path.GetFullPath(local.Path)
            : null;
        ApplyPresentation(_currentAudioPath is not null);
        if (_currentAudioPath is not { } path) return;
        LoadTag(path);
        LoadArtwork(path);
    }

    public void Reset()
    {
        ResetArtwork();
        StatusText = null;
        _currentAudioPath = null;
        ApplyPresentation(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }

    private void LoadTag(string path) => _ = Task.Run(() =>
    {
        var tagText = AudioTagReader.ReadDisplayText(path);
        if (tagText is null) return;
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrent(path)) return;
            StatusText = tagText;
            _setStatus(tagText);
        });
    });

    private void LoadArtwork(string path)
    {
        var requestId = ++_artworkRequestId;
        var cancellation = new CancellationTokenSource();
        var previous = _artworkCancellation;
        _artworkCancellation = cancellation;
        previous?.Cancel();
        previous?.Dispose();
        _ = LoadArtworkAsync(path, requestId, cancellation.Token);
    }

    private async Task LoadArtworkAsync(string path, int requestId, CancellationToken cancellationToken)
    {
        try
        {
            var artwork = await Task.Run(() => AudioTagReader.ReadArtwork(path), cancellationToken).ConfigureAwait(false);
            if (artwork is null || cancellationToken.IsCancellationRequested) return;
            _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (!IsCurrent(path, requestId, cancellationToken)) return;
                    var image = await DecodeAsync(artwork, cancellationToken);
                    if (image is null || !IsCurrent(path, requestId, cancellationToken)) return;
                    _view.AlbumArt.Source = image;
                    _view.Fallback.Visibility = Visibility.Collapsed;
                }
                catch (OperationCanceledException) { }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _ = AppLog.WriteAsync("warning", "playback", "AUDIO_ARTWORK_DECODE_ERROR", exception.Message, exception);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _ = AppLog.WriteAsync("warning", "playback", "AUDIO_ARTWORK_READ_ERROR", exception.Message, exception);
        }
    }

    private bool IsCurrent(string path) =>
        string.Equals(_currentAudioPath, path, StringComparison.OrdinalIgnoreCase);

    private bool IsCurrent(string path, int requestId, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        requestId == _artworkRequestId &&
        _view.Surface.Visibility == Visibility.Visible &&
        IsCurrent(path);

    private void ResetArtwork()
    {
        ++_artworkRequestId;
        var cancellation = _artworkCancellation;
        _artworkCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        _view.AlbumArt.Source = null;
        _view.Fallback.Visibility = Visibility.Visible;
    }

    private void ApplyPresentation(bool isLocalAudio)
    {
        _view.Surface.Visibility = isLocalAudio ? Visibility.Visible : Visibility.Collapsed;
        _getVideoHost()?.SetMediaVisible(!isLocalAudio);
    }

    private static bool IsLocalAudioPath(string source) =>
        Path.IsPathFullyQualified(source) && MediaFileClassifier.IsAudio(source);

    private static async Task<BitmapImage?> DecodeAsync(AudioArtwork artwork, CancellationToken cancellationToken)
    {
        if (artwork.Bytes is not { Length: > 0 }) return null;
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(artwork.Bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);
        var image = new BitmapImage { DecodePixelWidth = 1600 };
        await image.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }
}

internal sealed record AudioPresentationViewElements(
    FrameworkElement Surface,
    Image AlbumArt,
    FrameworkElement Fallback);
