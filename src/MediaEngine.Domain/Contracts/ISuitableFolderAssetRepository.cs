namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Which files sit inside the folders an administrator marked "Suitable for limited profiles" (simpler access D7). It answers with the
/// first check's folder rule (<c>/media/kids2</c> is not inside <c>/media/kids</c>), never a second copy of it.
/// </summary>
public interface ISuitableFolderAssetRepository
{
    /// <summary>
    /// The media assets (files still on disk that belong to an edition of a work) inside any of <paramref name="folderPaths"/>. It asks from the folder
    /// side, one range read on the folded-path index per folder, so the cost follows the marked folders and not the length of any list.
    /// With no folder nothing is read and nothing is returned.
    /// </summary>
    Task<IReadOnlySet<Guid>> FindAssetsInFoldersAsync(IReadOnlyList<string> folderPaths, CancellationToken ct = default);
}
