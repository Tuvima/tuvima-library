using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Who owns a collection and who it is shared with decide who may see it. Every read that lists collections with
/// their works must bring those fields back, never the library-wide defaults.
/// </summary>
public sealed class CollectionRepositoryOwnershipTests : IDisposable
{
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Guest = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_collection_owner_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly Guid _privateList = Guid.NewGuid();
    private readonly Guid _sharedList = Guid.NewGuid();
    private readonly Guid _libraryList = Guid.NewGuid();
    private readonly Guid _series = Guid.NewGuid();

    public CollectionRepositoryOwnershipTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        using var connection = _db.CreateConnection();
        connection.Execute(
            """
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@owner, 'Owner', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP),
                   (@guest, 'Guest', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP);
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, primary_area, owner_kind, audience, profile_id)
            VALUES (@privateList, 'Private', 'Custom', 'user', 'materialized', 'Manual', 'Watch', 'Profile', 'Private', @owner),
                   (@sharedList, 'Shared', 'Custom', 'user', 'materialized', 'Manual', 'Read', 'Profile', 'SelectedProfiles', @owner),
                   (@libraryList, 'Library', 'Custom', 'library', 'materialized', 'Smart', 'Mixed', 'Library', 'Everyone', NULL),
                   (@series, 'Series', 'Series', 'library', 'query', 'Smart', 'Mixed', 'Library', 'Everyone', NULL);
            INSERT INTO collection_profile_audience (collection_id, profile_id) VALUES (@sharedList, @guest);
            INSERT INTO works (id, media_type, work_kind, curator_state, collection_id)
            VALUES (@work, 'Movies', 'standalone', 'accepted', @series);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
            VALUES (@asset, @edition, 'hash-ownership', 'C:/library/ownership.mkv', CURRENT_TIMESTAMP);
            """,
            new
            {
                owner = Owner,
                guest = Guest,
                privateList = _privateList,
                sharedList = _sharedList,
                libraryList = _libraryList,
                series = _series,
                work = Guid.NewGuid(),
                edition = Guid.NewGuid(),
                asset = Guid.NewGuid(),
            });
    }

    [Fact]
    public async Task GetAllAsync_ReturnsOwnershipMembershipAudienceAndSharedProfiles()
    {
        var all = (await new CollectionRepository(_db).GetAllAsync()).ToDictionary(collection => collection.Id);

        var priv = all[_privateList];
        Assert.Equal(ContainerOwnerKind.Profile, priv.OwnerKind);
        Assert.Equal(ContainerAudience.Private, priv.Audience);
        Assert.Equal(ContainerMembershipMode.Manual, priv.MembershipMode);
        Assert.Equal(CollectionPrimaryArea.Watch, priv.PrimaryArea);
        Assert.Equal(Owner, priv.ProfileId);
        Assert.Empty(priv.AudienceProfileIds);

        var shared = all[_sharedList];
        Assert.Equal(ContainerAudience.SelectedProfiles, shared.Audience);
        Assert.Equal([Guest], shared.AudienceProfileIds);

        var library = all[_libraryList];
        Assert.Equal(ContainerOwnerKind.Library, library.OwnerKind);
        Assert.Equal(ContainerAudience.Everyone, library.Audience);
        Assert.Equal(ContainerMembershipMode.Smart, library.MembershipMode);
    }

    [Fact]
    public async Task ContentGroupAndSingleCollectionReads_ReturnTheSameOwnershipFields()
    {
        var repository = new CollectionRepository(_db);

        var groups = await repository.GetContentGroupsAsync();
        var withWorks = await repository.GetCollectionWithWorksAsync(_series);
        var privateList = await repository.GetCollectionWithWorksAsync(_privateList);
        var shared = await repository.GetCollectionWithWorksAsync(_sharedList);

        Assert.Equal(ContainerOwnerKind.Library, Assert.Single(groups).OwnerKind);
        Assert.Equal(ContainerAudience.Everyone, withWorks!.Audience);
        Assert.Equal(ContainerOwnerKind.Profile, privateList!.OwnerKind);
        Assert.Equal(ContainerAudience.Private, privateList.Audience);
        Assert.Equal([Guest], shared!.AudienceProfileIds);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }
}
