using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Universe;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Universe panels read the Contracts shapes the Engine really sends: paths and family
/// trees are QID lists, so names, pictures and relationships come from the loaded graph.
/// </summary>
public sealed class UniverseWireContractTests : AsyncBunitContext
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public UniverseWireContractTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddHttpClient();
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<MediaEngine.Web.Services.Editing.MediaEditorLauncherService>();
        Services.AddScoped<DeviceContextService>();
    }

    private static UniverseGraphResponse DuneGraph() => new(
        new UniverseGraphRef("Q1", "Dune"),
        [
            Phase8AdvancedIntelligenceTests.Node("Q10", "Paul Atreides", "Character"),
            Phase8AdvancedIntelligenceTests.Node("Q11", "Arrakis", "Location"),
            Phase8AdvancedIntelligenceTests.Node("Q13", "Leto Atreides", "Character") with { image = "/art/leto.jpg" },
        ],
        [
            Phase8AdvancedIntelligenceTests.Edge("Q10", "Q11", "residence", "resides in"),
        ]);

    [Fact]
    public async Task PathFinderPanel_NamesEachHopFromTheGraphAndFallsBackToTheQid()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.FindPathsAsync), _ =>
                Task.FromResult<UniversePathsResponse?>(new UniversePathsResponse(
                    "Q1",
                    "Q13",
                    "Q99",
                    new IReadOnlyList<string>[] { ["Q13", "Q10", "Q11", "Q99"] })))));

        var cut = Render<PathFinderPanel>(parameters => parameters
            .Add(component => component.UniverseQid, "Q1")
            .Add(component => component.Graph, DuneGraph()));
        await cut.InvokeAsync(() =>
        {
            cut.Instance.SetNode("Q13", "Leto Atreides");
            cut.Instance.SetNode("Q99", "Q99");
        });

        cut.FindAll("button").First(button => button.TextContent.Contains("Find Path")).Click();

        cut.WaitForAssertion(() =>
        {
            var hops = cut.FindAll(".hop-node").Select(node => node.TextContent).ToArray();
            Assert.Equal(["Leto Atreides", "Paul Atreides", "Arrakis", "Q99"], hops);
        });
    }

    [Fact]
    public async Task PathFinderPanel_PointsTheRelationshipForwardWhenTheEdgeRunsInPathOrder()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.FindPathsAsync), _ =>
                Task.FromResult<UniversePathsResponse?>(new UniversePathsResponse(
                    "Q1",
                    "Q10",
                    "Q11",
                    new IReadOnlyList<string>[] { ["Q10", "Q11"] })))));

        var cut = Render<PathFinderPanel>(parameters => parameters
            .Add(component => component.UniverseQid, "Q1")
            .Add(component => component.Graph, DuneGraph()));
        await cut.InvokeAsync(() =>
        {
            cut.Instance.SetNode("Q10", "Paul Atreides");
            cut.Instance.SetNode("Q11", "Arrakis");
        });

        cut.FindAll("button").First(button => button.TextContent.Contains("Find Path")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("-> (resides in) ->", cut.Find(".hop-chain").TextContent);
        });
    }

    [Fact]
    public async Task PathFinderPanel_PointsTheRelationshipBackWhenOnlyTheReverseEdgeExists()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.FindPathsAsync), _ =>
                Task.FromResult<UniversePathsResponse?>(new UniversePathsResponse(
                    "Q1",
                    "Q11",
                    "Q10",
                    new IReadOnlyList<string>[] { ["Q11", "Q10"] })))));

        var cut = Render<PathFinderPanel>(parameters => parameters
            .Add(component => component.UniverseQid, "Q1")
            .Add(component => component.Graph, DuneGraph()));
        await cut.InvokeAsync(() =>
        {
            cut.Instance.SetNode("Q11", "Arrakis");
            cut.Instance.SetNode("Q10", "Paul Atreides");
        });

        cut.FindAll("button").First(button => button.TextContent.Contains("Find Path")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("<- (resides in) <-", cut.Find(".hop-chain").TextContent);
        });
    }

    [Fact]
    public async Task FamilyTreeView_NamesMembersAndShowsPicturesFromTheGraphAndFallsBackToTheQid()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetFamilyTreeAsync), _ =>
                Task.FromResult<UniverseFamilyTreeResponse?>(new UniverseFamilyTreeResponse(
                    "Q1",
                    "Q10",
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["-1"] = ["Q13", "Q404"],
                        ["0"] = ["Q10"],
                    })))));

        var cut = Render<FamilyTreeView>(parameters => parameters
            .Add(component => component.UniverseQid, "Q1")
            .Add(component => component.Graph, DuneGraph()));
        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync("Q10"));

        cut.FindAll("button").First(button => button.TextContent.Contains("Show Tree")).Click();

        cut.WaitForAssertion(() =>
        {
            var members = cut.FindAll(".family-node");
            Assert.Equal(3, members.Count);
            Assert.Contains("Leto Atreides", members[0].TextContent);
            Assert.Contains("Q404", members[1].TextContent);
            Assert.Contains("Paul Atreides", members[2].TextContent);
            Assert.Equal("/art/leto.jpg", members[0].QuerySelector("img")!.GetAttribute("src"));
            Assert.Null(members[1].QuerySelector("img"));
        });
    }

    [Fact]
    public void ChronicleExplorer_CountsChangedEntitiesFromTheContractsLoreDeltaShape()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetUniverseGraphAsync), _ =>
                Task.FromResult<UniverseGraphResponse?>(DuneGraph()));
            stub.SetHandler(nameof(IEngineApiClient.CheckLoreDeltaAsync), _ =>
                Task.FromResult<IReadOnlyList<UniverseLoreDeltaResponse>>(
                [
                    new UniverseLoreDeltaResponse("Q10", "Paul Atreides", 5, 7, true),
                    new UniverseLoreDeltaResponse("Q11", "Arrakis", 3, 3, false),
                    new UniverseLoreDeltaResponse("Q13", "Leto Atreides", 1, 2, true),
                ]));
        }));

        var cut = Render<ChronicleExplorer>(parameters => parameters
            .Add(component => component.Qid, "Q1"));

        cut.WaitForAssertion(() =>
            Assert.Contains("New Lore Discovered: 2 entities have been updated", cut.Markup));
    }

    [Fact]
    public void LoreDeltaResponse_ReadsTheSnakeCaseNamesTheEngineSends()
    {
        const string json = """
            [{"entity_qid":"Q10","label":"Paul Atreides","cached_revision":5,"current_revision":7,"has_changed":true}]
            """;

        var items = JsonSerializer.Deserialize<List<UniverseLoreDeltaResponse>>(json, WebJson)!;

        var item = Assert.Single(items);
        Assert.Equal("Q10", item.entity_qid);
        Assert.Equal(5, item.cached_revision);
        Assert.Equal(7, item.current_revision);
        Assert.True(item.has_changed);
    }

    [Fact]
    public void UniverseSearchResponse_ReadsOnlyTheFieldsTheEngineSends()
    {
        const string json = """
            {"candidates":[{"qid":"Q1","label":"Dune","description":"novel","instance_of":"novel","year":"1965",
            "author":"Frank Herbert","director":null,"cover_url":null,"wikipedia_extract":null,"resolution_tier":"bridge",
            "confidence":0.9,"bridge_ids":{"isbn":"123"},"media_type":"Epub","media_type_metadata":null,"match_scores":null}],
            "query":"dune","media_type":"Epub"}
            """;

        var response = JsonSerializer.Deserialize<MediaEngine.Contracts.Search.SearchUniverseResponseDto>(json, WebJson)!;

        var candidate = Assert.Single(response.Candidates);
        Assert.Equal("Q1", candidate.Qid);
        Assert.Equal("bridge", candidate.ResolutionTier);
        Assert.Equal("123", candidate.BridgeIds["isbn"]);
        Assert.Equal("Epub", candidate.MediaType);
    }
}
