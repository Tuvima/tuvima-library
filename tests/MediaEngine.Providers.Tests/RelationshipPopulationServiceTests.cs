using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Tests;

public sealed class RelationshipPopulationServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DatabaseConnection _database;
    private readonly FictionalEntityRepository _entities;
    private readonly EntityRelationshipRepository _relationships;

    public RelationshipPopulationServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_relationship_population_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _database = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _entities = new FictionalEntityRepository(_database);
        _relationships = new EntityRelationshipRepository(_database);
    }

    [Fact]
    public async Task PopulateAsync_PersistsStatementScopedQualifiersAndAppearances()
    {
        var source = await CreateEntityAsync("Q-character", FictionalEntityType.Character);
        var service = CreateService();
        var statementEvidence = new[]
        {
            Statement(
                "student_of_qid",
                "Q-mentor",
                [
                    Qualifier("P10663", "Q-film", "WikidataQid"),
                    Qualifier("P580", "+2001-01-01T00:00:00Z", "Time"),
                    Qualifier("P582", "+2002-01-01T00:00:00Z", "Time"),
                    Qualifier("P4895", "chapter-12", "Text"),
                    Qualifier("P7528", "Q-sequel", "WikidataQid"),
                ]),
            Statement(
                "present_in_work_qid",
                "Q-film",
                [
                    Qualifier("P5800", "protagonist", "Text"),
                    Qualifier("P10663", "Q-film", "WikidataQid"),
                    Qualifier("P1545", "12", "Quantity"),
                    Qualifier("P4895", "chapter-12", "Text"),
                    Qualifier("P7528", "Q-sequel", "WikidataQid"),
                ]),
        };

        await service.PopulateAsync(
            source.WikidataQid,
            new Dictionary<string, string>(),
            "Q-universe",
            "Universe",
            statementEvidence: statementEvidence,
            maxDepth: 0);

        var fact = Assert.Single(await _relationships.GetBySubjectAsync(source.WikidataQid));
        Assert.Equal(RelationshipType.StudentOf, fact.RelationshipTypeValue);
        Assert.Equal("Q-film", fact.ContextWorkQid);
        Assert.Equal("wikidata", fact.SourceProvider);
        Assert.Equal("Wikidata", fact.Provenance);
        Assert.Contains(fact.Qualifiers, qualifier => qualifier.QualifierType == GraphQualifierType.AppliesToWork && qualifier.Value == "Q-film");
        Assert.Contains(fact.Qualifiers, qualifier => qualifier.QualifierType == GraphQualifierType.StartTime);
        Assert.Contains(fact.Qualifiers, qualifier => qualifier.QualifierType == GraphQualifierType.EndTime);
        Assert.Contains(fact.Qualifiers, qualifier => qualifier.QualifierType == GraphQualifierType.TimeIndex && qualifier.Value == "chapter-12");
        Assert.Contains(fact.Qualifiers, qualifier => qualifier.QualifierType == GraphQualifierType.SpoilerForWork && qualifier.Value == "Q-sequel");

        var appearance = Assert.Single(await _entities.GetWorkLinksAsync(source.Id));
        Assert.Equal("Q-film", appearance.WorkQid);
        Assert.Equal("protagonist", appearance.AppearanceRole);
        Assert.Equal("ordinal", appearance.AnchorKind);
        Assert.Equal("12", appearance.AnchorValue);
        Assert.Equal("chapter-12", appearance.NarrativeTimeIndex);
        Assert.Equal("Q-sequel", appearance.SpoilerForWorkQid);
        Assert.Equal("wikidata", appearance.SourceProvider);
    }

    [Fact]
    public async Task PopulateAsync_CreatesEventAndObjectTargetsWithTheirFirstClassTypes()
    {
        var eventSource = await CreateEntityAsync("Q-event", FictionalEntityType.Event);
        var objectSource = await CreateEntityAsync("Q-object", FictionalEntityType.Object);
        var service = CreateService();

        await service.PopulateAsync(
            eventSource.WikidataQid,
            new Dictionary<string, string>(),
            "Q-universe",
            "Universe",
            statementEvidence:
            [
                Statement("event_location_qid", "Q-place", []),
                Statement("participant_qid", "Q-participant", []),
                Statement("cause_qid", "Q-cause", []),
                Statement("part_of_qid", "Q-war", []),
            ],
            maxDepth: 0);
        await service.PopulateAsync(
            objectSource.WikidataQid,
            new Dictionary<string, string>(),
            "Q-universe",
            "Universe",
            statementEvidence: [Statement("has_parts_qid", "Q-component", [])],
            maxDepth: 0);

        Assert.Equal(FictionalEntityType.Location, (await _entities.FindByQidAsync("Q-place"))!.EntitySubType);
        Assert.Equal(FictionalEntityType.Character, (await _entities.FindByQidAsync("Q-participant"))!.EntitySubType);
        Assert.Equal(FictionalEntityType.Event, (await _entities.FindByQidAsync("Q-cause"))!.EntitySubType);
        Assert.Equal(FictionalEntityType.Event, (await _entities.FindByQidAsync("Q-war"))!.EntitySubType);
        Assert.Equal(FictionalEntityType.Object, (await _entities.FindByQidAsync("Q-component"))!.EntitySubType);
    }

    [Fact]
    public async Task PopulateAsync_DoesNotFallbackToFlattenedClaimsWhenStructuredEvidenceIsPresent()
    {
        var source = await CreateEntityAsync("Q-source", FictionalEntityType.Character);
        var service = CreateService();

        await service.PopulateAsync(
            source.WikidataQid,
            new Dictionary<string, string> { ["student_of_qid"] = "Q-deprecated-mentor" },
            "Q-universe",
            "Universe",
            statementEvidence: [],
            maxDepth: 0);

        Assert.Empty(await _relationships.GetBySubjectAsync(source.WikidataQid));
        Assert.Null(await _entities.FindByQidAsync("Q-deprecated-mentor"));
    }

    [Fact]
    public async Task SignificantPersonAndEnemyClaims_BecomeCharacterRelationships()
    {
        // The property map is read from the shipped config, so a missing P3342/P7047 entry fails here.
        var config = JsonSerializer.Deserialize<ReconciliationProviderConfig>(
            await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "config", "providers", "wikidata_reconciliation.json")),
            new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip });
        Assert.NotNull(config);
        var source = await CreateEntityAsync("Q-hero", FictionalEntityType.Character);
        var entity = new WikidataEntityInfo
        {
            Id = source.WikidataQid,
            Claims = new Dictionary<string, IReadOnlyList<WikidataClaim>>
            {
                ["P3342"] = [EntityClaim("P3342", "Q-ally")],
                ["P7047"] = [EntityClaim("P7047", "Q-nemesis")],
            },
        };

        var evidence = ReconciliationAdapter.BuildFictionalEntityGraphEvidence(
            entity,
            config!.DataExtension.CharacterProperties,
            config.DataExtension.PropertyLabels);
        await CreateService().PopulateAsync(
            source.WikidataQid,
            new Dictionary<string, string>(),
            "Q-universe",
            "Universe",
            statementEvidence: evidence.Statements,
            maxDepth: 0);

        var facts = await _relationships.GetBySubjectAsync(source.WikidataQid);
        Assert.Equal(2, facts.Count);
        Assert.Contains(facts, fact => fact.RelationshipTypeValue == RelationshipType.SignificantPerson && fact.ObjectQid == "Q-ally");
        Assert.Contains(facts, fact => fact.RelationshipTypeValue == RelationshipType.Enemy && fact.ObjectQid == "Q-nemesis");
        Assert.Equal(FictionalEntityType.Character, (await _entities.FindByQidAsync("Q-ally"))!.EntitySubType);
        Assert.Equal(FictionalEntityType.Character, (await _entities.FindByQidAsync("Q-nemesis"))!.EntitySubType);
    }

    [Fact]
    public async Task AParticipantInClaim_BecomesAnEventParticipationRelationship_NotAnOpponent()
    {
        // P1344 is Wikidata's "participant in": the character took part in an event.
        var config = JsonSerializer.Deserialize<ReconciliationProviderConfig>(
            await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "config", "providers", "wikidata_reconciliation.json")),
            new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip });
        Assert.NotNull(config);
        var source = await CreateEntityAsync("Q-fighter", FictionalEntityType.Character);
        var entity = new WikidataEntityInfo
        {
            Id = source.WikidataQid,
            Claims = new Dictionary<string, IReadOnlyList<WikidataClaim>>
            {
                ["P1344"] = [EntityClaim("P1344", "Q-battle")],
            },
        };

        var evidence = ReconciliationAdapter.BuildFictionalEntityGraphEvidence(
            entity,
            config!.DataExtension.CharacterProperties,
            config.DataExtension.PropertyLabels);
        await CreateService().PopulateAsync(
            source.WikidataQid,
            new Dictionary<string, string>(),
            "Q-universe",
            "Universe",
            statementEvidence: evidence.Statements,
            maxDepth: 0);

        var fact = Assert.Single(await _relationships.GetBySubjectAsync(source.WikidataQid));
        Assert.Equal(RelationshipType.ParticipantIn, fact.RelationshipTypeValue);
        Assert.Equal("Q-battle", fact.ObjectQid);
        Assert.NotEqual("opponent", fact.RelationshipTypeValue);
        Assert.Equal(FictionalEntityType.Event, (await _entities.FindByQidAsync("Q-battle"))!.EntitySubType);
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // SQLite pool cleanup can briefly retain a test file on Windows.
        }
    }

    private async Task<FictionalEntity> CreateEntityAsync(string qid, string subType)
    {
        var entity = new FictionalEntity
        {
            Id = Guid.NewGuid(),
            WikidataQid = qid,
            Label = qid,
            EntitySubType = subType,
            FictionalUniverseQid = "Q-universe",
            FictionalUniverseLabel = "Universe",
        };
        await _entities.CreateAsync(entity);
        return entity;
    }

    private RelationshipPopulationService CreateService() => new(
        _relationships,
        _entities,
        new RecordingHarvestQueue(),
        new NullUniverseGraphQueryService(),
        NullLogger<RelationshipPopulationService>.Instance);

    private static FictionalEntityRelationshipStatement Statement(
        string claimKey,
        string targetQid,
        IReadOnlyList<FictionalEntityStatementQualifier> qualifiers) => new(
            claimKey,
            targetQid,
            targetQid,
            0.9,
            qualifiers);

    private static WikidataClaim EntityClaim(string propertyId, string entityId) => new()
    {
        PropertyId = propertyId,
        Rank = "normal",
        Value = new WikidataValue
        {
            Kind = WikidataValueKind.EntityId,
            EntityId = entityId,
            EntityLabel = entityId,
            RawValue = entityId,
        },
    };

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(RelationshipPopulationServiceTests).Assembly.Location);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not find repository root (MediaEngine.slnx).");
    }

    private static FictionalEntityStatementQualifier Qualifier(string propertyId, string value, string kind) =>
        new(propertyId, value, kind);

    private sealed class RecordingHarvestQueue : IMetadataHarvestQueueAdmission
    {
        public int PendingCount => 0;
        public ValueTask EnqueueAsync(MediaEngine.Domain.Models.HarvestRequest request, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class NullUniverseGraphQueryService : IUniverseGraphQueryService
    {
        public Task<IReadOnlyList<IReadOnlyList<string>>> FindPathsAsync(string universeQid, string fromQid, string toQid, int maxHops = 4, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>([]);
        public Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetFamilyTreeAsync(string universeQid, string characterQid, int generations = 3, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<string>>>(new Dictionary<int, IReadOnlyList<string>>());
        public void InvalidateCache(string universeQid) { }
    }
}
