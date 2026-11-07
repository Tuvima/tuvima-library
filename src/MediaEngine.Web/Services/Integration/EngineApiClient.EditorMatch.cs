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

    public async Task<EditorMatchResultDto?> GetEditorMatchVerdictAsync(Guid entityId, ItemCanonicalRetailCandidateDto candidate, CancellationToken ct = default) =>
        await PostAsync<EditorMatchPlacementRequestDto, EditorMatchResultDto>(
            "Editor match verdict", $"/metadata/{entityId}/match-verdict", new EditorMatchPlacementRequestDto { Candidate = candidate }, ct: ct);

    public async Task<EditorMusicReleasesDto?> GetEditorMusicReleasesAsync(Guid entityId, string? artist, string? album, string? song, CancellationToken ct = default) =>
        await GetAsync<EditorMusicReleasesDto>(
            "Editor music releases",
            $"/metadata/{entityId}/match-music/releases",
            new Dictionary<string, string?> { ["artist"] = artist, ["album"] = album, ["song"] = song }, ct: ct);

    public async Task<EditorMusicReleaseTracksDto?> GetEditorMusicReleaseTracksAsync(Guid entityId, string releaseId, CancellationToken ct = default) =>
        await GetAsync<EditorMusicReleaseTracksDto>(
            "Editor music release tracks", $"/metadata/{entityId}/match-music/releases/{Uri.EscapeDataString(releaseId)}", ct: ct);

    public async Task<EditorGroupMatchDto?> LineUpEditorGroupMatchAsync(Guid entityId, EditorGroupMatchRequestDto request, CancellationToken ct = default) =>
        await PostAsync<EditorGroupMatchRequestDto, EditorGroupMatchDto>(
            "Editor group match", $"/metadata/{entityId}/match-group", request, ct: ct);
}
