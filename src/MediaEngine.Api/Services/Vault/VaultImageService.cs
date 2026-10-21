using System.Security.Cryptography;
using System.Text;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Vault;

/// <summary>How a Vault image action ended, in the terms the routes need.</summary>
public enum VaultImageOutcome
{
    Done,

    /// <summary>The image or gallery is not there (or is not a Vault one). The route answers "not found".</summary>
    NotFound,

    /// <summary>The request is not acceptable; <see cref="VaultImageResult{T}.Message"/> says why in plain words.</summary>
    Rejected,

    /// <summary>The file could not be moved or put back; nothing was changed.</summary>
    Failed,
}

/// <summary>The answer to one Vault image action.</summary>
public readonly record struct VaultImageResult<T>(VaultImageOutcome Outcome, T? Value = default, string? Message = null)
{
    public bool Succeeded => Outcome == VaultImageOutcome.Done;

    public static VaultImageResult<T> Ok(T? value) => new(VaultImageOutcome.Done, value);

    public static VaultImageResult<T> NotFound() => new(VaultImageOutcome.NotFound);

    public static VaultImageResult<T> Rejected(string message) => new(VaultImageOutcome.Rejected, default, message);

    public static VaultImageResult<T> Failed(string message) => new(VaultImageOutcome.Failed, default, message);
}

/// <summary>A Vault image file ready to send: the file on disk and its type.</summary>
public sealed record VaultImageFile(string Path, string ContentType);

/// <summary>What a list of Vault images asks for. At most one of the gallery and tag narrows it; search and the favourite mark add to it.</summary>
public sealed record VaultImageListRequest(
    Guid? GalleryId = null,
    Guid? TagId = null,
    bool FavoritesOnly = false,
    string? Text = null,
    string? Cursor = null,
    int Take = VaultImageService.DefaultTake,
    bool DeletedOnly = false);

/// <summary>
/// The Vault's images and galleries (Videos spec D44). Images are ordinary View-style items read through the same local asset repository,
/// timeline, search and Smart Gallery rules, but always with the Vault scope on, so a Vault image never comes back from a View query and a
/// View item never comes back from here. Nothing in this service logs a title, file name, path, tag, gallery name or search text.
/// </summary>
public sealed class VaultImageService(
    IVaultLibraryRepository vaultLibraries,
    IVaultImageRepository images,
    IVaultGalleryRepository galleries,
    ILocalAssetRepository assets,
    IVaultImageIndexer indexer,
    IConfigurationLoader configuration,
    ILibraryFolderResolver folders,
    ISourceMutationPolicyGate gate,
    ViewThumbnailService thumbnails,
    RecycleBinService recycleBin,
    ILogger<VaultImageService> logger)
{
    public const int DefaultTake = 60;
    public const int MaximumTake = 200;
    public const int MinimumSearchLength = 2;

    // ------------------------------------------------------------------ finding images

    /// <summary>
    /// Looks in every Vault library's folders for images and registers new ones, then puts each in the gallery of the folder it sits in.
    /// A file that was deleted and has come back on disk becomes active again. Counts only are returned or logged.
    /// </summary>
    public async Task<VaultImageScanDto> ScanAsync(CancellationToken ct = default)
    {
        var found = 0;
        var added = 0;
        var errors = 0;
        var libraries = configuration.LoadLibraries().Libraries;
        foreach (var vault in await vaultLibraries.GetAllAsync(ct).ConfigureAwait(false))
        {
            var imageLibrary = await images.EnsureLibraryAsync(vault.LibraryId, ct).ConfigureAwait(false);
            var config = libraries.FirstOrDefault(library => string.Equals(library.Id, vault.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (imageLibrary is null || config is null)
            {
                continue;
            }

            foreach (var source in config.ScannableSources)
            {
                ct.ThrowIfCancellationRequested();
                var root = Path.GetFullPath(source.Path);
                var result = await indexer.IndexFolderAsync(imageLibrary.LibraryId, root, source.IncludeSubdirectories, ct).ConfigureAwait(false);
                found += result.FilesSeen;
                added += result.ItemsAdded;
                errors += result.Errors;
                await FileIntoFoldersAsync(imageLibrary.LibraryId, root, result.Images, ct).ConfigureAwait(false);
                await ReviveReturnedAsync(result.Images, ct).ConfigureAwait(false);
            }
        }

        logger.LogInformation("Vault images looked for: {Found} found, {Added} added, {Errors} could not be read.", found, added, errors);
        return new VaultImageScanDto { ImagesFound = found, ImagesAdded = added, Errors = errors };
    }

    private async Task FileIntoFoldersAsync(Guid libraryId, string root, IReadOnlyList<VaultIndexedImage> indexed, CancellationToken ct)
    {
        foreach (var folder in indexed.GroupBy(image => image.FolderPath, StringComparer.OrdinalIgnoreCase))
        {
            var folderId = await images.EnsureFolderAsync(libraryId, FolderKey(root, folder.Key), FolderName(root, folder.Key), ct).ConfigureAwait(false);
            await images.AssignFolderAsync(folder.Select(image => image.ItemId).ToList(), folderId, ct).ConfigureAwait(false);
        }
    }

    // A deleted image whose file is back on disk (put back by hand or from the recycle bin) is an image again.
    private async Task ReviveReturnedAsync(IReadOnlyList<VaultIndexedImage> indexed, CancellationToken ct)
    {
        var existing = indexed.Where(image => !image.ItemAdded).Select(image => image.ItemId).ToList();
        if (existing.Count == 0)
        {
            return;
        }

        foreach (var item in assets.FindMany(existing, ct).Where(item => item.TrashedAt is not null))
        {
            await assets.SetLifecycleStateAsync(item.Id, LocalAssetLifecycleState.Active, ct).ConfigureAwait(false);
            await images.SetRecycleEntriesAsync(item.Id, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A hash of the folder's place under the library folder, so no path is stored and a renamed library folder keeps its galleries.</summary>
    internal static string FolderKey(string root, string folder)
    {
        var relative = Path.GetRelativePath(root, folder).Replace('\\', '/').Trim('/');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("vault-folder:" + relative.ToLowerInvariant())));
    }

    /// <summary>The folder's own name; for the library folder itself, the name of that folder.</summary>
    internal static string FolderName(string root, string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "Images" : name;
    }

    // ------------------------------------------------------------------ galleries

    /// <summary>Every gallery: the folders first (by name), then the hand-made and rule galleries.</summary>
    public async Task<IReadOnlyList<VaultImageGalleryDto>> ListGalleriesAsync(CancellationToken ct = default)
    {
        var result = new List<VaultImageGalleryDto>();
        foreach (var folder in await images.ListFoldersAsync(ct).ConfigureAwait(false))
        {
            result.Add(new VaultImageGalleryDto
            {
                Id = folder.Id,
                Kind = VaultImageGalleryKinds.Folder,
                Name = folder.Name,
                ImageCount = folder.ImageCount,
                CoverImageId = folder.CoverItemId,
            });
        }

        foreach (var gallery in await galleries.ListAsync(ct).ConfigureAwait(false))
        {
            result.Add(await ToDtoAsync(gallery, ct).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<VaultImageGalleryDto?> GetGalleryAsync(Guid galleryId, CancellationToken ct = default)
    {
        if (await images.GetFolderAsync(galleryId, ct).ConfigureAwait(false) is { } folder)
        {
            return new VaultImageGalleryDto
            {
                Id = folder.Id,
                Kind = VaultImageGalleryKinds.Folder,
                Name = folder.Name,
                ImageCount = folder.ImageCount,
                CoverImageId = (await ListInternalAsync(new LocalAssetVaultScope(FolderId: folder.Id), null, new VaultImageListRequest(Take: 1), ct).ConfigureAwait(false)).Items.FirstOrDefault()?.Id,
            };
        }

        return await galleries.GetAsync(galleryId, ct).ConfigureAwait(false) is { } gallery
            ? await ToDtoAsync(gallery, ct).ConfigureAwait(false)
            : null;
    }

    public async Task<VaultImageResult<VaultImageGalleryDto>> CreateGalleryAsync(
        VaultImageGalleryRequest request, Guid? profileId, CancellationToken ct = default)
    {
        if (!VaultImageGalleryKinds.IsEditable(request.Kind))
        {
            return VaultImageResult<VaultImageGalleryDto>.Rejected("Choose a hand-made or a smart gallery.");
        }

        var libraries = await images.GetLibrariesAsync(ct).ConfigureAwait(false);
        if (libraries.Count == 0)
        {
            return VaultImageResult<VaultImageGalleryDto>.Rejected("There are no Vault images to put in a gallery yet.");
        }

        var smart = request.Kind == VaultImageGalleryKinds.Smart;
        if (!TryNormalizeRule(smart ? request.SmartRuleJson : null, out var ruleJson, out var problem))
        {
            return VaultImageResult<VaultImageGalleryDto>.Rejected(problem);
        }

        try
        {
            var created = await galleries.CreateAsync(
                libraries[0].LibraryId, request.Name, request.Description,
                smart ? VaultGalleryKind.Smart : VaultGalleryKind.Manual, ruleJson, profileId, ct).ConfigureAwait(false);
            return VaultImageResult<VaultImageGalleryDto>.Ok(await ToDtoAsync(created, ct).ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return VaultImageResult<VaultImageGalleryDto>.Rejected(exception.Message.Split('(')[0].Trim());
        }
    }

    public async Task<VaultImageResult<VaultImageGalleryDto>> UpdateGalleryAsync(
        Guid galleryId, VaultImageGalleryRequest request, CancellationToken ct = default)
    {
        var existing = await galleries.GetAsync(galleryId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return VaultImageResult<VaultImageGalleryDto>.NotFound();
        }

        if (!TryNormalizeRule(existing.Kind == VaultGalleryKind.Smart ? request.SmartRuleJson : null, out var ruleJson, out var problem))
        {
            return VaultImageResult<VaultImageGalleryDto>.Rejected(problem);
        }

        if (request.CoverImageId is { } cover && !(await images.GetFactsAsync([cover], ct).ConfigureAwait(false)).ContainsKey(cover))
        {
            return VaultImageResult<VaultImageGalleryDto>.Rejected("That picture is not in the Vault.");
        }

        try
        {
            var updated = await galleries.UpdateAsync(galleryId, request.Name, request.Description, ruleJson, request.CoverImageId, ct).ConfigureAwait(false);
            return updated is null
                ? VaultImageResult<VaultImageGalleryDto>.NotFound()
                : VaultImageResult<VaultImageGalleryDto>.Ok(await ToDtoAsync(updated, ct).ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return VaultImageResult<VaultImageGalleryDto>.Rejected(exception.Message.Split('(')[0].Trim());
        }
    }

    public async Task<bool> DeleteGalleryAsync(Guid galleryId, CancellationToken ct = default) =>
        await galleries.DeleteAsync(galleryId, ct).ConfigureAwait(false);

    public async Task<VaultImageResult<int>> AddToGalleryAsync(Guid galleryId, IReadOnlyList<Guid> imageIds, CancellationToken ct = default)
    {
        var gallery = await galleries.GetAsync(galleryId, ct).ConfigureAwait(false);
        if (gallery is null)
        {
            return VaultImageResult<int>.NotFound();
        }

        return gallery.Kind != VaultGalleryKind.Manual
            ? VaultImageResult<int>.Rejected("A smart gallery fills itself from its rule.")
            : VaultImageResult<int>.Ok(await galleries.AddItemsAsync(galleryId, imageIds, ct).ConfigureAwait(false));
    }

    public async Task<VaultImageResult<int>> RemoveFromGalleryAsync(Guid galleryId, IReadOnlyList<Guid> imageIds, CancellationToken ct = default)
    {
        var gallery = await galleries.GetAsync(galleryId, ct).ConfigureAwait(false);
        if (gallery is null)
        {
            return VaultImageResult<int>.NotFound();
        }

        return gallery.Kind != VaultGalleryKind.Manual
            ? VaultImageResult<int>.Rejected("A smart gallery fills itself from its rule.")
            : VaultImageResult<int>.Ok(await galleries.RemoveItemsAsync(galleryId, imageIds, ct).ConfigureAwait(false));
    }

    private async Task<VaultImageGalleryDto> ToDtoAsync(VaultGallery gallery, CancellationToken ct)
    {
        var scope = new LocalAssetVaultScope(GalleryId: gallery.Kind == VaultGalleryKind.Manual ? gallery.Id : null);
        var rule = gallery.Kind == VaultGalleryKind.Smart ? ParseRule(gallery.SmartRuleJson) : null;
        var count = 0;
        Guid? coverId = gallery.CoverItemId;
        var libraryIds = (await images.GetLibrariesAsync(ct).ConfigureAwait(false)).Select(library => library.LibraryId).ToList();
        if (libraryIds.Count > 0 && (gallery.Kind == VaultGalleryKind.Manual || rule is not null))
        {
            count = assets.QueryTimelineIndex(Query(libraryIds, scope, rule, new VaultImageListRequest(), null, 1), ct).Sum(bucket => bucket.AssetCount);
            if (coverId is null && count > 0)
            {
                coverId = assets.QueryTimeline(Query(libraryIds, scope, rule, new VaultImageListRequest(), null, 1), ct).Items.FirstOrDefault()?.Id;
            }
        }

        return new VaultImageGalleryDto
        {
            Id = gallery.Id,
            Kind = gallery.Kind == VaultGalleryKind.Smart ? VaultImageGalleryKinds.Smart : VaultImageGalleryKinds.Manual,
            Name = gallery.Name,
            Description = gallery.Description,
            ImageCount = count,
            CoverImageId = coverId,
            SmartRuleJson = gallery.SmartRuleJson,
            Editable = true,
        };
    }

    private static bool TryNormalizeRule(string? json, out string? normalized, out string problem)
    {
        normalized = null;
        problem = string.Empty;
        if (json is null)
        {
            return true;
        }

        try
        {
            normalized = ViewSmartGalleryRules.SerializeForVault(ViewSmartGalleryRules.Parse(json));
            return true;
        }
        catch (ArgumentException exception)
        {
            // The rule checks say what is wrong in words that name no image or tag, so the message is safe to show.
            problem = exception.Message.Split('(')[0].Trim();
            return false;
        }
    }

    private static CollectionRuleDefinition? ParseRule(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : ViewSmartGalleryRules.ParseForVault(json);
        }
        catch (ArgumentException)
        {
            // A stored rule the current checks no longer accept matches nothing rather than everything.
            return null;
        }
    }

    // ------------------------------------------------------------------ listing and finding

    /// <summary>Lists images, all of them or those of one gallery; null when the gallery named in the request is not there.</summary>
    public async Task<VaultImagePageDto?> ListAsync(VaultImageListRequest request, CancellationToken ct = default)
    {
        if (request.GalleryId is null)
        {
            return await ListInternalAsync(new LocalAssetVaultScope(), null, request, ct).ConfigureAwait(false);
        }

        return await ResolveGalleryAsync(request.GalleryId.Value, ct).ConfigureAwait(false) is { } resolved
            ? await ListInternalAsync(resolved.Scope, resolved.Rule, request, ct).ConfigureAwait(false)
            : null;
    }

    private async Task<(LocalAssetVaultScope Scope, CollectionRuleDefinition? Rule)?> ResolveGalleryAsync(Guid galleryId, CancellationToken ct)
    {
        if (await images.GetFolderAsync(galleryId, ct).ConfigureAwait(false) is { } folder)
        {
            return (new LocalAssetVaultScope(FolderId: folder.Id), null);
        }

        if (await galleries.GetAsync(galleryId, ct).ConfigureAwait(false) is not { } gallery)
        {
            return null;
        }

        return gallery.Kind == VaultGalleryKind.Manual
            ? (new LocalAssetVaultScope(GalleryId: gallery.Id), null)
            // A smart gallery whose saved rule cannot be read shows nothing rather than everything.
            : ParseRule(gallery.SmartRuleJson) is { } rule
                ? (new LocalAssetVaultScope(), rule)
                : (new LocalAssetVaultScope(GalleryId: Guid.Empty), null);
    }

    private async Task<VaultImagePageDto> ListInternalAsync(
        LocalAssetVaultScope scope, CollectionRuleDefinition? rule, VaultImageListRequest request, CancellationToken ct)
    {
        var libraryIds = (await images.GetLibrariesAsync(ct).ConfigureAwait(false)).Select(library => library.LibraryId).ToList();
        if (libraryIds.Count == 0)
        {
            return new VaultImagePageDto();
        }

        var take = Math.Clamp(request.Take, 1, MaximumTake);
        var query = Query(libraryIds, scope, WithTag(rule, request.TagId), request, ViewTimelineCursorCodec.Decode(request.Cursor), take);
        var page = assets.QueryTimeline(query, ct);
        return new VaultImagePageDto
        {
            Items = await ToDtosAsync(page.Items, ct).ConfigureAwait(false),
            NextCursor = ViewTimelineCursorCodec.Encode(page.NextCursor),
            HasMore = page.HasMore,
        };
    }

    private static LocalAssetTimelineQuery Query(
        IReadOnlyCollection<Guid> libraryIds,
        LocalAssetVaultScope scope,
        CollectionRuleDefinition? rule,
        VaultImageListRequest request,
        LocalAssetTimelineCursor? cursor,
        int take) => new(
            libraryIds,
            take,
            cursor?.EffectiveAt,
            cursor?.ItemId,
            string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim(),
            [LocalAssetMediaKinds.Image],
            request.FavoritesOnly,
            IncludeHidden: true,
            Lifecycle: request.DeletedOnly ? LocalAssetLifecycleFilter.Trashed : LocalAssetLifecycleFilter.Active,
            SmartRule: rule,
            Vault: scope);

    // A tag chip adds one more group to a rule: "and the image has this tag". It is joined with "and", so it narrows whatever came before.
    private static CollectionRuleDefinition? WithTag(CollectionRuleDefinition? rule, Guid? tagId)
    {
        if (tagId is not { } tag)
        {
            return rule;
        }

        var tagGroup = new CollectionRuleGroup
        {
            JoinWithPrevious = "and",
            MatchMode = "all",
            Conditions = [new CollectionRulePredicate { Field = "tags", Op = "eq", Value = tag.ToString("D") }],
        };
        var combined = new CollectionRuleDefinition { Groups = [.. rule?.Groups ?? [], tagGroup] };
        return combined;
    }

    public async Task<VaultImagePageDto?> SearchAsync(VaultImageSearchRequest request, CancellationToken ct = default)
    {
        var text = request.Text?.Trim() ?? string.Empty;
        if (text.Length < MinimumSearchLength)
        {
            return new VaultImagePageDto();
        }

        return await ListAsync(
            new VaultImageListRequest(request.GalleryId, Text: text, Cursor: request.Cursor, Take: request.Take ?? DefaultTake), ct)
            .ConfigureAwait(false);
    }

    public async Task<VaultImageDetailDto?> GetAsync(Guid imageId, CancellationToken ct = default)
    {
        var facts = await images.GetFactsAsync([imageId], ct).ConfigureAwait(false);
        if (!facts.TryGetValue(imageId, out var fact) || assets.Find(imageId, ct) is not { } item)
        {
            return null;
        }

        var tags = await images.GetTagsAsync([imageId], ct).ConfigureAwait(false);
        var folder = fact.FolderId is { } folderId ? await images.GetFolderAsync(folderId, ct).ConfigureAwait(false) : null;
        var dto = (await ToDtosAsync([item], ct).ConfigureAwait(false))[0];
        var device = string.Join(' ', new[] { item.DeviceMake, item.DeviceModel }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new VaultImageDetailDto
        {
            Image = dto,
            Tags = tags.TryGetValue(imageId, out var list)
                ? list.Select(tag => new VaultImageTagDto { Id = tag.Id, Name = tag.Name }).ToList()
                : [],
            Folder = folder?.Name,
            SizeBytes = item.Files.FirstOrDefault()?.ByteSize,
            Device = device.Length == 0 ? null : device,
        };
    }

    private async Task<IReadOnlyList<VaultImageDto>> ToDtosAsync(IReadOnlyList<LocalAssetDto> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var ids = items.Select(item => item.Id).ToList();
        var facts = await images.GetFactsAsync(ids, ct).ConfigureAwait(false);
        var tags = await images.GetTagsAsync(ids, ct).ConfigureAwait(false);
        return items.Select(item => new VaultImageDto
        {
            Id = item.Id,
            Title = string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileNameWithoutExtension(item.FileName) : item.Title!,
            FileName = item.FileName,
            MimeType = item.MimeType,
            Width = item.Width,
            Height = item.Height,
            CapturedAt = item.CapturedAt ?? item.CreatedAt,
            AddedAt = item.CreatedAt,
            Favorite = item.Favorite,
            Deleted = item.TrashedAt is not null,
            FolderId = facts.TryGetValue(item.Id, out var fact) ? fact.FolderId : null,
            Tags = tags.TryGetValue(item.Id, out var list) ? list.Select(tag => tag.Name).ToList() : [],
        }).ToList();
    }

    // ------------------------------------------------------------------ pictures

    /// <summary>The thumbnail (160, 320 or 640), the preview (1280 or 2048) or, when <paramref name="width"/> is null, the original file.</summary>
    public async Task<VaultImageFile?> OpenAsync(Guid imageId, int? width, CancellationToken ct = default)
    {
        if (!(await images.GetFactsAsync([imageId], ct).ConfigureAwait(false)).ContainsKey(imageId)
            || assets.ResolveContent(imageId, LocalAssetFileRoles.Primary, ct) is not { } file
            || !File.Exists(file.FilePath))
        {
            return null;
        }

        if (width is null)
        {
            return new VaultImageFile(file.FilePath, file.MimeType);
        }

        if (!IsAllowedWidth(width.Value))
        {
            return null;
        }

        var sized = await thumbnails.GetOrCreateVaultSizedAsync(imageId, file, width.Value, ct).ConfigureAwait(false);
        return sized is null ? null : new VaultImageFile(sized, "image/jpeg");
    }

    /// <summary>The only sizes the Engine makes, so a request can never fill the disk with odd ones.</summary>
    public static bool IsAllowedWidth(int width) =>
        ViewThumbnailService.ThumbnailEdges.Contains(width) || ViewThumbnailService.PreviewEdges.Contains(width);

    // ------------------------------------------------------------------ marks and tags

    public async Task<bool> SetFavoriteAsync(Guid imageId, bool favorite, CancellationToken ct = default) =>
        (await images.GetFactsAsync([imageId], ct).ConfigureAwait(false)).ContainsKey(imageId)
        && await assets.SetFlagsAsync(imageId, favorite, null, ct).ConfigureAwait(false);

    public async Task<bool> SetTagsAsync(Guid imageId, IReadOnlyList<string> tags, CancellationToken ct = default) =>
        await images.ReplaceTagsAsync(imageId, tags, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<VaultImageTagDto>> ListTagsAsync(CancellationToken ct = default) =>
        (await images.ListTagsAsync(ct).ConfigureAwait(false))
            .Select(tag => new VaultImageTagDto { Id = tag.Id, Name = tag.Name, ImageCount = tag.ImageCount }).ToList();

    // ------------------------------------------------------------------ recycle bin (D14, D34)

    /// <summary>
    /// Deletes an image: every file of it goes into the recycle bin (where it is listed, restored and emptied only inside NSFW mode) and
    /// leaves the disk only when the bin is emptied. The image stops appearing everywhere at once.
    /// </summary>
    public async Task<VaultImageResult<bool>> DeleteAsync(Guid imageId, CancellationToken ct = default)
    {
        var facts = await images.GetFactsAsync([imageId], ct).ConfigureAwait(false);
        if (!facts.TryGetValue(imageId, out var fact) || assets.Find(imageId, ct) is not { TrashedAt: null } item)
        {
            return VaultImageResult<bool>.NotFound();
        }

        var vaultLibraryId = (await images.GetLibrariesAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(library => library.LibraryId == fact.LibraryId)?.VaultLibraryId;

        // Original source media stay protected: every file must pass the same source-file safety gate the rest of the Library uses
        // (a folder Library only reads is never touched), and nothing moves unless all of them do.
        var files = item.Files.Select(file => assets.ResolveContent(imageId, file.Role, ct)).OfType<LocalAssetContentLocation>()
            .Where(file => File.Exists(file.FilePath)).ToList();
        if (files.Any(file => !MayDelete(file.FilePath)))
        {
            return VaultImageResult<bool>.Rejected("This image is in a folder that Library only reads, so it can't be deleted from here.");
        }

        var entries = new List<Guid>();
        try
        {
            foreach (var file in files)
            {
                entries.Add(recycleBin.MoveIn(file.FilePath, null, vaultLibraryId).Id);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("A Vault image could not be moved to the recycle bin ({Kind}).", exception.GetType().Name);
            foreach (var entry in entries)
            {
                recycleBin.Restore(entry);
            }

            return VaultImageResult<bool>.Failed("The image could not be moved to the recycle bin. Nothing was changed.");
        }

        await assets.SetLifecycleStateAsync(imageId, LocalAssetLifecycleState.Trashed, ct).ConfigureAwait(false);
        await images.SetRecycleEntriesAsync(imageId, entries, ct).ConfigureAwait(false);
        return VaultImageResult<bool>.Ok(true);
    }

    // The answer to "may this file be deleted" comes only from the shared safety gate, built the way the rest of the Library builds it.
    private bool MayDelete(string filePath)
    {
        if (folders.ResolveSourceForPath(filePath) is not { } resolved)
        {
            return false;
        }

        var policy = FileSourceMutationPolicyFactory.Create(resolved.Library, resolved.Source, allowDelete: true, isMergeTarget: false, isMergeJob: false);
        return gate.Evaluate(new SourceMutationRequest { Source = policy, Mutation = SourceMutationKind.Delete, Path = filePath }).Allowed;
    }

    /// <summary>Puts a deleted image back where it was. Nothing is overwritten.</summary>
    public async Task<VaultImageResult<bool>> RestoreAsync(Guid imageId, CancellationToken ct = default)
    {
        var facts = await images.GetFactsAsync([imageId], ct).ConfigureAwait(false);
        if (!facts.TryGetValue(imageId, out var fact) || assets.Find(imageId, ct) is not { TrashedAt: not null })
        {
            return VaultImageResult<bool>.NotFound();
        }

        foreach (var entry in fact.RecycleEntryIds)
        {
            var result = recycleBin.Restore(entry);
            if (result == RecycleBinRestoreResult.PlaceTaken)
            {
                return VaultImageResult<bool>.Rejected("Something is already at that place, so nothing was put back.");
            }
            else if (result == RecycleBinRestoreResult.Failed)
            {
                return VaultImageResult<bool>.Failed("The image could not be put back. It is still in the recycle bin.");
            }
        }

        await assets.SetLifecycleStateAsync(imageId, LocalAssetLifecycleState.Active, ct).ConfigureAwait(false);
        await images.SetRecycleEntriesAsync(imageId, null, ct).ConfigureAwait(false);
        return VaultImageResult<bool>.Ok(true);
    }

    /// <summary>The deleted images, newest first.</summary>
    public async Task<VaultImagePageDto> ListDeletedAsync(string? cursor, int take, CancellationToken ct = default)
    {
        // Images whose bin entries were emptied some other way (the main recycle bin) are gone for good; their records go too.
        var vanished = (await images.ListDeletedAsync(ct).ConfigureAwait(false))
            .Where(fact => fact.RecycleEntryIds.Count > 0 && fact.RecycleEntryIds.All(entry => !recycleBin.Contains(entry)))
            .Select(fact => fact.ItemId).ToList();
        if (vanished.Count > 0)
        {
            await images.RemoveItemsAsync(vanished, ct).ConfigureAwait(false);
        }

        return await ListInternalAsync(
            new LocalAssetVaultScope(), null, new VaultImageListRequest(Cursor: cursor, Take: take, DeletedOnly: true), ct).ConfigureAwait(false);
    }

    /// <summary>Removes every deleted Vault image from the disk for good, and their records with them. Returns how many images went.</summary>
    public async Task<int> EmptyBinAsync(CancellationToken ct = default)
    {
        var deleted = await images.ListDeletedAsync(ct).ConfigureAwait(false);
        var gone = new List<Guid>();
        foreach (var fact in deleted)
        {
            ct.ThrowIfCancellationRequested();
            if (fact.RecycleEntryIds.Count > 0 && !RemoveEntriesFromBin(fact.RecycleEntryIds))
            {
                continue;
            }

            gone.Add(fact.ItemId);
        }

        return await images.RemoveItemsAsync(gone, ct).ConfigureAwait(false);
    }

    // The bin's own empty routine works on everything the caller can see, so a single entry is removed by deleting its folder through
    // a restore-free path: the bin keeps one folder per entry named by id.
    private bool RemoveEntriesFromBin(IReadOnlyList<Guid> entryIds) => recycleBin.Remove(entryIds);
}
