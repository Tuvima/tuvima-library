using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
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

    [Fact]
    public async Task FindUniverseByQidAsync_IsNotShadowedBySeriesWithTheSameQid_AndReturnsSharedProfiles()
    {
        var universe = Guid.NewGuid();
        var shadow = Guid.NewGuid();
        var child = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            // The Series is created first and inserted first, so a plain "first row with this QID" would return it.
            connection.Execute(
                """
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, wikidata_qid, created_at)
                VALUES (@shadow, 'Shadow series', 'Series', 'library', 'query', 'Q777', '2000-01-01 00:00:00');
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, wikidata_qid, created_at)
                VALUES (@universe, 'Real universe', 'Universe', 'library', 'materialized', 'Q777', '2001-01-01 00:00:00');
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, owner_kind, audience, profile_id, parent_collection_id)
                VALUES (@child, 'Shared child', 'Series', 'user', 'query', 'Smart', 'Profile', 'SelectedProfiles', @owner, @universe);
                INSERT INTO collection_profile_audience (collection_id, profile_id) VALUES (@child, @guest);
                """,
                new { shadow, universe, child, owner = Owner, guest = Guest });
        }

        var repository = new CollectionRepository(_db);

        var found = await repository.FindUniverseByQidAsync("Q777");
        var children = await repository.GetChildCollectionsAsync(universe);

        Assert.Equal(universe, found!.Id);
        var member = Assert.Single(children);
        Assert.Equal(ContainerAudience.SelectedProfiles, member.Audience);
        Assert.Equal([Guest], member.AudienceProfileIds);
        // The general lookup still answers for any type, so its other callers are unchanged.
        Assert.NotNull(await repository.FindByQidAsync("Q777"));
        Assert.Null(await repository.FindUniverseByQidAsync("Q778"));
        // A Series on its own never counts as a Universe.
        using var second = _db.CreateConnection();
        second.Execute(
            "INSERT INTO collections (id, display_name, collection_type, scope, resolution, wikidata_qid) VALUES (@id, 'Lone series', 'Series', 'library', 'query', 'Q779');",
            new { id = Guid.NewGuid() });
        Assert.Null(await repository.FindUniverseByQidAsync("Q779"));
    }

    [Fact]
    public async Task ReadingAnUnknownStoredOwnerOrAudience_FailsFastInsteadOfFallingBackToShared()
    {
        using (var connection = _db.CreateConnection())
        {
            connection.Execute("PRAGMA ignore_check_constraints = ON;");
            connection.Execute("UPDATE collections SET audience = 'Everybody' WHERE id = @id;", new { id = _privateList });
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionRepository(_db).GetAllAsync());
    }

    [Theory]
    [InlineData("Library", ContainerOwnerKind.Library)]
    [InlineData("profile", ContainerOwnerKind.Profile)]
    public void StoredOwnerKind_ParsesTheKnownNames(string stored, ContainerOwnerKind expected) =>
        Assert.Equal(expected, AggregateStateSerializer.ParseStoredContainerOwnerKind(stored));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("Owner")]
    public void StoredOwnerKind_RejectsMissingUnknownAndNumericValues(string? stored) =>
        Assert.Throws<InvalidOperationException>(() => AggregateStateSerializer.ParseStoredContainerOwnerKind(stored));

    [Theory]
    [InlineData("Everyone", ContainerAudience.Everyone)]
    [InlineData("selectedprofiles", ContainerAudience.SelectedProfiles)]
    [InlineData("Private", ContainerAudience.Private)]
    public void StoredAudience_ParsesTheKnownNames(string stored, ContainerAudience expected) =>
        Assert.Equal(expected, AggregateStateSerializer.ParseStoredContainerAudience(stored));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("Everybody")]
    public void StoredAudience_RejectsMissingUnknownAndNumericValues(string? stored) =>
        Assert.Throws<InvalidOperationException>(() => AggregateStateSerializer.ParseStoredContainerAudience(stored));

    public void Dispose()
    {
        try { _db.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }
}
