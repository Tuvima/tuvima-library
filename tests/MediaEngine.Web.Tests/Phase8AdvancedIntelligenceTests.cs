using Bunit;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class Phase8AdvancedIntelligenceTests : AsyncBunitContext
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public Phase8AdvancedIntelligenceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddHttpClient();
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<MediaEngine.Web.Services.Editing.MediaEditorLauncherService>();
    }

    [Fact]
    public void Recommendations_DoNotRenderFakeItems()
    {
        var endpoint = File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "MediaEngine.Api",
            "Endpoints",
            "CollectionEndpoints.cs"));

        Assert.DoesNotContain("OrderBy(_ => Guid.NewGuid())", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Explore Your Library", endpoint, StringComparison.Ordinal);
        Assert.Contains("Same Series", endpoint, StringComparison.Ordinal);
        Assert.Contains("Same Creator", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void ChronicleExplorer_RendersGraphData()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetUniverseGraphAsync), _ =>
                Task.FromResult<UniverseGraphResponse?>(new UniverseGraphResponse
                {
                    Universe = new UniverseInfo { Qid = "Q1", Label = "Dune" },
                    Nodes =
                    [
                        new GraphNodeDto { Id = "Q10", Label = "Paul Atreides", Type = "Character" },
                        new GraphNodeDto { Id = "Q11", Label = "Arrakis", Type = "Location" },
                    ],
                    Edges =
                    [
                        new GraphEdgeDto { Source = "Q10", Target = "Q11", Type = "residence", Label = "resides in" },
                    ],
                }));
            stub.SetHandler(nameof(IEngineApiClient.CheckLoreDeltaAsync), _ =>
                Task.FromResult<IReadOnlyList<LoreDeltaResultDto>>([]));
        }));
        Services.AddScoped<DeviceContextService>();

        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<ChronicleExplorer>(1);
            builder.AddAttribute(2, nameof(ChronicleExplorer.Qid), "Q1");
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Dune", cut.Markup);
            Assert.Contains("2 entities", cut.Markup);
            Assert.Contains("1 relationships", cut.Markup);
            Assert.DoesNotContain("Universe Graph unavailable", cut.Markup);
        });
    }

    [Fact]
    public async Task ChronicleExplorer_RendersGraphAndNodeListOnPhone()
    {
        AddThreeNodeGraph();
        Services.AddScoped<DeviceContextService>();
        var device = Services.GetRequiredService<DeviceContextService>();
        await device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));

        var cut = Render<ChronicleExplorer>(parameters => parameters
            .Add(component => component.Qid, "Q1"));

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("unavailable on mobile", cut.Markup);
            Assert.NotNull(cut.Find("#cytoscape-container"));
            Assert.Equal(3, cut.FindAll(".chronicle-node-list .tl-list-item-button").Count);
        });
        foreach (var label in new[] { "Zoom in", "Zoom out", "Fit graph to view", "Reset view" })
        {
            Assert.NotEmpty(cut.FindAll($"button[aria-label='{label}']"));
        }
    }

    [Fact]
    public void ChronicleExplorer_NodeListIsSortedByLabelWithTypeAndRelationshipCount()
    {
        AddThreeNodeGraph();
        Services.AddScoped<DeviceContextService>();

        var cut = Render<ChronicleExplorer>(parameters => parameters
            .Add(component => component.Qid, "Q1"));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".chronicle-node-list .tl-list-item-button").Count));
        var rows = cut.FindAll(".chronicle-node-list .tl-list-item-button");
        Assert.Equal(
            ["Arrakis", "Bene Gesserit", "Paul Atreides"],
            rows.Select(row => row.QuerySelector(".chronicle-node-list__label")!.TextContent).ToArray());
        Assert.Equal(
            ["Location · 1 relationship", "Organization · 1 relationship", "Character · 2 relationships"],
            rows.Select(row => row.QuerySelector(".chronicle-node-list__meta")!.TextContent).ToArray());
    }

    [Fact]
    public void ChronicleExplorer_NodeListRowCentresTheNodeAndOpensThePanel()
    {
        AddThreeNodeGraph();
        Services.AddScoped<DeviceContextService>();

        var cut = Render<ChronicleExplorer>(parameters => parameters
            .Add(component => component.Qid, "Q1"));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".chronicle-node-list .tl-list-item-button").Count));

        cut.FindAll(".chronicle-node-list .tl-list-item-button")[0].Click();

        cut.WaitForAssertion(() =>
        {
            var focus = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "CytoscapeInterop.focusNode");
            Assert.Equal("Q11", focus.Arguments[0]);
            Assert.Contains("QID: Q11", cut.Markup);
        });
    }

    [Fact]
    public void ChronicleExplorer_ZoomButtonsCallTheGraphInterop()
    {
        AddThreeNodeGraph();
        Services.AddScoped<DeviceContextService>();

        var cut = Render<ChronicleExplorer>(parameters => parameters
            .Add(component => component.Qid, "Q1"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Zoom in']")));

        cut.Find("button[aria-label='Zoom in']").Click();
        cut.Find("button[aria-label='Reset view']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "CytoscapeInterop.zoomIn");
            Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "CytoscapeInterop.resetView");
        });
    }

    private void AddThreeNodeGraph()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetUniverseGraphAsync), _ =>
                Task.FromResult<UniverseGraphResponse?>(new UniverseGraphResponse
                {
                    Universe = new UniverseInfo { Qid = "Q1", Label = "Dune" },
                    Nodes =
                    [
                        new GraphNodeDto { Id = "Q10", Label = "Paul Atreides", Type = "Character" },
                        new GraphNodeDto { Id = "Q11", Label = "Arrakis", Type = "Location" },
                        new GraphNodeDto { Id = "Q12", Label = "Bene Gesserit", Type = "Organization" },
                    ],
                    Edges =
                    [
                        new GraphEdgeDto { Source = "Q10", Target = "Q11", Type = "residence", Label = "resides in" },
                        new GraphEdgeDto { Source = "Q10", Target = "Q12", Type = "member", Label = "member of" },
                    ],
                }));
            stub.SetHandler(nameof(IEngineApiClient.CheckLoreDeltaAsync), _ =>
                Task.FromResult<IReadOnlyList<LoreDeltaResultDto>>([]));
        }));
    }

    [Fact]
    public void ChronicleExplorer_RendersGraphEmptyState()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetUniverseGraphAsync), _ =>
                Task.FromResult<UniverseGraphResponse?>(new UniverseGraphResponse
                {
                    Universe = new UniverseInfo { Qid = "Q1", Label = "Unenriched Universe" },
                }));
            stub.SetHandler(nameof(IEngineApiClient.CheckLoreDeltaAsync), _ =>
                Task.FromResult<IReadOnlyList<LoreDeltaResultDto>>([]));
        }));
        Services.AddScoped<DeviceContextService>();

        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<ChronicleExplorer>(1);
            builder.AddAttribute(2, nameof(ChronicleExplorer.Qid), "Q1");
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Universe Graph unavailable", cut.Markup);
            Assert.DoesNotContain("cytoscape-container", cut.Markup);
        });
    }
}
