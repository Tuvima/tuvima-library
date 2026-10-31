using Dapper;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

/// <summary>Safer deleting in View: files deleted outside Library are marked missing, hidden everywhere, found only by the Missing search, and purged after the setting.</summary>
public sealed class ViewMissingFileRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-missing-files-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly LocalAssetRepository _assets;
    private readonly ViewMissingFileRepository _missing;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly Guid _libraryId = Guid.NewGuid();
    private Guid _profileId;
    private Guid _spaceId;
    private Guid _sourceId;

    public ViewMissingFileRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _assets = new LocalAssetRepository(_database);
        _missing = new ViewMissingFileRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
        CreateOwnership();
    }

    [Fact]
    public async Task AMissingFile_LeavesTheTimelineAndEveryListButIsKeptWithItsData()
    {
        var photo = await AddPhoto("a", "Lake", @"C:\p\lake.jpg", tags: ["holiday"]);
        var other = await AddPhoto("b", "Hill", @"C:\p\hill.jpg");
        Assert.Equal(2, Timeline().Items.Count);

        var changed = await _missing.ApplyAsync([PlaceOf(@"C:\p\lake.jpg")], [], DateTimeOffset.UtcNow);

        Assert.Equal(1, changed);
        Assert.Equal([other.ItemId], Timeline().Items.Select(item => item.Id).ToArray());
        Assert.Equal([other.ItemId], _assets.Query(new LocalAssetQuery(_libraryId)).Items.Select(item => item.Id).ToArray());
        Assert.Single(_assets.QueryTimelineIndex(new LocalAssetTimelineQuery([_libraryId])));
        // Nothing is removed: the record, its tags and its search entry are all still there.
        Assert.NotNull(_assets.Find(photo.ItemId));
        Assert.True(_assets.Find(photo.ItemId)!.FileMissing);
    }

    [Fact]
    public async Task OnlyASearchWithTheMissingFilterOnFindsAMissingItem_ByNameNoteOrTag()
    {
        var photo = await AddPhoto("a", "Sunset at the lake", @"C:\p\lake.jpg", tags: ["holiday"]);
        await _missing.ApplyAsync([PlaceOf(@"C:\p\lake.jpg")], [], DateTimeOffset.UtcNow);

        Assert.Empty(_assets.Query(new LocalAssetQuery(_libraryId, Search: "lake")).Items);
        Assert.Empty(_assets.QueryTimeline(new LocalAssetTimelineQuery([_libraryId], Search: "holiday")).Items);

        var byName = Assert.Single(_assets.Query(new LocalAssetQuery(_libraryId, Search: "lake", IncludeMissing: true)).Items);
        var byTag = Assert.Single(_assets.QueryTimeline(new LocalAssetTimelineQuery([_libraryId], Search: "holiday", IncludeMissing: true)).Items);
        Assert.Equal(photo.ItemId, byName.Id);
        Assert.Equal(photo.ItemId, byTag.Id);
        Assert.True(byName.FileMissing);
        Assert.True(byTag.FileMissing);
    }

    [Fact]
    public async Task AMissingItem_IsNeverFoundInAnotherLibrary_EvenWithTheFilterOn()
    {
        await AddPhoto("a", "Private lake", @"C:\p\lake.jpg");
        await _missing.ApplyAsync([PlaceOf(@"C:\p\lake.jpg")], [], DateTimeOffset.UtcNow);

        var elsewhere = _assets.Query(new LocalAssetQuery(Guid.NewGuid(), Search: "lake", IncludeMissing: true));

        Assert.Empty(elsewhere.Items);
    }

    [Fact]
    public async Task AFileThatComesBack_BringsTheItemBackWithEverything()
    {
        var photo = await AddPhoto("a", "Lake", @"C:\p\lake.jpg", tags: ["holiday"]);
        await _missing.ApplyAsync([PlaceOf(@"C:\p\lake.jpg")], [], DateTimeOffset.UtcNow);

        var changed = await _missing.ApplyAsync([], [PlaceOf(@"C:\p\lake.jpg")], DateTimeOffset.UtcNow);

        Assert.Equal(1, changed);
        var back = Assert.Single(Timeline().Items);
        Assert.Equal(photo.ItemId, back.Id);
        Assert.False(back.FileMissing);
        Assert.Contains("holiday", back.Tags);
    }

    [Fact]
    public async Task AScanThatFindsTheFileAgain_ClearsTheMarkImmediately()
    {
        var photo = await AddPhoto("a", "Lake", @"C:\p\lake.jpg");
        await _missing.ApplyAsync([PlaceOf(@"C:\p\lake.jpg")], [], DateTimeOffset.UtcNow);

        await AddPhoto("a", "Lake", @"C:\p\lake.jpg");

        Assert.False(_assets.Find(photo.ItemId)!.FileMissing);
        Assert.Single(Timeline().Items);
    }

    [Fact]
    public async Task AMovedFile_KeepsItsItemBecauseItsNewPlaceHoldsTheSameContents()
    {
        var photo = await AddPhoto("a", "Lake", @"C:\p\lake.jpg");
        // The scan indexes the new place (same contents), then the sweep marks the old place missing.
        await AddPhoto("a", "Lake", @"C:\p\moved\lake.jpg");

        var changed = await _missing.ApplyAsync([PlaceOf(@"C:\p\lake.jpg")], [], DateTimeOffset.UtcNow);

        Assert.Equal(0, changed);
        Assert.Equal(photo.ItemId, Assert.Single(Timeline().Items).Id);
        Assert.False(_assets.Find(photo.ItemId)!.FileMissing);
    }

    [Fact]
    public async Task ALivePhotoWhoseVideoWasDeleted_StaysNormalWithAPartMissingNote_ButMissingWhenItsPhotoGoes()
    {
        var live = await AddItem("live", "Live", new LocalAssetFileRegistration(@"C:\p\IMG_1.heic", Hash('1'), "IMG_1.heic", "image/heic", 100, DateTimeOffset.UtcNow),
            new LocalAssetFileRegistration(@"C:\p\IMG_1.mov", Hash('2'), "IMG_1.mov", "video/quicktime", 100, DateTimeOffset.UtcNow, LocalAssetFileRoles.LivePhotoVideo));

        await _missing.ApplyAsync([PlaceOf(@"C:\p\IMG_1.mov")], [], DateTimeOffset.UtcNow);
        var partly = Assert.Single(Timeline().Items);
        Assert.False(partly.FileMissing);
        Assert.True(partly.PartMissing);

        await _missing.ApplyAsync([PlaceOf(@"C:\p\IMG_1.heic")], [], DateTimeOffset.UtcNow);
        Assert.Empty(Timeline().Items);
        Assert.True(_assets.Find(live.ItemId)!.FileMissing);
    }

    [Fact]
    public async Task ARawPlusJpegSet_IsMissingOnlyWhenNoViewableFileIsLeft()
    {
        var set = await AddItem("raw", "Set",
            new LocalAssetFileRegistration(@"C:\p\A.jpg", Hash('3'), "A.jpg", "image/jpeg", 100, DateTimeOffset.UtcNow),
            new LocalAssetFileRegistration(@"C:\p\A.cr3", Hash('4'), "A.cr3", "image/x-canon-cr3", 100, DateTimeOffset.UtcNow, LocalAssetFileRoles.Raw),
            new LocalAssetFileRegistration(@"C:\p\A.xmp", Hash('5'), "A.xmp", "application/xml", 100, DateTimeOffset.UtcNow, LocalAssetFileRoles.Sidecar));

        await _missing.ApplyAsync([PlaceOf(@"C:\p\A.jpg"), PlaceOf(@"C:\p\A.xmp")], [], DateTimeOffset.UtcNow);
        Assert.False(_assets.Find(set.ItemId)!.FileMissing);

        await _missing.ApplyAsync([PlaceOf(@"C:\p\A.cr3")], [], DateTimeOffset.UtcNow);
        Assert.True(_assets.Find(set.ItemId)!.FileMissing);
    }

    [Fact]
    public async Task Purge_RemovesOnlyItemsMissingLongerThanTheCutoff_AndTheirData()
    {
        var old = await AddPhoto("a", "Old", @"C:\p\old.jpg", tags: ["gone"]);
        var recent = await AddPhoto("b", "Recent", @"C:\p\recent.jpg");
        var present = await AddPhoto("c", "Present", @"C:\p\present.jpg");
        var now = DateTimeOffset.UtcNow;
        await _missing.ApplyAsync([PlaceOf(@"C:\p\old.jpg")], [], now.AddDays(-100));
        await _missing.ApplyAsync([PlaceOf(@"C:\p\recent.jpg")], [], now.AddDays(-10));
        var cutoff = now.AddDays(-90);

        var candidates = await _missing.GetPurgeCandidatesAsync(cutoff, 50);
        Assert.Equal([old.ItemId], candidates.ToArray());
        Assert.True(await _missing.PurgeItemAsync(old.ItemId, cutoff));

        Assert.Null(_assets.Find(old.ItemId));
        Assert.NotNull(_assets.Find(recent.ItemId));
        Assert.NotNull(_assets.Find(present.ItemId));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_item_tag_links WHERE item_id = @id", new { id = old.ItemId }));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_file_sources WHERE file_path = @p", new { p = @"C:\p\old.jpg" }));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_files WHERE content_hash = @h", new { h = Hash('a') }));
    }

    [Fact]
    public async Task Purge_SkipsAnItemWhoseFolderCouldNotBeReached_AndAnItemThatCameBack()
    {
        var offline = await AddPhoto("a", "Offline", @"C:\p\offline.jpg");
        var back = await AddPhoto("b", "Back", @"C:\p\back.jpg");
        var now = DateTimeOffset.UtcNow;
        await _missing.ApplyAsync([PlaceOf(@"C:\p\offline.jpg"), PlaceOf(@"C:\p\back.jpg")], [], now.AddDays(-200));
        await _missing.SetFolderReachableAsync(_sourceId, false);
        var cutoff = now.AddDays(-90);

        Assert.Empty(await _missing.GetPurgeCandidatesAsync(cutoff, 50));
        Assert.False(await _missing.PurgeItemAsync(offline.ItemId, cutoff));

        await _missing.SetFolderReachableAsync(_sourceId, true);
        await _missing.ApplyAsync([], [PlaceOf(@"C:\p\back.jpg")], now);
        Assert.False(await _missing.PurgeItemAsync(back.ItemId, cutoff));
        Assert.NotNull(_assets.Find(back.ItemId));
        Assert.True(await _missing.PurgeItemAsync(offline.ItemId, cutoff));
    }

    [Fact]
    public async Task TheRetentionClock_IsNotRestartedByARecheck()
    {
        await AddPhoto("a", "Lake", @"C:\p\lake.jpg");
        var first = DateTimeOffset.UtcNow.AddDays(-5);
        await _missing.ApplyAsync([PlaceOf(@"C:\p\lake.jpg")], [], first);

        await _missing.ApplyAsync([PlaceOf(@"C:\p\lake.jpg")], [], DateTimeOffset.UtcNow);

        var since = DateTimeOffset.Parse(Text("SELECT missing_since FROM local_items LIMIT 1"));
        Assert.True(Math.Abs((since - first).TotalSeconds) < 1);
    }

    private LocalAssetTimelinePage Timeline() => _assets.QueryTimeline(new LocalAssetTimelineQuery([_libraryId]));

    private Guid PlaceOf(string path)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<Guid>("SELECT id FROM local_file_sources WHERE file_path = @path;", new { path });
    }

    private Task<LocalAssetUpsertResult> AddPhoto(string hashChar, string title, string path, IReadOnlyCollection<string>? tags = null) =>
        AddItem(hashChar, title, new LocalAssetFileRegistration(path, Hash(hashChar[0]), Path.GetFileName(path.Replace('\\', '/')), "image/jpeg", 100, DateTimeOffset.UtcNow),
            tags: tags);

    private Task<LocalAssetUpsertResult> AddItem(
        string key, string title, params LocalAssetFileRegistration[] files) => AddItem(key, title, files[0], tags: null, more: files.Skip(1).ToArray());

    private Task<LocalAssetUpsertResult> AddItem(
        string key, string title, LocalAssetFileRegistration first, IReadOnlyCollection<string>? tags, LocalAssetFileRegistration[]? more = null)
    {
        _ = key;
        var files = new[] { first }.Concat(more ?? []).Select(file => file with { SourceId = _sourceId }).ToList();
        return _assets.UpsertAsync(new LocalAssetRegistration(
            _libraryId, _spaceId, _profileId, LocalAssetMediaKinds.Image, title,
            new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero), files, Tags: tags));
    }

    private void CreateOwnership()
    {
        _profileId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
                VALUES (@profileId, @name, '#7C4DFF', 'RestrictedProfile', @now);
                """, new { profileId = _profileId, name = $"Profile {_profileId:N}", now = DateTimeOffset.UtcNow });
        }

        var space = _spaces.CreateAsync(_profileId, _libraryId).GetAwaiter().GetResult();
        _spaceId = space.Id;
        _sourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _spaces.UpsertSourceAsync(new ViewSource(
            _sourceId, space.Id, ViewSourceType.Folder, "Photos", "test:photos", null, now, now,
            ViewSourceStorageMode.Linked, ExternalPath: @"C:\p", IncludeSubdirectories: true, Enabled: true)).GetAwaiter().GetResult();
    }

    private long Scalar(string sql, object args)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<long>(sql, args);
    }

    private string Text(string sql)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<string>(sql)!;
    }

    private static string Hash(char character) => new(character, 64);

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (System.IO.File.Exists(_path))
        {
            System.IO.File.Delete(_path);
        }
    }
}
