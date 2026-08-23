using AIMediaWorker.History;
using AIMediaWorker.Media;
using AIMediaWorker.Network;
using AIMediaWorker.Settings;
using System.Net;
using System.Text;

namespace AIMediaWorker.Tests;

public sealed class ServicesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AIMediaWorker.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Ctrl+Shift+S", "S", true, true, false, true)]
    [InlineData("Ctrl+S", "S", true, true, false, false)]
    [InlineData("Space", "Space", false, false, false, true)]
    [InlineData("Alt+F11", "F11", false, false, true, true)]
    public void ShortcutGesturesMatchExactModifiers(string gesture, string key, bool control, bool shift, bool alt, bool expected)
    {
        Assert.Equal(expected, ShortcutGesture.Matches(gesture, key, control, shift, alt));
    }

    [Fact]
    public async Task SettingsRoundTripAndCorruptionRecovery()
    {
        var path = Path.Combine(_folder, "settings.json");
        var service = new SettingsService(path);
        var settings = new AppSettings();
        settings.Asr.Language = "ko";
        settings.Playback.DefaultVolume = 77;
        settings.Playback.ShowSubtitles = false;
        settings.Window.HasPlacement = true;
        settings.Window.X = 120;
        settings.Window.Y = 80;
        settings.Window.Width = 1440;
        settings.Window.Height = 900;
        settings.Window.IsMaximized = true;
        settings.Window.RightPanelWidth = 440;
        settings.Window.BottomPanelHeight = 220;
        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();
        Assert.Equal("ko", loaded.Asr.Language);
        Assert.Equal(77, loaded.Playback.DefaultVolume);
        Assert.False(loaded.Playback.ShowSubtitles);
        Assert.True(loaded.Window.HasPlacement);
        Assert.Equal((120, 80, 1440, 900, true), (loaded.Window.X, loaded.Window.Y, loaded.Window.Width, loaded.Window.Height, loaded.Window.IsMaximized));
        Assert.Equal((440, 220), (loaded.Window.RightPanelWidth, loaded.Window.BottomPanelHeight));
        await File.WriteAllTextAsync(path, "{ definitely broken");
        var recovered = await service.LoadAsync();
        Assert.Equal("auto", recovered.Asr.Language);
        Assert.NotEmpty(Directory.GetFiles(_folder, "settings.json.corrupt-*"));
    }

    [Fact]
    public async Task SettingsNormalizeMissingSectionsAndShortcutDefaults()
    {
        var path = Path.Combine(_folder, "settings-null-sections.json");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, "{\"Playback\":null,\"General\":{\"Shortcuts\":null},\"Network\":null,\"Window\":null}");

        var loaded = await new SettingsService(path).LoadAsync();

        Assert.NotNull(loaded.Playback);
        Assert.True(loaded.Playback.ShowSubtitles);
        Assert.NotNull(loaded.Network);
        Assert.NotNull(loaded.Window);
        Assert.Equal(1280, loaded.Window.Width);
        Assert.Equal("Ctrl+Shift+S", loaded.General.Shortcuts[ShortcutActions.SaveSubtitleAs]);
        Assert.Equal("Enter", loaded.General.Shortcuts[ShortcutActions.Fullscreen]);
        Assert.Equal("V", loaded.General.Shortcuts[ShortcutActions.ToggleSubtitles]);
        Assert.Equal("Ctrl+W", loaded.General.Shortcuts[ShortcutActions.CloseWindow]);
    }

    [Fact]
    public async Task RecentMediaDeduplicatesAndPersists()
    {
        var path = Path.Combine(_folder, "history.json");
        var history = new MediaHistoryService(path);
        history.AddRecent(new HttpMediaSource(new Uri("https://example.test/movie.mp4")), 100, 2);
        history.AddRecent(new HttpMediaSource(new Uri("https://example.test/movie.mp4")), 200, 2);
        history.AddRecent(new HttpMediaSource(new Uri("https://example.test/other.mkv")), 300, 2);
        Assert.Equal(2, history.Recent.Count);
        Assert.Equal(200, history.Recent[1].LastPlaybackPositionMicroseconds);
        await history.SaveAsync();
        var loaded = new MediaHistoryService(path);
        await loaded.LoadAsync();
        Assert.Equal(2, loaded.Recent.Count);
    }

    [Fact]
    public async Task FolderFavoritesPersistAndDeduplicate()
    {
        var path = Path.Combine(_folder, "favorites.json");
        var history = new MediaHistoryService(path);
        var source = new LocalMediaSource(Path.Combine(_folder, "Videos"));
        history.AddFavorite(source, true);
        history.AddFavorite(source, true);
        await history.SaveAsync();

        var loaded = new MediaHistoryService(path);
        await loaded.LoadAsync();

        var favorite = Assert.Single(loaded.Favorites);
        Assert.True(favorite.IsFolder);
    }

    [Fact]
    public void WebDavUriResolutionAndCredentialIdentifiersAreStable()
    {
        var directory = new Uri("https://dav.example.test/root/folder/");
        Assert.Equal("https://dav.example.test/root/video%20one.mkv", WebDavClient.ResolveChild(directory, "../video%20one.mkv").AbsoluteUri);
        var id = Guid.Parse("6e4f9d87-51b7-4cfb-a83d-e1009a728302");
        Assert.Equal("AIMediaWorker/WebDAV/6e4f9d87-51b7-4cfb-a83d-e1009a728302", CredentialIdentifier.ForWebDav(id));
        Assert.Equal("AIMediaWorker/LLM/opencode_zen", CredentialIdentifier.ForLlm("OpenCode_Zen"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WebDavMultiStatusIsParsedAndSorted()
    {
        const string xml = "<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/root/</d:href><d:propstat><d:prop><d:displayname>root</d:displayname><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response><d:response><d:href>/root/folder/</d:href><d:propstat><d:prop><d:displayname>folder</d:displayname><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response><d:response><d:href>/root/movie.mkv</d:href><d:propstat><d:prop><d:displayname>movie.mkv</d:displayname><d:resourcetype/><d:getcontentlength>1234</d:getcontentlength></d:prop></d:propstat></d:response></d:multistatus>";
        using var http = new HttpClient(new StaticHandler(new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") }));
        using var client = new WebDavClient(new EmptyCredentials(), http);
        var server = new WebDavServerSettings { Url = "https://dav.example/root/" };
        var entries = await client.ListAsync(server, new Uri(server.Url));
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].IsCollection);
        Assert.Equal(1234, entries[1].ContentLength);
    }

    private sealed class EmptyCredentials : ICredentialService
    {
        public void Save(string identifier, string username, string secret) { }
        public (string Username, string Secret)? Read(string identifier) => null;
        public bool Delete(string identifier) => false;
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
