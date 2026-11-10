using Dapper;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

// View folders in libraries WP3b: the optional folder (source) filter on the View list, its timeline index and the added-by counts.
public sealed class ViewSourceFilterRepositoryTests : IDisposable
{
    private static readonly Guid TestHousehold = new("7e57a000-0000-0000-0000-000000000004");

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-view-source-filter-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly LocalAssetRepository _assets;

    public ViewSourceFilterRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _spaces = new ViewPersonalSpaceRepository(_database);
        _assets = new LocalAssetRepository(_database);
    }

    [Fact]
    public async Task TheListReturnsOnlyTheItemsOfTheChosenFolder_AndNoFilterReturnsEverything()
    {
        var (owner, pictures, scans) = await TwoFoldersAsync();
        var inPictures = await AddInSourceAsync(owner, pictures.Id, 'a');
        var alsoInPictures = await AddInSourceAsync(owner, pictures.Id, 'b');
        var inScans = await AddInSourceAsync(owner, scans.Id, 'c');

        Assert.Equal(3, _assets.QueryTimeline(Query(owner)).Items.Count);
        Assert.Equal(
            new[] { inPictures, alsoInPictures }.Order().ToArray(),
            _assets.QueryTimeline(Query(owner, [pictures.Id])).Items.Select(item => item.Id).Order().ToArray());
        Assert.Equal([inScans], _assets.QueryTimeline(Query(owner, [scans.Id])).Items.Select(item => item.Id).ToArray());
        Assert.Equal(3, _assets.QueryTimeline(Query(owner, [pictures.Id, scans.Id])).Items.Count);
    }

    [Fact]
    public async Task AFolderFilterThatNamesOnlyUnknownFoldersMatchesNothing_AndAnEmptyListFiltersNothing()
    {
        var (owner, pictures, _) = await TwoFoldersAsync();
        await AddInSourceAsync(owner, pictures.Id, 'a');

        Assert.Empty(_assets.QueryTimeline(Query(owner, [Guid.NewGuid()])).Items);
        Assert.Empty(_assets.QueryTimeline(Query(owner, [Guid.Empty])).Items);
        Assert.Empty(_assets.QueryTimelineIndex(Query(owner, [Guid.NewGuid()])));
        Assert.Single(_assets.QueryTimeline(Query(owner, null)).Items);
        Assert.Single(_assets.QueryTimeline(Query(owner, [])).Items);
    }

    [Fact]
    public async Task TheTimelineIndexAndTheAddedByCountsFollowTheSameFolderFilter()
    {
        var (owner, pictures, scans) = await TwoFoldersAsync();
        await AddInSourceAsync(owner, pictures.Id, 'a');
        await AddInSourceAsync(owner, pictures.Id, 'b');
        await AddInSourceAsync(owner, scans.Id, 'c');

        Assert.Equal(3, _assets.QueryTimelineIndex(Query(owner)).Sum(bucket => bucket.AssetCount));
        Assert.Equal(2, _assets.QueryTimelineIndex(Query(owner, [pictures.Id])).Sum(bucket => bucket.AssetCount));
        Assert.Equal(1, _assets.QueryTimelineIndex(Query(owner, [scans.Id])).Sum(bucket => bucket.AssetCount));

        Assert.Equal(3, _assets.QueryAddedBy(Query(owner)).Sum(count => count.ItemCount));
        Assert.Equal(2, _assets.QueryAddedBy(Query(owner, [pictures.Id])).Sum(count => count.ItemCount));
        Assert.Empty(_assets.QueryAddedBy(Query(owner, [Guid.NewGuid()])));
    }

    [Fact]
    public async Task AFolderFilterNeverReachesAnotherLibrarysItemsEvenWithTheirFolderId()
    {
        var (owner, pictures, _) = await TwoFoldersAsync();
        await AddInSourceAsync(owner, pictures.Id, 'a');
        var other = await CreateOwnerAsync("Other");

        Assert.Empty(_assets.QueryTimeline(Query(other, [pictures.Id])).Items);
    }

    private static LocalAssetTimelineQuery Query(
        (Guid ProfileId, Guid SpaceId, Guid LibraryId) owner, IReadOnlyCollection<Guid>? sources = null) =>
        new([owner.LibraryId], SourceIds: sources);

    private async Task<((Guid ProfileId, Guid SpaceId, Guid LibraryId) Owner, ViewSource Pictures, ViewSource Scans)> TwoFoldersAsync()
    {
        var owner = await CreateOwnerAsync("Owner");
        return (owner, await AddFolderAsync(owner, "Pictures"), await AddFolderAsync(owner, "Scans"));
    }

    private async Task<ViewSource> AddFolderAsync((Guid ProfileId, Guid SpaceId, Guid LibraryId) owner, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return await _spaces.UpsertSourceAsync(new ViewSource(
            Guid.NewGuid(), owner.SpaceId, ViewSourceType.Folder, name, $"test:{name}", null, now, now,
            ViewSourceStorageMode.Linked, ExternalPath: $@"D:\{name}", ContentKind: ViewContentKinds.Photos));
    }

    private async Task<Guid> AddInSourceAsync((Guid ProfileId, Guid SpaceId, Guid LibraryId) owner, Guid sourceId, char hash) =>
        (await _assets.UpsertAsync(new LocalAssetRegistration(
            owner.LibraryId, owner.SpaceId, owner.ProfileId, LocalAssetMediaKinds.Image, $"Item {hash}", DateTimeOffset.UtcNow,
            [new LocalAssetFileRegistration($@"C:\view\{owner.ProfileId:N}\{hash}.bin", new string(hash, 64), $"{hash}.bin",
                "application/octet-stream", 1024, DateTimeOffset.UtcNow) with { SourceId = sourceId }],
            ContentKind: ViewContentKinds.Photos, AddedByProfileId: owner.ProfileId))).ItemId;

    private async Task<(Guid ProfileId, Guid SpaceId, Guid LibraryId)> CreateOwnerAsync(string name)
    {
        var profileId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT OR IGNORE INTO households (id, name, created_at) VALUES (@household, 'Test household', @now);
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at, household_id)
                VALUES (@id, @name, '#7C4DFF', 'StandardUser', @now, @household);
                """, new { id = profileId, name, now = DateTimeOffset.UtcNow, household = TestHousehold });
        }

        var libraryId = Guid.NewGuid();
        var space = await _spaces.CreateAsync(profileId, libraryId);
        return (profileId, space.Id, libraryId);
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
