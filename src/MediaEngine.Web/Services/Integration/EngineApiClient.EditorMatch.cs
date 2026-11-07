using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<EditorCurrentMatchDto?> GetEditorCurrentMatchAsync(Guid entityId, CancellationToken ct = default) =>
        await GetAsync<EditorCurrentMatchDto>("Editor current match", $"/metadata/{entityId}/current-match", ct: ct);

    public async Task<EditorMatchSetupDto?> GetEditorMatchSetupAsync(Guid entityId, CancellationToken ct = default) =>
        await GetAsync<EditorMatchSetupDto>("Editor match setup", $"/metadata/{entityId}/match-setup", ct: ct);

    public async Task<EditorMatchSearchResultDto?> SearchEditorMatchAsync(Guid entityId, EditorMatchSearchRequestDto request, CancellationToken ct = default) =>
        await PostAsync<EditorMatchSearchRequestDto, EditorMatchSearchResultDto>("Editor match search", $"/metadata/{entityId}/match-search", request, ct: ct);

    public async Task<EditorMatchPlacementDto?> GetEditorMatchPlacementAsync(Guid entityId, ItemCanonicalRetailCandidateDto candidate, CancellationToken ct = default) =>
        await PostAsync<EditorMatchPlacementRequestDto, EditorMatchPlacementDto>(
            "Editor match placement", $"/metadata/{entityId}/match-placement", new EditorMatchPlacementRequestDto { Candidate = candidate }, ct: ct);
}
