using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Open Universe" on a tile and the TV Universe button are offered only when the profile can really open that Universe
/// (Universe entry points follow-up): a Kids or private profile never gets a way into something hidden from it.
/// </summary>
public sealed class UniverseOpenMembersTests : IDisposable
{
    private const string UniverseQid = "Q4242";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_universe_open_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _everyone = Guid.NewGuid();
    private readonly Guid _private = Guid.NewGuid();

    public UniverseOpenMembersTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@owner, 'Owner', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP), (@other, 'Other', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP);
            INSERT INTO narrative_roots (qid, label, level, created_at) VALUES ('Q4242', 'Test Universe', 'Universe', '2026-01-01 00:00:00');
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, wikidata_qid, universe_qid, owner_kind, audience)
            VALUES (@everyone, 'Shared Series', 'Series', 'library', 'query', 1, 'Q1', 'Q4242', 'Library', 'Everyone');
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, wikidata_qid, universe_qid, owner_kind, audience, profile_id)
            VALUES (@private, 'Private Series', 'Series', 'user', 'query', 1, 'Q2', 'Q4242', 'Profile', 'Private', @owner);
            """,
            new { owner = _owner, other = _other, everyone = _everyone, @private = _private });
    }

    // The member read looks at collection records only, so the display and connections services are never touched.
    private UniverseClientReadService Service() =>
        new(new CollectionRepository(_db), display: null!, new MediaEngine.Api.Services.Display.DisplayCardBuilder(), connections: null!);

    [Fact]
    public async Task AProfileSeesTheSeriesItMayOpen_AndNotAnotherProfilesPrivateOne()
    {
        var members = await Service().OpenUniverseMemberIdsAsync(UniverseQid, new Profile { Id = _other });

        Assert.NotNull(members);
        Assert.Contains(_everyone, members!);
        Assert.DoesNotContain(_private, members);
    }

    [Fact]
    public async Task AUniverseWhoseOnlySeriesIsPrivateToSomeoneElse_CannotBeOpened()
    {
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE collections SET is_enabled = 0 WHERE id = @everyone;", new { everyone = _everyone });
        }

        Assert.Null(await Service().OpenUniverseMemberIdsAsync(UniverseQid, new Profile { Id = _other }));
        Assert.NotNull(await Service().OpenUniverseMemberIdsAsync(UniverseQid, new Profile { Id = _owner }));
    }

    [Fact]
    public async Task ABadOrUnknownQid_CannotBeOpened()
    {
        Assert.Null(await Service().OpenUniverseMemberIdsAsync("not-a-qid", new Profile { Id = _other }));
        Assert.Null(await Service().OpenUniverseMemberIdsAsync("Q999999", new Profile { Id = _other }));
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { /* temp file; the next run cleans it */ }
    }
}
