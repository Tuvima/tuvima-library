using System.Globalization;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Tells a plugin that shares plays with an outside service what was played (Plugin architecture spec PA6). The Library decides what the
/// plugin may see before it answers: only music, only where the central access gate allows the plugin (a Vault item, or a library the
/// plugin is limited away from, gets no answer), and only a track it can name. The plugin never gets a file path or a way to ask about
/// anything else.
/// </summary>
public sealed class PluginPlayDetailsService(
    IMediaAssetRepository assets,
    IWorkRepository works,
    ICanonicalValueRepository canonicalValues,
    IBridgeIdRepository bridgeIds,
    IVaultLibraryRepository vault,
    IPluginAccessGate gate)
{
    public async Task<PluginPlayedTrack?> GetTrackAsync(string pluginId, Guid assetId, CancellationToken ct)
    {
        if (assetId == Guid.Empty)
        {
            return null;
        }

        var asset = await assets.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return null;
        }

        // A Vault item is never described to a plugin, whatever the plugin declares about the Vault: its own declaration could otherwise
        // let it through. The gate below still applies the library limit.
        if (!string.IsNullOrWhiteSpace(asset.LibraryId) && (await vault.GetIdsAsync(ct).ConfigureAwait(false)).Contains(asset.LibraryId))
        {
            return null;
        }

        // The plugin's own audience was already applied when the event was handed to it; here only the Vault rule and the library limit
        // decide, so no requester is named.
        if (await gate.CheckAsync(new PluginAccessQuery(pluginId, null, asset.LibraryId, asset.Id), ct).ConfigureAwait(false) != PluginAccessOutcome.Allowed)
        {
            return null;
        }

        var lineage = await works.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false);
        if (lineage is null || lineage.MediaType != MediaType.Music)
        {
            return null;
        }

        var entityIds = new List<Guid> { asset.Id, lineage.WorkId, lineage.TargetForParentScope }.Distinct().ToList();
        var canonical = await canonicalValues.GetByEntitiesAsync(entityIds, ct).ConfigureAwait(false);
        var bridges = await bridgeIds.GetByEntitiesAsync(entityIds, ct).ConfigureAwait(false);

        // The track's own facts win over its album's, so the asset, then the work, then the parent are read in that order.
        var values = entityIds.Where(canonical.ContainsKey).SelectMany(id => canonical[id])
            .GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        var ids = bridges.Values.SelectMany(group => group)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.IdValue))
            .GroupBy(entry => entry.IdType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.IdValue.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);

        var title = Value(values, MetadataFieldConstants.Title);
        var artist = Value(values, MetadataFieldConstants.Artist);
        if (title is null || artist is null)
        {
            // A track with no name or no artist cannot be shared in a way the other service could use.
            return null;
        }

        return new PluginPlayedTrack(
            title,
            artist,
            Value(values, MetadataFieldConstants.Album),
            ParseSeconds(Value(values, MetadataFieldConstants.DurationField)),
            Single(ids, BridgeIdKeys.MusicBrainzRecordingId),
            Single(ids, BridgeIdKeys.MusicBrainzReleaseId),
            ids.TryGetValue(BridgeIdKeys.MusicBrainzArtistId, out var artistIds) ? artistIds : [],
            int.TryParse(Value(values, MetadataFieldConstants.TrackNumber), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null);
    }

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    // Only an id that is certain is passed on; two different ids for one track mean the Library is unsure, so neither is shared.
    private static string? Single(IReadOnlyDictionary<string, List<string>> ids, string key) =>
        ids.TryGetValue(key, out var found) && found.Count == 1 ? found[0] : null;

    private static double? ParseSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds > 0 ? seconds : null;
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) && span > TimeSpan.Zero ? span.TotalSeconds : null;
    }
}

/// <summary>Gives one plugin the details of what was played, only with the <c>play-details</c> permission.</summary>
internal sealed class BoundPluginPlayDetails(string pluginId, IPluginPermissionGate permissions, PluginPlayDetailsService service) : IPluginPlayDetails
{
    public Task<PluginPlayedTrack?> GetTrackAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var registration = permissions.Demand(pluginId, PluginPermissionIds.PlayDetails);
        return service.GetTrackAsync(registration.Manifest.Id, assetId, cancellationToken);
    }
}

/// <summary>Tells one plugin whether a profile has linked one of the plugin's own personal credentials. Never the value.</summary>
internal sealed class BoundPluginProfileLinks(string pluginId, IPluginPermissionGate permissions, PluginProfileConnectionService connections) : IPluginProfileLinks
{
    public async Task<bool> IsLinkedAsync(Guid profileId, string credentialName, CancellationToken cancellationToken = default)
    {
        var registration = permissions.Demand(pluginId, PluginPermissionIds.PluginOutbox);
        if (profileId == Guid.Empty || string.IsNullOrWhiteSpace(credentialName) || !connections.IsPersonalCredential(registration.Manifest.Id, credentialName))
        {
            return false;
        }

        return await connections.IsLinkedAsync(registration.Manifest.Id, profileId, credentialName, cancellationToken).ConfigureAwait(false);
    }
}
