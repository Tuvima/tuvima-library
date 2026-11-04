using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Universe contents on the Universe page (spec universe-contents WP5): seven kinds of thing as sections, the one spoiler rule
/// for every kind, hand-added things, and the Timeline, through the same real host and data store as the other Universe tests.
/// </summary>
public sealed partial class CollectionClientEndpointsTests
{
    [Fact]
    public async Task UniverseContents_ListsOnlyNonEmptySections_InTheFixedOrder_AndHidesConceptsWithoutFandom()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.Everything);
        var species = AddEntity(UniverseQid, "Q9301", "Fremen", "Species");
        var faction = AddEntity(UniverseQid, "Q9302", "House Test", "Organization");
        var concept = AddEntity(UniverseQid, "Q9303", "The Test Way", "Concept");
        LinkEntityTyped(species, Episode1Qid, "present_in_work");
        LinkEntityTyped(faction, Episode1Qid, "present_in_work");
        LinkEntityTyped(concept, Episode1Qid, "present_in_work");
        await using var app = await StartAsync(mapGraphActions: true);

        var contents = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["contents"]!;

        Assert.Equal(["characters", "factions", "species"], contents["sections"]!.AsArray().Select(section => section!["key"]!.GetValue<string>()));
        Assert.DoesNotContain("The Test Way", contents.ToJsonString(), StringComparison.Ordinal);
        var factions = contents["sections"]!.AsArray().Single(section => section!["key"]!.GetValue<string>() == "factions")!;
        Assert.Equal("Factions", factions["label"]!.GetValue<string>());
        Assert.Equal("House Test", factions["tiles"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task UniverseContents_HidesAnEventTheViewerHasNotReached_AndOnlyTellsTheNumber()
    {
        var show = SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.UpToProgress);
        WatchFully(show.Episode1);
        WatchFully(show.Episode2);
        var seen = AddEntity(UniverseQid, "Q9311", "The Early Battle", "Event");
        var later = AddEntity(UniverseQid, "Q9312", "The Late Battle", "Event");
        LinkEntityTyped(seen, Episode1Qid, "first_appearance");
        LinkEntityTyped(later, Episode3Qid, "first_appearance");
        await using var app = await StartAsync(mapGraphActions: true);

        var contents = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["contents"]!;

        var events = contents["sections"]!.AsArray().Single(section => section!["key"]!.GetValue<string>() == "events")!;
        Assert.Equal(["The Early Battle"], events["tiles"]!.AsArray().Select(tile => tile!["label"]!.GetValue<string>()));
        Assert.Equal(2, contents["hidden_count"]!.GetValue<int>());
        Assert.DoesNotContain("The Late Battle", contents.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniverseContents_ShowsAHandAddedFaction_WithoutAWikidataId_EvenWithNoWorkLink()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.UpToProgress);
        var id = AddEntity(UniverseQid, "local:abc123", "Spacing Guild", "Organization");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE fictional_entities SET added_by_user = 1 WHERE id = @id;", new { id });
        }

        await using var app = await StartAsync(mapGraphActions: true);

        var contents = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["contents"]!;

        var tile = contents["sections"]!.AsArray().Single(section => section!["key"]!.GetValue<string>() == "factions")!["tiles"]![0]!;
        Assert.Equal("Spacing Guild", tile["label"]!.GetValue<string>());
        Assert.True(tile["added_by_user"]!.GetValue<bool>());
        Assert.Null(tile["qid"]);
        Assert.DoesNotContain("local:abc123", contents.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniverseContents_BuildsAStoryOrderTimeline_WhenThreeEventsCarryStoryDates()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.Everything);
        AddDatedEvent("Q9321", "Third Age Ends", "+3019-00-00T00:00:00Z");
        AddDatedEvent("Q9322", "The Founding", "+1015-00-00T00:00:00Z");
        AddDatedEvent("Q9323", "The Long War", "+2400-00-00T00:00:00Z");
        await using var app = await StartAsync(mapGraphActions: true);

        var timeline = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["contents"]!["timeline"]!;

        Assert.Equal("story", timeline["default_order"]!.GetValue<string>());
        Assert.True(timeline["story_order_available"]!.GetValue<bool>());
        Assert.Equal(
            ["The Founding", "The Long War", "Third Age Ends"],
            timeline["stops"]!.AsArray().Select(stop => stop!["label"]!.GetValue<string>()));
    }

    [Fact]
    public async Task UniverseContents_OmitsTheTimeline_WhenFewerThanThreeStopsAreDated()
    {
        SeedSpoilerShow();
        SetSpoilerLevel(SpoilerLevels.Everything);
        AddDatedEvent("Q9331", "Only Dated One", "+1015-00-00T00:00:00Z");
        await using var app = await StartAsync(mapGraphActions: true);

        var contents = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["contents"]!;

        Assert.Null(contents["timeline"]);
    }

    private void AddDatedEvent(string qid, string label, string startTime)
    {
        var id = AddEntity(UniverseQid, qid, label, "Event");
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO fictional_entity_work_links (id, appearance_key, entity_id, work_qid, link_type, start_time)
            VALUES (@linkId, @key, @id, @workQid, 'present_in_work', @startTime);
            """,
            new { linkId = Guid.NewGuid(), key = $"{id:N}:{Episode1Qid}:dated", id, workQid = Episode1Qid, startTime });
    }
}
