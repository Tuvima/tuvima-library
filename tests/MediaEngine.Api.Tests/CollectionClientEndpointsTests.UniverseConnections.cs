using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Universe;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The evidence-backed Connections on the Universe page read: Characters, Cast and Adaptations, each row with the source it
/// was learned from, through the same real host and data store as the helper-app Universe read.
/// </summary>
public sealed partial class CollectionClientEndpointsTests
{
    private const string HeroQid = "Q8200";
    private const string VillainQid = "Q8201";

    private static readonly string[] WikidataSource = ["wikidata:Wikidata"];

    [Fact]
    public async Task Connections_ListOnlyVisibleCharacters_MostWorksFirst_EachWithItsSource()
    {
        SeedCast();
        SetContentLimit("PG");
        await using var app = await StartAsync(mapGraphActions: true);

        var connections = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!;

        // Hidden Villain only appears in the R-rated work, so a PG profile never hears of them.
        var characters = connections["characters"]!.AsArray();
        Assert.Equal(["Hero", "Villain"], characters.Select(character => character!["label"]!.GetValue<string>()));
        Assert.Equal([HeroQid, VillainQid], characters.Select(character => character!["qid"]!.GetValue<string>()));
        Assert.Equal([2, 1], characters.Select(character => character!["work_count"]!.GetValue<int>()));
        Assert.Equal(2, connections["character_count"]!.GetValue<int>());
        Assert.All(characters, character =>
        {
            Assert.False(character!["no_plot_details"]!.GetValue<bool>());
            Assert.Equal(WikidataSource, SourcesOf(character));
        });
        Assert.Equal(
            ["label", "no_plot_details", "qid", "sources", "work_count"],
            ((JsonObject)characters[0]!).Select(pair => pair.Key).Order());
        Assert.Equal(
            ["kind", "label"],
            ((JsonObject)characters[0]!["sources"]![0]!).Select(pair => pair.Key).Order());
        Assert.DoesNotContain("Hidden Villain", connections.ToJsonString(), StringComparison.Ordinal);

        // With no limit the Hidden Villain is a one-work character like the Villain, ahead of it by name.
        SetContentLimit(null);
        var everyone = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!;
        Assert.Equal(
            ["Hero", "Hidden Villain", "Villain"],
            everyone["characters"]!.AsArray().Select(character => character!["label"]!.GetValue<string>()));
        Assert.Equal([3, 1, 1], everyone["characters"]!.AsArray().Select(character => character!["work_count"]!.GetValue<int>()));
        Assert.Equal(3, everyone["character_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Connections_ListACharacterWhoseOnlyAppearanceIsASpoiler_LastWithNoPlotDetails()
    {
        SeedCast();
        // Only seen as a spoiler for another work: named, but no work count and no plot details.
        var spoilerOnly = AddEntity(UniverseQid, "Q8300", "A Zealous Spoiler", "Character");
        LinkEntityWithEvidence(spoilerOnly, "QW1", spoilerForWorkQid: "QW2");
        // One spoiler appearance and one plain one: an ordinary character, counted by its plain appearance only.
        var mixed = AddEntity(UniverseQid, "Q8301", "Mixed Appearances", "Character");
        LinkEntityWithEvidence(mixed, "QW1", spoilerForWorkQid: "QW2");
        LinkEntityWithEvidence(mixed, "QW2", appearanceSuffix: "plain");
        await using var app = await StartAsync(mapGraphActions: true);

        var connections = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!;

        var characters = connections["characters"]!.AsArray();
        Assert.Equal(
            ["Hero", "Hidden Villain", "Mixed Appearances", "Villain", "A Zealous Spoiler"],
            characters.Select(character => character!["label"]!.GetValue<string>()));
        var last = characters[^1]!;
        Assert.True(last["no_plot_details"]!.GetValue<bool>());
        Assert.Equal(0, last["work_count"]!.GetValue<int>());
        Assert.Equal(WikidataSource, SourcesOf(last));
        var normal = characters.Single(character => character!["qid"]!.GetValue<string>() == "Q8301")!;
        Assert.False(normal["no_plot_details"]!.GetValue<bool>());
        Assert.Equal(1, normal["work_count"]!.GetValue<int>());
        Assert.Equal(5, connections["character_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Connections_NameAnApprovedLoreSource_ThatHoldsTheCharacter_ButNotAPendingOne()
    {
        SeedCast();
        var approved = AddLoreSource("Dune Wiki", "Approved");
        AddLoreEntity(approved, HeroQid, "Hero (wiki)");
        // Same character in a second approved source, and a character only a source nobody approved has heard of.
        var secondApproved = AddLoreSource("Arrakis Archive", "Approved");
        AddLoreEntity(secondApproved, HeroQid, "Hero (archive)");
        var pending = AddLoreSource("Pending Wiki", "Pending");
        AddLoreEntity(pending, VillainQid, "Villain (pending)");
        await using var app = await StartAsync(mapGraphActions: true);

        var characters = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!["characters"]!.AsArray();

        // Wikidata first, then the lore sources by name.
        Assert.Equal(
            ["wikidata:Wikidata", "lore_plugin:Arrakis Archive", "lore_plugin:Dune Wiki"],
            SourcesOf(characters.Single(character => character!["qid"]!.GetValue<string>() == HeroQid)!));
        Assert.Equal(
            WikidataSource,
            SourcesOf(characters.Single(character => character!["qid"]!.GetValue<string>() == VillainQid)!));
    }

    [Fact]
    public async Task Connections_ShowAnUnrecognisedProvenance_AsAnOtherSourceWithThatLabel()
    {
        SeedCast();
        var character = AddEntity(UniverseQid, "Q8302", "Community Pick", "Character");
        LinkEntityWithEvidence(character, "QW1", provenance: "Community Import");
        await using var app = await StartAsync(mapGraphActions: true);

        var characters = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!["characters"]!.AsArray();

        Assert.Equal(
            ["other:Community Import"],
            SourcesOf(characters.Single(row => row!["qid"]!.GetValue<string>() == "Q8302")!));
    }

    [Fact]
    public async Task Connections_GiveEveryCastAndAdaptationRowASource()
    {
        SeedCast();
        AddRelationship("Q8100", "based_on", "Q8101");
        AddRelationship("Q8100", "inspired_by", "Q8102");
        await using var app = await StartAsync(mapGraphActions: true);

        var connections = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!;

        var cast = connections["cast"]!.AsArray();
        var adaptations = connections["adaptations"]!.AsArray();
        Assert.NotEmpty(cast);
        Assert.NotEmpty(adaptations);
        Assert.All(cast, row => Assert.Equal(WikidataSource, SourcesOf(row!)));
        Assert.All(adaptations, row => Assert.Equal(WikidataSource, SourcesOf(row!)));
        Assert.Equal(["member", "sources"], ((JsonObject)cast[0]!).Select(pair => pair.Key).Order());
        Assert.Equal(["adaptation", "sources"], ((JsonObject)adaptations[0]!).Select(pair => pair.Key).Order());
    }

    [Fact]
    public async Task Connections_CombineTheSources_OfEveryRelationshipThatCollapsesIntoOneAdaptationRow()
    {
        SeedCast();
        AddRelationship("Q8100", "based_on", "Q8101");
        AddRelationship("Q8100", "based_on", "Q8101", provenance: "Plugin", sourceProvider: "Dune Wiki", keySuffix: "lore");
        await using var app = await StartAsync(mapGraphActions: true);

        var adaptations = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!["adaptations"]!.AsArray();

        var row = Assert.Single(adaptations)!;
        Assert.Equal(["wikidata:Wikidata", "lore_plugin:Dune Wiki"], SourcesOf(row));
    }

    [Fact]
    public async Task Connections_AreEmpty_ForAUniverseWithNoFictionalEntities()
    {
        SeedUniverse("Q7200", "Quiet World", [("Movies", "Quiet One")]);
        await using var app = await StartAsync(mapGraphActions: true);

        var connections = (await GetJsonAsync(app, $"{GraphActions}/universe/Q7200/overview"))["connections"]!;

        Assert.Empty(connections["characters"]!.AsArray());
        Assert.Equal(0, connections["character_count"]!.GetValue<int>());
        Assert.Empty(connections["cast"]!.AsArray());
        Assert.Empty(connections["adaptations"]!.AsArray());
    }

    [Fact]
    public async Task Connections_ListAtMostTheCap_WhileCharacterCountReportsEveryCharacter()
    {
        SeedCast();
        const int extra = 62;
        for (var i = 0; i < extra; i++)
        {
            LinkEntity(AddEntity(UniverseQid, $"Q86{i:D2}", $"Extra {i:D2}", "Character"), "QW1");
        }

        await using var app = await StartAsync(mapGraphActions: true);

        var connections = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!;

        Assert.Equal(UniverseConnectionsReadService.MaxCharacters, connections["characters"]!.AsArray().Count);
        Assert.Equal(3 + extra, connections["character_count"]!.GetValue<int>());
        // The cap cuts the end of the order, so the character seen in the most works is still listed.
        Assert.Equal("Hero", connections["characters"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheAdaptationTreeAction_IsEmpty_WhenNoAdaptationRelationshipExists()
    {
        SeedCast();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO narrative_roots (qid, label, level, created_at) VALUES (@qid, 'Test Universe', 'Universe', '2026-01-01 00:00:00');",
                new { qid = UniverseQid });
        }

        await using var app = await StartAsync(mapGraphActions: true);

        var tree = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/adaptations");

        // The entities exist, but they are not adaptations, so none is listed as one.
        Assert.Equal(UniverseQid, tree["universe_qid"]!.GetValue<string>());
        Assert.Empty(tree["works"]!.AsArray());
    }

    [Fact]
    public async Task TheHelperAppUniverseRead_CarriesNoConnectionsOrSources()
    {
        SeedCast();
        AddRelationship("Q8100", "based_on", "Q8101");
        await using var app = await StartAsync();

        var universe = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");

        var keys = AllKeys(universe).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("connections", keys);
        Assert.DoesNotContain("sources", keys);
        Assert.NotEmpty(universe["cast"]!.AsArray());
        Assert.NotEmpty(universe["adaptations"]!.AsArray());
    }

    [Theory]
    [InlineData(null, null, "wikidata", "Wikidata")]
    [InlineData("", "", "wikidata", "Wikidata")]
    [InlineData("  ", null, "wikidata", "Wikidata")]
    [InlineData("wikidata", "Anything", "wikidata", "Wikidata")]
    [InlineData("WIKIDATA", null, "wikidata", "Wikidata")]
    [InlineData("Plugin", "Dune Wiki", "lore_plugin", "Dune Wiki")]
    [InlineData("plugin", null, "lore_plugin", "Lore plugin")]
    [InlineData("plugin", "  ", "lore_plugin", "Lore plugin")]
    [InlineData("Fandom lore", "Dune Wiki", "lore_plugin", "Dune Wiki")]
    [InlineData("Lore", null, "lore_plugin", "Lore plugin")]
    [InlineData("  Community Import  ", "Dune Wiki", "other", "Community Import")]
    public void ProvenanceBecomesTheSourceAPersonSees(string? provenance, string? provider, string kind, string label)
    {
        var source = UniverseSourceLabels.FromProvenance(provenance, provider);

        Assert.Equal(kind, source.Kind);
        Assert.Equal(label, source.Label);
    }

    [Fact]
    public void CombinedSources_AreDistinctWithWikidataFirstThenByLabel()
    {
        UniverseSourceDto Source(string kind, string label) => new() { Kind = kind, Label = label };

        var combined = UniverseSourceLabels.Combine(
        [
            Source(UniverseSourceDto.LorePlugin, "Zed Wiki"),
            Source(UniverseSourceDto.Other, "Community"),
            Source(UniverseSourceDto.LorePlugin, "dune wiki"),
            Source(UniverseSourceDto.Wikidata, "Wikidata"),
            Source(UniverseSourceDto.LorePlugin, "Dune Wiki"),
            Source(UniverseSourceDto.Wikidata, "Wikidata"),
        ]);

        Assert.Equal(
            ["wikidata:Wikidata", "other:Community", "lore_plugin:dune wiki", "lore_plugin:Zed Wiki"],
            combined.Select(source => $"{source.Kind}:{source.Label}"));
    }

    // Seeds and reads --------------------------------------------------------------------------------------------

    private static string[] SourcesOf(JsonNode row) =>
        row["sources"]!.AsArray()
            .Select(source => $"{source!["kind"]!.GetValue<string>()}:{source["label"]!.GetValue<string>()}")
            .ToArray();

    private static IEnumerable<string> AllKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    yield return key;
                    foreach (var nested in AllKeys(value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    foreach (var nested in AllKeys(child))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    /// <summary>A work link with the evidence columns set: where it came from and whether it is held back as a spoiler.</summary>
    private void LinkEntityWithEvidence(
        Guid entity,
        string workQid,
        string provenance = "Wikidata",
        string? sourceProvider = null,
        string? spoilerForWorkQid = null,
        string appearanceSuffix = "")
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO fictional_entity_work_links
                (id, appearance_key, entity_id, work_qid, provenance, source_provider, spoiler_for_work_qid)
            VALUES (@id, @key, @entity, @workQid, @provenance, @sourceProvider, @spoilerForWorkQid);
            """,
            new
            {
                id = Guid.NewGuid(),
                key = $"{entity:N}:{workQid}:{spoilerForWorkQid}:{appearanceSuffix}",
                entity,
                workQid,
                provenance,
                sourceProvider,
                spoilerForWorkQid,
            });
    }

    private void AddRelationship(
        string subjectQid,
        string type,
        string objectQid,
        string provenance,
        string? sourceProvider,
        string keySuffix)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO entity_relationships
                (id, statement_key, subject_qid, relationship_type, object_qid, provenance, source_provider, discovered_at)
            VALUES (@id, @key, @subjectQid, @type, @objectQid, @provenance, @sourceProvider, '2026-01-01 00:00:00');
            """,
            new
            {
                id = Guid.NewGuid(),
                key = $"{subjectQid}|{type}|{objectQid}|{keySuffix}",
                subjectQid,
                type,
                objectQid,
                provenance,
                sourceProvider,
            });
    }

    private Guid AddLoreSource(string name, string status)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO plugin_lore_sources
                (id, universe_qid, plugin_id, source_key, source_name, base_url, api_url, status, created_at, updated_at)
            VALUES (@id, @universeQid, 'fandom-lore', @key, @name, 'https://lore.example', 'https://lore.example/api', @status,
                    '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
            """,
            new { id, universeQid = UniverseQid, key = name.ToLowerInvariant().Replace(' ', '-'), name, status });
        return id;
    }

    private void AddLoreEntity(Guid sourceId, string wikidataQid, string label)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO plugin_lore_entities
                (id, source_id, universe_qid, plugin_id, external_key, wikidata_qid, label, entity_type, source_url, created_at, updated_at)
            VALUES (@id, @sourceId, @universeQid, 'fandom-lore', @key, @wikidataQid, @label, 'Character', 'https://lore.example/page',
                    '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
            """,
            new { id = Guid.NewGuid(), sourceId, universeQid = UniverseQid, key = label, wikidataQid, label });
    }
}
