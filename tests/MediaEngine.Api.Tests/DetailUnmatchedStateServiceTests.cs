using Dapper;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The "..." menu's Unmatched entries and the page's Unmatched file list (Library setup spec D34, D40): an administrator sees them for the
/// page's own files, nobody else does, and a Vault file never offers them.
/// </summary>
public sealed class DetailUnmatchedStateServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly DetailActionAuthorizationContext Administrator = new(true, true);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_detail_unmatched_state_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly UnmatchedAssetRepository _unmatched;
    private readonly DetailUnmatchedStateService _service;

    public DetailUnmatchedStateServiceTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _unmatched = new UnmatchedAssetRepository(_database);
        _service = new DetailUnmatchedStateService(_unmatched, new VaultLibraryRepository(_database));
    }

    public void Dispose()
    {
        _database.Dispose();
        DatabaseConnection.ReleasePooledConnections(_databasePath);
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task AnAdministrator_SeesTheUnmatchedFile_AndRetryNowAndKeepAsIs_ForAFileThatIsNotKept()
    {
        var asset = await AddUnmatchedAssetAsync("movies");
        var page = Page();

        await _service.ApplyAsync(page, [asset], Administrator, default);

        var listed = Assert.Single(page.UnmatchedAssets);
        Assert.Equal(asset, listed.AssetId);
        Assert.False(listed.KeptAsIs);
        Assert.Equal(["edit", "unmatched-retry", "unmatched-keep"], page.OverflowActions.Select(action => action.Key).ToArray());
    }

    [Fact]
    public async Task AnAdministrator_IsOfferedOnlyLookUpAgain_WhenEveryUnmatchedFileIsKept()
    {
        var first = await AddUnmatchedAssetAsync("movies");
        var second = await AddUnmatchedAssetAsync("movies");
        await _unmatched.KeepAsIsAsync(first, Now);
        await _unmatched.KeepAsIsAsync(second, Now);
        var page = Page();

        await _service.ApplyAsync(page, [first, second], Administrator, default);

        Assert.Equal(2, page.UnmatchedAssets.Count);
        Assert.All(page.UnmatchedAssets, asset => Assert.True(asset.KeptAsIs));
        Assert.Equal(["edit", "unmatched-look-up-again"], page.OverflowActions.Select(action => action.Key).ToArray());
    }

    [Fact]
    public async Task AnAdministrator_IsOfferedRetryAndKeep_WhenOnlySomeOfThePagesFilesAreKept()
    {
        var kept = await AddUnmatchedAssetAsync("movies");
        var open = await AddUnmatchedAssetAsync("movies");
        await _unmatched.KeepAsIsAsync(kept, Now);
        var page = Page();

        await _service.ApplyAsync(page, [kept, open], Administrator, default);

        Assert.Equal(["edit", "unmatched-retry", "unmatched-keep"], page.OverflowActions.Select(action => action.Key).ToArray());
    }

    [Fact]
    public async Task ANonAdministrator_SeesNoUnmatchedActions_AndTheUnmatchedFileListIsEmpty()
    {
        var asset = await AddUnmatchedAssetAsync("movies");
        var page = Page();

        await _service.ApplyAsync(page, [asset], new DetailActionAuthorizationContext(false), default);

        Assert.Empty(page.UnmatchedAssets);
        Assert.Equal(["edit"], page.OverflowActions.Select(action => action.Key).ToArray());
    }

    [Fact]
    public async Task AServiceApplicationThatMayManageMetadata_SeesNoUnmatchedActions()
    {
        var asset = await AddUnmatchedAssetAsync("movies");
        var page = Page();

        await _service.ApplyAsync(page, [asset], new DetailActionAuthorizationContext(true, false), default);

        Assert.Empty(page.UnmatchedAssets);
        Assert.Equal(["edit"], page.OverflowActions.Select(action => action.Key).ToArray());
    }

    [Fact]
    public async Task AFileInTheVault_NeverOffersTheUnmatchedActions_EvenToAnAdministrator()
    {
        var vaultLibrary = Guid.NewGuid().ToString("D");
        using (var connection = _database.CreateConnection())
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vaultLibrary,'none',@now,@now,@now);",
                new { vaultLibrary, now });
        }

        var asset = await AddUnmatchedAssetAsync(vaultLibrary);
        var page = Page();

        await _service.ApplyAsync(page, [asset], Administrator, default);

        Assert.Empty(page.UnmatchedAssets);
        Assert.Equal(["edit"], page.OverflowActions.Select(action => action.Key).ToArray());
    }

    [Fact]
    public async Task APageWithNoUnmatchedFile_OrNoOwnFiles_IsLeftAsComposed_AndItsPlacementFlagIsUntouched()
    {
        var matched = AddAsset("movies");
        var withoutFiles = Page();
        var withMatchedFile = Page();
        withMatchedFile.IsUnmatched = true;

        await _service.ApplyAsync(withoutFiles, null, Administrator, default);
        await _service.ApplyAsync(withMatchedFile, [matched], Administrator, default);

        Assert.Empty(withoutFiles.UnmatchedAssets);
        Assert.Empty(withMatchedFile.UnmatchedAssets);
        Assert.Equal(["edit"], withMatchedFile.OverflowActions.Select(action => action.Key).ToArray());
        Assert.True(withMatchedFile.IsUnmatched);
    }

    private static DetailPageViewModel Page() => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        OverflowActions = [new DetailAction { Key = "edit", Label = "Edit this movie" }],
    };

    private async Task<Guid> AddUnmatchedAssetAsync(string libraryId)
    {
        var asset = AddAsset(libraryId);
        await _unmatched.MarkUnmatchedAsync(asset, libraryId, UnmatchedReasons.NoMatch, Now, Now.AddDays(7));
        return asset;
    }

    private Guid AddAsset(string libraryId)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
            VALUES (@assetId, @editionId, @contentHash, @path, @libraryId);
            """,
            new { workId, editionId, assetId, contentHash = Guid.NewGuid().ToString("N"), path = $"/media/{assetId:N}.mkv", libraryId });
        return assetId;
    }
}
