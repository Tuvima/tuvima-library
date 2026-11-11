using System.Net;
using System.Text;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>The Universe editor's Dashboard side (universal editor v4, WP11): the rail's state, adding a thing, its Review row and its Quick Facts.</summary>
public sealed class EditorUniverseTests
{
    private const string Qid = "Q4024";
    private static readonly Guid UniverseId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static EditorUniverseKindDto Kind(string key, string label, string singular, int count, bool available = true) =>
        new() { Key = key, Label = label, Singular = singular, Count = count, Available = available };

    private static EditorUniverseThingsDto Page(string? kindKey, int total, bool hasMore, params EditorUniverseThingDto[] things) => new()
    {
        UniverseId = UniverseId,
        UniverseQid = Qid,
        UniverseName = "Dune",
        Kinds = [Kind("characters", "Characters", "Character", 12), Kind("factions", "Factions", "Faction", 3), Kind("concepts", "Concepts", "Concept", 0, available: false)],
        KindKey = kindKey,
        Total = total,
        HasMore = hasMore,
        Things = things,
    };

    private static EditorUniverseThingDto Thing(string label, bool addedByUser = false) =>
        new() { Id = Guid.NewGuid(), Label = label, KindKey = "characters", AddedByUser = addedByUser };

    private static EditorUniverseState State(Func<string?, string?, int, EditorUniverseThingsDto?> answer, List<(string? Kind, string? Search, int Offset)>? calls = null)
    {
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetEditorUniverseThingsAsync), args =>
        {
            var kind = (string?)args![1];
            var search = (string?)args[2];
            var offset = (int)args[3]!;
            calls?.Add((kind, search, offset));
            return Task.FromResult(answer(kind, search, offset));
        }));
        return new EditorUniverseState(api, NullLogger.Instance);
    }

    [Fact]
    public async Task TheRail_ShowsOnlyTheKindsTheUniverseHas_InTheEnginesOrder()
    {
        var state = State((_, _, _) => Page(null, 0, false));

        await state.EnsureAsync(Qid);

        Assert.True(state.Visible);
        Assert.Equal("Dune", state.UniverseName);
        Assert.Equal(UniverseId, state.UniverseId);
        Assert.Equal(["characters", "factions"], state.Kinds.Select(kind => kind.Key));
    }

    [Fact]
    public async Task AskingAgainForTheSameUniverse_ReadsNothingMore()
    {
        var calls = new List<(string?, string?, int)>();
        var state = State((_, _, _) => Page(null, 0, false), calls);

        await state.EnsureAsync(Qid);
        await state.EnsureAsync(Qid.ToLowerInvariant());

        Assert.Single(calls);
    }

    [Fact]
    public async Task AFailedRead_LeavesTheRailWithOnlyTheUniverse()
    {
        var state = State((_, _, _) => null);

        await state.EnsureAsync(Qid);

        Assert.True(state.Visible);
        Assert.True(state.Failed);
        Assert.Empty(state.Kinds);
    }

    [Fact]
    public async Task OpeningAKind_ReadsItsFirstPage_AndMoreIsPagedIn()
    {
        var first = Enumerable.Range(0, 40).Select(index => Thing("Person " + index)).ToArray();
        var second = new[] { Thing("Last one") };
        var calls = new List<(string? Kind, string? Search, int Offset)>();
        var state = State((kind, _, offset) => kind is null
            ? Page(null, 0, false)
            : offset == 0 ? Page(kind, 41, true, first) : Page(kind, 41, false, second), calls);
        await state.EnsureAsync(Qid);

        await state.OpenListAsync("characters", null);
        Assert.Equal(40, state.ListFor("characters").Items.Count);
        Assert.True(state.ListFor("characters").HasMore);

        await state.LoadMoreAsync("characters");

        var list = state.ListFor("characters");
        Assert.Equal(41, list.Items.Count);
        Assert.False(list.HasMore);
        Assert.Contains((("characters"), (string?)null, 40), calls.Select(call => (call.Kind, call.Search, call.Offset)));
    }

    [Fact]
    public async Task ATypedSearch_IsSentToTheEngine_AndKeptWithTheList()
    {
        var calls = new List<(string? Kind, string? Search, int Offset)>();
        var state = State((kind, search, _) => kind is null ? Page(null, 0, false) : Page(kind, 1, false, Thing("Paul")), calls);
        await state.EnsureAsync(Qid);

        await state.OpenListAsync("characters", "  pau ");

        Assert.Equal("pau", state.ListFor("characters").Search);
        Assert.Contains(calls, call => call.Kind == "characters" && call.Search == "pau");
    }

    [Fact]
    public async Task Resetting_ForgetsTheUniverseAndItsLists()
    {
        var state = State((kind, _, _) => kind is null ? Page(null, 0, false) : Page(kind, 1, false, Thing("Paul")));
        await state.EnsureAsync(Qid);
        await state.OpenListAsync("characters", null);

        state.Reset();

        Assert.False(state.Visible);
        Assert.Empty(state.Kinds);
        Assert.False(state.ListFor("characters").Loaded);
    }

    [Fact]
    public void AddingThings_StagesEachOneApartFromTheOthers_AndNeverMixesUniverses()
    {
        var pending = new PendingEditList();
        var other = Guid.NewGuid();

        pending.AddThing(UniverseId, "House Atreides", "factions", "Rulers of Caladan", "Faction");
        pending.AddThing(UniverseId, "House Harkonnen", "factions", null, "Faction");
        pending.AddThing(other, "Somewhere else", "places", null, "Place");

        var adds = pending.ThingAdds(UniverseId);
        Assert.Equal(2, adds.Count);
        Assert.All(adds, change => Assert.Equal(EditSessionChangeKinds.ThingAdd, change.Kind));
        Assert.Equal(2, adds.Select(change => change.TargetId).Distinct().Count());
        var first = adds[0].Fields;
        Assert.Equal("House Atreides", first.Single(field => field.Key == EditSessionThingAdd.NameKey).Value);
        Assert.Equal("factions", first.Single(field => field.Key == EditSessionThingAdd.KindKey).Value);
        Assert.Equal("Rulers of Caladan", first.Single(field => field.Key == EditSessionThingAdd.DescriptionKey).Value);
        Assert.DoesNotContain(adds[1].Fields, field => field.Key == EditSessionThingAdd.DescriptionKey);
    }

    [Fact]
    public void AStagedThing_ReadsOnReviewAsAnAddition_NamedAfterItsKind()
    {
        var pending = new PendingEditList();
        pending.AddThing(UniverseId, "House Atreides", "factions", null, "Faction");

        var row = Assert.Single(EditReviewRows.Build(pending, _ => "Dune", null));

        Assert.Equal(EditSessionChangeKinds.ThingAdd, row.Kind);
        Assert.Equal("Add faction", row.What);
        Assert.Equal("Not in this Universe", row.Before);
        Assert.Equal("House Atreides", row.After);
        Assert.Equal(EditReviewRows.ThingAddNote, row.Warning);
    }

    [Fact]
    public void AThing_ShowsItsKind_WhereItAppears_AndWhereItCameFrom()
    {
        var fields = new Dictionary<string, string> { ["kind"] = "Faction", ["wikidata"] = "Q123" };
        var totals = new ItemQuickFactsDto { ItemCount = 4 };

        var model = EditorQuickFactsBuilder.Build(QuickFactsKind.Thing, keys => keys.Select(key => fields.GetValueOrDefault(key)).FirstOrDefault(value => value is not null), totals, false);

        Assert.Equal(["kind", "appears_in", "wikidata"], model.Facts.Select(fact => fact.Key));
        Assert.Equal("Faction", model.Facts[0].Value);
        Assert.Equal("4", model.Facts[1].Value);
        Assert.Equal("Q123", model.Facts[2].Value);
    }

    [Fact]
    public void AHandAddedThing_SaysSoInsteadOfShowingAnId_AndAppearsInNothingYet()
    {
        var fields = new Dictionary<string, string> { ["kind"] = "Place", ["wikidata"] = "Not on Wikidata, added by you" };

        var model = EditorQuickFactsBuilder.Build(QuickFactsKind.Thing, keys => keys.Select(key => fields.GetValueOrDefault(key)).FirstOrDefault(value => value is not null), new ItemQuickFactsDto { ItemCount = 0 }, false);

        Assert.DoesNotContain(model.Facts, fact => fact.Key == "appears_in");
        Assert.Contains(model.Facts, fact => fact.Value == "Not on Wikidata, added by you");
    }

    [Theory]
    [InlineData("Q42", true)]
    [InlineData(" q7 ", true)]
    [InlineData("Q", false)]
    [InlineData("Not on Wikidata, added by you", false)]
    [InlineData(null, false)]
    public void ARecordIsLinkedToWikidata_OnlyWhenItLooksLikeAnId(string? text, bool expected) =>
        Assert.Equal(expected, EditorUniverseRules.LooksLikeQid(text));

    [Fact]
    public void TheUniverseTabs_AreNamedForWhatTheyHold()
    {
        Assert.Equal("Pictures", EditorUniverseRules.ArtworkTabLabel(EditorSubjectKinds.Universe));
        Assert.Equal("Pictures", EditorUniverseRules.ArtworkTabLabel(EditorSubjectKinds.FictionalEntity));
        Assert.Equal("Artwork", EditorUniverseRules.ArtworkTabLabel(EditorSubjectKinds.Person));
        Assert.Equal("Match", EditorUniverseRules.MatchTabLabel(EditorSubjectKinds.Universe));
        Assert.Equal("Match", EditorUniverseRules.MatchTabLabel(null));
        Assert.Equal("Faction in Dune", EditorUniverseRules.KindLine(EditorSubjectKinds.FictionalEntity, "Faction", "Dune"));
        Assert.Equal("Universe", EditorUniverseRules.KindLine(EditorSubjectKinds.Universe, null, "Dune"));
    }

    [Fact]
    public async Task TheClient_ReadsAKindByQid_WithSearchAndPaging_AndDeletesByIdAtOnce()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {"universe_id":"aaaaaaaa-0000-0000-0000-000000000001","universe_qid":"Q4024","universe_name":"Dune","kinds":[],"kind_key":"factions","total":0,"offset":40,"has_more":false,"things":[]}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var page = await client.GetEditorUniverseThingsAsync(Qid, "factions", "house of", 40, 40);

        Assert.NotNull(page);
        Assert.Equal("GET", handler.Method);
        Assert.Equal("/library/editor-subjects/universes/Q4024/things", handler.Path);
        Assert.Contains("kind=factions", handler.Query, StringComparison.Ordinal);
        Assert.Contains("search=house%20of", handler.Query, StringComparison.Ordinal);
        Assert.Contains("offset=40", handler.Query, StringComparison.Ordinal);

        var thing = Guid.NewGuid();
        var deleted = new RecordingHandler(HttpStatusCode.NoContent, string.Empty);
        using var http2 = new HttpClient(deleted) { BaseAddress = new Uri("http://engine.test") };
        using var client2 = new EngineApiClient(http2, NullLogger<EngineApiClient>.Instance);

        Assert.True(await client2.DeleteEditorUniverseThingAsync(thing));
        Assert.Equal("DELETE", deleted.Method);
        Assert.Equal("/library/editor-subjects/things/" + thing.ToString("D"), deleted.Path);
    }

    [Fact]
    public async Task TheClient_ReadsAnItemsEditorHistory_AndSaysSoWhenItCannot()
    {
        var id = Guid.NewGuid();
        var ok = new RecordingHandler(HttpStatusCode.OK, $$"""
            [{"id":"{{Guid.NewGuid()}}","entity_id":"{{id}}","occurred_at":"2026-11-03T10:00:00+00:00","event_type":"user_field_edit","label":"Name changed","detail":"Dune","category":"metadata","actor_label":"You"}]
            """);
        using var http = new HttpClient(ok) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var (items, error) = await client.GetEditorSubjectHistoryAsync(id);

        Assert.Null(error);
        Assert.Equal("/library/editor-subjects/" + id.ToString("D") + "/history", ok.Path);
        var row = Assert.Single(items);
        Assert.Equal("Name changed", row.Label);
        Assert.Equal("You", row.ActorLabel);

        var refused = new RecordingHandler(HttpStatusCode.Forbidden, "{}");
        using var http2 = new HttpClient(refused) { BaseAddress = new Uri("http://engine.test") };
        using var client2 = new EngineApiClient(http2, NullLogger<EngineApiClient>.Instance);

        var (none, why) = await client2.GetEditorSubjectHistoryAsync(id);

        Assert.Empty(none);
        Assert.NotNull(why);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Method { get; private set; }

        public string? Path { get; private set; }

        public string Query { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Path = request.RequestUri!.AbsolutePath;
            Query = request.RequestUri.Query;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
