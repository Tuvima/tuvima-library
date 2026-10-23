namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Which files sit inside the folders an administrator marked "Suitable for limited profiles" (simpler access D7). It answers with the
/// first check's folder rule (<c>/media/kids2</c> is not inside <c>/media/kids</c>), never a second copy of it.
/// </summary>
public interface ISuitableFolderAssetRepository
{
    /// <summary>
    /// The listed files (media asset ids) whose file is inside any of <paramref name="folderPaths"/>. An id that is not a file, or sits
    /// elsewhere, is left out. With no folder nothing is read and nothing is returned.
    /// </summary>
    Task<IReadOnlySet<Guid>> FindAssetsInFoldersAsync(
        IReadOnlyCollection<Guid> assetIds, IReadOnlyList<string> folderPaths, CancellationToken ct = default);
}
