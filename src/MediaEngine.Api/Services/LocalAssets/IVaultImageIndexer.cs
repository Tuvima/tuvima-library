namespace MediaEngine.Api.Services.LocalAssets;

/// <summary>One Vault image the indexer registered, and the folder it sits in.</summary>
public sealed record VaultIndexedImage(Guid ItemId, string FolderPath, bool ItemAdded);

/// <summary>What indexing one Vault folder did. Counts only; no name or path is ever part of it.</summary>
public sealed record VaultImageIndexResult(int FilesSeen, int ItemsAdded, int Errors, IReadOnlyList<VaultIndexedImage> Images);

/// <summary>
/// Reads the images under a Vault library's folder with View's own image reading (file types, RAW and JPEG pairs, sizes, capture date)
/// and registers them in the Vault image library (Videos spec D44). Unlike a View scan it makes no Personal Space, source or device,
/// keeps no GPS position, queues no text recognition, records no activity and logs no file name or path.
/// </summary>
public interface IVaultImageIndexer
{
    /// <param name="libraryId">The Vault image library the images are registered in.</param>
    /// <param name="root">One folder of the Vault library.</param>
    /// <param name="includeSubdirectories">Whether folders inside <paramref name="root"/> are read too.</param>
    Task<VaultImageIndexResult> IndexFolderAsync(Guid libraryId, string root, bool includeSubdirectories, CancellationToken ct = default);
}
