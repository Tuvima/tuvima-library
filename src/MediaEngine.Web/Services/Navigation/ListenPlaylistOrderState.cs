using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// Reads, updates, and serializes the profile-owned Listen playlist order without
/// coupling page components to the navigation-config JSON representation.
/// </summary>
public static class ListenPlaylistOrderState
{
    private const string PropertyName = "listenPlaylistOrder";

    public static List<Guid> Read(string? navigationConfig)
    {
        try
        {
            var order = Parse(navigationConfig)[PropertyName] as JsonArray;
            return order?
                .Select(node => Guid.TryParse(node?.GetValue<string>(), out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList() ?? [];
        }
        catch (InvalidOperationException)
        {
            // A syntactically valid config can still contain an unexpected node type.
            return [];
        }
    }

    public static string Write(string? navigationConfig, IEnumerable<Guid> orderedIds)
    {
        var root = Parse(navigationConfig);
        root[PropertyName] = new JsonArray(
            orderedIds.Distinct().Select(id => JsonValue.Create(id.ToString("D"))).ToArray<JsonNode?>());
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>
    /// The order playlists are listed in, in the sidebar and on the Playlists page (spec listen-playlists D3): the ones this profile pinned
    /// come first, then every playlist in the order the profile keeps (it follows the person across devices); a playlist the saved order
    /// does not know yet follows the rest, newest first.
    /// </summary>
    public static List<ManagedCollectionViewModel> Order(IEnumerable<ManagedCollectionViewModel> playlists, IReadOnlyList<Guid> savedOrder)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(savedOrder);
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

    public static bool TryMoveByOffset(
        IReadOnlyList<Guid> orderedIds,
        Guid playlistId,
        int direction,
        out List<Guid> updatedOrder)
    {
        updatedOrder = orderedIds.ToList();
        var index = updatedOrder.IndexOf(playlistId);
        if (index < 0 || updatedOrder.Count == 0)
        {
            return false;
        }

        var targetIndex = Math.Clamp(index + direction, 0, updatedOrder.Count - 1);
        if (targetIndex == index)
        {
            return false;
        }

        updatedOrder.RemoveAt(index);
        updatedOrder.Insert(targetIndex, playlistId);
        return true;
    }

    public static bool TryMoveBefore(
        IReadOnlyList<Guid> orderedIds,
        Guid playlistId,
        Guid targetPlaylistId,
        out List<Guid> updatedOrder)
    {
        updatedOrder = orderedIds.ToList();
        if (playlistId == targetPlaylistId)
        {
            return false;
        }

        var sourceIndex = updatedOrder.IndexOf(playlistId);
        var targetIndex = updatedOrder.IndexOf(targetPlaylistId);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return false;
        }

        updatedOrder.RemoveAt(sourceIndex);
        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        updatedOrder.Insert(targetIndex, playlistId);
        return true;
    }

    private static JsonObject Parse(string? navigationConfig)
    {
        try
        {
            return JsonNode.Parse(navigationConfig ?? "{}") as JsonObject ?? [];
        }
        catch (JsonException)
        {
            // Invalid profile JSON is treated as an empty current-format configuration.
            return [];
        }
    }
}
