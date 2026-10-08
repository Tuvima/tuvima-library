using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Tests;

public sealed class UniverseGraphEndpointTests
{
    [Fact]
    public void EgoNetwork_PreservesRequestedDepthAndBothEdgeDirections()
    {
        var qids = new HashSet<string>(["Q1", "Q2", "Q3", "Q4"], StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<EntityRelationship> edges =
        [
            Edge("Q1", "Q2"),
            Edge("Q3", "Q2"),
            Edge("Q4", "Q3"),
            Edge("Q1", "Q-outside"),
        ];

        Assert.Equal(["Q1"], UniverseGraphEndpoints.BuildEgoNetwork("Q1", 0, qids, edges).Order().ToArray());
        Assert.Equal(["Q1", "Q2"], UniverseGraphEndpoints.BuildEgoNetwork("Q1", 1, qids, edges).Order().ToArray());
        Assert.Equal(["Q1", "Q2", "Q3"], UniverseGraphEndpoints.BuildEgoNetwork("Q1", 2, qids, edges).Order().ToArray());
        Assert.Equal(["Q1", "Q2", "Q3", "Q4"], UniverseGraphEndpoints.BuildEgoNetwork("Q1", 3, qids, edges).Order().ToArray());
    }

    [Fact]
    public void GraphRoute_UsesBatchedReadsInsteadOfPerNodeRepositoryCalls()
    {
        var source = File.ReadAllText(GetRepoFilePath(
            @"src\MediaEngine.Api\Endpoints\UniverseGraphEndpoints.cs"));
        var start = source.IndexOf("group.MapGet(\"/universe/{qid}/graph\"", StringComparison.Ordinal);
        var end = source.IndexOf("group.MapPost(\"/universe/entity/{qid}/deep-enrich\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var graphRoute = source[start..end];
        Assert.Contains("LoadVisibleUniverseScopeAsync(", graphRoute, StringComparison.Ordinal);
        Assert.Contains("entityRepo.GetWorkLinksAsync(", source, StringComparison.Ordinal);
        Assert.Contains("allEntities.Select(entity => entity.Id)", source, StringComparison.Ordinal);
        Assert.Contains("visibleWorkQids.Contains(link.WorkQid)", source, StringComparison.Ordinal);
        Assert.Contains("eraActorResolver.ResolveActorsForEraAsync(", graphRoute, StringComparison.Ordinal);
        Assert.Contains("BuildEgoNetwork(center, maxDepth, entityQids, relationships)", graphRoute, StringComparison.Ordinal);
        Assert.Contains("ApiImageUrls.BuildPersonHeadshotUrl(", graphRoute, StringComparison.Ordinal);
        Assert.DoesNotContain("GetWorkLinksAsync(entity.Id", graphRoute, StringComparison.Ordinal);
        Assert.DoesNotContain("relRepo.GetByEntityAsync", graphRoute, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveActorForEraAsync", graphRoute, StringComparison.Ordinal);
    }

    [Fact]
    public void VisibleUniverseEntitiesRequireProvenanceFromAnAuthorizedWork()
    {
        var visible = new FictionalEntity { Id = Guid.NewGuid(), WikidataQid = "Q1" };
        var withheld = new FictionalEntity { Id = Guid.NewGuid(), WikidataQid = "Q2" };
        IReadOnlyList<MediaEngine.Domain.Contracts.FictionalEntityWorkLink> visibleLinks =
        [
            new(visible.Id, "Q100", "Visible work", "appears_in"),
        ];

        var filtered = UniverseGraphEndpoints.FilterVisibleEntities(
            [visible, withheld], visibleLinks);

        Assert.Equal(visible.Id, Assert.Single(filtered).Id);
    }

    [Fact]
    public void GraphRoute_MapsScopedFactAndAppearanceFields()
    {
        var source = File.ReadAllText(GetRepoFilePath(
            @"src\MediaEngine.Api\Endpoints\UniverseGraphEndpoints.cs"));
        var start = source.IndexOf("group.MapGet(\"/universe/{qid}/graph\"", StringComparison.Ordinal);
        var end = source.IndexOf("group.MapPost(\"/universe/entity/{qid}/deep-enrich\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var graphRoute = source[start..end];
        foreach (var mapping in new[]
                 {
                     "link_type: link.LinkType",
                     "appearance_role: link.AppearanceRole",
                     "work_context: link.WorkContext",
                     "anchor_kind: link.AnchorKind",
                     "anchor_value: link.AnchorValue",
                     "narrative_time_index: link.NarrativeTimeIndex",
                     "start_time: link.StartTime",
                     "end_time: link.EndTime",
                     "spoiler_for_work_qid: link.SpoilerForWorkQid",
                     "source_provider: link.SourceProvider",
                     "provenance: link.Provenance",
                     "supplemental: link.IsSupplemental",
                     "confidence: link.Confidence",
                     "appearance_key: link.AppearanceKey",
                     "source_provider: r.SourceProvider",
                 })
        {
            Assert.Contains(mapping, graphRoute, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RichGraphWireContracts_SerializeFactAndAppearanceFields()
    {
        var appearance = new UniverseGraphWorkLinkDto(
            qid: "Q-work",
            label: "Work",
            link_type: "appears_in",
            appearance_role: "protagonist",
            work_context: "alternate timeline",
            anchor_kind: "chapter",
            anchor_value: "12",
            narrative_time_index: "42",
            start_time: "2024-01-01",
            end_time: "2024-01-02",
            spoiler_for_work_qid: "Q-spoiler",
            source_provider: "wikidata",
            provenance: "wikidata",
            supplemental: true,
            confidence: 0.85,
            appearance_key: "appearance-key");
        var fact = new UniverseGraphEdgeDto(
            source: "Q-subject",
            target: "Q-object",
            type: "member_of",
            label: "member of",
            confidence: 0.9,
            context_work: "Q-work",
            start_time: "2024-01-01",
            end_time: "2024-01-02",
            supplemental: false,
            provenance: "wikidata",
            source_plugin: null,
            source_url: null,
            statement_key: "statement-key",
            qualifiers:
            [
                new UniverseGraphQualifierDto(
                    type: "spoiler_for_work",
                    value: "Q-spoiler",
                    value_kind: "wikidata_qid",
                    provenance: "wikidata",
                    supplemental: false,
                    source_provider: "wikidata",
                    confidence: 0.9),
            ],
            source_provider: "wikidata");

        var json = JsonSerializer.Serialize(
            new UniverseGraphResponse(
                universe: new UniverseGraphRef("Q-universe", "Universe"),
                nodes:
                [
                    new UniverseGraphNodeDto(
                        id: "Q-subject",
                        label: "Subject",
                        type: "character",
                        description: null,
                        image: null,
                        works: [appearance],
                        supplemental: false,
                        provenance: "wikidata",
                        source_plugin: null,
                        source_url: null),
                ],
                edges: [fact]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        var work = document.RootElement.GetProperty("nodes")[0].GetProperty("works")[0];
        Assert.Equal("appears_in", work.GetProperty("link_type").GetString());
        Assert.Equal("protagonist", work.GetProperty("appearance_role").GetString());
        Assert.Equal("alternate timeline", work.GetProperty("work_context").GetString());
        Assert.Equal("chapter", work.GetProperty("anchor_kind").GetString());
        Assert.Equal("12", work.GetProperty("anchor_value").GetString());
        Assert.Equal("42", work.GetProperty("narrative_time_index").GetString());
        Assert.Equal("Q-spoiler", work.GetProperty("spoiler_for_work_qid").GetString());
        Assert.Equal("wikidata", work.GetProperty("source_provider").GetString());
        Assert.Equal("wikidata", work.GetProperty("provenance").GetString());
        Assert.True(work.GetProperty("supplemental").GetBoolean());
        Assert.Equal(0.85, work.GetProperty("confidence").GetDouble());
        Assert.Equal("appearance-key", work.GetProperty("appearance_key").GetString());

        var edge = document.RootElement.GetProperty("edges")[0];
        Assert.Equal("wikidata", edge.GetProperty("source_provider").GetString());
        Assert.Equal("statement-key", edge.GetProperty("statement_key").GetString());
    }

    private static EntityRelationship Edge(string subject, string target) => new()
    {
        Id = Guid.NewGuid(),
        SubjectQid = subject,
        ObjectQid = target,
        RelationshipTypeValue = "member_of",
        DiscoveredAt = DateTimeOffset.UtcNow,
    };

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
