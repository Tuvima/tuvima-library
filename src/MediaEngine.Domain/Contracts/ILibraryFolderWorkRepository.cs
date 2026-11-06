namespace MediaEngine.Domain.Contracts;

/// <summary>Reads which titles (works) have a live file beneath one folder, for the Folders filter on a library's browse page (Libraries v3 D18).</summary>
public interface ILibraryFolderWorkRepository
{
    /// <summary>The ids of the works with at least one live media asset beneath <paramref name="folderPath"/> (never a sibling folder that starts with the same letters). Orphaned files and catalogue-only works are left out.</summary>
    Task<IReadOnlySet<Guid>> ListWorkIdsInFolderAsync(string folderPath, CancellationToken ct = default);
}
