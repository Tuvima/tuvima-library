using Dapper;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Wikidata link written from the Match tab, over a real temp data store (universal editor v5, 3c): the link lands on the item's own Work
/// (an episode's own Work, not the show's) as the person's locked decision; setting it queues one refresh only where the pipeline stores the
/// item on its own Work; removing it retires every claim that carried it, remembers the item as turned down and queues nothing.
/// </summary>
public sealed class WikidataIdentityWriterTests : IDisposable
{
    private const string Qid = "Q4242";
    private const string OtherQid = "Q777";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_wikidata_writer_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly PipelineSpy _pipeline = new();
    private readonly WikidataIdentityWriter _writer;

    public WikidataIdentityWriterTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_path);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _writer = new WikidataIdentityWriter(
            new MetadataEditorRepository(_db),
            new MetadataClaimRepository(_db),
            new CanonicalValueRepository(_db),
            new BridgeIdRepository(_db),
            new WorkRepository(_db),
            new CollectionRepository(_db),
            new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance),
            new SystemActivityRepository(_db),
            _pipeline,
            NullLogger<WikidataIdentityWriter>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        using (var pool = new SqliteConnection($"Data Source={_path}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        File.Delete(_path);
    }

    [Fact]
    public async Task LinkingAFilm_StoresThePersonsLockedDecisionOnTheFilm_AndQueuesOneRefreshFromTheLinkedItem()
    {
        var film = await SeedFilmAsync();

        await _writer.SetAsync(film.AssetId, Qid, CancellationToken.None);

        var state = await StateOfAsync(film.WorkId);
        Assert.Equal(Qid, state.wikidata_qid);
        Assert.Equal(WorkWikidataStatus.UserConfirmed, state.wikidata_status);
        Assert.Equal(WorkWikidataMatchSource.User, state.wikidata_match_source);
        Assert.Equal(1L, state.wikidata_match_locked);
        Assert.Equal(Qid, await CanonicalAsync(film.WorkId));
        var job = Assert.Single(_pipeline.Requests);
        Assert.Equal(Qid, job.PreResolvedQid);
        Assert.Equal(film.AssetId, job.EntityId);
        Assert.True(job.IsUserResolution);
    }

    [Fact]
    public async Task LinkingAnEpisode_WritesTheEpisodesOwnWork_NeverTheShow_AndSendsItsQidToNoPipeline()
    {
        var episode = await SeedEpisodeAsync();

        await _writer.SetAsync(episode.AssetId, Qid, CancellationToken.None);

        Assert.Equal(Qid, (await StateOfAsync(episode.WorkId)).wikidata_qid);
        Assert.Equal(WorkWikidataStatus.UserConfirmed, (await StateOfAsync(episode.WorkId)).wikidata_status);
        Assert.Null((await StateOfAsync(episode.ShowId)).wikidata_qid);
        Assert.Null(await CanonicalAsync(episode.ShowId));
        // The episode's pipeline job is parent-scoped: its QID would be stored on the show. Nothing is queued with it.
        Assert.Empty(_pipeline.Requests);
    }

    [Fact]
    public async Task ChangingTheLink_SaysReplaced_AndKeepsItLocked()
    {
        var film = await SeedFilmAsync();
        await _writer.SetAsync(film.AssetId, Qid, CancellationToken.None);

        await _writer.SetAsync(film.AssetId, OtherQid, CancellationToken.None);

        var state = await StateOfAsync(film.WorkId);
        Assert.Equal(OtherQid, state.wikidata_qid);
        Assert.Equal(WorkWikidataStatus.UserReplaced, state.wikidata_status);
        Assert.Equal(1L, state.wikidata_match_locked);
        Assert.Equal(OtherQid, await CanonicalAsync(film.WorkId));
    }

    [Fact]
    public async Task RemovingTheLink_RetiresEveryClaimThatCarriedIt_RemembersTheItemAsTurnedDown_AndQueuesNothing()
    {
        var film = await SeedFilmAsync();
        await _writer.SetAsync(film.AssetId, Qid, CancellationToken.None);
        _pipeline.Requests.Clear();

        await _writer.RemoveAsync(film.AssetId, CancellationToken.None);

        var state = await StateOfAsync(film.WorkId);
        Assert.True(string.IsNullOrEmpty(state.wikidata_qid));
        Assert.Equal(WorkWikidataStatus.UserRejected, state.wikidata_status);
        Assert.Equal(WorkWikidataMatchSource.User, state.wikidata_match_source);
        Assert.Contains(Qid, state.wikidata_rejected_qids_json ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(await CanonicalAsync(film.WorkId));
        using var connection = _db.CreateConnection();
        Assert.Equal(0L, connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @work AND claim_key = @key AND is_current = 1",
            new { work = film.WorkId, key = BridgeIdKeys.WikidataQid }));
        Assert.True(connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @work AND claim_key = @key",
            new { work = film.WorkId, key = BridgeIdKeys.WikidataQid }) >= 1, "the claims stay as history");
        Assert.Empty(_pipeline.Requests);
    }

    // ----- a retail match changes the provider side only ------------------------------------------------------------------------

    [Fact]
    public async Task ARetailMatch_LeavesTheStateAndQidAPersonSetUntouched()
    {
        var film = await SeedFilmAsync();
        await _writer.SetAsync(film.AssetId, Qid, CancellationToken.None);

        await MediaEngine.Api.Endpoints.ItemCanonicalEndpoints.ResetAutomaticWikidataStateAsync(
            new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance), new CollectionRepository(_db), film.WorkId, CancellationToken.None);

        var state = await StateOfAsync(film.WorkId);
        Assert.Equal(Qid, state.wikidata_qid);
        Assert.Equal(WorkWikidataStatus.UserConfirmed, state.wikidata_status);
        Assert.Equal(WorkWikidataMatchSource.User, state.wikidata_match_source);
        Assert.Equal(1L, state.wikidata_match_locked);
    }

    [Fact]
    public async Task ARetailMatch_PutsAnAutomaticLinkBackToPending_SoItIsLookedUpAgain()
    {
        var film = await SeedFilmAsync();
        using (var connection = _db.CreateConnection())
        {
            await connection.ExecuteAsync(
                "UPDATE works SET wikidata_qid = @qid, wikidata_status = @status, wikidata_match_source = @source, wikidata_match_locked = 0 WHERE id = @work",
                new { qid = Qid, status = WorkWikidataStatus.AutoAligned, source = WorkWikidataMatchSource.Retail, work = film.WorkId });
        }

        await MediaEngine.Api.Endpoints.ItemCanonicalEndpoints.ResetAutomaticWikidataStateAsync(
            new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance), new CollectionRepository(_db), film.WorkId, CancellationToken.None);

        var state = await StateOfAsync(film.WorkId);
        Assert.True(string.IsNullOrEmpty(state.wikidata_qid));
        Assert.Equal(WorkWikidataStatus.Pending, state.wikidata_status);
    }

    [Fact]
    public async Task AfterTheLinkWasRemoved_ARetailMatchMayAlignAgain_ButTheRemovedItemStaysTurnedDown()
    {
        var film = await SeedFilmAsync();
        await _writer.SetAsync(film.AssetId, Qid, CancellationToken.None);
        await _writer.RemoveAsync(film.AssetId, CancellationToken.None);

        await MediaEngine.Api.Endpoints.ItemCanonicalEndpoints.ResetAutomaticWikidataStateAsync(
            new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance), new CollectionRepository(_db), film.WorkId, CancellationToken.None);

        var state = await StateOfAsync(film.WorkId);
        Assert.Equal(WorkWikidataStatus.ProviderOnly, state.wikidata_status);
        Assert.Contains(Qid, state.wikidata_rejected_qids_json ?? string.Empty, StringComparison.Ordinal);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private sealed record Seeded(Guid WorkId, Guid AssetId, Guid ShowId);

    private async Task<Seeded> SeedFilmAsync()
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync("""
            INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@work, 'Movies', 'standalone', 'Owned');
            INSERT INTO editions (id, work_id, format_label) VALUES (@edition, @work, 'MKV');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@asset, @edition, @hash, 'C:/library/film.mkv', 'Normal');
            """, new { work, edition, asset, hash = Guid.NewGuid().ToString("N") });
        return new Seeded(work, asset, work);
    }

    private async Task<Seeded> SeedEpisodeAsync()
    {
        var collection = Guid.NewGuid();
        var show = Guid.NewGuid();
        var episode = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync("""
            INSERT INTO collections (id, display_name, collection_type) VALUES (@collection, 'Test Show', 'Universe');
            INSERT INTO works (id, collection_id, media_type, work_kind, ownership) VALUES (@show, @collection, 'TV', 'parent', 'Owned');
            INSERT INTO works (id, collection_id, parent_work_id, media_type, work_kind, ownership) VALUES (@episode, @collection, @show, 'TV', 'child', 'Owned');
            INSERT INTO editions (id, work_id, format_label) VALUES (@edition, @episode, 'MKV');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@asset, @edition, @hash, 'C:/library/episode.mkv', 'Normal');
            """, new { collection, show, episode, edition, asset, hash = Guid.NewGuid().ToString("N") });
        return new Seeded(episode, asset, show);
    }

    private sealed record WorkState(string? wikidata_qid, string? wikidata_status, string? wikidata_match_source, long wikidata_match_locked, string? wikidata_rejected_qids_json);

    private async Task<WorkState> StateOfAsync(Guid work)
    {
        using var connection = _db.CreateConnection();
        return await connection.QuerySingleAsync<WorkState>(
            "SELECT wikidata_qid, wikidata_status, wikidata_match_source, wikidata_match_locked, wikidata_rejected_qids_json FROM works WHERE id = @work",
            new { work });
    }

    private async Task<string?> CanonicalAsync(Guid work) =>
        (await new CanonicalValueRepository(_db).GetByEntityAsync(work))
        .FirstOrDefault(value => value.Key == BridgeIdKeys.WikidataQid)?.Value;

    private sealed class PipelineSpy : IHydrationPipelineService
    {
        public List<HarvestRequest> Requests { get; } = [];

        public ValueTask<Guid> EnqueueAsync(HarvestRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Guid.NewGuid());
        }

        public Task<HydrationResult> RunSynchronousAsync(HarvestRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
