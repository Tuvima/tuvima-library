namespace MediaEngine.Domain.Contracts;

/// <summary>
/// A View-style library that holds the images of one Vault library (Videos spec D44). <see cref="LibraryId"/> is the id the images
/// carry as their library; <see cref="VaultLibraryId"/> is the Vault library it belongs to.
/// </summary>
public sealed record VaultImageLibrary(Guid LibraryId, string VaultLibraryId);

/// <summary>One folder of Vault images, shown as a gallery of its own. The name is the folder's own name; no path is kept.</summary>
public sealed record VaultImageFolder(Guid Id, Guid LibraryId, string Name, int ImageCount, Guid? CoverItemId);

/// <summary>A Vault image tag and how many active images use it. Tags are the Vault's own and never join the shared tag list.</summary>
public sealed record VaultImageTag(Guid Id, string Name, int ImageCount);

/// <summary>The kinds of Vault gallery. A folder gallery is not stored as a gallery: it is the folder itself.</summary>
public enum VaultGalleryKind
{
    Manual,
    Smart,
}

/// <summary>A Vault gallery made by hand or by a rule. <see cref="ImageCount"/> counts what it holds now (a rule is counted when it is read).</summary>
public sealed record VaultGallery(
    Guid Id,
    Guid LibraryId,
    string Name,
    string? Description,
    VaultGalleryKind Kind,
    string? SmartRuleJson,
    Guid? CoverItemId,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>What the Vault image rules say about one image beyond its View-style record: which folder it is in and whether it is deleted.</summary>
public sealed record VaultImageFacts(Guid ItemId, Guid LibraryId, Guid? FolderId, IReadOnlyList<Guid> RecycleEntryIds);

/// <summary>
/// The Vault's image records (Videos spec D44): which libraries hold images, the folder galleries, the Vault's own image tags and the
/// recycle bin link. The images themselves are ordinary View-style items read and written through the local asset repository; this
/// repository only holds what makes them Vault.
/// </summary>
public interface IVaultImageRepository
{
    /// <summary>Every Vault image library.</summary>
    Task<IReadOnlyList<VaultImageLibrary>> GetLibrariesAsync(CancellationToken ct = default);

    /// <summary>
    /// The image library of a Vault library, made on first use. Returns null when there is no such Vault library. The image library's
    /// id is the Vault library's own id when that is a GUID, so it is the same on every start.
    /// </summary>
    Task<VaultImageLibrary?> EnsureLibraryAsync(string vaultLibraryId, CancellationToken ct = default);

    /// <summary>The folder gallery for <paramref name="folderKey"/> (a hash of the folder's place), made on first use and renamed when its name changed.</summary>
    Task<Guid> EnsureFolderAsync(Guid libraryId, string folderKey, string name, CancellationToken ct = default);

    /// <summary>Puts images into a folder gallery.</summary>
    Task AssignFolderAsync(IReadOnlyCollection<Guid> itemIds, Guid folderId, CancellationToken ct = default);

    /// <summary>The folder galleries that hold at least one image that is not deleted, ordered by name.</summary>
    Task<IReadOnlyList<VaultImageFolder>> ListFoldersAsync(CancellationToken ct = default);

    Task<VaultImageFolder?> GetFolderAsync(Guid folderId, CancellationToken ct = default);

    /// <summary>The Vault facts of images, for those among <paramref name="itemIds"/> that are Vault images (others are left out).</summary>
    Task<IReadOnlyDictionary<Guid, VaultImageFacts>> GetFactsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>The tags of each image among <paramref name="itemIds"/>; an image with none is absent.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<VaultImageTag>>> GetTagsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>All tags with at least one active image, most used first.</summary>
    Task<IReadOnlyList<VaultImageTag>> ListTagsAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces an image's tags by name, adding names that are new to the Vault's own tags. Returns false when the id is not a Vault
    /// image. The image's search words are rebuilt, so search finds it by its tags.
    /// </summary>
    Task<bool> ReplaceTagsAsync(Guid itemId, IReadOnlyCollection<string> names, CancellationToken ct = default);

    /// <summary>Links an image to its recycle bin entries, one per file (deleted), or clears the link (put back).</summary>
    Task<bool> SetRecycleEntriesAsync(Guid itemId, IReadOnlyList<Guid>? entryIds, CancellationToken ct = default);

    /// <summary>The deleted images, newest first.</summary>
    Task<IReadOnlyList<VaultImageFacts>> ListDeletedAsync(CancellationToken ct = default);

    /// <summary>Removes the records of images whose files have left the disk for good (the bin was emptied). Returns how many.</summary>
    Task<int> RemoveItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);
}

/// <summary>The Vault's galleries made by hand or by a rule (Videos spec D44).</summary>
public interface IVaultGalleryRepository
{
    Task<IReadOnlyList<VaultGallery>> ListAsync(CancellationToken ct = default);

    Task<VaultGallery?> GetAsync(Guid galleryId, CancellationToken ct = default);

    /// <summary>Makes a gallery. A smart gallery needs a rule document (already validated by the caller); a manual one must not have one.</summary>
    Task<VaultGallery> CreateAsync(
        Guid libraryId, string name, string? description, VaultGalleryKind kind, string? smartRuleJson, Guid? createdByProfileId,
        CancellationToken ct = default);

    /// <summary>Renames or re-describes a gallery and replaces the rule of a smart one. Returns null when it is not there.</summary>
    Task<VaultGallery?> UpdateAsync(
        Guid galleryId, string name, string? description, string? smartRuleJson, Guid? coverItemId, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid galleryId, CancellationToken ct = default);

    /// <summary>Adds Vault images of the gallery's own library to a manual gallery. Returns how many were added; a smart gallery takes none.</summary>
    Task<int> AddItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>Takes images out of a manual gallery. Returns how many were removed.</summary>
    Task<int> RemoveItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>How many active images a manual gallery holds.</summary>
    Task<int> CountManualItemsAsync(Guid galleryId, CancellationToken ct = default);
}
