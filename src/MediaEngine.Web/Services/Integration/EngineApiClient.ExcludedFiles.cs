using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<IReadOnlyList<ExcludedFileDto>> GetExcludedFilesAsync(string? libraryId, CancellationToken ct = default) =>
        await GetAsync<List<ExcludedFileDto>>(
            "GET /library/excluded-files",
            "/library/excluded-files",
            static () => [],
            query: string.IsNullOrWhiteSpace(libraryId)
                ? null
                : new Dictionary<string, string?> { ["library_id"] = libraryId },
            ct: ct);

    public Task<ExcludedFileAddBackResultDto?> AddBackExcludedFileAsync(Guid id, CancellationToken ct = default) =>
        PostAsync<object, ExcludedFileAddBackResultDto>(
            "POST /library/excluded-files/{id}/add-back",
            $"/library/excluded-files/{id:D}/add-back",
            new { },
            ct: ct);
}
