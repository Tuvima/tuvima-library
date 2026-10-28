using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The plugin dependency map (Plugin architecture spec C22): what a plugin needs is checked, a plugin waits instead of running
/// when a required plugin is off, switching on offers the needed plugins under one approval, switching off offers to switch off
/// the plugins that need it, and plugins that need each other in a circle are refused.
/// </summary>
public sealed class PluginDependencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-dependencies", Guid.NewGuid().ToString("N"));
    private readonly ApprovalTestKit _kit = new();
    private readonly ConfigurationDirectoryLoader _loader;

    public PluginDependencyTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
    }

    public void Dispose()
    {
        _kit.Dispose();
        _loader.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    private static PluginManifest Manifest(string id, string name, string version = "1.0.0", params PluginRequirement[] needs) => new()
    {
        Id = id,
        Name = name,
        Version = version,
        Declaration = new PluginDeclaration { Requires = [.. needs] },
    };

    private static PluginRequirement Needs(string id, bool optional = false, string? range = null) =>
        new() { Kind = "plugin", Id = id, Optional = optional, VersionRange = range };

    private PluginCatalog Catalog(params PluginManifest[] manifests) => new(
        manifests.Select(manifest => (ITuvimaPlugin)new BarePlugin(manifest)).ToList(),
        new PluginSettingsService(_loader),
        _loader,
        AllowAllPluginApprovals.Instance,
        NullLogger<PluginCatalog>.Instance);

    // ── The map ─────────────────────────────────────────────────────────────

    [Fact]
    public void WhatAPluginNeeds_IsReadyOffMissingOrTheWrongVersion()
    {
        var catalog = Catalog(
            Manifest("a.base", "Base", "1.0.0"),
            Manifest("a.old", "Old", "0.5.0"),
            Manifest("a.user", "User", "1.0.0", Needs("a.base"), Needs("a.old", range: "1.0.0"), Needs("a.gone")));
        catalog.SetEnabled("a.base", true);

        var needs = PluginDependencyGraph.Needs(catalog.Get("a.user")!, catalog.List());

        Assert.Equal(
            [("a.base", PluginDependencyState.Ready), ("a.old", PluginDependencyState.WrongVersion), ("a.gone", PluginDependencyState.Missing)],
            needs.Select(need => (need.Id, need.State)));
    }

    [Fact]
    public void APluginWhoseRequiredPluginIsOff_WaitsForIt_ButAnOptionalOneOnlyImprovesResults()
    {
        var catalog = Catalog(
            Manifest("a.base", "Skip markers"),
            Manifest("a.extra", "Extra"),
            Manifest("a.user", "Commercial skip", "1.0.0", Needs("a.base"), Needs("a.extra", optional: true)));

        Assert.Equal("Waiting for Skip markers", catalog.WaitingFor("a.user"));

        catalog.SetEnabled("a.base", true);

        Assert.Null(catalog.WaitingFor("a.user"));
    }

    [Theory]
    [InlineData(null, "3.0.0", true)]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("1.0.0", "2.4.1", true)]
    [InlineData("1.0.0", "0.9.0", false)]
    [InlineData("1.0.0-2.0.0", "2.0.0", true)]
    [InlineData("1.0.0-2.0.0", "2.0.1", false)]
    [InlineData("1.0.0", "1.2.0-beta+7", true)]
    [InlineData("1.0.0", "not-a-version", false)]
    public void AVersionRange_MeansThisOrNewer_OrAnInclusiveSpan(string? range, string version, bool fits)
    {
        Assert.Equal(fits, PluginDependencyGraph.VersionFits(range, version));
    }

    [Fact]
    public void SwitchingOn_ListsNeededPluginsDependenciesFirst_AndLeavesOutOnesAlreadyOn()
    {
        var catalog = Catalog(
            Manifest("a.root", "Root"),
            Manifest("a.middle", "Middle", "1.0.0", Needs("a.root")),
            Manifest("a.top", "Top", "1.0.0", Needs("a.middle"), Needs("a.root")));

        var order = PluginDependencyGraph.EnableOrder("a.top", catalog.List(), out var blocked);

        Assert.Empty(blocked);
        Assert.Equal(["a.root", "a.middle", "a.top"], order.Select(plugin => plugin.Manifest.Id));

        catalog.SetEnabled("a.root", true);
        Assert.Equal(["a.middle", "a.top"], PluginDependencyGraph.EnableOrder("a.top", catalog.List(), out _).Select(plugin => plugin.Manifest.Id));
    }

    [Fact]
    public void SwitchingOn_CannotListANeededPluginThatIsNotInstalled()
    {
        var catalog = Catalog(Manifest("a.user", "User", "1.0.0", Needs("a.gone")));

        var order = PluginDependencyGraph.EnableOrder("a.user", catalog.List(), out var blocked);

        Assert.Equal(["a.user"], order.Select(plugin => plugin.Manifest.Id));
        Assert.Contains("User needs a.gone, which is not installed.", blocked);
    }

    [Fact]
    public void ThoseWhoNeedAPlugin_AreFoundAcrossTheChain_NotCountingOptionalOnesOrOnesThatAreOff()
    {
        var catalog = Catalog(
            Manifest("a.root", "Root"),
            Manifest("a.middle", "Middle", "1.0.0", Needs("a.root")),
            Manifest("a.top", "Top", "1.0.0", Needs("a.middle")),
            Manifest("a.casual", "Casual", "1.0.0", Needs("a.root", optional: true)),
            Manifest("a.idle", "Idle", "1.0.0", Needs("a.root")));
        foreach (var id in new[] { "a.root", "a.middle", "a.top", "a.casual" })
        {
            catalog.SetEnabled(id, true);
        }

        var dependants = PluginDependencyGraph.Dependants("a.root", catalog.List());

        Assert.Equal(["a.middle", "a.top"], dependants.Select(plugin => plugin.Manifest.Id).Order());
    }

    [Fact]
    public void PluginsThatNeedEachOtherInACircle_AreRefusedWhenTheCatalogueLoads_AndOthersAreNot()
    {
        var catalog = Catalog(
            Manifest("a.one", "One", "1.0.0", Needs("a.two")),
            Manifest("a.two", "Two", "1.0.0", Needs("a.one")),
            Manifest("a.fine", "Fine", "1.0.0", Needs("a.one")));

        Assert.Contains("circle", catalog.Get("a.one")!.LoadError);
        Assert.Contains("Two", catalog.Get("a.one")!.LoadError);
        Assert.NotNull(catalog.Get("a.two")!.LoadError);
        Assert.Null(catalog.Get("a.fine")!.LoadError);
        Assert.Throws<PluginCannotBeEnabledException>(() => catalog.SetEnabled("a.one", true));
    }

    [Fact]
    public void PluginsStartInDependencyOrder_EvenWhenTheirNamesSortTheOtherWay()
    {
        var catalog = Catalog(
            Manifest("a.alpha", "Alpha", "1.0.0", Needs("a.zulu")),
            Manifest("a.zulu", "Zulu"));

        Assert.Equal(["a.zulu", "a.alpha"], catalog.ListInStartOrder().Select(plugin => plugin.Manifest.Id));
    }

    // ── Switching on and off ────────────────────────────────────────────────

    [Fact]
    public async Task SwitchingOnAPluginThatNeedsAnOffPlugin_AsksFirst_AndChangesNothing()
    {
        var catalog = Catalog(Manifest("a.base", "Base"), Manifest("a.user", "User", "1.0.0", Needs("a.base")));

        var (status, body) = await Execute(await Enable(catalog, "a.user", new PluginEnableRequest()));

        Assert.Equal(409, status);
        Assert.Contains("needs_off", body);
        Assert.Contains("User needs Base", body);
        Assert.False(catalog.Get("a.base")!.Enabled);
        Assert.False(catalog.Get("a.user")!.Enabled);
    }

    [Fact]
    public async Task SwitchingOnWithTheNeededPlugins_ShowsOneApprovalForAll_ThenSwitchesThemOnDependenciesFirst()
    {
        var catalog = Catalog(Manifest("a.base", "Base"), Manifest("a.user", "User", "1.0.0", Needs("a.base")));

        var (firstStatus, firstBody) = await Execute(await Enable(catalog, "a.user", new PluginEnableRequest { AlsoSwitchOnNeeded = true }));
        Assert.Equal(409, firstStatus);
        var required = JsonSerializer.Deserialize<ApprovalRequiredResponse>(firstBody)!;
        Assert.Equal(["a.base", "a.user"], required.Approvals.Select(preview => preview.ItemId));
        Assert.False(catalog.Get("a.base")!.Enabled);

        var hashes = required.Approvals.Select(preview => _kit.Service.PreviewPlugin(catalog.Get(preview.ItemId)!).StatementHash).ToList();
        var (status, body) = await Execute(await Enable(catalog, "a.user", new PluginEnableRequest { AlsoSwitchOnNeeded = true, ApprovalHashes = hashes }));

        Assert.Equal(200, status);
        Assert.Contains("\"also_changed\":[\"a.base\"]", body);
        Assert.True(catalog.Get("a.base")!.Enabled);
        Assert.True(catalog.Get("a.user")!.Enabled);
        Assert.Equal(2, (await _kit.Records.ListAsync("plugin", "a.base")).Count + (await _kit.Records.ListAsync("plugin", "a.user")).Count);
    }

    [Fact]
    public async Task ApprovingOnlyThePluginItself_IsNotEnoughWhenTheNeededOneIsNotApproved()
    {
        var catalog = Catalog(Manifest("a.base", "Base"), Manifest("a.user", "User", "1.0.0", Needs("a.base")));
        var userHash = _kit.Service.PreviewPlugin(catalog.Get("a.user")!).StatementHash;

        var (status, body) = await Execute(await Enable(catalog, "a.user", new PluginEnableRequest { AlsoSwitchOnNeeded = true, ApprovalHash = userHash }));

        Assert.Equal(409, status);
        Assert.Contains("approval_required", body);
        Assert.False(catalog.Get("a.user")!.Enabled);
    }

    [Fact]
    public async Task SwitchingOnAPluginThatNeedsOneThatIsNotInstalled_ExplainsWhy()
    {
        var catalog = Catalog(Manifest("a.user", "User", "1.0.0", Needs("a.gone")));

        var (status, body) = await Execute(await Enable(catalog, "a.user", new PluginEnableRequest { AlsoSwitchOnNeeded = true }));

        Assert.Equal(409, status);
        Assert.Contains("needs_unavailable", body);
        Assert.Contains("not installed", body);
    }

    [Fact]
    public async Task SwitchingOffAPluginOthersNeed_AsksFirst_AndWithTheOptionSwitchesTheOthersOffFirst()
    {
        var catalog = Catalog(
            Manifest("a.base", "Base"),
            Manifest("a.middle", "Middle", "1.0.0", Needs("a.base")),
            Manifest("a.top", "Top", "1.0.0", Needs("a.middle")));
        foreach (var id in new[] { "a.base", "a.middle", "a.top" })
        {
            catalog.SetEnabled(id, true);
        }

        var (askedStatus, askedBody) = await Execute(await Disable(catalog, "a.base", new PluginDisableRequest()));
        Assert.Equal(409, askedStatus);
        Assert.Contains("has_dependants", askedBody);
        Assert.Contains("Middle and Top", askedBody);
        Assert.True(catalog.Get("a.base")!.Enabled);

        var (status, body) = await Execute(await Disable(catalog, "a.base", new PluginDisableRequest { AlsoSwitchOffDependants = true }));

        Assert.Equal(200, status);
        Assert.Contains("\"also_changed\":[\"a.top\",\"a.middle\"]", body);
        Assert.All(catalog.List(), plugin => Assert.False(plugin.Enabled));
    }

    [Fact]
    public async Task SwitchingOffAPluginNobodyNeeds_JustWorks()
    {
        var catalog = Catalog(Manifest("a.base", "Base"));
        catalog.SetEnabled("a.base", true);

        var (status, _) = await Execute(await Disable(catalog, "a.base", null));

        Assert.Equal(200, status);
        Assert.False(catalog.Get("a.base")!.Enabled);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<IResult> Enable(PluginCatalog catalog, string pluginId, PluginEnableRequest request) =>
        await PluginEndpoints.EnablePlugin(pluginId, request, _kit.Http(), catalog, _kit.Service, _kit.Resolver, default);

    private async Task<IResult> Disable(PluginCatalog catalog, string pluginId, PluginDisableRequest? request) =>
        await PluginEndpoints.DisablePlugin(
            pluginId,
            request,
            _kit.Http(),
            catalog,
            new AiFeatureRetractionService(null!, NullLogger<AiFeatureRetractionService>.Instance),
            new PluginActivityService(new PluginActivityRepository(_kit.Database), new PluginCircuitBreaker(TimeProvider.System), TimeProvider.System, NullLogger<PluginActivityService>.Instance),
            new PluginPeopleActionLog(new SystemActivityRepository(_kit.Database), NullLogger<PluginPeopleActionLog>.Instance),
            _kit.Resolver,
            default);

    private static async Task<(int Status, string Body)> Execute(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class BarePlugin(PluginManifest manifest) : ITuvimaPlugin
    {
        public PluginManifest Manifest { get; } = manifest;

        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
    }
}
