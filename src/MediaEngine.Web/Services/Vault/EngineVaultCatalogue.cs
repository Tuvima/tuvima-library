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

    // How many of the newest images join the first page of Recently added.
    private const int RecentImageCount = 24;

    // The sort names the Engine's scene list understands.
    private const string SortDateAdded = "date_added";
    private const string SortTitle = "title";
    private const string SortReleaseDate = "release_date";
    private const string SortDuration = "duration";

    public async Task<VaultCatalogueResult> ListAsync(
        string viewKey, VaultViewPreferences preferences, VaultListFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new VaultListFilter();

        // The Images view: a gallery tile opens to its images, "all" lists every image, and with neither the view lists the galleries.
        if (viewKey == "images")
        {
            return filter.ImageGallery is null ? await GalleriesAsync(ct) : await ImagesAsync(filter, ct);
        }

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

        // The Vault's images are searched by their own route (file-derived title, file name, date and the Vault's own tags); the words
        // travel in the body of that request too. A refusal there only means no images are listed.
        var images = await api.SearchVaultImagesAsync(new VaultImageSearchRequest { Text = query, Take = RecentImageCount }, ct);
        if (images.Succeeded && images.Value is { } found)
        {
            items.AddRange(found.Items.Select(Image));
        }

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
        // Recently added can show images only; that is the newest images, not videos.
        if (filter.Kind == "images" && forceMatch is null)
        {
            return await ImagesAsync(filter with { ImageGallery = VaultViews.AllImages }, ct);
        }

        var (sort, descending) = newestFirst ? (SortDateAdded, true) : SortFor(preferences.Sort);
        var call = await api.GetVaultScenesAsync(
            forceMatch ?? filter.Match, sort, descending, filter.PerformerId, filter.StudioId, filter.TagId, filter.FolderId,
            filter.Skip, filter.Take > 0 ? filter.Take : DefaultPageSize, ct);
        if (!call.Succeeded || call.Value is not { } list)
        {
            return VaultCatalogueResult.NotYetAvailable;
        }

        var scenes = list.Items.Select(scene => Scene(scene, preferences.HoverStills)).ToList();
        if (!newestFirst || filter.Kind != "all" || filter.Skip != 0)
        {
            return new VaultCatalogueResult(true, scenes)
            {
                Heading = list.Heading,
                Total = list.Total,
            };
        }

        // Recently added shows videos and images together, newest first. The newest images join the first page; the rest of the
        // pictures are always one choice away on the Images view.
        var recent = await api.GetVaultImagesAsync(null, null, false, null, RecentImageCount, ct);
        var images = recent.Succeeded && recent.Value is { } page ? page.Items.Select(Image).ToList() : [];
        var merged = scenes.Concat(images).OrderByDescending(item => item.AddedAt ?? DateTimeOffset.MinValue).ToList();
        return new VaultCatalogueResult(true, merged)
        {
            Heading = list.Heading,
            Total = list.Total + images.Count,
        };
    }

    private async Task<VaultCatalogueResult> GalleriesAsync(CancellationToken ct)
    {
        var call = await api.GetVaultImageGalleriesAsync(ct);
        if (!call.Succeeded || call.Value is not { } galleries)
        {
            return VaultCatalogueResult.NotYetAvailable;
        }

        // Every image first, then each folder, hand-made and rule gallery. With no gallery at all there is nothing to open.
        if (galleries.Count == 0)
        {
            return new VaultCatalogueResult(true, []);
        }

        var tiles = new List<VaultCatalogueItem> { AllImagesTile(galleries) };
        tiles.AddRange(galleries.Select(Gallery));
        return new VaultCatalogueResult(true, tiles);
    }

    private async Task<VaultCatalogueResult> ImagesAsync(VaultListFilter filter, CancellationToken ct)
    {
        Guid? gallery = Guid.TryParse(filter.ImageGallery, out var id) ? id : null;
        if (filter.ImageGallery != VaultViews.AllImages && gallery is null && filter.ImageGallery is not null)
        {
            return VaultCatalogueResult.NotYetAvailable;
        }

        var call = await api.GetVaultImagesAsync(gallery, null, false, filter.Cursor, filter.Take > 0 ? filter.Take : DefaultPageSize, ct);
        if (!call.Succeeded || call.Value is not { } page)
        {
            return VaultCatalogueResult.NotYetAvailable;
        }

        return new VaultCatalogueResult(true, page.Items.Select(Image).ToList()) { NextCursor = page.HasMore ? page.NextCursor : null };
    }

    private static VaultCatalogueItem AllImagesTile(IReadOnlyList<VaultImageGalleryDto> galleries)
    {
        var total = galleries.Where(g => g.Kind == VaultImageGalleryKinds.Folder).Sum(g => g.ImageCount);
        var cover = galleries.FirstOrDefault(g => g.CoverImageId is not null)?.CoverImageId;
        return new VaultCatalogueItem(
            VaultViews.AllImages, "All images", total > 0 ? ImageCount(total) : null,
            cover is { } id ? VaultArtworkUrls.ImageThumbnail(id) : null, VaultViews.TileRoute("images", VaultViews.AllImages))
        {
            Shape = VaultCardShape.Scene,
            CoverUrl2x = cover is { } large ? VaultArtworkUrls.ImageThumbnailLarge(large) : null,
        };
    }

    private static VaultCatalogueItem Gallery(VaultImageGalleryDto gallery) => new(
        gallery.Id.ToString("D"),
        gallery.Name,
        ImageCount(gallery.ImageCount),
        gallery.CoverImageId is { } image ? VaultArtworkUrls.ImageThumbnail(image) : null,
        VaultViews.TileRoute("images", gallery.Id.ToString("D")))
    {
        Shape = VaultCardShape.Scene,
        CoverUrl2x = gallery.CoverImageId is { } large ? VaultArtworkUrls.ImageThumbnailLarge(large) : null,
        Count = gallery.ImageCount,
    };

    private static VaultCatalogueItem Image(VaultImageDto image) => new(
        image.Id.ToString("D"),
        image.Title,
        null,
        VaultArtworkUrls.ImageThumbnail(image.Id),
        VaultViews.TileRoute("images", VaultViews.AllImages))
    {
        Shape = VaultCardShape.Image,
        CoverUrl2x = VaultArtworkUrls.ImageThumbnailLarge(image.Id),
        ImageId = image.Id,
        FileName = image.FileName,
        // The timeline orders and groups by when the picture was taken, else when it was added.
        AddedAt = image.CapturedAt ?? image.AddedAt,
        Tags = image.Tags,
        Favorite = image.Favorite,
    };

    private static string ImageCount(int count) => count == 1 ? "1 image" : $"{count.ToString(CultureInfo.InvariantCulture)} images";

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
