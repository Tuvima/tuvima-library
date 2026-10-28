using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The actions the player may offer for one item (Plugin architecture spec C4, PA7): only switched-on plugins and sources, only actions the
/// Library can run, only within each action's audience, only where the central gate allows the item, and in menu order.
/// </summary>
public sealed class PlayerPluginActionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-player-actions", Guid.NewGuid().ToString("N"));
    private readonly ApprovalTestKit _kit = new();
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginCatalog _catalog;
    private readonly Guid _asset = Guid.NewGuid();
    private readonly HashSet<string> _blocked = [];
    private MediaType _mediaType = MediaType.Music;

    public PlayerPluginActionTests()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(config, "providers"));
        File.WriteAllText(
            Path.Combine(config, "providers", "lyrics_source.json"),
            """
            { "name": "lyrics_source", "version": "1.0", "enabled": true, "can_handle": { "media_types": ["Music"] }, "endpoints": { "api": "https://lyrics.example" },
              "declaration": { "audience": "everyone", "actions": [ { "id": "find-lyrics", "kind": "find_lyrics", "label": "Look up lyrics", "slot": "panel" } ] } }
            """);
        File.WriteAllText(
            Path.Combine(config, "providers", "off_source.json"),
            """
            { "name": "off_source", "version": "1.0", "enabled": false, "endpoints": { "api": "https://off.example" },
              "declaration": { "audience": "everyone", "actions": [ { "id": "find-lyrics", "kind": "find_lyrics", "label": "Off lyrics", "slot": "panel" } ] } }
            """);
        _loader = new ConfigurationDirectoryLoader(config);
        _catalog = new PluginCatalog(
            [new ActionPlugin("test.skip", ["rescan_skip_markers", "refresh_lore"], "administrators", ["Videos"]),
             new ActionPlugin("test.second", ["rescan_skip_markers"], "everyone", ["Music"], priority: 10, label: "Second thing")],
            new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _catalog.SetEnabled("test.skip", true);
        _catalog.SetEnabled("test.second", true);
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

    [Fact]
    public async Task AnOrdinaryPerson_SeesOnlyWhatTheirAudienceAllows_InMenuOrder()
    {
        var actions = await Service().ListAsync(_asset, PluginAudience.Everyone, CancellationToken.None);

        Assert.Equal(["test.second", "lyrics_source"], actions.Select(action => action.PluginId));
        Assert.Equal(["rescan_skip_markers", "find_lyrics"], actions.Select(action => action.Kind));
    }

    [Fact]
    public async Task AnAdministrator_AlsoSeesTheAdministratorsOnlyAction_ForMediaItWorksOn()
    {
        _mediaType = MediaType.Videos;

        var actions = await Service().ListAsync(_asset, PluginAudience.Administrators, CancellationToken.None);

        Assert.Equal(["test.skip"], actions.Select(action => action.PluginId));
        Assert.DoesNotContain(actions, action => action.Kind == "refresh_lore");
    }

    [Fact]
    public async Task AnItemTheGateBlocks_OffersNothingFromThatItem()
    {
        _blocked.Add("lyrics_source");

        var actions = await Service().ListAsync(_asset, PluginAudience.Everyone, CancellationToken.None);

        Assert.DoesNotContain(actions, action => action.PluginId == "lyrics_source");
    }

    [Fact]
    public async Task APluginSwitchedOff_AndASourceSwitchedOff_OfferNothing()
    {
        _catalog.SetEnabled("test.second", false);

        var actions = await Service().ListAsync(_asset, PluginAudience.Everyone, CancellationToken.None);

        Assert.DoesNotContain(actions, action => action.PluginId == "test.second");
        Assert.DoesNotContain(actions, action => action.PluginId == "off_source");
    }

    private PlayerPluginActionService Service() => new(
        _catalog,
        _loader,
        MethodStub.Create<IMediaAssetRepository>(("FindByIdAsync", _ => Task.FromResult<MediaAsset?>(new MediaAsset { Id = _asset, LibraryId = "lib-1" }))),
        MethodStub.Create<IWorkRepository>(("GetLineageByAssetAsync", _ => Task.FromResult<WorkLineage?>(
            new WorkLineage(_asset, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), WorkKind.Standalone, _mediaType)))),
        new BlockingGate(_blocked));

    private sealed class BlockingGate(HashSet<string> blocked) : IPluginAccessGate
    {
        public Task<PluginAccessOutcome> CheckAsync(PluginAccessQuery query, CancellationToken ct = default) =>
            Task.FromResult(blocked.Contains(query.ItemId) ? PluginAccessOutcome.VaultBlocked : PluginAccessOutcome.Allowed);
    }

    private sealed class ActionPlugin(string id, string[] kinds, string audience, string[] mediaTypes, int priority = 50, string label = "Do it") : ITuvimaPlugin
    {
        public string Kind => "action-test";

        public PluginManifest Manifest => new()
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            MediaTypes = mediaTypes,
            Declaration = new PluginDeclaration
            {
                Audience = "everyone",
                Actions = kinds.Select(kind => new PluginActionDeclaration
                {
                    Id = kind.Replace('_', '-'),
                    Kind = kind,
                    Label = label,
                    Slot = "player_overflow",
                    Audience = audience,
                    Priority = priority,
                }).ToList(),
            },
        };

        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
    }
}
