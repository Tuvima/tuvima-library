using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /library/excluded-files - the files that stayed on disk when their title was deleted, newest first; one library's when <paramref name="libraryId"/> is given. Empty when the Engine cannot be reached.</summary>
    Task<IReadOnlyList<ExcludedFileDto>> GetExcludedFilesAsync(string? libraryId, CancellationToken ct = default);

    /// <summary>POST /library/excluded-files/{id}/add-back - removes the exclusion and queues a scan of that file. Null when the call failed.</summary>
    Task<ExcludedFileAddBackResultDto?> AddBackExcludedFileAsync(Guid id, CancellationToken ct = default);
}
