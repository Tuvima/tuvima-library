using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Remove match" saved through the edit session over a real temp data store (universal editor v5, Match tab, A9): the provider ids stop being
/// the item's, claims are retired rather than deleted, the details and the Wikidata id stay, the decision is pinned, and history says what was removed.
/// </summary>
public sealed class MatchRemovalEditSessionTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly LibraryMoveTestWorld _world = new(withFiles: false);

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task Save_TakesTheProviderIdsAway_ButKeepsDetailsAndTheWikidataId()
    {
        var (work, asset) = await MatchedMovieAsync();

        var result = await Service().RunAsync(Request(previewOnly: false, work), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.Ok, Assert.Single(result.Changes).Status);
        var kept = Canonicals(work);
        Assert.DoesNotContain(BridgeIdKeys.TmdbId, kept.Keys);
        Assert.DoesNotContain(BridgeIdKeys.ImdbId, kept.Keys);
        Assert.Equal("Q1000", kept[BridgeIdKeys.WikidataQid]);
        Assert.Equal("The Matrix", kept[MetadataFieldConstants.Title]);
        Assert.DoesNotContain(BridgeIdKeys.TmdbId, Canonicals(asset).Keys);
        var bridges = await new BridgeIdRepository(_world.Database).GetByEntityAsync(work);
        Assert.Equal([BridgeIdKeys.WikidataQid], bridges.Select(b => b.IdType).ToArray());
    }

    [Fact]
    public async Task Save_RetiresTheClaimsInsteadOfDeletingThem()
    {
        var (work, _) = await MatchedMovieAsync();

        await Service().RunAsync(Request(previewOnly: false, work), NoRefusals);

        using var connection = _world.Database.CreateConnection();
        var rows = connection.Query<(long Current, string Key)>(
            "SELECT is_current AS Current, claim_key AS Key FROM metadata_claims WHERE entity_id = @work AND claim_key = 'tmdb_id';",
            new { work }).ToList();
        Assert.Single(rows);
        Assert.Equal(0, rows[0].Current);
        var current = await new MetadataClaimRepository(_world.Database).GetByEntityAsync(work);
        Assert.DoesNotContain(current, claim => claim.ClaimKey == BridgeIdKeys.TmdbId);
    }

    [Fact]
    public async Task Save_PinsTheDecision_SoAScanDoesNotMatchTheSameRecordAgain()
    {
        var (work, _) = await MatchedMovieAsync();

        await Service().RunAsync(Request(previewOnly: false, work), NoRefusals);

        Assert.Equal("true", Canonicals(work)[MetadataFieldConstants.MatchPinned]);
    }

    [Fact]
    public async Task Save_WritesOneHistoryLine_NamingWhatWasRemoved()
    {
        var (work, _) = await MatchedMovieAsync();

        await Service().RunAsync(Request(previewOnly: false, work), NoRefusals);

        var recent = await new SystemActivityRepository(_world.Database).GetRecentAsync(20);
        var line = Assert.Single(recent, entry => entry.EntityId == work && entry.Detail!.StartsWith("Match removed", StringComparison.Ordinal));
        Assert.Equal("Match removed (was TMDB · 603).", line.Detail);
    }

    [Fact]
    public async Task Save_DoesNotSendTheItemToNeedsReview()
    {
        var (work, _) = await MatchedMovieAsync();

        await Service().RunAsync(Request(previewOnly: false, work), NoRefusals);

        using var connection = _world.Database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM review_queue WHERE entity_id = @work;", new { work }));
    }

    [Fact]
    public async Task Check_ForAnItemWithNoMatch_SaysThereIsNothingToRemove()
    {
        var (work, _) = await MatchedMovieAsync(withMatch: false);

        var result = await Service().RunAsync(Request(previewOnly: true, work), NoRefusals);

        var change = Assert.Single(result.Changes);
        Assert.Equal(EditSessionChangeStatuses.Invalid, change.Status);
        Assert.Equal(EditSessionReasons.NoMatchToRemove, change.Reason);
    }

    private async Task<(Guid Work, Guid Asset)> MatchedMovieAsync(bool withMatch = true)
    {
        var parent = _world.AddParent("Movies", "The Matrix");
        var (work, _, asset) = _world.AddChild(parent, "Movies", "The Matrix", 1, hasFile: true);
        _world.Put(work, MetadataFieldConstants.Title, "The Matrix");
        _world.Put(work, BridgeIdKeys.WikidataQid, "Q1000");
        if (withMatch)
        {
            foreach (var (key, value) in new[] { (BridgeIdKeys.TmdbId, "603"), (BridgeIdKeys.ImdbId, "tt0133093") })
            {
                _world.Put(work, key, value);
            }

            _world.Put(asset!.Value, BridgeIdKeys.TmdbId, "603");
            await new MetadataClaimRepository(_world.Database).InsertBatchAsync(
            [
                new MetadataClaim
                {
                    Id = Guid.NewGuid(),
                    EntityId = work,
                    ProviderId = WellKnownProviders.Tmdb,
                    ClaimKey = BridgeIdKeys.TmdbId,
                    ClaimValue = "603",
                    ClaimedAt = DateTimeOffset.UtcNow,
                    Confidence = 1.0,
                },
            ]);
            var bridges = new BridgeIdRepository(_world.Database);
            await bridges.UpsertBatchAsync(
            [
                new BridgeIdEntry { EntityId = work, IdType = BridgeIdKeys.TmdbId, IdValue = "603", ProviderId = "tmdb" },
                new BridgeIdEntry { EntityId = work, IdType = BridgeIdKeys.WikidataQid, IdValue = "Q1000", ProviderId = "wikidata" },
            ]);
        }

        return (work, asset ?? Guid.Empty);
    }

    private Dictionary<string, string> Canonicals(Guid entity)
    {
        using var connection = _world.Database.CreateConnection();
        return connection.Query<(string Key, string Value)>(
            "SELECT key AS Key, value AS Value FROM canonical_values WHERE entity_id = @entity;", new { entity })
            .ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
    }

    private EditSessionService Service()
    {
        var database = _world.Database;
        var scopes = new ServiceCollection()
            .AddSingleton<IMetadataEditorRepository>(new MetadataEditorRepository(database))
            .AddSingleton<ICanonicalValueRepository>(new CanonicalValueRepository(database))
            .AddSingleton<IMetadataClaimRepository>(new MetadataClaimRepository(database))
            .AddSingleton<IBridgeIdRepository>(new BridgeIdRepository(database))
            .AddSingleton<IWorkRepository>(new WorkRepository(database))
            .AddSingleton<ISystemActivityRepository>(new SystemActivityRepository(database))
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new EditSessionService([new MatchRemovalEditSessionChangeHandler(scopes)], NullLogger<EditSessionService>.Instance);
    }

    private static EditSessionRequest Request(bool previewOnly, Guid item) => new()
    {
        PreviewOnly = previewOnly,
        Changes = [new EditSessionChange { Id = "remove", Kind = EditSessionChangeKinds.MatchRemoval, EntityId = item }],
    };
}
