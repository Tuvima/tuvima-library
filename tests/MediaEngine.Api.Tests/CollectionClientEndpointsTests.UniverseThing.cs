using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>
/// One thing of a Universe on its own page (spec universe-contents section 4.2): the same visibility rule as the rails, the links
/// the Library already holds, and a person's own "belongs to".
/// </summary>
public sealed partial class CollectionClientEndpointsTests
{
    [Fact]
    public async Task UniverseThing_ShowsTheWorksItAppearsIn_AndTheThingsItIsConnectedTo_BothWays()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.Everything);
        var faction = AddEntity(UniverseQid, "Q9401", "House Test", "Organization");
        var hero = AddEntity(UniverseQid, "Q9402", "Cora Test", "Character");
        var home = AddEntity(UniverseQid, "Q9403", "Test Keep", "Location");
        LinkEntityTyped(faction, Episode1Qid, "present_in_work");
        LinkEntityTyped(hero, Episode1Qid, "present_in_work");
        LinkEntityTyped(home, Episode1Qid, "present_in_work");
        AddRelationship("Q9402", "member_of", "Q9401");
        AddRelationship("Q9401", "located_in", "Q9403");
        await using var app = await StartAsync(mapGraphActions: true);

        var heroPage = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/things/{hero:D}");
        var factionPage = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/things/{faction:D}");

        Assert.Equal("Cora Test", heroPage["label"]!.GetValue<string>());
        Assert.Equal("characters", heroPage["kind_key"]!.GetValue<string>());
        Assert.Equal("Character", heroPage["kind_label"]!.GetValue<string>());
        Assert.NotEmpty(heroPage["appears_in"]!.AsArray());
        Assert.Equal("House Test", Group(heroPage, "member_of")["things"]![0]!["label"]!.GetValue<string>());
        // The same link read from the other end.
        Assert.Equal("Cora Test", Group(factionPage, "members")["things"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("Test Keep", Group(factionPage, "located_in")["things"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task UniverseThing_IsNotFound_ForAThingTheViewerHasNotReached_AndItIsLeftOutOfOtherPages()
    {
        var show = SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.UpToProgress);
        WatchFully(show.Episode1);
        var seen = AddEntity(UniverseQid, "Q9411", "Early Ally", "Character");
        var later = AddEntity(UniverseQid, "Q9412", "Late Traitor", "Character");
        LinkEntityTyped(seen, Episode1Qid, "first_appearance");
        LinkEntityTyped(later, Episode3Qid, "first_appearance");
        AddRelationship("Q9411", "enemy", "Q9412");
        AddRelationship("Q9412", "member_of", "Q9411");
        await using var app = await StartAsync(mapGraphActions: true);

        using var hidden = await app.Client.GetAsync($"{GraphActions}/universe/{UniverseQid}/things/{later:D}");
        var visible = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/things/{seen:D}");

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.DoesNotContain("Late Traitor", visible.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniverseThing_IsNotFound_ForAThingOfAnotherUniverse_OrOneThatIsHiddenByHand()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.Everything);
        var other = AddEntity("Q999999", "Q9421", "Elsewhere", "Character");
        var mine = AddEntity(UniverseQid, "Q9422", "Tucked Away", "Character");
        LinkEntityTyped(other, Episode1Qid, "present_in_work");
        LinkEntityTyped(mine, Episode1Qid, "present_in_work");
        SetUserLinks(mine, belongsTo: null, hidden: true);
        await using var app = await StartAsync(mapGraphActions: true);

        using var elsewhere = await app.Client.GetAsync($"{GraphActions}/universe/{UniverseQid}/things/{other:D}");
        using var tucked = await app.Client.GetAsync($"{GraphActions}/universe/{UniverseQid}/things/{mine:D}");

        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, tucked.StatusCode);
    }

    [Fact]
    public async Task UniverseThing_ShowsAPersonsOwnBelongsToLink_BothWays_ForAHandAddedThingToo()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.UpToProgress);
        var guild = AddEntity(UniverseQid, "local:guild01", "Spacing Guild", "Organization");
        var pilot = AddEntity(UniverseQid, "local:pilot01", "Guild Pilot", "Character");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE fictional_entities SET added_by_user = 1, description = 'Moves ships.' WHERE id IN (@guild, @pilot);", new { guild, pilot });
        }

        SetUserLinks(pilot, belongsTo: guild, hidden: false);
        await using var app = await StartAsync(mapGraphActions: true);

        var pilotPage = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/things/{pilot:D}");
        var guildPage = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/things/{guild:D}");

        Assert.True(pilotPage["added_by_user"]!.GetValue<bool>());
        Assert.Null(pilotPage["qid"]);
        Assert.Equal("you", pilotPage["description_source"]!.GetValue<string>());
        Assert.Equal("Spacing Guild", Group(pilotPage, "belongs_to")["things"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("Guild Pilot", Group(guildPage, "belongs_here")["things"]![0]!["label"]!.GetValue<string>());
        Assert.DoesNotContain("local:", pilotPage.ToJsonString() + guildPage.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniverseThing_ShowsOnlyTheNameWhenTheLibraryCannotPlaceIt()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.UpToProgress);
        var unplaced = AddEntity(UniverseQid, "Q9431", "Arrakis Test", "Location");
        LinkEntityTyped(unplaced, Episode1Qid, "present_in_work");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE fictional_entities SET description = 'A desert world.' WHERE id = @unplaced;", new { unplaced });
        }

        await using var app = await StartAsync(mapGraphActions: true);

        var page = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/things/{unplaced:D}");

        Assert.Equal("Arrakis Test", page["label"]!.GetValue<string>());
        Assert.True(page["details_hidden"]!.GetValue<bool>());
        Assert.Null(page["description"]);
        Assert.Empty(page["appears_in"]!.AsArray());
        Assert.DoesNotContain("A desert world.", page.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniverseThing_LabelsWhereTheDescriptionCameFrom()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.Everything);
        var place = AddEntity(UniverseQid, "Q9441", "Arrakis Test", "Location");
        LinkEntityTyped(place, Episode1Qid, "present_in_work");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE fictional_entities SET description = 'A desert world.' WHERE id = @place;", new { place });
        }

        await using var app = await StartAsync(mapGraphActions: true);

        var page = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/things/{place:D}");

        Assert.Equal("places", page["kind_key"]!.GetValue<string>());
        Assert.Equal("Place", page["kind_label"]!.GetValue<string>());
        Assert.Equal("A desert world.", page["description"]!.GetValue<string>());
        Assert.Equal("wikidata", page["description_source"]!.GetValue<string>());
    }

    private static JsonNode Group(JsonObject thing, string key) =>
        thing["related"]!.AsArray().Single(group => group!["key"]!.GetValue<string>() == key)!;

    private void SetUserLinks(Guid entity, Guid? belongsTo, bool hidden)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO fictional_entity_user_overrides (entity_id, belongs_to_entity_id, is_hidden, updated_at)
            VALUES (@entity, @belongsTo, @hidden, datetime('now'))
            ON CONFLICT(entity_id) DO UPDATE SET belongs_to_entity_id = excluded.belongs_to_entity_id, is_hidden = excluded.is_hidden;
            """,
            new { entity, belongsTo, hidden = hidden ? 1 : 0 });
    }
}
