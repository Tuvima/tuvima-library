using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The one spoiler rule on the Universe page and the graph, through the same real host and data store as the Connections
/// tests. A test show owns three identified episodes; the profile's progress is real <c>user_states</c> rows.
/// </summary>
public sealed partial class CollectionClientEndpointsTests
{
    private const string ShowQid = "Q9100";
    private const string Episode1Qid = "Q9101";
    private const string Episode2Qid = "Q9102";
    private const string Episode3Qid = "Q9103";
    private const string AriaQid = "Q9201";
    private const string BramQid = "Q9202";
    private const string CoraQid = "Q9203";

    [Fact]
    public async Task ATestShow_HidesCharactersFirstSeenAfterTheViewersEpisode_AndLabelsUnknownOnesNoPlotDetails()
    {
        var show = SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.UpToProgress);
        WatchFully(show.Episode1);
        WatchFully(show.Episode2);
        await using var app = await StartAsync(mapGraphActions: true);

        var connections = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!;

        // Aria first appears in episode 1 (seen), Bram in episode 3 (not yet), Cora is tied only to the show as a whole.
        var characters = connections["characters"]!.AsArray();
        Assert.Equal(["Aria", "Cora"], characters.Select(character => character!["label"]!.GetValue<string>()));
        var aria = characters[0]!;
        var cora = characters[1]!;
        Assert.False(aria["no_plot_details"]!.GetValue<bool>());
        Assert.Equal(1, aria["work_count"]!.GetValue<int>());
        Assert.True(cora["no_plot_details"]!.GetValue<bool>());
        Assert.Equal(0, cora["work_count"]!.GetValue<int>());
        Assert.Equal(2, connections["character_count"]!.GetValue<int>());
        Assert.Equal(1, connections["hidden_character_count"]!.GetValue<int>());
        // The hidden character is not named anywhere in the answer, not even beside the actor who plays them.
        Assert.DoesNotContain("Bram", connections.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(BramQid, connections.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACharacterLinkedToTheShow_IsVisible_WhenEveryEpisodeHasItsOwnQid()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.Everything);
        await using var app = await StartAsync(mapGraphActions: true);

        var characters = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!["characters"]!.AsArray();

        var cora = characters.Single(character => character!["qid"]!.GetValue<string>() == CoraQid)!;
        Assert.Equal(1, cora["work_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task ATestShow_ShowsEveryCharacter_WhenTheProfileChoosesEverything()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.Everything);
        await using var app = await StartAsync(mapGraphActions: true);

        var connections = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!;

        Assert.Equal(
            ["Aria", "Bram", "Cora"],
            connections["characters"]!.AsArray().Select(character => character!["label"]!.GetValue<string>()).Order());
        Assert.All(connections["characters"]!.AsArray(), character => Assert.False(character!["no_plot_details"]!.GetValue<bool>()));
        Assert.Equal(0, connections["hidden_character_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task ATestShow_LabelsEveryoneNoPlotDetails_ForAViewerWhoHasNotStartedIt()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.UpToProgress);
        await using var app = await StartAsync(mapGraphActions: true);

        var connections = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!;

        var characters = connections["characters"]!.AsArray();
        Assert.Equal(3, characters.Count);
        Assert.All(characters, character => Assert.True(character!["no_plot_details"]!.GetValue<bool>()));
        Assert.Equal(0, connections["hidden_character_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task ACharacterFirstSeenInTheEpisodeTheViewerIsOn_IsShown()
    {
        var show = SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.UpToProgress);
        WatchFully(show.Episode1);
        WatchFully(show.Episode2);
        SetProgress(show.Episode3, 12);
        await using var app = await StartAsync(mapGraphActions: true);

        var connections = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!;

        Assert.Equal(
            ["Aria", "Bram", "Cora"],
            connections["characters"]!.AsArray().Select(character => character!["label"]!.GetValue<string>()).Order());
        Assert.Equal(0, connections["hidden_character_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheCastRowOfAnActor_DropsOnlyTheHiddenCharacters_AndKeepsThePerson()
    {
        var show = SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.UpToProgress);
        WatchFully(show.Episode1);
        await using var app = await StartAsync(mapGraphActions: true);

        var cast = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["connections"]!["cast"]!.AsArray();

        var both = cast.Single(row => row!["member"]!["name"]!.GetValue<string>() == "Ann Actor")!["member"]!;
        Assert.Equal(["Aria"], both["characters"]!.AsArray().Select(character => character!["label"]!.GetValue<string>()));
        // Bob plays only Bram: the person stays in the cast, without an "as ..." line.
        var onlyHidden = cast.Single(row => row!["member"]!["name"]!.GetValue<string>() == "Bob Actor")!["member"]!;
        Assert.Empty(onlyHidden["characters"]!.AsArray());
    }

    [Fact]
    public async Task TheCompactUniverseRead_AndTheCastAction_FollowTheSameRule()
    {
        var show = SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.UpToProgress);
        WatchFully(show.Episode1);
        await using var app = await StartAsync(mapGraphActions: true);

        var compact = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");
        var castAction = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/cast");

        Assert.DoesNotContain("Bram", compact["cast"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("Aria", compact["cast"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(
            ["Aria", "Cora"],
            castAction["characters"]!.AsArray().Select(character => character!["label"]!.GetValue<string>()).Order());
    }

    [Fact]
    public async Task TheGraph_LeavesOutAHiddenCharacter_AndEveryEdgeToThem()
    {
        var show = SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.UpToProgress);
        WatchFully(show.Episode1);
        AddRelationship(AriaQid, "ally", BramQid);
        AddRelationship(AriaQid, "ally", CoraQid);
        AddNarrativeRoot();
        await using var app = await StartAsync(mapGraphActions: true);

        var graph = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/graph");

        var nodeIds = graph["nodes"]!.AsArray().Select(node => node!["id"]!.GetValue<string>()).Order().ToArray();
        Assert.Equal([AriaQid, CoraQid], nodeIds);
        var edges = graph["edges"]!.AsArray();
        Assert.DoesNotContain(edges, edge => edge!["source"]!.GetValue<string>() == BramQid || edge["target"]!.GetValue<string>() == BramQid);
        Assert.Single(edges);
        // A character labelled "No plot details" is named with nothing else.
        Assert.Null(graph["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == CoraQid)!["description"]);
    }

    [Fact]
    public async Task TheGraph_ListsEveryCharacter_AtTheLevelEverything()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.Everything);
        AddNarrativeRoot();
        await using var app = await StartAsync(mapGraphActions: true);

        var graph = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/graph");

        Assert.Equal(
            [AriaQid, BramQid, CoraQid],
            graph["nodes"]!.AsArray().Select(node => node!["id"]!.GetValue<string>()).Order());
    }

    // Seeds ------------------------------------------------------------------------------------------------------

    private sealed record SpoilerShow(Guid Show, Guid Episode1, Guid Episode2, Guid Episode3);

    /// <summary>
    /// A show with three owned episodes (season 1) in Series Alpha, each with an identity of its own, so the show's identity
    /// is not any row's. Aria's first appearance is episode 1, Bram's is episode 3 and Cora is tied only to the show. Ann plays
    /// Aria and Bram, Bob plays Bram.
    /// </summary>
    private SpoilerShow SeedSpoilerShow()
    {
        var show = AddShowWork("The Test Show", ShowQid);
        var one = AddEpisodeWork(show, 1, Episode1Qid);
        var two = AddEpisodeWork(show, 2, Episode2Qid);
        var three = AddEpisodeWork(show, 3, Episode3Qid);

        var aria = AddEntity(UniverseQid, AriaQid, "Aria", "Character");
        var bram = AddEntity(UniverseQid, BramQid, "Bram", "Character");
        var cora = AddEntity(UniverseQid, CoraQid, "Cora", "Character");
        LinkEntityTyped(aria, Episode1Qid, "first_appearance");
        LinkEntityTyped(aria, Episode2Qid, "present_in_work");
        LinkEntityTyped(bram, Episode3Qid, "first_appearance");
        LinkEntityTyped(cora, ShowQid, "present_in_work");

        var ann = AddPerson("Ann Actor");
        var bob = AddPerson("Bob Actor");
        AddPerformance(ann, aria, Episode1Qid);
        AddPerformance(ann, bram, Episode3Qid);
        AddPerformance(bob, bram, Episode3Qid);
        return new SpoilerShow(show, one, two, three);
    }

    private Guid AddShowWork(string title, string qid)
    {
        var work = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state, collection_id, wikidata_qid)
            VALUES (@work, 'TV', 'parent', 'accepted', @collection, @qid);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@work, 'title', @title, CURRENT_TIMESTAMP),
                   (@work, 'wikidata_qid', @qid, CURRENT_TIMESTAMP),
                   (@work, 'content_rating', 'PG', CURRENT_TIMESTAMP);
            """,
            new { work, collection = _alpha, title, qid });
        return work;
    }

    private Guid AddEpisodeWork(Guid show, int number, string? qid)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state, collection_id, wikidata_qid)
            VALUES (@work, 'TV', 'child', @show, @number, 'accepted', @collection, @qid);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP, @library);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@work, 'title', @title, CURRENT_TIMESTAMP),
                   (@work, 'content_rating', 'PG', CURRENT_TIMESTAMP),
                   (@work, 'show_name', 'The Test Show', CURRENT_TIMESTAMP),
                   (@asset, 'title', @title, CURRENT_TIMESTAMP),
                   (@asset, 'season_number', '1', CURRENT_TIMESTAMP),
                   (@asset, 'episode_number', @numberText, CURRENT_TIMESTAMP);
            """,
            new
            {
                work,
                edition,
                asset,
                show,
                number,
                numberText = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                collection = _alpha,
                qid,
                title = $"Episode {number}",
                hash = asset.ToString("N"),
                path = $"C:/library/The Test Show/S01E0{number}-{asset:N}.mkv",
                library = _library.ToString("D"),
            });
        return work;
    }

    private void LinkEntityTyped(Guid entity, string workQid, string linkType)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO fictional_entity_work_links (id, appearance_key, entity_id, work_qid, link_type)
            VALUES (@id, @key, @entity, @workQid, @linkType);
            """,
            new { id = Guid.NewGuid(), key = $"{entity:N}:{workQid}:{linkType}", entity, workQid, linkType });
    }

    private void AddNarrativeRoot()
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO narrative_roots (qid, label, level, created_at) VALUES (@qid, 'Test Universe', 'Universe', '2026-01-01 00:00:00');",
            new { qid = UniverseQid });
    }

    private Guid WorkIdOf(string workQid)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<Guid>("SELECT id FROM works WHERE wikidata_qid = @workQid;", new { workQid });
    }

    private void WatchFully(Guid work) => SetProgress(work, 100);

    private void SetProgress(Guid work, double percent)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO user_states (user_id, asset_id, content_hash, progress_pct, last_accessed, extended_properties, revision)
            SELECT @profile, ma.id, ma.content_hash, @percent, CURRENT_TIMESTAMP, '{}', 0
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            WHERE e.work_id = @work;
            """,
            new { profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId, percent, work });
    }

    private void SetSpoilerLevel(string level) =>
        new UserPlaybackSettingsService(_database, new ProfileRepository(_database))
            .SetSpoilerLevelAsync(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, level)
            .GetAwaiter()
            .GetResult();
}
