using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

// What the Vault's browsing lists read (Videos spec WP14, D39 and D41): matched and unmatched scenes together, performers, studios
// and tags that list only names with owned scenes, a picture chosen once, folders without paths, and nothing from outside the Vault.
public sealed class VaultBrowseStorageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_vault_browse_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly VaultBrowseRepository _browse;
    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), $"tuvima_vault_browse_cfg_{Guid.NewGuid():N}");
    private readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _vaultLibrary = Guid.NewGuid().ToString("D");
    private readonly string _normalLibrary = Guid.NewGuid().ToString("D");

    public VaultBrowseStorageTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _browse = new VaultBrowseRepository(_database, new MediaEngine.Storage.Secrets.ConfigFolderSecretsStore(_configDirectory));
        var admin = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@admin,'Admin','#7C4DFF','Administrator',NULL,@now);",
            new { admin, now = _now.ToString("O") });
        connection.Execute(
            "INSERT INTO vault_libraries(library_id, lookup_source, lookup_server_id, adults_confirmed_at, created_by_profile_id, created_at, updated_at) VALUES(@id,'none',NULL,@now,@admin,@now,@now);",
            new { id = _vaultLibrary, admin, now = _now.ToString("O") });
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch (ObjectDisposedException) { }
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        if (Directory.Exists(_configDirectory))
        {
            Directory.Delete(_configDirectory, recursive: true);
        }

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private Guid AddScene(string path, string? library = null, bool matched = false, string? title = null, string? description = null,
        DateTimeOffset? added = null, string? release = null, bool orphaned = false)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Videos', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id, is_orphaned)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId, @orphaned);
            """,
            new { workId, editionId, assetId, path, libraryId = library ?? _vaultLibrary, orphaned = orphaned ? 1 : 0, hash = "hash-" + assetId.ToString("N") });
        if (library is null)
        {
            connection.Execute(
                "INSERT INTO vault_activity(id, asset_id, kind, file_name, detail, created_at) VALUES(@id,@assetId,'added','x','',@at);",
                new { id = Guid.NewGuid(), assetId, at = (added ?? _now).ToString("O") });
        }

        if (matched)
        {
            connection.Execute(
                "INSERT INTO vault_matches(asset_id, server_id, record_id, matched_by, applied_via, matched_at) VALUES(@assetId,'stashdb','rec','fingerprint','automatic',@now);",
                new { assetId, now = _now.ToString("O") });
        }

        foreach (var (key, value) in new[] { ("title", title), ("description", description), ("original_release_date", release) })
        {
            if (value is not null)
            {
                connection.Execute(
                    "INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES(@assetId,@key,@value,@now);",
                    new { assetId, key, value, now = _now.ToString("O") });
            }
        }

        return assetId;
    }

    private Guid AddName(string table, string link, string linkColumn, string name, params Guid[] assets)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            $"INSERT INTO {table}(id, library_id, name, created_at, updated_at) VALUES(@id,@lib,@name,@now,@now);",
            new { id, lib = _vaultLibrary, name, now = _now.ToString("O") });
        foreach (var asset in assets)
        {
            connection.Execute($"INSERT INTO {link}(asset_id, {linkColumn}) VALUES(@asset,@id);", new { asset, id });
        }

        return id;
    }

    private Guid Performer(string name, params Guid[] assets) => AddName("vault_performers", "vault_asset_performers", "performer_id", name, assets);

    private Guid Studio(string name, params Guid[] assets) => AddName("vault_studios", "vault_asset_studios", "studio_id", name, assets);

    private Guid Tag(string name, params Guid[] assets) => AddName("vault_tags", "vault_asset_tags", "tag_id", name, assets);

    private Guid Art(string ownerKind, Guid owner, string kind, int secondsAfter)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO vault_artwork(id, owner_kind, owner_id, artwork_kind, file_path, created_at) VALUES(@id,@ownerKind,@owner,@kind,'x.jpg',@at);",
            new { id, ownerKind, owner, kind, at = _now.AddSeconds(secondsAfter).ToString("O") });
        return id;
    }

    [Fact]
    public async Task Scenes_ListMatchedAndUnmatchedTogether_NewestFirst_AndTheChipsNarrowThem()
    {
        var older = AddScene("C:/vault/A/old.mkv", matched: true, title: "Old", added: _now.AddDays(-2));
        var newer = AddScene("C:/vault/A/new.file.mkv", added: _now.AddDays(-1));

        var all = await _browse.ListScenesAsync(new VaultSceneQuery());
        var matched = await _browse.ListScenesAsync(new VaultSceneQuery(Match: VaultSceneMatchFilter.Matched));
        var unmatched = await _browse.ListScenesAsync(new VaultSceneQuery(Match: VaultSceneMatchFilter.Unmatched));

        Assert.Equal([newer, older], all.Items.Select(i => i.AssetId));
        Assert.Equal(2, all.Total);
        Assert.Equal([older], matched.Items.Select(i => i.AssetId));
        Assert.Equal([newer], unmatched.Items.Select(i => i.AssetId));
        Assert.True(matched.Items[0].Matched);
        Assert.False(unmatched.Items[0].Matched);
    }

    [Fact]
    public async Task Scenes_LeaveOutOtherLibrariesAndOrphanedFiles()
    {
        var kept = AddScene("C:/vault/kept.mkv");
        AddScene("C:/videos/normal.mkv", library: _normalLibrary);
        AddScene("C:/vault/gone.mkv", orphaned: true);

        var list = await _browse.ListScenesAsync(new VaultSceneQuery());

        Assert.Equal([kept], list.Items.Select(i => i.AssetId));
        Assert.Equal(1, list.Total);
    }

    [Fact]
    public async Task ASceneWithNoStoredTitle_ReadsItsFileNameInPlainWords()
    {
        AddScene("C:/vault/Some.Plain_File.name.mkv");

        var item = Assert.Single((await _browse.ListScenesAsync(new VaultSceneQuery())).Items);

        Assert.Equal("Some Plain File name", item.Title);
        Assert.Equal("Some.Plain_File.name.mkv", item.FileName);
    }

    [Fact]
    public async Task APerformerStudioOrTagFilter_ListsOnlyItsScenes_AndTitlesThePage()
    {
        var a = AddScene("C:/vault/a.mkv", matched: true);
        var b = AddScene("C:/vault/b.mkv", matched: true);
        var performer = Performer("Ana Example", a);
        var studio = Studio("Studio A", a, b);
        var tag = Tag("alpha", b);

        var byPerformer = await _browse.ListScenesAsync(new VaultSceneQuery(PerformerId: performer));
        var byStudio = await _browse.ListScenesAsync(new VaultSceneQuery(StudioId: studio));
        var byTag = await _browse.ListScenesAsync(new VaultSceneQuery(TagId: tag));

        Assert.Equal([a], byPerformer.Items.Select(i => i.AssetId));
        Assert.Equal("Ana Example", byPerformer.Heading);
        Assert.Equal(2, byStudio.Total);
        Assert.Equal("Studio A", byStudio.Heading);
        Assert.Equal([b], byTag.Items.Select(i => i.AssetId));
        Assert.Equal("alpha", byTag.Heading);
        Assert.Equal("Studio A", byStudio.Items[0].Studio);
    }

    [Fact]
    public async Task PerformersStudiosAndTags_ListOnlyNamesWithOwnedScenes()
    {
        var matched = AddScene("C:/vault/a.mkv", matched: true);
        var unmatched = AddScene("C:/vault/b.mkv");
        var orphan = AddScene("C:/vault/c.mkv", matched: true, orphaned: true);
        Performer("Linked", matched);
        Performer("Nobody", orphan);
        Studio("Studio A", matched);
        Tag("alpha", matched, unmatched);

        var performers = await _browse.ListPerformersAsync(VaultNameSorts.Name, false);
        var studios = await _browse.ListStudiosAsync(VaultNameSorts.Name, false);
        var tags = await _browse.ListTagsAsync(VaultNameSorts.SceneCount, true);

        Assert.Equal(["Linked"], performers.Select(p => p.Name));
        Assert.Equal(["Studio A"], studios.Select(p => p.Name));
        Assert.Equal(1, performers[0].SceneCount);
        Assert.Equal(2, Assert.Single(tags).SceneCount);
    }

    [Fact]
    public async Task APerformerTile_CarriesItsPortrait_AndAStudioItsLogo()
    {
        var scene = AddScene("C:/vault/a.mkv", matched: true);
        var performer = Performer("Ana", scene);
        var studio = Studio("Studio A", scene);
        var portrait = Art("performer", performer, "portrait", 0);
        var logo = Art("studio", studio, "logo", 0);

        Assert.Equal(portrait, Assert.Single(await _browse.ListPerformersAsync(VaultNameSorts.Name, false)).ImageArtworkId);
        Assert.Equal(logo, Assert.Single(await _browse.ListStudiosAsync(VaultNameSorts.Name, false)).ImageArtworkId);
    }

    [Fact]
    public async Task ASceneImage_IsTheStashBoxCoverWhenThereIsOne_ElseTheFirstSavedStill_AndStaysTheSameBetweenReads()
    {
        var withCover = AddScene("C:/vault/a.mkv", matched: true);
        var withStills = AddScene("C:/vault/b.mkv");
        var bare = AddScene("C:/vault/c.mkv");
        var stills = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            stills.Add(Art("asset", withStills, "still", i));
        }

        Art("asset", withCover, "still", 0);
        var cover = Art("asset", withCover, "cover", 5);

        var first = (await _browse.ListScenesAsync(new VaultSceneQuery())).Items.ToDictionary(i => i.AssetId);
        var second = (await _browse.ListScenesAsync(new VaultSceneQuery())).Items.ToDictionary(i => i.AssetId);

        Assert.Equal(cover, first[withCover].ImageArtworkId);
        Assert.Equal(stills[0], first[withStills].ImageArtworkId);
        Assert.Null(first[bare].ImageArtworkId);
        Assert.Equal(first[withStills].ImageArtworkId, second[withStills].ImageArtworkId);
        Assert.InRange(first[withStills].StillArtworkIds.Count, 1, 4);
    }

    [Fact]
    public async Task Search_MatchesTitleFileDescriptionAndNames_AndTreatsPercentSignsAsText()
    {
        var byTitle = AddScene("C:/vault/1.mkv", matched: true, title: "Findable Title");
        var byFile = AddScene("C:/vault/special-file.mkv");
        var byDescription = AddScene("C:/vault/3.mkv", matched: true, description: "has a needle inside");
        var byName = AddScene("C:/vault/4.mkv", matched: true);
        Performer("Quentin Name", byName);
        AddScene("C:/vault/5.mkv", title: "100% pure");

        async Task<List<Guid>> Find(string text) =>
            (await _browse.ListScenesAsync(new VaultSceneQuery(Text: text))).Items.Select(i => i.AssetId).ToList();

        Assert.Equal([byTitle], await Find("findable"));
        Assert.Equal([byFile], await Find("special-file"));
        Assert.Equal([byDescription], await Find("needle"));
        Assert.Equal([byName], await Find("quentin"));
        Assert.Single(await Find("100%"));
        Assert.Empty(await Find("%zzz"));
        Assert.Empty(await Find("_ure"));
    }

    [Fact]
    public async Task SearchNames_FindsPerformersAndStudiosWithOwnedScenesOnly()
    {
        var scene = AddScene("C:/vault/a.mkv", matched: true);
        Performer("Ana Example", scene);
        Performer("Ana Unlinked");
        Studio("Ana Studio", scene);

        var result = await _browse.SearchNamesAsync("ana", 10);

        Assert.Equal(["Ana Example"], result.Performers.Select(p => p.Name));
        Assert.Equal(["Ana Studio"], result.Studios.Select(p => p.Name));
    }

    [Fact]
    public async Task Folders_GroupScenesByDirectory_WithAnIdThatCarriesNoPath_AndOpenToTheirScenes()
    {
        AddScene("C:/vault/Series A/one.mkv");
        AddScene("C:/vault/Series A/two.mkv", matched: true);
        var other = AddScene("C:/vault/Series B/three.mkv");

        var folders = await _browse.ListFoldersAsync();

        Assert.Equal(["Series A", "Series B"], folders.Select(f => f.Name).Order());
        Assert.Equal(2, folders.Single(f => f.Name == "Series A").SceneCount);
        Assert.All(folders, f =>
        {
            Assert.DoesNotContain("/", f.Id);
            Assert.DoesNotContain("vault", f.Id, StringComparison.OrdinalIgnoreCase);
        });

        var inB = await _browse.ListScenesAsync(new VaultSceneQuery(FolderId: folders.Single(f => f.Name == "Series B").Id));
        Assert.Equal([other], inB.Items.Select(i => i.AssetId));
    }

    [Fact]
    public async Task FolderIds_AreKeyedWithThisInstallsSecret_StableAcrossRestarts_AndNotAGuessableHashOfThePath()
    {
        AddScene("C:/vault/Series A/one.mkv");
        var first = (await _browse.ListFoldersAsync()).Single().Id;

        // A restart: a new repository over the same secrets folder gives the same id.
        var restarted = new VaultBrowseRepository(_database, new MediaEngine.Storage.Secrets.ConfigFolderSecretsStore(_configDirectory));
        Assert.Equal(first, (await restarted.ListFoldersAsync()).Single().Id);

        // The old unsalted form, a hash of the lower-cased path, no longer matches, so a guessed path proves nothing.
        var unsalted = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("c:/vault/series a")))[..16].ToLowerInvariant();
        Assert.NotEqual(unsalted, first);

        // Another install has another secret, so the same folder has another id.
        var otherConfig = Path.Combine(Path.GetTempPath(), $"tuvima_vault_browse_cfg_{Guid.NewGuid():N}");
        try
        {
            var other = new VaultBrowseRepository(_database, new MediaEngine.Storage.Secrets.ConfigFolderSecretsStore(otherConfig));
            Assert.NotEqual(first, (await other.ListFoldersAsync()).Single().Id);
        }
        finally
        {
            Directory.Delete(otherConfig, recursive: true);
        }
    }

    [Fact]
    public async Task Sorts_AreWhitelisted_AndPagingKeepsTheTotal()
    {
        AddScene("C:/vault/b.mkv", title: "Bravo", release: "2024-01-01");
        AddScene("C:/vault/a.mkv", title: "Alpha", release: "2025-01-01");
        AddScene("C:/vault/c.mkv", title: "Charlie", release: "2023-01-01");

        var byTitle = await _browse.ListScenesAsync(new VaultSceneQuery(Sort: VaultSceneSorts.Title, Descending: false));
        var byRelease = await _browse.ListScenesAsync(new VaultSceneQuery(Sort: VaultSceneSorts.ReleaseDate, Descending: true));
        var hostile = await _browse.ListScenesAsync(new VaultSceneQuery(Sort: "title; DROP TABLE media_assets"));
        var page = await _browse.ListScenesAsync(new VaultSceneQuery(Sort: VaultSceneSorts.Title, Descending: false, Skip: 1, Take: 1));

        Assert.Equal(["Alpha", "Bravo", "Charlie"], byTitle.Items.Select(i => i.Title));
        Assert.Equal(["Alpha", "Bravo", "Charlie"], byRelease.Items.Select(i => i.Title));
        Assert.Equal(3, hostile.Items.Count);
        Assert.Equal(["Bravo"], page.Items.Select(i => i.Title));
        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task AScenesOwnPage_HasItsDescriptionAndNames_AndANormalLibraryFileHasNone()
    {
        var scene = AddScene("C:/vault/a.mkv", matched: true, title: "A", description: "About it.");
        Performer("Ana", scene);
        Studio("Studio A", scene);
        Tag("alpha", scene);
        var normal = AddScene("C:/videos/n.mkv", library: _normalLibrary);

        var detail = await _browse.GetSceneAsync(scene);

        Assert.NotNull(detail);
        Assert.Equal("About it.", detail.Description);
        Assert.Equal("fingerprint", detail.MatchedBy);
        Assert.Equal(["Ana"], detail.Performers.Select(n => n.Name));
        Assert.Equal(["Studio A"], detail.Studios.Select(n => n.Name));
        Assert.Equal(["alpha"], detail.Tags.Select(n => n.Name));
        Assert.Null(await _browse.GetSceneAsync(normal));
        Assert.Null(await _browse.GetSceneAsync(Guid.NewGuid()));
    }
}
