using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>One action a switched-on plugin or source offers on the player, ready to draw (Plugin architecture spec C4, PA7).</summary>
public sealed record PlayerPluginAction(
    string PluginId,
    string PluginName,
    string ActionId,
    string Kind,
    string Label,
    string Slot,
    int Priority,
    bool Edits);

/// <summary>
/// Lists the actions the player may offer for one song or video, to one person. Only actions the Library knows how to run are listed, and
/// only for a plugin or source that is switched on, whose audience includes the person, that may work on this item (the Vault rule and
/// library limits, through the central gate), and that works on this kind of media. The player draws its own fixed buttons for the core
/// kinds; everything else is a row in the one overflow menu.
/// </summary>
public sealed class PlayerPluginActionService(
    PluginCatalog catalog,
    IConfigurationLoader configuration,
    IMediaAssetRepository assets,
    IWorkRepository works,
    IPluginAccessGate gate)
{
    /// <summary>The kinds the Library can run on one item today.</summary>
    public static readonly IReadOnlyList<string> RunnableKinds = ["find_lyrics", "find_subtitles", "rescan_skip_markers"];

    public async Task<IReadOnlyList<PlayerPluginAction>> ListAsync(Guid assetId, PluginAudience requester, CancellationToken ct)
    {
        var asset = await assets.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return [];
        }

        var mediaType = (await works.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false))?.MediaType;
        var offered = new List<PlayerPluginAction>();
        foreach (var plugin in catalog.List().Where(plugin => plugin.Enabled && plugin.LoadError is null))
        {
            await AddAsync(offered, plugin.Manifest.Id, plugin.Manifest.Name, plugin.Manifest.Declaration, plugin.Manifest.ResolvedDeclaration.Audience,
                plugin.Manifest.MediaTypes, mediaType, asset.LibraryId, assetId, requester, ct).ConfigureAwait(false);
        }

        foreach (var source in configuration.LoadAllProviders().Where(source => source.Enabled))
        {
            var resolved = PluginDeclarationRules.Resolve(source.Declaration, source.Endpoints.Count > 0, source.CanHandle?.MediaTypes, null);
            await AddAsync(offered, source.Name, source.DisplayName ?? source.Name, source.Declaration, resolved.Audience,
                source.CanHandle?.MediaTypes ?? [], mediaType, asset.LibraryId, assetId, requester, ct).ConfigureAwait(false);
        }

        return offered
            .OrderBy(action => action.Priority)
            .ThenBy(action => action.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task AddAsync(
        List<PlayerPluginAction> offered,
        string itemId,
        string itemName,
        PluginDeclaration? declaration,
        PluginAudience itemAudience,
        IReadOnlyList<string> worksOn,
        MediaType? mediaType,
        string? libraryId,
        Guid assetId,
        PluginAudience requester,
        CancellationToken ct)
    {
        var actions = (declaration?.Actions ?? [])
            .Where(action => RunnableKinds.Contains(action.Kind, StringComparer.Ordinal))
            .Where(action => action.Slot is "player_overflow" or "player_core" or "panel")
            .Where(action => PluginDeclarationRules.ActionAudience(action, itemAudience) >= requester)
            .ToList();
        if (actions.Count == 0 || !WorksOn(worksOn, mediaType))
        {
            return;
        }

        if (await gate.CheckAsync(new PluginAccessQuery(itemId, requester, libraryId, assetId), ct).ConfigureAwait(false) != PluginAccessOutcome.Allowed)
        {
            return;
        }

        offered.AddRange(actions.Select(action => new PlayerPluginAction(
            itemId, itemName, action.Id, action.Kind, action.Label, action.Slot!, action.Priority, PluginDeclarationRules.ActionEdits(action))));
    }

    // An empty list means "all kinds"; otherwise the item must name this kind of media.
    private static bool WorksOn(IReadOnlyList<string> worksOn, MediaType? mediaType) =>
        worksOn.Count == 0
        || mediaType is null
        || worksOn.Any(word => string.Equals(word, mediaType.ToString(), StringComparison.OrdinalIgnoreCase));
}
