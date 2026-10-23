namespace MediaEngine.Domain.Contracts;

/// <summary>
/// How many files (media assets) each library holds, for a folder or for a list of files. Answers "who would lose sight of these files"
/// (simpler access D13) with one grouped read; it counts files, not titles, and leaves out files that are no longer on disk.
/// </summary>
public interface IMediaAssetLibraryCountRepository
{
    /// <summary>
    /// The files inside <paramref name="folderPath"/> (the same folder rule the first check uses, so <c>/media/kids2</c> is not inside
    /// <c>/media/kids</c>), counted per library they are in now. With <paramref name="libraryId"/> only the files in that library are counted.
    /// A file with no library is left out.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> CountByLibraryInFolderAsync(string folderPath, Guid? libraryId, CancellationToken ct = default);

    /// <summary>The listed files counted per library they are in now. An id that is not a file, or has no library, is left out.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountByLibraryForAssetsAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken ct = default);
}
