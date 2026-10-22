using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The Vault's images and galleries (Videos spec D44). They are View-style items in a library of their own, so the tests prove the two
/// worlds stay apart in every direction (queries, smart rules, galleries, tags, search and the data structure's own triggers) and that
/// Vault galleries, tags and the recycle bin link work. Real temp SQLite.
/// </summary>
public sealed class VaultImageStorageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-vault-images-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly LocalAssetRepository _assets;
    private readonly VaultImageRepository _images;
    private readonly VaultGalleryRepository _galleries;
    private readonly VaultLibraryRepository _vaultLibraries;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _vaultLibraryId = Guid.NewGuid();
    private Guid _imageLibrary;

    public VaultImageStorageTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _assets = new LocalAssetRepository(_database);
        _images = new VaultImageRepository(_database);
        _galleries = new VaultGalleryRepository(_database);
        _vaultLibraries = new VaultLibraryRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
        _vaultLibraries.AddAsync(new VaultLibrary(_vaultLibraryId.ToString("D"), VaultLookupSources.None, null, _now, null, _now, _now))
            .GetAwaiter().GetResult();
        _imageLibrary = _images.EnsureLibraryAsync(_vaultLibraryId.ToString("D")).GetAwaiter().GetResult()!.LibraryId;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_path);
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    // ── Isolation: a query decides, and the data structure backs it up ─────────────────────────────

    [Fact]
    public async Task AQueryWithoutTheVaultScope_NeverReturnsAVaultImage_EvenWhenHandedTheVaultLibrary()
    {
        var vault = await AddVaultImage("vault-a.jpg", 'a');
        var view = await AddViewImage("view-a.jpg", 'b');

        var page = _assets.QueryTimeline(new LocalAssetTimelineQuery([_imageLibrary, view.LibraryId], IncludeHidden: true));
        var all = _assets.Query(new LocalAssetQuery(_imageLibrary, IncludeHidden: true));

        Assert.DoesNotContain(page.Items, item => item.Id == vault);
        Assert.DoesNotContain(all.Items, item => item.Id == vault);
        Assert.Equal(view.ItemId, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task AQueryWithTheVaultScope_ReturnsOnlyVaultImages_EvenWhenHandedAViewLibrary()
    {
        var vault = await AddVaultImage("vault-a.jpg", 'a');
        var view = await AddViewImage("view-a.jpg", 'b');

        var page = _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [_imageLibrary, view.LibraryId], IncludeHidden: true, Vault: new LocalAssetVaultScope()));

        Assert.Equal(vault, Assert.Single(page.Items).Id);
        Assert.DoesNotContain(page.Items, item => item.Id == view.ItemId);
    }

    [Fact]
    public async Task ASmartRule_RunInTheVaultScope_FindsOnlyVaultImages_AndTheSameRuleInViewFindsNone()
    {
        var vault = await AddVaultImage("vault-a.jpg", 'a');
        var view = await AddViewImage("view-a.jpg", 'b');
        var rule = CollectionRuleDefinition.SingleGroup([new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "image" }]);

        var inVault = _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [_imageLibrary, view.LibraryId], IncludeHidden: true, SmartRule: rule, Vault: new LocalAssetVaultScope()));
        var inView = _assets.QueryTimeline(new LocalAssetTimelineQuery([_imageLibrary, view.LibraryId], IncludeHidden: true, SmartRule: rule));

        Assert.Equal(vault, Assert.Single(inVault.Items).Id);
        Assert.Equal(view.ItemId, Assert.Single(inView.Items).Id);
    }

    [Fact]
    public async Task AVaultRule_UsesTheVaultsOwnTags_AndAViewRuleNeverSeesThem()
    {
        var vault = await AddVaultImage("vault-a.jpg", 'a');
        await _images.ReplaceTagsAsync(vault, ["Harbour"]);
        var view = await AddViewImage("view-a.jpg", 'b', tags: ["Harbour"]);
        var rule = CollectionRuleDefinition.SingleGroup([new CollectionRulePredicate { Field = "tags", Op = "eq", Value = "harbour" }]);

        var inVault = _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [_imageLibrary, view.LibraryId], IncludeHidden: true, SmartRule: rule, Vault: new LocalAssetVaultScope()));
        var inView = _assets.QueryTimeline(new LocalAssetTimelineQuery([_imageLibrary, view.LibraryId], IncludeHidden: true, SmartRule: rule));

        Assert.Equal(vault, Assert.Single(inVault.Items).Id);
        Assert.Equal(view.ItemId, Assert.Single(inView.Items).Id);
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM local_item_tag_links WHERE item_id = @id;", new { id = vault }));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM tags WHERE name = 'Harbour' COLLATE NOCASE AND id IN (SELECT tag_id FROM vault_image_tags);"));
    }

    [Theory]
    [InlineData("people")]
    [InlineData("device")]
    [InlineData("source")]
    [InlineData("owner")]
    [InlineData("document_type")]
    [InlineData("text_contains")]
    public void AVaultRule_ThatReachesIntoViewsFields_IsRefused(string field)
    {
        var rule = CollectionRuleDefinition.SingleGroup([new CollectionRulePredicate { Field = field, Op = "eq", Value = "x" }]);

        Assert.Throws<ArgumentException>(() => ViewSmartGalleryRules.ValidateForVault(rule));
        Assert.ThrowsAny<Exception>(() => LocalAssetSmartRuleSqlCompiler.CompileForVault(rule));
    }

    [Fact]
    public async Task ASearchInTheVaultScope_FindsVaultImagesByNameAndTag_AndNeverViewImages()
    {
        var vault = await AddVaultImage("lighthouse-dawn.jpg", 'a');
        await _images.ReplaceTagsAsync(vault, ["Coast"]);
        var view = await AddViewImage("lighthouse-view.jpg", 'b');
        var scope = new LocalAssetVaultScope();

        var byName = _assets.QueryTimeline(new LocalAssetTimelineQuery([_imageLibrary, view.LibraryId], Search: "lighthouse", IncludeHidden: true, Vault: scope));
        var byTag = _assets.QueryTimeline(new LocalAssetTimelineQuery([_imageLibrary], Search: "coast", IncludeHidden: true, Vault: scope));
        var viewSearch = _assets.QueryTimeline(new LocalAssetTimelineQuery([_imageLibrary, view.LibraryId], Search: "lighthouse", IncludeHidden: true));

        Assert.Equal(vault, Assert.Single(byName.Items).Id);
        Assert.Equal(vault, Assert.Single(byTag.Items).Id);
        Assert.Equal(view.ItemId, Assert.Single(viewSearch.Items).Id);
    }

    [Fact]
    public async Task TheDataStructure_RefusesToMixTheTwoWorlds()
    {
        var vault = await AddVaultImage("vault-a.jpg", 'a');
        var view = await AddViewImage("view-a.jpg", 'b', tags: ["Shared"]);
        var viewGallery = await new ViewGalleryRepository(_database).CreateAsync(
            new CreateViewGalleryCommand(view.ProfileId, view.SpaceId, "Holidays", ViewGalleryKind.Manual));
        using var connection = _database.CreateConnection();

        // A Vault image can't be put in a View gallery, tagged with a shared tag, or used as a View gallery's cover.
        Assert.ThrowsAny<Exception>(() => connection.Execute(
            "INSERT INTO view_gallery_items(gallery_id,item_id,position,added_at) VALUES (@g,@i,0,@now);",
            new { g = GuidSql.ToBlob(viewGallery.Id), i = GuidSql.ToBlob(vault), now = _now.ToString("O") }));
        Assert.True(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM tags;") > 0);
        Assert.ThrowsAny<Exception>(() => connection.Execute(
            "INSERT INTO local_item_tag_links(item_id,tag_id,added_at) SELECT @i, id, @now FROM tags LIMIT 1;",
            new { i = GuidSql.ToBlob(vault), now = _now.ToString("O") }));
        Assert.ThrowsAny<Exception>(() => connection.Execute(
            "UPDATE view_galleries SET cover_item_id = @i WHERE id = @g;",
            new { g = GuidSql.ToBlob(viewGallery.Id), i = GuidSql.ToBlob(vault) }));
        // A View library can't become a Vault image library, and the other way round.
        Assert.ThrowsAny<Exception>(() => connection.Execute(
            "INSERT INTO vault_image_libraries(library_id,vault_library_id,created_at) VALUES (@l,@v,@now);",
            new { l = GuidSql.ToBlob(view.LibraryId), v = _vaultLibraryId.ToString("D"), now = _now.ToString("O") }));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM vault_image_items WHERE item_id = @i;", new { i = GuidSql.ToBlob(vault) }));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM vault_image_items WHERE item_id = @i;", new { i = GuidSql.ToBlob(view.ItemId) }));
    }

    // ── Galleries ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFolderOfImages_IsAGalleryOfItsOwn_ThatHoldsOnlyItsImages()
    {
        var first = await AddVaultImage("one.jpg", 'a');
        var second = await AddVaultImage("two.jpg", 'b');
        var other = await AddVaultImage("three.jpg", 'c');
        var folder = await _images.EnsureFolderAsync(_imageLibrary, "folder-key-1", "Trips");
        var elsewhere = await _images.EnsureFolderAsync(_imageLibrary, "folder-key-2", "Pets");
        await _images.AssignFolderAsync([first, second], folder);
        await _images.AssignFolderAsync([other], elsewhere);

        var listed = await _images.ListFoldersAsync();
        var page = _assets.QueryTimeline(new LocalAssetTimelineQuery([_imageLibrary], IncludeHidden: true, Vault: new LocalAssetVaultScope(FolderId: folder)));

        Assert.Equal(["Pets", "Trips"], listed.Select(f => f.Name).ToArray());
        Assert.Equal(2, listed.Single(f => f.Name == "Trips").ImageCount);
        Assert.Equal(new[] { first, second }.Order().ToArray(), page.Items.Select(i => i.Id).Order().ToArray());
        Assert.DoesNotContain(page.Items, i => i.Id == other);
    }

    [Fact]
    public async Task AHandMadeGallery_HoldsVaultImagesOfItsLibrary_AndListsOnlyThem()
    {
        var kept = await AddVaultImage("one.jpg", 'a');
        var left = await AddVaultImage("two.jpg", 'b');
        var view = await AddViewImage("view-a.jpg", 'c');
        var gallery = await _galleries.CreateAsync(_imageLibrary, "Favourites", null, VaultGalleryKind.Manual, null, null);

        var added = await _galleries.AddItemsAsync(gallery.Id, [kept, view.ItemId]);
        var page = _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [_imageLibrary, view.LibraryId], IncludeHidden: true, Vault: new LocalAssetVaultScope(GalleryId: gallery.Id)));

        Assert.Equal(1, added);
        Assert.Equal(kept, Assert.Single(page.Items).Id);
        Assert.DoesNotContain(page.Items, i => i.Id == left || i.Id == view.ItemId);
        Assert.Equal(1, await _galleries.CountManualItemsAsync(gallery.Id));
        Assert.Equal(1, await _galleries.RemoveItemsAsync(gallery.Id, [kept]));
    }

    [Fact]
    public async Task ASmartGallery_TakesNoHandPickedImages_AndNeedsARule()
    {
        var image = await AddVaultImage("one.jpg", 'a');
        var rule = ViewSmartGalleryRules.SerializeForVault(
            CollectionRuleDefinition.SingleGroup([new CollectionRulePredicate { Field = "favorite", Op = "eq", Value = "true" }]));
        var smart = await _galleries.CreateAsync(_imageLibrary, "Starred", null, VaultGalleryKind.Smart, rule, null);

        Assert.Equal(0, await _galleries.AddItemsAsync(smart.Id, [image]));
        await Assert.ThrowsAnyAsync<Exception>(() => _galleries.CreateAsync(_imageLibrary, "No rule", null, VaultGalleryKind.Smart, null, null));
        await Assert.ThrowsAnyAsync<Exception>(() => _galleries.CreateAsync(_imageLibrary, "  ", null, VaultGalleryKind.Manual, null, null));
    }

    [Fact]
    public async Task DeletingAGallery_KeepsItsImages()
    {
        var image = await AddVaultImage("one.jpg", 'a');
        var gallery = await _galleries.CreateAsync(_imageLibrary, "Temp", null, VaultGalleryKind.Manual, null, null);
        await _galleries.AddItemsAsync(gallery.Id, [image]);

        Assert.True(await _galleries.DeleteAsync(gallery.Id));

        Assert.Null(await _galleries.GetAsync(gallery.Id));
        Assert.Equal(image, Assert.Single(_assets.QueryTimeline(new LocalAssetTimelineQuery([_imageLibrary], IncludeHidden: true, Vault: new LocalAssetVaultScope())).Items).Id);
    }

    // ── Tags and the recycle bin link ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task VaultTags_AreTheVaultsOwn_ReplaceByName_AndCountOnlyActiveImages()
    {
        var one = await AddVaultImage("one.jpg", 'a');
        var two = await AddVaultImage("two.jpg", 'b');

        Assert.True(await _images.ReplaceTagsAsync(one, ["Sea", " sea ", "Dunes"]));
        Assert.True(await _images.ReplaceTagsAsync(two, ["sea"]));
        var listed = await _images.ListTagsAsync();

        Assert.Equal(["Sea", "Dunes"], listed.OrderByDescending(t => t.ImageCount).Select(t => t.Name).ToArray());
        Assert.Equal(2, listed.Single(t => t.Name == "Sea").ImageCount);
        Assert.False(await _images.ReplaceTagsAsync(Guid.NewGuid(), ["Nope"]));

        await _images.ReplaceTagsAsync(two, []);
        Assert.Equal(1, (await _images.ListTagsAsync()).Single(t => t.Name == "Sea").ImageCount);
    }

    [Fact]
    public async Task ADeletedImage_LeavesEveryListButStaysUntilTheBinIsEmptied()
    {
        var gone = await AddVaultImage("gone.jpg", 'a');
        var kept = await AddVaultImage("kept.jpg", 'b');
        var entry = Guid.NewGuid();

        Assert.True(await _images.SetRecycleEntriesAsync(gone, [entry]));
        await _assets.SetLifecycleStateAsync(gone, LocalAssetLifecycleState.Trashed);

        var active = _assets.QueryTimeline(new LocalAssetTimelineQuery([_imageLibrary], IncludeHidden: true, Vault: new LocalAssetVaultScope()));
        var deleted = Assert.Single(await _images.ListDeletedAsync());

        Assert.Equal(kept, Assert.Single(active.Items).Id);
        Assert.Equal(gone, deleted.ItemId);
        Assert.Equal([entry], deleted.RecycleEntryIds);

        Assert.Equal(1, await _images.RemoveItemsAsync([gone]));
        Assert.Empty(await _images.ListDeletedAsync());
        Assert.Null(_assets.Find(gone));
    }

    [Fact]
    public async Task AVaultLibraryWithImages_CannotBeRemovedFromUnderThem()
    {
        await AddVaultImage("one.jpg", 'a');
        using var connection = _database.CreateConnection();

        Assert.ThrowsAny<Exception>(() => connection.Execute("DELETE FROM vault_libraries WHERE library_id = @id;", new { id = _vaultLibraryId.ToString("D") }));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> AddVaultImage(string fileName, char hash)
    {
        var result = await _assets.UpsertAsync(new LocalAssetRegistration(
            _imageLibrary, null, null, LocalAssetMediaKinds.Image, Path.GetFileNameWithoutExtension(fileName),
            new DateTimeOffset(2026, 8, 19, 19, 32, 0, TimeSpan.Zero),
            [new LocalAssetFileRegistration($"/vault/{fileName}", new string(hash, 64), fileName, "image/jpeg", 2048, _now)],
            Width: 1600, Height: 900, ScopeKind: LocalAssetScopeKinds.Vault));
        return result.ItemId;
    }

    private async Task<(Guid ItemId, Guid LibraryId, Guid ProfileId, Guid SpaceId)> AddViewImage(string fileName, char hash, string[]? tags = null)
    {
        var libraryId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO profiles (id, display_name, avatar_color, role, created_at) VALUES (@profileId, @name, '#7C4DFF', 'RestrictedProfile', @now);",
                new { profileId, name = $"Profile {profileId:N}", now = DateTimeOffset.UtcNow });
        }

        var space = await _spaces.CreateAsync(profileId, libraryId);
        var result = await _assets.UpsertAsync(new LocalAssetRegistration(
            libraryId, space.Id, profileId, LocalAssetMediaKinds.Image, Path.GetFileNameWithoutExtension(fileName),
            new DateTimeOffset(2026, 8, 19, 19, 32, 0, TimeSpan.Zero),
            [new LocalAssetFileRegistration($"/view/{fileName}", new string(hash, 64), fileName, "image/jpeg", 2048, _now)],
            Width: 1600, Height: 900, Tags: tags));
        return (result.ItemId, libraryId, profileId, space.Id);
    }
}
