using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Contracts.Collections;

namespace MediaEngine.Api.Services.Collections;

/// <summary>
/// Which collections are playlists a person sees, and the order the Dashboard's Listen page lists them in, so a paired app
/// shows the same list (spec listen-playlists D3): the ones this profile pinned come first, then every playlist in the order
/// the profile keeps (<c>listenPlaylistOrder</c> in its navigation config, which follows the person across devices); a
/// playlist the saved order does not know yet follows the rest, newest first.
/// </summary>
internal static class PlaylistListOrder
{
    private const string OrderProperty = "listenPlaylistOrder";

    /// <summary>The same three types the Dashboard lists as playlists (MediaTileComposerService.IsUserVisiblePlaylist).</summary>
    public static bool IsUserVisiblePlaylist(ManagedCollectionDto collection) =>
        string.Equals(collection.CollectionType, "Playlist", StringComparison.OrdinalIgnoreCase)
        || string.Equals(collection.CollectionType, "Smart", StringComparison.OrdinalIgnoreCase)
        || string.Equals(collection.CollectionType, "PlaylistFolder", StringComparison.OrdinalIgnoreCase);

    public static List<ManagedCollectionDto> Order(IEnumerable<ManagedCollectionDto> playlists, string? navigationConfig)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        var savedOrder = ReadSavedOrder(navigationConfig);
        var positions = new Dictionary<Guid, int>();
        for (var index = 0; index < savedOrder.Count; index++)
        {
            positions.TryAdd(savedOrder[index], index);
        }

        return [.. playlists
            .OrderByDescending(playlist => playlist.IsPinned == true)
            .ThenBy(playlist => positions.GetValueOrDefault(playlist.Id, int.MaxValue))
            .ThenByDescending(playlist => playlist.ModifiedAt ?? playlist.CreatedAt)
            .ThenBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<Guid> ReadSavedOrder(string? navigationConfig)
    {
        if (string.IsNullOrWhiteSpace(navigationConfig))
        {
            return [];
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(navigationConfig);
        }
        catch (JsonException)
        {
            // A profile whose saved navigation config is not valid JSON simply has no saved playlist order.
            return [];
        }

        if (root is not JsonObject config || config[OrderProperty] is not JsonArray order)
        {
            return [];
        }

        var ids = new List<Guid>();
        foreach (var node in order)
        {
            if (node is JsonValue value
                && value.TryGetValue<string>(out var text)
                && Guid.TryParse(text, out var id)
                && id != Guid.Empty
                && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
