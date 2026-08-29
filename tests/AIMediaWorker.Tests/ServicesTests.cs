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
    [InlineData("Ctrl+P", "P", true, false, false, true)]
    [InlineData("Ctrl+Shift+N", "N", true, true, false, true)]
    [InlineData("Ctrl+F", "F", true, false, false, true)]
    [InlineData("Ctrl+B", "B", true, false, false, true)]
    [InlineData("Ctrl+1", "Number1", true, false, false, true)]
    [InlineData("Ctrl+2", "Number2", true, false, false, true)]
    [InlineData("Ctrl+1", "D1", true, false, false, true)]
    [InlineData("Ctrl+2", "NumberPad2", true, false, false, true)]
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
        settings.General.Language = AppLanguage.Japanese;
        settings.General.Theme = AppTheme.Light;
        settings.General.UiFontFamily = "Arial";
        settings.Asr.Language = "ko";
        settings.Playback.DefaultVolume = 77;
        settings.Playback.UseLargeToolbarIcons = true;
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
        Assert.Equal(AppLanguage.Japanese, service.LoadLanguage());
        Assert.Equal(AppTheme.Light, service.LoadTheme());
        var loaded = await service.LoadAsync();
        Assert.Equal(AppLanguage.Japanese, loaded.General.Language);
        Assert.Equal("Arial", loaded.General.UiFontFamily);
        Assert.Equal("ko", loaded.Asr.Language);
        Assert.Equal(77, loaded.Playback.DefaultVolume);
        Assert.True(loaded.Playback.UseLargeToolbarIcons);
        Assert.False(loaded.Playback.ShowSubtitles);
        Assert.True(loaded.Window.HasPlacement);
        Assert.Equal((120, 80, 1440, 900, true), (loaded.Window.X, loaded.Window.Y, loaded.Window.Width, loaded.Window.Height, loaded.Window.IsMaximized));
        Assert.Equal((440, 220), (loaded.Window.RightPanelWidth, loaded.Window.BottomPanelHeight));
        await File.WriteAllTextAsync(path, "{ definitely broken");
        Assert.Equal(AppLanguage.Default, service.LoadLanguage());
        Assert.Equal(GeneralSettings.DefaultTheme, service.LoadTheme());
        var recovered = await service.LoadAsync();
        Assert.Equal("auto", recovered.Asr.Language);
        Assert.NotEmpty(Directory.GetFiles(_folder, "settings.json.corrupt-*"));
    }

    [Fact]
    public void MissingSettingsUseDarkStartupTheme()
    {
        var path = Path.Combine(_folder, "missing-settings.json");

        Assert.Equal(AppTheme.Dark, new SettingsService(path).LoadTheme());
    }

    [Fact]
    public async Task SettingsNormalizeMissingSectionsAndShortcutDefaults()
    {
        var path = Path.Combine(_folder, "settings-null-sections.json");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, "{\"Playback\":null,\"Asr\":{\"ModelPath\":null,\"AlignerPath\":\"  \"},\"General\":{\"Shortcuts\":null},\"Network\":null,\"Window\":null}");

        var loaded = await new SettingsService(path).LoadAsync();

        Assert.NotNull(loaded.Playback);
        Assert.True(loaded.Playback.ShowSubtitles);
        Assert.NotNull(loaded.Network);
        Assert.NotNull(loaded.Window);
        Assert.Equal(1280, loaded.Window.Width);
        Assert.Equal(AsrSettings.DefaultModelId, loaded.Asr.ModelPath);
        Assert.Equal(AsrSettings.DefaultAlignerId, loaded.Asr.AlignerPath);
        Assert.Equal("Ctrl+Shift+S", loaded.General.Shortcuts[ShortcutActions.SaveSubtitleAs]);
        Assert.Equal("Enter", loaded.General.Shortcuts[ShortcutActions.Fullscreen]);
        Assert.Equal("V", loaded.General.Shortcuts[ShortcutActions.ToggleSubtitles]);
        Assert.Equal("Ctrl+W", loaded.General.Shortcuts[ShortcutActions.CloseWindow]);
        Assert.Equal("Ctrl+P", loaded.General.Shortcuts[ShortcutActions.PlayPauseAlternate]);
        Assert.Equal("Ctrl+Shift+N", loaded.General.Shortcuts[ShortcutActions.PlayFromBeginning]);
        Assert.Equal("Ctrl+B", loaded.General.Shortcuts[ShortcutActions.PreviousMedia]);
        Assert.Equal("Ctrl+F", loaded.General.Shortcuts[ShortcutActions.NextMedia]);
        Assert.Equal("Ctrl+1", loaded.General.Shortcuts[ShortcutActions.ToggleTimelinePanel]);
        Assert.Equal("Ctrl+2", loaded.General.Shortcuts[ShortcutActions.ToggleSidePanel]);
        Assert.Equal(GeneralSettings.DefaultUiFontFamily, loaded.General.UiFontFamily);
    }

    [Fact]
    public async Task SettingsNormalizeBlankUiFontToNotoSansCjkJp()
    {
        var path = Path.Combine(_folder, "settings-blank-ui-font.json");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, "{\"General\":{\"UiFontFamily\":\"   \"}}");

        var loaded = await new SettingsService(path).LoadAsync();

        Assert.Equal("Noto Sans CJK JP", loaded.General.UiFontFamily);
    }

    [Fact]
    public async Task SettingsMigrateReversedMediaShortcuts()
    {
        var path = Path.Combine(_folder, "settings-old-media-shortcuts.json");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, "{\"General\":{\"Shortcuts\":{\"PreviousMedia\":\"Ctrl+F\",\"NextMedia\":\"Ctrl+B\"}}}");

        var loaded = await new SettingsService(path).LoadAsync();

        Assert.Equal("Ctrl+B", loaded.General.Shortcuts[ShortcutActions.PreviousMedia]);
        Assert.Equal("Ctrl+F", loaded.General.Shortcuts[ShortcutActions.NextMedia]);
    }

    [Fact]
    public async Task SettingsMigrateLegacyDefaultSubtitleFont()
    {
        var path = Path.Combine(_folder, "settings-old-subtitle-font.json");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":2,\"Subtitle\":{\"FontFamily\":\"Segoe UI\"}}");

        var loaded = await new SettingsService(path).LoadAsync();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(SubtitleSettings.DefaultFontFamily, loaded.Subtitle.FontFamily);
    }

    [Fact]
    public async Task SettingsMigrateWindowsCaptionPreviousSentenceCountDefault()
    {
        var path = Path.Combine(_folder, "settings-old-caption-history.json");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":5,\"Capture\":{\"WindowsCaptionPreviousSentenceCount\":2}}");

        var service = new SettingsService(path);
        var loaded = await service.LoadAsync();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(1, loaded.Capture.WindowsCaptionPreviousSentenceCount);

        await service.SaveAsync(loaded);
        var saved = await service.LoadAsync();
        Assert.Equal(1, saved.Capture.WindowsCaptionPreviousSentenceCount);
    }

    [Fact]
    public async Task SettingsMigrateGeneralDefaultFolderToCaptureFolder()
    {
        var path = Path.Combine(_folder, "settings-old-capture-folder.json");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":6,\"General\":{\"DefaultFolder\":\"  D:\\\\Captures  \"}}");

        var loaded = await new SettingsService(path).LoadAsync();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(@"D:\Captures", loaded.Capture.CaptureFolder);
    }

    [Fact]
    public async Task SettingsMigrateUnslothProviderName()
    {
        var path = Path.Combine(_folder, "settings-old-unsloth.json");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, "{\"Llm\":{\"Provider\":\"Unsloth\"}}");

        var loaded = await new SettingsService(path).LoadAsync();

        Assert.Equal("Unsloth Desktop", loaded.Llm.Provider);
    }

    [Fact]
    public async Task RecentMediaDeduplicatesAndPersists()
    {
        var recentPath = Path.Combine(_folder, "recent.json");
        var favoritesPath = Path.Combine(_folder, "favorites.json");
        var history = new MediaHistoryService(recentPath, favoritesPath);
        await history.LoadRecentAsync();
        history.AddRecent(new HttpMediaSource(new Uri("https://example.test/movie.mp4")), 100, 2);
        history.AddRecent(new HttpMediaSource(new Uri("https://example.test/movie.mp4")), 200, 2);
        history.AddRecent(new HttpMediaSource(new Uri("https://example.test/other.mkv")), 300, 2);
        Assert.Equal(2, history.Recent.Count);
        Assert.Equal(200, history.Recent[1].LastPlaybackPositionMicroseconds);
        await history.SaveRecentAsync();
        Assert.False(File.Exists(favoritesPath));

        var loaded = new MediaHistoryService(recentPath, favoritesPath);
        await loaded.LoadRecentAsync();
        Assert.Equal(2, loaded.Recent.Count);
    }

    [Fact]
    public async Task UnchangedFavoritesDoNotCreateAFile()
    {
        var recentPath = Path.Combine(_folder, "unchanged-recent.json");
        var favoritesPath = Path.Combine(_folder, "unchanged-favorites.json");
        var history = new MediaHistoryService(recentPath, favoritesPath);

        await history.LoadFavoritesAsync();
        await history.SaveFavoritesAsync();

        Assert.False(File.Exists(favoritesPath));
    }

    [Fact]
    public async Task FavoritesAreLoadedLazily()
    {
        var recentPath = Path.Combine(_folder, "lazy-recent.json");
        var favoritesPath = Path.Combine(_folder, "lazy-favorites.json");
        var lazyReader = new MediaHistoryService(recentPath, favoritesPath);
        var writer = new MediaHistoryService(recentPath, favoritesPath);
        await writer.LoadFavoritesAsync();
        writer.AddFavorite(new HttpMediaSource(new Uri("https://example.test/lazy.mp4")));
        await writer.SaveFavoritesAsync();

        await lazyReader.LoadFavoritesAsync();

        Assert.Single(lazyReader.Favorites);
    }

    [Fact]
    public async Task RecentMediaNeverExceedsTwentyItems()
    {
        var recentPath = Path.Combine(_folder, "recent-limit.json");
        var favoritesPath = Path.Combine(_folder, "favorites-limit.json");
        var history = new MediaHistoryService(recentPath, favoritesPath);
        await history.LoadRecentAsync();
        for (var index = 0; index < 25; index++)
        {
            history.AddRecent(new HttpMediaSource(new Uri($"https://example.test/media-{index}.mp4")), index, 100);
        }

        Assert.Equal(20, history.Recent.Count);
        Assert.Equal("https://example.test/media-24.mp4", history.Recent[0].Location);
        Assert.Equal("https://example.test/media-5.mp4", history.Recent[^1].Location);

        await history.SaveRecentAsync();
        var loaded = new MediaHistoryService(recentPath, favoritesPath);
        await loaded.LoadRecentAsync();
        Assert.Equal(20, loaded.Recent.Count);
    }

    [Fact]
    public async Task FolderFavoritesPersistAndDeduplicate()
    {
        var recentPath = Path.Combine(_folder, "recent.json");
        var favoritesPath = Path.Combine(_folder, "favorites.json");
        var history = new MediaHistoryService(recentPath, favoritesPath);
        await history.LoadFavoritesAsync();
        var source = new LocalMediaSource(Path.Combine(_folder, "Videos"));
        Assert.True(history.AddFavorite(source, true));
        Assert.False(history.AddFavorite(source, true));
        await history.SaveFavoritesAsync();
        Assert.False(File.Exists(recentPath));

        var loaded = new MediaHistoryService(recentPath, favoritesPath);
        await loaded.LoadFavoritesAsync();

        var favorite = Assert.Single(loaded.Favorites);
        Assert.True(favorite.IsFolder);
    }

    [Fact]
    public async Task FavoriteFoldersStayAboveFilesAndDraggedOrderPersists()
    {
        var recentPath = Path.Combine(_folder, "recent-order.json");
        var favoritesPath = Path.Combine(_folder, "favorite-order.json");
        var history = new MediaHistoryService(recentPath, favoritesPath);
        await history.LoadFavoritesAsync();
        var firstFile = new HttpMediaSource(new Uri("https://example.test/first.mp4"));
        var secondFile = new HttpMediaSource(new Uri("https://example.test/second.mp4"));
        var firstFolder = new LocalMediaSource(Path.Combine(_folder, "FirstFolder"));
        var secondFolder = new LocalMediaSource(Path.Combine(_folder, "SecondFolder"));

        history.AddFavorite(firstFile);
        history.AddFavorite(firstFolder, true);
        history.AddFavorite(secondFile);
        history.AddFavorite(secondFolder, true);

        Assert.Equal([firstFolder.Location, secondFolder.Location, firstFile.Location, secondFile.Location], history.Favorites.Select(item => item.Location));

        Assert.True(history.ReorderFavorites([secondFile.Location, secondFolder.Location, firstFile.Location, firstFolder.Location]));
        Assert.Equal([secondFolder.Location, firstFolder.Location, secondFile.Location, firstFile.Location], history.Favorites.Select(item => item.Location));

        await history.SaveFavoritesAsync();
        var loaded = new MediaHistoryService(recentPath, favoritesPath);
        await loaded.LoadFavoritesAsync();

        Assert.Equal([secondFolder.Location, firstFolder.Location, secondFile.Location, firstFile.Location], loaded.Favorites.Select(item => item.Location));
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
    public void WebDavConnectionDetailsRoundTripThroughOneCredentialEntry()
    {
        var credentials = new MemoryCredentials();
        var store = new WebDavCredentialStore(credentials);
        var serverId = Guid.NewGuid();
        var expected = new WebDavConnectionCredential("https://dav.example.test/root/", 8443, "media-user", "secret-value");

        store.Save(serverId, expected);

        Assert.Equal(expected, store.Read(serverId));
        Assert.Equal("https://dav.example.test:8443/root/", store.Read(serverId)!.RootUri.AbsoluteUri);
        Assert.Equal(string.Empty, credentials.Read(CredentialIdentifier.ForWebDav(serverId))!.Value.Username);
    }

    [Fact]
    public void WebDavAddressDefaultsToHttpsAndRejectsHttp()
    {
        Assert.True(WebDavConnectionCredential.TryParseHttpsAddress("dav.example.test/root", out var withoutScheme));
        Assert.Equal("https://dav.example.test/root", withoutScheme.AbsoluteUri);
        Assert.True(WebDavConnectionCredential.TryParseHttpsAddress("https://dav.example.test/root", out var withScheme));
        Assert.Equal("https://dav.example.test/root", withScheme.AbsoluteUri);
        Assert.False(WebDavConnectionCredential.TryParseHttpsAddress("http://dav.example.test/root", out _));
        Assert.Throws<ArgumentException>(() => new WebDavConnectionCredential("http://dav.example.test/root/", 80, "", "").RootUri);
    }

    [Fact]
    public async Task WebDavSettingsContainOnlyNonSecretServerMetadata()
    {
        var path = Path.Combine(_folder, "webdav-settings.json");
        var settings = new AppSettings();
        settings.Network.WebDavServers.Add(new WebDavServerSettings { Name = "Private server" });

        await new SettingsService(path).SaveAsync(settings);

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("Private server", json);
        Assert.DoesNotContain("\"Url\"", json);
        Assert.DoesNotContain("\"Username\"", json);
        Assert.DoesNotContain("Password", json);
    }

    [Fact]
    public void LegacyWebDavSettingsMigrateIntoCredentialManagerPayload()
    {
        var credentials = new MemoryCredentials();
        var server = new WebDavServerSettings { LegacyUrl = "https://legacy.example.test:9443/dav/", LegacyUsername = "legacy-user" };
        credentials.Save(CredentialIdentifier.ForWebDav(server.Id), "old-user-field", "legacy-password");
        var store = new WebDavCredentialStore(credentials);

        Assert.True(store.MigrateLegacy(server));

        var migrated = store.Read(server.Id);
        Assert.NotNull(migrated);
        Assert.Equal(("https://legacy.example.test/dav/", 9443, "legacy-user", "legacy-password"), (migrated.Address, migrated.Port, migrated.Username, migrated.Password));
        Assert.Null(server.LegacyUrl);
        Assert.Null(server.LegacyUsername);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WebDavMultiStatusIsParsedAndSorted()
    {
        const string xml = "<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/root/</d:href><d:propstat><d:prop><d:displayname>root</d:displayname><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response><d:response><d:href>/root/folder/</d:href><d:propstat><d:prop><d:displayname>folder</d:displayname><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response><d:response><d:href>/root/movie.mkv</d:href><d:propstat><d:prop><d:displayname>movie.mkv</d:displayname><d:resourcetype/><d:getcontentlength>1234</d:getcontentlength></d:prop></d:propstat></d:response></d:multistatus>";
        using var http = new HttpClient(new StaticHandler(new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") }));
        var credentials = new MemoryCredentials();
        var server = new WebDavServerSettings();
        new WebDavCredentialStore(credentials).Save(server.Id, new WebDavConnectionCredential("https://dav.example/root/", 443, "user", "password"));
        using var client = new WebDavClient(credentials, http);
        var entries = await client.ListAsync(server, new Uri("https://dav.example/root/"));
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].IsCollection);
        Assert.Equal(1234, entries[1].ContentLength);

        using var mediaRequest = client.CreateMediaRequest(server, entries[1].Uri);
        Assert.Equal("Basic", mediaRequest.Headers.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("user:password")), mediaRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task WebDavSubtitleDownloadUsesStoredAuthentication()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<SAMI><SYNC Start=0><P>caption"))
        });
        using var http = new HttpClient(handler);
        var credentials = new MemoryCredentials();
        var server = new WebDavServerSettings();
        new WebDavCredentialStore(credentials).Save(server.Id, new WebDavConnectionCredential("https://dav.example/root/", 443, "user", "password"));
        using var client = new WebDavClient(credentials, http);

        var bytes = await client.DownloadAsync(server, new Uri("https://dav.example/root/movie.smi"));

        Assert.Contains("caption", Encoding.UTF8.GetString(bytes));
        Assert.Equal("Basic", handler.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("user:password")), handler.Authorization?.Parameter);
    }

    [Fact]
    public async Task WebDavSearchRecursesAndSupportsRegularExpressions()
    {
        using var http = new HttpClient(new WebDavTreeHandler());
        var credentials = new MemoryCredentials();
        var server = new WebDavServerSettings();
        new WebDavCredentialStore(credentials).Save(server.Id, new WebDavConnectionCredential("https://dav.example/root/", 443, "user", "password"));
        using var client = new WebDavClient(credentials, http);

        var results = await client.SearchAsync(server, new Uri("https://dav.example/root/"), @"Episode\s+\d+\.mkv$", useRegex: true);

        var result = Assert.Single(results);
        Assert.Equal("folder/Episode 02.mkv", result.SearchRelativePath);
        Assert.Equal("https://dav.example/root/folder/Episode%2002.mkv", result.Uri.AbsoluteUri);
    }

    private sealed class MemoryCredentials : ICredentialService
    {
        private readonly Dictionary<string, (string Username, string Secret)> _values = [];
        public void Save(string identifier, string username, string secret) => _values[identifier] = (username, secret);
        public (string Username, string Secret)? Read(string identifier) => _values.TryGetValue(identifier, out var value) ? value : null;
        public bool Delete(string identifier) => _values.Remove(identifier);
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            return Task.FromResult(response);
        }
    }

    private sealed class WebDavTreeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            var xml = path == "/root/folder/"
                ? MultiStatus(
                    Response("/root/folder/", "folder", collection: true),
                    Response("/root/folder/Episode%2002.mkv", "Episode 02.mkv", collection: false))
                : MultiStatus(
                    Response("/root/", "root", collection: true),
                    Response("/root/folder/", "folder", collection: true),
                    Response("/root/other.mp4", "other.mp4", collection: false));
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });
        }

        private static string MultiStatus(params string[] responses) =>
            $"<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\">{string.Concat(responses)}</d:multistatus>";

        private static string Response(string href, string name, bool collection) =>
            $"<d:response><d:href>{href}</d:href><d:propstat><d:prop><d:displayname>{name}</d:displayname><d:resourcetype>{(collection ? "<d:collection/>" : string.Empty)}</d:resourcetype></d:prop></d:propstat></d:response>";
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
