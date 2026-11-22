using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>People found in this person's own photos: <paramref name="named"/> true for named people, false for the "Who's this?" groups, null for both.</summary>
    Task<ViewFaceGroupsDto?> GetViewFaceGroupsAsync(bool? named, bool includeHidden, int take = 100, CancellationToken ct = default);

    Task<ViewFaceGroupDto?> GetViewFaceGroupAsync(Guid personId, CancellationToken ct = default);

    Task<ViewFaceBoxesDto?> GetViewFaceGroupFacesAsync(Guid personId, int take = 60, CancellationToken ct = default);

    Task<ViewFaceSuggestionsDto?> GetViewFaceSuggestionsAsync(Guid personId, CancellationToken ct = default);

    Task<ViewFaceBoxesDto?> GetViewItemFacesAsync(Guid itemId, CancellationToken ct = default);

    Task<ViewFaceProgressDto?> GetViewFaceProgressAsync(CancellationToken ct = default);

    Task<ViewNamePersonResultDto?> NameViewFacePersonAsync(Guid personId, string name, CancellationToken ct = default);

    Task<bool> MergeViewFacePeopleAsync(Guid personId, Guid intoPersonId, CancellationToken ct = default);

    Task<bool> ConfirmViewFacesAsync(Guid personId, IReadOnlyList<Guid> accepted, IReadOnlyList<Guid> rejected, CancellationToken ct = default);

    Task<bool> RejectViewFacesAsync(Guid personId, IReadOnlyList<Guid> faceIds, CancellationToken ct = default);

    Task<bool> HideViewFacePersonAsync(Guid personId, bool hidden, CancellationToken ct = default);

    Task<bool> SetViewFacePersonCoverAsync(Guid personId, Guid faceId, CancellationToken ct = default);

    Task<bool> DeleteViewFacePersonAsync(Guid personId, CancellationToken ct = default);

    Task<bool> RemoveViewFaceAsync(Guid faceId, CancellationToken ct = default);

    Task<bool> DeleteAllViewFaceDataAsync(CancellationToken ct = default);

    Task<bool> GetViewRecognizePeopleAsync(CancellationToken ct = default);

    Task<bool> SetViewRecognizePeopleAsync(bool recognize, CancellationToken ct = default);
}
