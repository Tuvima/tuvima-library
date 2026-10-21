using System.Globalization;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Vault;

/// <summary>
/// The Vault's lists and search, read from the Engine's Vault routes (Videos spec D39 to D41). The Engine answers only a device
/// session with NSFW mode on; for a locked session every call comes back "not available" and nothing is drawn. Performers, studios
/// and tags are their own lists and never appear in the main page's lists; an unmatched video appears only in Recently added,
/// Scenes, Folders and Unmatched.
/// </summary>
public sealed class EngineVaultCatalogue(IEngineApiClient api) : IVaultCatalogue
{
    public const int DefaultPageSize = 120;

    // The sort names the Engine's scene list understands.
    private const string SortDateAdded = "date_added";
    private const string SortTitle = "title";
    private const string SortReleaseDate = "release_date";
    private const string SortDuration = "duration";

    public async Task<VaultCatalogueResult> ListAsync(
        string viewKey, VaultViewPreferences preferences, VaultListFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new VaultListFilter();

        // A tile that opens to its scenes: the list is the owned scenes of that performer, studio, tag or folder.
        if (filter.PerformerId is not null || filter.StudioId is not null || filter.TagId is not null || filter.FolderId is not null)
        {
            return await ScenesAsync(preferences, filter, newestFirst: false, forceMatch: null, ct);
        }

        switch (viewKey)
        {
            case VaultViews.RecentlyAdded:
                return await ScenesAsync(preferences, filter, newestFirst: true, forceMatch: null, ct);
            case "scenes":
                return await ScenesAsync(preferences, filter with { Match = "all" }, newestFirst: false, forceMatch: null, ct);
            case "unmatched":
                return await ScenesAsync(preferences, filter, newestFirst: false, forceMatch: "unmatched", ct);
            case "folders":
                var folders = await api.GetVaultFoldersAsync(ct);
                return folders.Succeeded && folders.Value is { } rows
                    ? new VaultCatalogueResult(true, rows.Select(Folder).ToList())
                    : VaultCatalogueResult.NotYetAvailable;
            case "performers":
            case "studios":
            case "tags":
                var names = await api.GetVaultNamesAsync(viewKey, "name", false, ct);
                return names.Succeeded && names.Value is { } tiles
                    ? new VaultCatalogueResult(true, tiles.Select(tile => Tile(viewKey, tile)).ToList())
                    : VaultCatalogueResult.NotYetAvailable;
            case "images":
                // Images arrive with the Vault image galleries; until then the list is simply empty.
                return new VaultCatalogueResult(true, []);
            default:
                return VaultCatalogueResult.NotYetAvailable;
        }
    }

    public async Task<VaultCatalogueResult> SearchAsync(string query, CancellationToken ct = default)
    {
        var call = await api.SearchVaultAsync(query, ct);
        if (!call.Succeeded || call.Value is not { } result)
        {
            return VaultCatalogueResult.NotYetAvailable;
        }

        var items = new List<VaultCatalogueItem>();
        items.AddRange(result.Scenes.Select(scene => Scene(scene, hover: false)));
        items.AddRange(result.Performers.Select(tile => Tile("performers", tile)));
        items.AddRange(result.Studios.Select(tile => Tile("studios", tile)));
        return new VaultCatalogueResult(true, items);
    }

    public async Task<VaultSceneDetail?> GetSceneAsync(Guid assetId, CancellationToken ct = default)
    {
        var call = await api.GetVaultSceneAsync(assetId, ct);
        if (!call.Succeeded || call.Value is not { } detail)
        {
            return null;
        }

        return new VaultSceneDetail(
            Scene(detail.Scene, hover: false),
            detail.Scene.WorkId,
            detail.Description,
            detail.MatchedBy,
            detail.Performers.Select(n => new VaultLinkedName(n.Id, n.Name)).ToList(),
            detail.Studios.Select(n => new VaultLinkedName(n.Id, n.Name)).ToList(),
            detail.Tags.Select(n => new VaultLinkedName(n.Id, n.Name)).ToList());
    }

    private async Task<VaultCatalogueResult> ScenesAsync(
        VaultViewPreferences preferences, VaultListFilter filter, bool newestFirst, string? forceMatch, CancellationToken ct)
    {
        // Images arrive with the Vault image galleries; asking for images only lists no videos.
        if (filter.Kind == "images")
        {
            return new VaultCatalogueResult(true, []);
        }

        var (sort, descending) = newestFirst ? (SortDateAdded, true) : SortFor(preferences.Sort);
        var call = await api.GetVaultScenesAsync(
            forceMatch ?? filter.Match, sort, descending, filter.PerformerId, filter.StudioId, filter.TagId, filter.FolderId,
            filter.Skip, filter.Take > 0 ? filter.Take : DefaultPageSize, ct);
        if (!call.Succeeded || call.Value is not { } list)
        {
            return VaultCatalogueResult.NotYetAvailable;
        }

        return new VaultCatalogueResult(true, list.Items.Select(scene => Scene(scene, preferences.HoverStills)).ToList())
        {
            Heading = list.Heading,
            Total = list.Total,
        };
    }

    // "Recently played" is not offered by the Engine: watching the Vault leaves no trace (D33), so it falls back to date added.
    private static (string Sort, bool Descending) SortFor(VaultSort sort) => sort switch
    {
        VaultSort.Title => (SortTitle, false),
        VaultSort.Year => (SortReleaseDate, true),
        VaultSort.Duration => (SortDuration, true),
        _ => (SortDateAdded, true),
    };

    private static VaultCatalogueItem Scene(VaultSceneDto scene, bool hover) => new(
        scene.Id.ToString("D"),
        scene.Title,
        SceneSubtitle(scene),
        scene.ImageId is { } image ? VaultArtworkUrls.Scene(image) : null,
        VaultViews.ScenePath(scene.Id))
    {
        Shape = VaultCardShape.Scene,
        Unmatched = !scene.Matched,
        AssetId = scene.Id,
        CoverUrl2x = scene.ImageId is { } large ? VaultArtworkUrls.SceneLarge(large) : null,
        FileName = scene.FileName,
        Folder = scene.Folder,
        FolderId = scene.FolderId,
        DurationSeconds = scene.DurationSeconds,
        SizeBytes = scene.SizeBytes,
        AddedAt = scene.AddedAt,
        ReleaseDate = scene.ReleaseDate,
        Tags = scene.Tags,
        // Pointing at a matched scene cycles through its saved stills (D41); an unmatched scene has its one still and nothing to cycle.
        HoverUrls = hover && scene.Matched && scene.StillIds.Count > 1 ? scene.StillIds.Select(VaultArtworkUrls.Scene).ToList() : [],
    };

    private static string? SceneSubtitle(VaultSceneDto scene)
    {
        if (!scene.Matched)
        {
            return null;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(scene.Studio))
        {
            parts.Add(scene.Studio);
        }

        if (scene.ReleaseDate is { Length: >= 4 } date && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            parts.Add(year.ToString(CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static VaultCatalogueItem Folder(VaultFolderDto folder) => new(
        folder.Id,
        folder.Name,
        SceneCount(folder.SceneCount),
        folder.ImageId is { } image ? VaultArtworkUrls.Scene(image) : null,
        VaultViews.TileRoute("folders", folder.Id))
    {
        Shape = VaultCardShape.Scene,
        CoverUrl2x = folder.ImageId is { } large ? VaultArtworkUrls.SceneLarge(large) : null,
        Count = folder.SceneCount,
    };

    private static VaultCatalogueItem Tile(string kind, VaultNameTileDto tile)
    {
        var shape = kind switch
        {
            "performers" => VaultCardShape.Performer,
            "studios" => VaultCardShape.Studio,
            _ => VaultCardShape.Tag,
        };
        string? url = null;
        string? url2x = null;
        if (tile.ImageId is { } image)
        {
            (url, url2x) = shape switch
            {
                VaultCardShape.Performer => (VaultArtworkUrls.Performer(image), VaultArtworkUrls.PerformerLarge(image)),
                VaultCardShape.Studio => (VaultArtworkUrls.Studio(image), (string?)null),
                _ => ((string?)null, (string?)null),
            };
        }

        return new VaultCatalogueItem(
            tile.Id.ToString("D"), tile.Name, SceneCount(tile.SceneCount), url, VaultViews.TileRoute(kind, tile.Id.ToString("D")))
        {
            Shape = shape,
            CoverUrl2x = url2x,
            Count = tile.SceneCount,
        };
    }

    private static string SceneCount(int count) => count == 1 ? "1 scene" : $"{count.ToString(CultureInfo.InvariantCulture)} scenes";
}
