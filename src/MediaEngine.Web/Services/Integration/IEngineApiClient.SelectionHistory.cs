using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<(MediaEditorSelectionHistoryDto? History, string? Error)> GetMediaEditorSelectionHistoryAsync(
        Guid parentEntityId, IReadOnlyList<Guid> assetIds, CancellationToken ct = default);
}
