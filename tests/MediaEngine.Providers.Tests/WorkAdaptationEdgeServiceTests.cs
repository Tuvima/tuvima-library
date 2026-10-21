using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class WorkAdaptationEdgeServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DatabaseConnection _db;
    private readonly WorkRepository _works;
    private readonly CanonicalValueRepository _canonicalValues;
    private readonly CanonicalValueArrayRepository _canonicalArrays;
    private readonly EntityRelationshipRepository _relationships;

    public WorkAdaptationEdgeServiceTests()
    {
        DapperConfiguration.Configure();
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_work_adaptation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _db = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _works = new WorkRepository(_db);
        _canonicalValues = new CanonicalValueRepository(_db);
        _canonicalArrays = new CanonicalValueArrayRepository(_db);
        _relationships = new EntityRelationshipRepository(_db);
    }

    [Fact]
    public async Task ABasedOnClaim_WritesOneAdaptationEdge_AndARerunDoesNotDuplicateIt()
    {
        var film = await SeedWorkAsync("Q-film");
        await SetArrayAsync(film, MetadataFieldConstants.BasedOn, ("The novel", "Q-novel"));
        var service = CreateService();

        var first = await service.WriteAsync(film, fallbackWorkQid: null);
        var second = await service.WriteAsync(film, fallbackWorkQid: null);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        var edge = Assert.Single(await _relationships.GetByEntityAsync("Q-film"));
        Assert.Equal("Q-film", edge.SubjectQid);
        Assert.Equal(RelationshipType.BasedOn, edge.RelationshipTypeValue);
        Assert.Equal("Q-novel", edge.ObjectQid);
        Assert.Equal("wikidata", edge.SourceProvider);
        Assert.Equal("Wikidata", edge.Provenance);
    }

    [Fact]
    public async Task ADerivativeWorkClaim_WritesTheReverseAdaptationEdge()
    {
        // Wikidata states "the novel has the film as a derivative work". The edge reads "film is a derivative of novel":
        // subject is the derivative, object is the Work that carries the claim.
        var novel = await SeedWorkAsync("Q-novel");
        await SetArrayAsync(novel, MetadataFieldConstants.DerivativeWork, ("The film", "Q-film"), ("The series", "Q-series"));
        var service = CreateService();

        var written = await service.WriteAsync(novel, fallbackWorkQid: null);

        Assert.Equal(2, written);
        var edges = await _relationships.GetByObjectAsync("Q-novel");
        Assert.Equal(2, edges.Count);
        Assert.All(edges, edge => Assert.Equal(RelationshipType.DerivativeWork, edge.RelationshipTypeValue));
        Assert.Equal(["Q-film", "Q-series"], edges.Select(edge => edge.SubjectQid).Order().ToArray());
        Assert.Empty(await _relationships.GetBySubjectAsync("Q-novel"));
    }

    [Fact]
    public async Task AWorkWithNoAdaptationClaims_WritesNoAdaptationEdges()
    {
        var standalone = await SeedWorkAsync("Q-standalone");
        await SetArrayAsync(standalone, MetadataFieldConstants.Genre, ("Drama", "Q-drama"));
        var service = CreateService();

        var written = await service.WriteAsync(standalone, fallbackWorkQid: null);

        Assert.Equal(0, written);
        Assert.Empty(await _relationships.GetByEntityAsync("Q-standalone"));
    }

    [Fact]
    public async Task AWorkWhoseClaimsDoNotNameItself_UsesTheFallbackQidForItsEdge()
    {
        var work = await _works.InsertParentAsync(MediaType.Movies, "movies:fallback", null, null);
        await SetArrayAsync(work, MetadataFieldConstants.BasedOn, ("The novel", "Q-novel"));

        var written = await CreateService().WriteAsync(work, fallbackWorkQid: "Q-fallback");

        Assert.Equal(1, written);
        var edge = Assert.Single(await _relationships.GetBySubjectAsync("Q-fallback"));
        Assert.Equal("Q-novel", edge.ObjectQid);
    }

    [Fact]
    public async Task AWorkThatNamesItselfAsItsOwnSource_WritesNoEdge()
    {
        var work = await SeedWorkAsync("Q-loop");
        await SetArrayAsync(work, MetadataFieldConstants.BasedOn, ("Itself", "Q-loop"));

        var written = await CreateService().WriteAsync(work, fallbackWorkQid: null);

        Assert.Equal(0, written);
        Assert.Empty(await _relationships.GetByEntityAsync("Q-loop"));
    }

    [Fact]
    public async Task AClaimThatIsGone_RemovesTheEdgeThisServiceWrote_AndKeepsTheOnesStillClaimed()
    {
        var film = await SeedWorkAsync("Q-film");
        await SetArrayAsync(film, MetadataFieldConstants.BasedOn, ("The novel", "Q-novel"), ("The play", "Q-play"));
        var service = CreateService();
        await service.WriteAsync(film, fallbackWorkQid: null);

        await SetArrayAsync(film, MetadataFieldConstants.BasedOn, ("The play", "Q-play"));
        await service.WriteAsync(film, fallbackWorkQid: null);

        var edge = Assert.Single(await _relationships.GetBySubjectAsync("Q-film"));
        Assert.Equal("Q-play", edge.ObjectQid);
    }

    [Fact]
    public async Task WhenEveryClaimIsGone_NoEdgeIsLeftBehind_ForEitherDirection()
    {
        var novel = await SeedWorkAsync("Q-novel");
        await SetArrayAsync(novel, MetadataFieldConstants.BasedOn, ("The poem", "Q-poem"));
        await SetArrayAsync(novel, MetadataFieldConstants.DerivativeWork, ("The film", "Q-film"));
        var service = CreateService();
        await service.WriteAsync(novel, fallbackWorkQid: null);
        Assert.Equal(2, (await _relationships.GetByEntityAsync("Q-novel")).Count);

        await SetArrayAsync(novel, MetadataFieldConstants.BasedOn);
        await SetArrayAsync(novel, MetadataFieldConstants.DerivativeWork);
        var written = await service.WriteAsync(novel, fallbackWorkQid: null);

        Assert.Equal(0, written);
        Assert.Empty(await _relationships.GetByEntityAsync("Q-novel"));
    }

    [Fact]
    public async Task AnEdgeAnotherWriterStored_IsNeverRemoved()
    {
        var film = await SeedWorkAsync("Q-film");
        await _relationships.CreateAsync(new EntityRelationship
        {
            SubjectQid = "Q-film",
            RelationshipTypeValue = RelationshipType.BasedOn,
            ObjectQid = "Q-other-source",
            SourceProvider = "wikidata",
            Provenance = "Wikidata",
            DiscoveredAt = DateTimeOffset.UtcNow,
        });

        await CreateService().WriteAsync(film, fallbackWorkQid: null);

        Assert.Equal("Q-other-source", Assert.Single(await _relationships.GetBySubjectAsync("Q-film")).ObjectQid);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // SQLite pool cleanup can briefly retain a test file on Windows.
        }
    }

    private WorkAdaptationEdgeService CreateService() => new(
        _canonicalValues,
        _canonicalArrays,
        _relationships,
        NullLogger<WorkAdaptationEdgeService>.Instance);

    private async Task<Guid> SeedWorkAsync(string workQid)
    {
        var workId = await _works.InsertParentAsync(MediaType.Movies, $"movies:{workQid}", null, null);
        await _canonicalValues.UpsertBatchAsync(
        [
            new CanonicalValue
            {
                EntityId = workId,
                Key = BridgeIdKeys.WikidataQid,
                Value = workQid,
                LastScoredAt = DateTimeOffset.UtcNow,
            },
        ]);
        return workId;
    }

    private Task SetArrayAsync(Guid workId, string key, params (string Label, string Qid)[] entries) =>
        _canonicalArrays.SetValuesAsync(
            workId,
            key,
            entries.Select((entry, index) => new CanonicalArrayEntry
            {
                Ordinal = index,
                Value = entry.Label,
                ValueQid = entry.Qid,
            }).ToList());
}
