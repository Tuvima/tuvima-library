using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class UniverseGraphBatchRepositoryTests : IDisposable
{
    private const int LargeGraphSize = 1_105;
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public UniverseGraphBatchRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_graph_batch_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
    }

    [Fact]
    public async Task WorkLinksAndPeople_LoadAcrossMultipleParameterBatches()
    {
        var entityIds = Enumerable.Range(0, LargeGraphSize).Select(_ => Guid.NewGuid()).ToArray();
        var qids = Enumerable.Range(0, LargeGraphSize).Select(index => $"Q{index + 1}").ToArray();
        var personIds = Enumerable.Range(0, LargeGraphSize).Select(_ => Guid.NewGuid()).ToArray();
        var createdAt = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using (var tx = conn.BeginTransaction())
            {
                for (var index = 0; index < LargeGraphSize; index++)
                {
                    conn.Execute("""
                        INSERT INTO fictional_entities
                            (id, wikidata_qid, label, entity_sub_type, created_at)
                        VALUES (@Id, @Qid, @Label, 'Character', @CreatedAt);
    
                        INSERT INTO fictional_entity_work_links
                            (id, appearance_key, entity_id, work_qid, work_label, link_type)
                        VALUES (@AppearanceId, @AppearanceKey, @Id, @WorkQid, @WorkLabel, 'appears_in');
    
                        INSERT INTO persons
                            (id, name, wikidata_qid, headshot_url, created_at)
                        VALUES (@PersonId, @PersonName, @PersonQid, @HeadshotUrl, @CreatedAt);
                        """, new
                    {
                        Id = GuidSql.ToBlob(entityIds[index]),
                        Qid = qids[index],
                        Label = $"Character {index}",
                        CreatedAt = createdAt,
                        WorkQid = $"W{index}",
                        WorkLabel = $"Work {index}",
                        AppearanceId = GuidSql.ToBlob(Guid.NewGuid()),
                        AppearanceKey = $"appearance-{index}",
                        PersonId = GuidSql.ToBlob(personIds[index]),
                        PersonName = $"Actor {index}",
                        PersonQid = $"P{index}",
                        HeadshotUrl = $"https://example.test/{index}.jpg",
                    }, tx);
                }

                conn.Execute("""
                    INSERT INTO person_roles (person_id, role)
                    VALUES (@PersonId, 'Actor');
                    """, new { PersonId = GuidSql.ToBlob(personIds[0]) }, tx);
                tx.Commit();
            }
        }

        var fictionalEntities = new FictionalEntityRepository(_db);
        var links = await fictionalEntities.GetWorkLinksAsync(entityIds);
        Assert.Equal(LargeGraphSize, links.Count);
        Assert.Contains(links, link => link.FictionalEntityId == entityIds[^1] && link.WorkQid == $"W{LargeGraphSize - 1}");

        var entities = await fictionalEntities.FindByQidsAsync(
            qids.Select(qid => qid.ToLowerInvariant()));
        Assert.Equal(LargeGraphSize, entities.Count);
        Assert.Contains(entities, entity => entity.WikidataQid == qids[^1]);

        var personRepository = new PersonRepository(_db);
        var people = await personRepository.FindByQidsAsync(
            Enumerable.Range(0, LargeGraphSize).Select(index => $"p{index}"));
        Assert.Equal(LargeGraphSize, people.Count);
        Assert.Contains(people, person => person.WikidataQid == "P0" && person.Roles.SequenceEqual(["Actor"]));

        var peopleByName = await personRepository.FindByNamesAsync(
            Enumerable.Range(0, LargeGraphSize).Select(index => $"actor {index}"));
        Assert.Equal(LargeGraphSize, peopleByName.Count);
        Assert.Contains(peopleByName, person => person.Name == $"Actor {LargeGraphSize - 1}");
    }

    [Fact]
    public async Task RelationshipBatches_PreserveCrossBatchUniverseEdgesAndObjectReads()
    {
        var universeQids = Enumerable.Range(0, LargeGraphSize)
            .Select(index => $"Q{index + 1}")
            .ToArray();
        var repository = new EntityRelationshipRepository(_db);

        var internalEdges = new[]
        {
            Edge("Q1", "Q700", "member_of"),
            Edge("Q700", $"Q{LargeGraphSize}", "sibling"),
        };
        foreach (var edge in internalEdges)
        {
            await repository.CreateAsync(edge);
        }

        await repository.CreateAsync(Edge("Q1", "Q999999", "located_in"));
        await repository.CreateAsync(Edge("P1", "Q700", "performer"));

        var universeEdges = await repository.GetByUniverseAsync(
            universeQids.Select(qid => qid.ToLowerInvariant()).ToArray());
        Assert.Equal(2, universeEdges.Count);
        Assert.Contains(universeEdges, edge => edge.SubjectQid == "Q1" && edge.ObjectQid == "Q700");
        Assert.Contains(universeEdges, edge => edge.SubjectQid == "Q700" && edge.ObjectQid == $"Q{LargeGraphSize}");

        var incomingEdges = await repository.GetByObjectsAsync(
            universeQids.Select(qid => qid.ToLowerInvariant()));
        Assert.Contains(incomingEdges, edge => edge.SubjectQid == "P1" && edge.ObjectQid == "Q700");
    }

    [Fact]
    public async Task RelationshipFacts_PreserveScopedStatementsAndQueryableQualifiers()
    {
        var repository = new EntityRelationshipRepository(_db);
        var canonical = Edge("Q1", "Q2", "student_of");
        canonical.ContextWorkQid = "QMovie";
        canonical.SourceProvider = "wikidata";
        canonical.Qualifiers = new List<EntityRelationshipQualifier>
        {
            new EntityRelationshipQualifier
            {
                QualifierType = GraphQualifierType.SpoilerForWork,
                Value = "QMovie",
                ValueKind = "Qid",
            },
            new EntityRelationshipQualifier
            {
                QualifierType = GraphQualifierType.TimeIndex,
                Value = "chapter-12",
                ValueKind = "NarrativeIndex",
            },
        };
        var adaptation = Edge("Q1", "Q2", "student_of");
        adaptation.ContextWorkQid = "QBook";
        adaptation.StartTime = "+0010-00-00T00:00:00Z";
        adaptation.Provenance = "Plugin";
        adaptation.IsSupplemental = true;

        await repository.CreateAsync(canonical);
        await repository.CreateAsync(adaptation);
        await repository.CreateAsync(canonical);

        var facts = await repository.GetBySubjectAsync("Q1");
        Assert.Equal(2, facts.Count);
        Assert.NotEqual(canonical.StatementKey, adaptation.StatementKey);
        Assert.Contains(facts, fact => fact.ContextWorkQid == "QMovie"
            && fact.Qualifiers.Any(qualifier => qualifier.QualifierType == GraphQualifierType.SpoilerForWork));
        Assert.Single(await repository.GetByQualifierAsync(GraphQualifierType.TimeIndex, "chapter-12"));
        Assert.Equal(2, await repository.CountAsync());
    }

    [Fact]
    public async Task WorkLinks_PreserveRichAppearanceContext()
    {
        var entity = new FictionalEntity
        {
            Id = Guid.NewGuid(),
            WikidataQid = "Q999",
            Label = "Artifact",
            EntitySubType = FictionalEntityType.Object,
        };
        var repository = new FictionalEntityRepository(_db);
        await repository.CreateAsync(entity);

        var appearance = new FictionalEntityWorkLink(
            entity.Id,
            "QWork",
            "The Work",
            "appears_in",
            AppearanceRole: "carried",
            WorkContext: "film-adaptation",
            AnchorKind: "scene",
            AnchorValue: "scene-14",
            NarrativeTimeIndex: "year-3",
            SpoilerForWorkQid: "QWork",
            SourceProvider: "wikidata",
            Provenance: "Wikidata");

        await repository.LinkToWorkAsync(appearance);
        await repository.LinkToWorkAsync(appearance);

        var stored = Assert.Single(await repository.GetWorkLinksAsync(entity.Id));
        Assert.Equal("carried", stored.AppearanceRole);
        Assert.Equal("scene-14", stored.AnchorValue);
        Assert.Equal("QWork", stored.SpoilerForWorkQid);
        Assert.Equal(FictionalEntityType.Object, (await repository.FindByIdAsync(entity.Id))!.EntitySubType);
    }

    [Fact]
    public async Task UpdateUniverseAsync_RetainsOwnedWorkLabelWhenProviderCannotResolveOne()
    {
        var entity = new FictionalEntity
        {
            Id = Guid.NewGuid(),
            WikidataQid = "Q900",
            Label = "The Artifact",
            EntitySubType = FictionalEntityType.Object,
            FictionalUniverseQid = "QExisting",
            FictionalUniverseLabel = "Owned-work universe",
        };
        var repository = new FictionalEntityRepository(_db);
        await repository.CreateAsync(entity);

        await repository.UpdateUniverseAsync(entity.Id, "QAuthoritative", null);
        await repository.UpdateUniverseAsync(entity.Id, "QAuthoritative", "   ");

        var stored = await repository.FindByIdAsync(entity.Id);
        Assert.NotNull(stored);
        Assert.Equal("QAuthoritative", stored.FictionalUniverseQid);
        Assert.Equal("Owned-work universe", stored.FictionalUniverseLabel);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    private static EntityRelationship Edge(string subject, string target, string type) => new()
    {
        Id = Guid.NewGuid(),
        SubjectQid = subject,
        ObjectQid = target,
        RelationshipTypeValue = type,
        Confidence = 0.9,
        DiscoveredAt = DateTimeOffset.UtcNow,
    };
}
