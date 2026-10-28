namespace MediaEngine.Domain.Contracts;

/// <summary>Reads how many titles the catalogue holds for each folder of a library (Libraries spec D7, the Folders tab's Titles column).</summary>
public interface ILibraryFolderTitleCountRepository
{
    /// <summary>
    /// For each folder path, the number of distinct titles (works) that have at least one live media asset beneath it. Orphaned files and
    /// catalogue-only works are not counted. A folder with nothing in it is present with zero; keys follow the paths given.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> CountByFolderAsync(IReadOnlyCollection<string> folderPaths, CancellationToken ct = default);
}
