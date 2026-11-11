using System.Text.Json;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the Universe editor (universal editor v4, WP11): the rail's kinds and things, and adding a thing by hand.</summary>
public sealed class EditorUniverseContractTests
{
    [Fact]
    public void TheRailRead_UsesSnakeCaseNames_AndRoundTrips()
    {
        var universe = Guid.NewGuid();
        var thing = Guid.NewGuid();
        var dto = new EditorUniverseThingsDto
        {
            UniverseId = universe,
            UniverseQid = "Q4024",
            UniverseName = "Dune",
            Kinds = [new EditorUniverseKindDto { Key = "factions", Label = "Factions", Singular = "Faction", Count = 3, Available = true }],
            KindKey = "factions",
            Total = 1,
            Offset = 0,
            HasMore = false,
            Things = [new EditorUniverseThingDto { Id = thing, Label = "House Atreides", KindKey = "factions", PictureUrl = "/stream/artwork/x?size=s", AddedByUser = true, Hidden = false }],
        };

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"universe_id\":\"" + universe.ToString("D") + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"universe_qid\":\"Q4024\"", json, StringComparison.Ordinal);
        Assert.Contains("\"universe_name\":\"Dune\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind_key\":\"factions\"", json, StringComparison.Ordinal);
        Assert.Contains("\"has_more\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"picture_url\":\"/stream/artwork/x?size=s\"", json, StringComparison.Ordinal);
        Assert.Contains("\"added_by_user\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"singular\":\"Faction\"", json, StringComparison.Ordinal);
        Assert.Contains("\"available\":true", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<EditorUniverseThingsDto>(json)!;
        Assert.Equal(universe, back.UniverseId);
        Assert.Equal("Factions", Assert.Single(back.Kinds).Label);
        var read = Assert.Single(back.Things);
        Assert.Equal(thing, read.Id);
        Assert.True(read.AddedByUser);
    }

    [Fact]
    public void AddingAThing_IsAKnownChangeKind_WithFixedFieldNames()
    {
        Assert.Equal("thing_add", EditSessionChangeKinds.ThingAdd);
        Assert.True(EditSessionChangeKinds.IsKnown(EditSessionChangeKinds.ThingAdd));
        Assert.Contains(EditSessionChangeKinds.ThingAdd, EditSessionChangeKinds.All);
        Assert.Equal("name", EditSessionThingAdd.NameKey);
        Assert.Equal("kind", EditSessionThingAdd.KindKey);
        Assert.Equal("description", EditSessionThingAdd.DescriptionKey);
    }

    [Fact]
    public void AThingAddChange_TravelsLikeAnyOtherChange()
    {
        var universe = Guid.NewGuid();
        var request = new EditSessionRequest
        {
            Changes =
            [
                new EditSessionChange
                {
                    Id = "c1",
                    Kind = EditSessionChangeKinds.ThingAdd,
                    EntityId = universe,
                    TargetId = "token",
                    Fields =
                    [
                        new EditSessionFieldChange { Key = EditSessionThingAdd.NameKey, Value = "House Atreides" },
                        new EditSessionFieldChange { Key = EditSessionThingAdd.KindKey, Value = "factions" },
                    ],
                },
            ],
        };

        var back = JsonSerializer.Deserialize<EditSessionRequest>(JsonSerializer.Serialize(request))!;

        var change = Assert.Single(back.Changes);
        Assert.Equal("thing_add", change.Kind);
        Assert.Equal(universe, change.EntityId);
        Assert.Equal("token", change.TargetId);
        Assert.Equal(["name", "kind"], change.Fields.Select(field => field.Key));
    }

    [Fact]
    public void AThingsQuickFacts_AreAskedForByTheirOwnKind()
    {
        Assert.Equal("thing", ItemQuickFactsKinds.Thing);
    }
}
