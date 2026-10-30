using System.Net.Http.Json;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Services.Vault;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    // -- Vault mode and Vault libraries (/api/v1/nsfw, /api/v1/vault/libraries) --------------------------------
    // Every call goes through the shared envelope helpers and then reads the failure they recorded, so a refusal is told
    // apart without a raw HTTP call: 404 (this profile cannot use it), 428 (a PIN is needed or was wrong), 429 (too many
    // tries) and a plain-language 400/409. PINs travel only in request bodies; nothing here logs a body, and calls that carry
    // a PIN do not log failures as warnings.

    private const string NsfwStatusPath = "/api/v1/nsfw/status";
    private const string VaultLibrariesPath = "/api/v1/vault/libraries";
    private const string VaultPath = "/api/v1/vault";

    public async Task<VaultCall<NsfwStatusDto>> GetNsfwStatusAsync(CancellationToken ct = default)
    {
        var status = await GetAsync<NsfwStatusDto>("GET /api/v1/nsfw/status", NsfwStatusPath, logAsWarning: false, ct: ct);
        // A refusal, an unreachable Engine and an answer that is not one of the two modes a profile can use look the same here on
        // purpose: in each case nothing about Vault mode is drawn.
        return status is { Mode: "pin" or "allowed" } ? VaultCall<NsfwStatusDto>.Ok(status) : VaultCall<NsfwStatusDto>.Of(VaultOutcome.NotAvailable);
    }

    public async Task<VaultCall<NsfwStatusDto>> UnlockNsfwAsync(string? pin, CancellationToken ct = default)
    {
        var status = await PostAsync<NsfwUnlockRequest, NsfwStatusDto>(
            "POST /api/v1/nsfw/unlock", "/api/v1/nsfw/unlock", new NsfwUnlockRequest { Pin = pin }, logAsWarning: false, ct: ct);
        return status is null ? FailedVaultCall<NsfwStatusDto>() : VaultCall<NsfwStatusDto>.Ok(status);
    }

    public async Task<VaultCall<bool>> LockNsfwAsync(CancellationToken ct = default) =>
        await PostAsync("POST /api/v1/nsfw/lock", "/api/v1/nsfw/lock", new { }, logAsWarning: false, ct: ct)
            ? VaultCall<bool>.Ok(true)
            : FailedVaultCall<bool>();

    public async Task<VaultCall<bool>> KeepNsfwAliveAsync(CancellationToken ct = default) =>
        await PostAsync("POST /api/v1/nsfw/keepalive", "/api/v1/nsfw/keepalive", new { }, logAsWarning: false, ct: ct)
            ? VaultCall<bool>.Ok(true)
            : FailedVaultCall<bool>();

    public async Task<VaultCall<NsfwStatusDto>> SetNsfwIdleAsync(int minutes, CancellationToken ct = default)
    {
        var status = await PutAsync<NsfwIdleRequest, NsfwStatusDto>(
            "PUT /api/v1/nsfw/idle", "/api/v1/nsfw/idle", new NsfwIdleRequest { IdleLockMinutes = minutes }, logAsWarning: false, ct: ct);
        return status is null ? FailedVaultCall<NsfwStatusDto>() : VaultCall<NsfwStatusDto>.Ok(status);
    }

    public async Task<VaultCall<NsfwStatusDto>> SetNsfwNavigationAsync(bool show, CancellationToken ct = default)
    {
        var status = await PutAsync<NsfwNavigationRequest, NsfwStatusDto>(
            "PUT /api/v1/nsfw/navigation", "/api/v1/nsfw/navigation", new NsfwNavigationRequest { ShowInNavigation = show }, logAsWarning: false, ct: ct);
        return status is null ? FailedVaultCall<NsfwStatusDto>() : VaultCall<NsfwStatusDto>.Ok(status);
    }

    public async Task<VaultCall<NsfwStatusDto>> SetProfileNsfwAccessAsync(Guid profileId, SetNsfwAccessRequest request, CancellationToken ct = default)
    {
        var status = await PutAsync<SetNsfwAccessRequest, NsfwStatusDto>(
            "PUT /api/v1/profiles/{id}/nsfw-access", $"/api/v1/profiles/{profileId:D}/nsfw-access", request, logAsWarning: false, ct: ct);
        return status is null ? FailedVaultCall<NsfwStatusDto>() : VaultCall<NsfwStatusDto>.Ok(status);
    }

    public async Task<VaultCall<IReadOnlyList<VaultLibraryDto>>> GetVaultLibrariesAsync(CancellationToken ct = default)
    {
        var libraries = await GetAsync<List<VaultLibraryDto>>("GET /api/v1/vault/libraries", VaultLibrariesPath, logAsWarning: false, ct: ct);
        return libraries is null
            ? VaultCall<IReadOnlyList<VaultLibraryDto>>.Of(VaultOutcome.NotAvailable)
            : VaultCall<IReadOnlyList<VaultLibraryDto>>.Ok(libraries);
    }

    public async Task<VaultCall<VaultLibraryDto>> CreateVaultLibraryAsync(CreateVaultLibraryRequest request, CancellationToken ct = default)
    {
        var created = await PostAsync<CreateVaultLibraryRequest, VaultLibraryDto>(
            "POST /api/v1/vault/libraries", VaultLibrariesPath, request, logAsWarning: false, ct: ct);
        return created is null ? FailedVaultCall<VaultLibraryDto>() : VaultCall<VaultLibraryDto>.Ok(created);
    }

    public async Task<VaultCall<VaultLibraryDto>> UpdateVaultLibraryAsync(string libraryId, UpdateVaultLibraryRequest request, CancellationToken ct = default)
    {
        var updated = await PutAsync<UpdateVaultLibraryRequest, VaultLibraryDto>(
            "PUT /api/v1/vault/libraries/{id}", $"{VaultLibrariesPath}/{Uri.EscapeDataString(libraryId)}", request, logAsWarning: false, ct: ct);
        return updated is null ? FailedVaultCall<VaultLibraryDto>() : VaultCall<VaultLibraryDto>.Ok(updated);
    }

    public async Task<VaultCall<VaultLibraryDto>> AttachVaultFolderAsync(string libraryId, AttachVaultFolderRequest request, CancellationToken ct = default)
    {
        var attached = await PostAsync<AttachVaultFolderRequest, VaultLibraryDto>(
            "POST /api/v1/vault/libraries/{id}/folders", $"{VaultLibrariesPath}/{Uri.EscapeDataString(libraryId)}/folders", request, logAsWarning: false, ct: ct);
        return attached is null ? FailedVaultCall<VaultLibraryDto>() : VaultCall<VaultLibraryDto>.Ok(attached);
    }

    public async Task<VaultCall<ProfileVaultSectionsDto>> GetProfileVaultSectionsAsync(Guid profileId, CancellationToken ct = default)
    {
        var sections = await GetAsync<ProfileVaultSectionsDto>(
            "GET /api/v1/profiles/{id}/vault-sections", $"/api/v1/profiles/{profileId:D}/vault-sections", logAsWarning: false, ct: ct);
        return sections is null ? FailedVaultCall<ProfileVaultSectionsDto>() : VaultCall<ProfileVaultSectionsDto>.Ok(sections);
    }

    public async Task<VaultCall<ProfileVaultSectionsDto>> SetProfileVaultSectionsAsync(Guid profileId, IReadOnlyList<string> libraryIds, CancellationToken ct = default)
    {
        var sections = await PutAsync<SetProfileVaultSectionsRequest, ProfileVaultSectionsDto>(
            "PUT /api/v1/profiles/{id}/vault-sections", $"/api/v1/profiles/{profileId:D}/vault-sections",
            new SetProfileVaultSectionsRequest { LibraryIds = libraryIds }, logAsWarning: false, ct: ct);
        return sections is null ? FailedVaultCall<ProfileVaultSectionsDto>() : VaultCall<ProfileVaultSectionsDto>.Ok(sections);
    }

    public async Task<VaultCall<bool>> RemoveVaultLibraryAsync(string libraryId, RemoveVaultLibraryRequest request, CancellationToken ct = default) =>
        await PostAsync(
            "POST /api/v1/vault/libraries/{id}/remove", $"{VaultLibrariesPath}/{Uri.EscapeDataString(libraryId)}/remove", request, logAsWarning: false, ct: ct)
            ? VaultCall<bool>.Ok(true)
            : FailedVaultCall<bool>();

    public async Task<VaultCall<VaultMovePreviewDto>> PreviewVaultMoveAsync(VaultMovePreviewRequest request, CancellationToken ct = default)
    {
        var preview = await PostAsync<VaultMovePreviewRequest, VaultMovePreviewDto>(
            "POST /settings/vault-moves/preview", "/settings/vault-moves/preview", request, logAsWarning: false, ct: ct);
        return preview is null ? FailedVaultCall<VaultMovePreviewDto>() : VaultCall<VaultMovePreviewDto>.Ok(preview);
    }

    public async Task<VaultCall<StartVaultMoveResultDto>> StartVaultMoveAsync(StartVaultMoveRequest request, CancellationToken ct = default)
    {
        var started = await PostAsync<StartVaultMoveRequest, StartVaultMoveResultDto>(
            "POST /settings/vault-moves", "/settings/vault-moves", request, logAsWarning: false, ct: ct);
        return started is null ? FailedVaultCall<StartVaultMoveResultDto>() : VaultCall<StartVaultMoveResultDto>.Ok(started);
    }

    public async Task<VaultCall<IReadOnlyList<VaultMoveJobDto>>> GetVaultMovesAsync(CancellationToken ct = default)
    {
        var jobs = await GetAsync<List<VaultMoveJobDto>>("GET /settings/vault-moves", "/settings/vault-moves", logAsWarning: false, ct: ct);
        return jobs is null ? FailedVaultCall<IReadOnlyList<VaultMoveJobDto>>() : VaultCall<IReadOnlyList<VaultMoveJobDto>>.Ok(jobs);
    }

    // -- Identify, the match record and the activity list (/api/v1/vault/items, /api/v1/vault/activity) -----------
    // Typed search text travels only in the request body; no call here puts a title or a body in a log line.

    public async Task<VaultCall<VaultCandidateListDto>> SearchVaultIdentifyAsync(Guid assetId, string? text, CancellationToken ct = default)
    {
        var list = await PostAsync<VaultIdentifySearchRequest, VaultCandidateListDto>(
            "POST /api/v1/vault/items/{id}/identify/search", $"/api/v1/vault/items/{assetId:D}/identify/search",
            new VaultIdentifySearchRequest { Text = text }, logAsWarning: false, ct: ct);
        return list is null ? FailedVaultCall<VaultCandidateListDto>() : VaultCall<VaultCandidateListDto>.Ok(list);
    }

    public async Task<VaultCall<VaultIdentifyPreviewDto>> PreviewVaultIdentifyAsync(Guid assetId, VaultIdentifyPreviewRequest request, CancellationToken ct = default)
    {
        var preview = await PostAsync<VaultIdentifyPreviewRequest, VaultIdentifyPreviewDto>(
            "POST /api/v1/vault/items/{id}/identify/preview", $"/api/v1/vault/items/{assetId:D}/identify/preview", request, logAsWarning: false, ct: ct);
        return preview is null ? FailedVaultCall<VaultIdentifyPreviewDto>() : VaultCall<VaultIdentifyPreviewDto>.Ok(preview);
    }

    public async Task<VaultCall<VaultIdentifyApplyResultDto>> ApplyVaultIdentifyAsync(Guid assetId, VaultIdentifyApplyRequest request, CancellationToken ct = default)
    {
        var result = await PostAsync<VaultIdentifyApplyRequest, VaultIdentifyApplyResultDto>(
            "POST /api/v1/vault/items/{id}/identify/apply", $"/api/v1/vault/items/{assetId:D}/identify/apply", request, logAsWarning: false, ct: ct);
        return result is null ? FailedVaultCall<VaultIdentifyApplyResultDto>() : VaultCall<VaultIdentifyApplyResultDto>.Ok(result);
    }

    public async Task<VaultCall<VaultMatchDto>> GetVaultMatchAsync(Guid assetId, CancellationToken ct = default)
    {
        var match = await GetAsync<VaultMatchDto>(
            "GET /api/v1/vault/items/{id}/match", $"/api/v1/vault/items/{assetId:D}/match", logAsWarning: false, ct: ct);
        // A read that comes back empty is "not found" here: the Engine answers it the same for a locked session and a missing video.
        return match is null ? VaultCall<VaultMatchDto>.Of(VaultOutcome.NotAvailable) : VaultCall<VaultMatchDto>.Ok(match);
    }

    public async Task<VaultCall<IReadOnlyList<VaultActivityItemDto>>> GetVaultActivityAsync(CancellationToken ct = default)
    {
        var lines = await GetAsync<List<VaultActivityItemDto>>("GET /api/v1/vault/activity", "/api/v1/vault/activity", logAsWarning: false, ct: ct);
        return lines is null
            ? VaultCall<IReadOnlyList<VaultActivityItemDto>>.Of(VaultOutcome.NotAvailable)
            : VaultCall<IReadOnlyList<VaultActivityItemDto>>.Ok(lines);
    }

    public async Task<VaultCall<VaultRestoredBookmarksDto>> GetVaultRestoredBookmarksAsync(CancellationToken ct = default)
    {
        var waiting = await GetAsync<VaultRestoredBookmarksDto>(
            "GET /api/v1/vault/restored-bookmarks", "/api/v1/vault/restored-bookmarks", logAsWarning: false, ct: ct);
        return waiting is null
            ? VaultCall<VaultRestoredBookmarksDto>.Of(VaultOutcome.NotAvailable)
            : VaultCall<VaultRestoredBookmarksDto>.Ok(waiting);
    }

    // -- Browsing (/api/v1/vault/scenes, /folders, /performers, /studios, /tags, /search) ----------------------------
    // Every read answers "not found" for a locked session, so an empty answer here is "not available", never "empty".

    public async Task<VaultCall<VaultSceneListDto>> GetVaultScenesAsync(
        string? match, string? sort, bool? descending, Guid? performerId, Guid? studioId, Guid? tagId, string? folderId,
        int skip = 0, int take = 60, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["match"] = match,
            ["sort"] = sort,
            ["desc"] = descending?.ToString().ToLowerInvariant(),
            ["performer"] = performerId?.ToString("D"),
            ["studio"] = studioId?.ToString("D"),
            ["tag"] = tagId?.ToString("D"),
            ["folder"] = folderId,
            ["skip"] = skip.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["take"] = take.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var list = await GetAsync<VaultSceneListDto>("GET /api/v1/vault/scenes", VaultPath + "/scenes", query, logAsWarning: false, ct: ct);
        return list is null ? VaultCall<VaultSceneListDto>.Of(VaultOutcome.NotAvailable) : VaultCall<VaultSceneListDto>.Ok(list);
    }

    public async Task<VaultCall<VaultSceneDetailDto>> GetVaultSceneAsync(Guid assetId, CancellationToken ct = default)
    {
        var scene = await GetAsync<VaultSceneDetailDto>(
            "GET /api/v1/vault/scenes/{id}", $"{VaultPath}/scenes/{assetId:D}", logAsWarning: false, ct: ct);
        return scene is null ? VaultCall<VaultSceneDetailDto>.Of(VaultOutcome.NotAvailable) : VaultCall<VaultSceneDetailDto>.Ok(scene);
    }

    public async Task<VaultCall<IReadOnlyList<VaultFolderDto>>> GetVaultFoldersAsync(CancellationToken ct = default)
    {
        var folders = await GetAsync<List<VaultFolderDto>>("GET /api/v1/vault/folders", VaultPath + "/folders", logAsWarning: false, ct: ct);
        return folders is null
            ? VaultCall<IReadOnlyList<VaultFolderDto>>.Of(VaultOutcome.NotAvailable)
            : VaultCall<IReadOnlyList<VaultFolderDto>>.Ok(folders);
    }

    public async Task<VaultCall<IReadOnlyList<VaultNameTileDto>>> GetVaultNamesAsync(string kind, string? sort, bool? descending, CancellationToken ct = default)
    {
        // The kind picks one of three fixed routes; anything else is not a Vault list.
        if (kind is not ("performers" or "studios" or "tags"))
        {
            return VaultCall<IReadOnlyList<VaultNameTileDto>>.Of(VaultOutcome.NotAvailable);
        }

        var query = new Dictionary<string, string?> { ["sort"] = sort, ["desc"] = descending?.ToString().ToLowerInvariant() };
        var names = await GetAsync<List<VaultNameTileDto>>($"GET /api/v1/vault/{kind}", $"{VaultPath}/{kind}", query, logAsWarning: false, ct: ct);
        return names is null
            ? VaultCall<IReadOnlyList<VaultNameTileDto>>.Of(VaultOutcome.NotAvailable)
            : VaultCall<IReadOnlyList<VaultNameTileDto>>.Ok(names);
    }

    public async Task<VaultCall<VaultSearchResultDto>> SearchVaultAsync(string text, CancellationToken ct = default)
    {
        var result = await PostAsync<VaultSearchRequest, VaultSearchResultDto>(
            "POST /api/v1/vault/search", VaultPath + "/search", new VaultSearchRequest { Text = text }, logAsWarning: false, ct: ct);
        return result is null ? FailedVaultCall<VaultSearchResultDto>() : VaultCall<VaultSearchResultDto>.Ok(result);
    }

    // -- Images and galleries (/api/v1/vault/images) -------------------------------------------------------------------
    // Same rules as the scenes above: a locked session reads "not found", search words travel only in a body, and ids (never names,
    // paths or words) are what appear in an address.

    private const string VaultImagesPath = VaultPath + "/images";

    public async Task<VaultCall<VaultImagePageDto>> GetVaultImagesAsync(
        Guid? galleryId, Guid? tagId, bool favoritesOnly, string? cursor, int take, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["gallery"] = galleryId?.ToString("D"),
            ["tag"] = tagId?.ToString("D"),
            ["favorite"] = favoritesOnly ? "true" : null,
            ["cursor"] = cursor,
            ["take"] = take.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var page = await GetAsync<VaultImagePageDto>("GET /api/v1/vault/images", VaultImagesPath, query, logAsWarning: false, ct: ct);
        return page is null ? VaultCall<VaultImagePageDto>.Of(VaultOutcome.NotAvailable) : VaultCall<VaultImagePageDto>.Ok(page);
    }

    public async Task<VaultCall<VaultImagePageDto>> SearchVaultImagesAsync(VaultImageSearchRequest request, CancellationToken ct = default)
    {
        var page = await PostAsync<VaultImageSearchRequest, VaultImagePageDto>(
            "POST /api/v1/vault/images/search", VaultImagesPath + "/search", request, logAsWarning: false, ct: ct);
        return page is null ? FailedVaultCall<VaultImagePageDto>() : VaultCall<VaultImagePageDto>.Ok(page);
    }

    public async Task<VaultCall<IReadOnlyList<VaultImageGalleryDto>>> GetVaultImageGalleriesAsync(CancellationToken ct = default)
    {
        var galleries = await GetAsync<List<VaultImageGalleryDto>>(
            "GET /api/v1/vault/images/galleries", VaultImagesPath + "/galleries", logAsWarning: false, ct: ct);
        return galleries is null
            ? VaultCall<IReadOnlyList<VaultImageGalleryDto>>.Of(VaultOutcome.NotAvailable)
            : VaultCall<IReadOnlyList<VaultImageGalleryDto>>.Ok(galleries);
    }

    public async Task<VaultCall<VaultImageDetailDto>> GetVaultImageAsync(Guid imageId, CancellationToken ct = default)
    {
        var detail = await GetAsync<VaultImageDetailDto>(
            "GET /api/v1/vault/images/{id}", $"{VaultImagesPath}/{imageId:D}", logAsWarning: false, ct: ct);
        return detail is null ? VaultCall<VaultImageDetailDto>.Of(VaultOutcome.NotAvailable) : VaultCall<VaultImageDetailDto>.Ok(detail);
    }

    public async Task<VaultCall<VaultImageDetailDto>> SetVaultImageTagsAsync(Guid imageId, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        var detail = await PutAsync<VaultImageTagsRequest, VaultImageDetailDto>(
            "PUT /api/v1/vault/images/{id}/tags", $"{VaultImagesPath}/{imageId:D}/tags",
            new VaultImageTagsRequest { Tags = tags }, logAsWarning: false, ct: ct);
        return detail is null ? FailedVaultCall<VaultImageDetailDto>() : VaultCall<VaultImageDetailDto>.Ok(detail);
    }

    public async Task<VaultCall<bool>> SetVaultImageFavoriteAsync(Guid imageId, bool favorite, CancellationToken ct = default) =>
        await PutAsync(
            "PUT /api/v1/vault/images/{id}/favorite", $"{VaultImagesPath}/{imageId:D}/favorite",
            new VaultImageFavoriteRequest { Favorite = favorite }, logAsWarning: false, ct: ct)
            ? VaultCall<bool>.Ok(true)
            : FailedVaultCall<bool>();

    public async Task<VaultCall<VaultImageGalleryDto>> CreateVaultImageGalleryAsync(VaultImageGalleryRequest request, CancellationToken ct = default)
    {
        var gallery = await PostAsync<VaultImageGalleryRequest, VaultImageGalleryDto>(
            "POST /api/v1/vault/images/galleries", VaultImagesPath + "/galleries", request, logAsWarning: false, ct: ct);
        return gallery is null ? FailedVaultCall<VaultImageGalleryDto>() : VaultCall<VaultImageGalleryDto>.Ok(gallery);
    }

    public async Task<VaultCall<VaultImageGalleryDto>> UpdateVaultImageGalleryAsync(Guid galleryId, VaultImageGalleryRequest request, CancellationToken ct = default)
    {
        var gallery = await PutAsync<VaultImageGalleryRequest, VaultImageGalleryDto>(
            "PUT /api/v1/vault/images/galleries/{id}", $"{VaultImagesPath}/galleries/{galleryId:D}", request, logAsWarning: false, ct: ct);
        return gallery is null ? FailedVaultCall<VaultImageGalleryDto>() : VaultCall<VaultImageGalleryDto>.Ok(gallery);
    }

    public async Task<VaultCall<bool>> DeleteVaultImageGalleryAsync(Guid galleryId, CancellationToken ct = default) =>
        await DeleteAsync(
            "DELETE /api/v1/vault/images/galleries/{id}", $"{VaultImagesPath}/galleries/{galleryId:D}", logAsWarning: false, ct: ct)
            ? VaultCall<bool>.Ok(true)
            : FailedVaultCall<bool>();

    public async Task<VaultCall<int>> AddVaultImagesToGalleryAsync(Guid galleryId, IReadOnlyList<Guid> imageIds, CancellationToken ct = default)
    {
        var added = await PostAsync<VaultImageIdsRequest, int?>(
            "POST /api/v1/vault/images/galleries/{id}/items", $"{VaultImagesPath}/galleries/{galleryId:D}/items",
            new VaultImageIdsRequest { ImageIds = imageIds }, logAsWarning: false, ct: ct);
        return added is null ? FailedVaultCall<int>() : VaultCall<int>.Ok(added.Value);
    }

    public async Task<VaultCall<VaultImageScanDto>> ScanVaultImagesAsync(CancellationToken ct = default)
    {
        var scan = await PostAsync<object, VaultImageScanDto>(
            "POST /api/v1/vault/images/scan", VaultImagesPath + "/scan", new { }, logAsWarning: false, ct: ct);
        return scan is null ? FailedVaultCall<VaultImageScanDto>() : VaultCall<VaultImageScanDto>.Ok(scan);
    }

    public async Task<VaultCall<bool>> DeleteVaultImageAsync(Guid imageId, CancellationToken ct = default) =>
        await PostAsync(
            "POST /api/v1/vault/images/{id}/delete", $"{VaultImagesPath}/{imageId:D}/delete", new { }, logAsWarning: false, ct: ct)
            ? VaultCall<bool>.Ok(true)
            : FailedVaultCall<bool>();

    public async Task<VaultCall<bool>> RestoreVaultImageAsync(Guid imageId, CancellationToken ct = default) =>
        await PostAsync(
            "POST /api/v1/vault/images/{id}/restore", $"{VaultImagesPath}/{imageId:D}/restore", new { }, logAsWarning: false, ct: ct)
            ? VaultCall<bool>.Ok(true)
            : FailedVaultCall<bool>();

    public async Task<VaultCall<VaultImagePageDto>> GetVaultImageTrashAsync(string? cursor, int take, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["cursor"] = cursor,
            ["take"] = take.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var page = await GetAsync<VaultImagePageDto>(
            "GET /api/v1/vault/images/trash", VaultImagesPath + "/trash", query, logAsWarning: false, ct: ct);
        return page is null ? VaultCall<VaultImagePageDto>.Of(VaultOutcome.NotAvailable) : VaultCall<VaultImagePageDto>.Ok(page);
    }

    public async Task<VaultCall<VaultImageEmptiedDto>> EmptyVaultImageTrashAsync(CancellationToken ct = default)
    {
        var emptied = await PostAsync<object, VaultImageEmptiedDto>(
            "POST /api/v1/vault/images/trash/empty", VaultImagesPath + "/trash/empty", new { }, logAsWarning: false, ct: ct);
        return emptied is null ? FailedVaultCall<VaultImageEmptiedDto>() : VaultCall<VaultImageEmptiedDto>.Ok(emptied);
    }

    /// <summary>Reads the failure the shared envelope just recorded and names it in the terms the Vault screens use.</summary>
    private VaultCall<T> FailedVaultCall<T>()
    {
        var error = LastError ?? string.Empty;
        return LastStatusCode switch
        {
            404 => VaultCall<T>.Of(VaultOutcome.NotAvailable),
            428 => VaultCall<T>.Of(VaultOutcome.PinRequired),
            429 => VaultCall<T>.Of(VaultOutcome.TooManyAttempts),
            403 when error.Contains("Confirm it's you", StringComparison.OrdinalIgnoreCase) => VaultCall<T>.Of(VaultOutcome.ConfirmItsYou),
            400 or 409 => VaultCall<T>.Of(VaultOutcome.Rejected, PlainVaultReason(error)),
            _ => VaultCall<T>.Of(VaultOutcome.Failed),
        };
    }

    // The recorded error reads "HTTP 400: Title Detail Trace: id"; the screen wants only the sentence the Engine wrote.
    private static string? PlainVaultReason(string recorded)
    {
        var colon = recorded.IndexOf(':', StringComparison.Ordinal);
        var reason = colon >= 0 ? recorded[(colon + 1)..] : recorded;
        var trace = reason.IndexOf(" Trace:", StringComparison.Ordinal);
        if (trace >= 0)
        {
            reason = reason[..trace];
        }

        reason = reason.Trim();
        foreach (var title in (string[])["Invalid request.", "Conflict."])
        {
            if (reason.StartsWith(title, StringComparison.Ordinal))
            {
                reason = reason[title.Length..].Trim();
            }
        }

        return reason.Length == 0 ? null : reason;
    }
}
