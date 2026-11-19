using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<ViewFaceGroupsDto?> GetViewFaceGroupsAsync(bool? named, bool includeHidden, int take = 100, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["includeHidden"] = includeHidden ? "true" : "false",
            ["take"] = take.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (named.HasValue)
        {
            query["named"] = named.Value ? "true" : "false";
        }

        return GetAsync<ViewFaceGroupsDto>("GET /view/faces/groups", "/view/faces/groups", query: query, ct: ct);
    }

    public Task<ViewFaceGroupDto?> GetViewFaceGroupAsync(Guid personId, CancellationToken ct = default) =>
        GetAsync<ViewFaceGroupDto>("GET /view/faces/groups/{id}", $"/view/faces/groups/{personId:D}", ct: ct);

    public Task<ViewFaceBoxesDto?> GetViewFaceGroupFacesAsync(Guid personId, int take = 60, CancellationToken ct = default) =>
        GetAsync<ViewFaceBoxesDto>("GET /view/faces/groups/{id}/faces", $"/view/faces/groups/{personId:D}/faces",
            query: new Dictionary<string, string?> { ["take"] = take.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct: ct);

    public Task<ViewFaceSuggestionsDto?> GetViewFaceSuggestionsAsync(Guid personId, CancellationToken ct = default) =>
        GetAsync<ViewFaceSuggestionsDto>("GET /view/faces/groups/{id}/suggestions", $"/view/faces/groups/{personId:D}/suggestions", ct: ct);

    public Task<ViewFaceBoxesDto?> GetViewItemFacesAsync(Guid itemId, CancellationToken ct = default) =>
        GetAsync<ViewFaceBoxesDto>("GET /view/faces/items/{id}", $"/view/faces/items/{itemId:D}", ct: ct);

    public Task<ViewFaceProgressDto?> GetViewFaceProgressAsync(CancellationToken ct = default) =>
        GetAsync<ViewFaceProgressDto>("GET /view/faces/progress", "/view/faces/progress", ct: ct);

    public Task<ViewNamePersonResultDto?> NameViewFacePersonAsync(Guid personId, string name, CancellationToken ct = default) =>
        PostAsync<ViewNamePersonRequest, ViewNamePersonResultDto>("POST /view/faces/groups/{id}/name", $"/view/faces/groups/{personId:D}/name", new ViewNamePersonRequest(name), ct: ct);

    public Task<bool> MergeViewFacePeopleAsync(Guid personId, Guid intoPersonId, CancellationToken ct = default) =>
        PostAsync("POST /view/faces/groups/{id}/merge", $"/view/faces/groups/{personId:D}/merge", new ViewMergePeopleRequest(intoPersonId), ct: ct);

    public Task<bool> ConfirmViewFacesAsync(Guid personId, IReadOnlyList<Guid> accepted, IReadOnlyList<Guid> rejected, CancellationToken ct = default) =>
        PostAsync("POST /view/faces/groups/{id}/confirm", $"/view/faces/groups/{personId:D}/confirm", new ViewConfirmFacesRequest(accepted, rejected), ct: ct);

    public Task<bool> RejectViewFacesAsync(Guid personId, IReadOnlyList<Guid> faceIds, CancellationToken ct = default) =>
        PostAsync("POST /view/faces/groups/{id}/reject", $"/view/faces/groups/{personId:D}/reject", new ViewRejectFacesRequest(faceIds), ct: ct);

    public Task<bool> HideViewFacePersonAsync(Guid personId, bool hidden, CancellationToken ct = default) =>
        PutAsync("PUT /view/faces/groups/{id}/hidden", $"/view/faces/groups/{personId:D}/hidden", new ViewHidePersonRequest(hidden), ct: ct);

    public Task<bool> SetViewFacePersonCoverAsync(Guid personId, Guid faceId, CancellationToken ct = default) =>
        PutAsync("PUT /view/faces/groups/{id}/cover", $"/view/faces/groups/{personId:D}/cover", new ViewPersonCoverRequest(faceId), ct: ct);

    public Task<bool> DeleteViewFacePersonAsync(Guid personId, CancellationToken ct = default) =>
        DeleteAsync("DELETE /view/faces/groups/{id}", $"/view/faces/groups/{personId:D}", ct: ct);

    public Task<bool> RemoveViewFaceAsync(Guid faceId, CancellationToken ct = default) =>
        DeleteAsync("DELETE /view/faces/{id}", $"/view/faces/{faceId:D}", ct: ct);

    public Task<bool> DeleteAllViewFaceDataAsync(CancellationToken ct = default) =>
        DeleteAsync("DELETE /view/faces", "/view/faces/", ct: ct);

    public async Task<bool> GetViewRecognizePeopleAsync(CancellationToken ct = default) =>
        (await GetAsync<ViewRecognizePeopleDto>("GET /view/preferences/recognize-people", "/view/preferences/recognize-people", ct: ct))?.Recognize ?? true;

    public Task<bool> SetViewRecognizePeopleAsync(bool recognize, CancellationToken ct = default) =>
        PutAsync("PUT /view/preferences/recognize-people", "/view/preferences/recognize-people", new ViewRecognizePeopleDto(recognize), ct: ct);
}
