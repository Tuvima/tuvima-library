using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Faces found in a person's own photos, the people they belong to, and which photos were already looked at (spec view-people-search).
/// Everything is keyed by the owning profile and never read across profiles; the Vault is never offered to the scan.
/// </summary>
public interface IViewFaceRepository
{
    /// <summary>Photos of this person's own library (never trashed, missing or Vault photos) that have not been looked at yet, paged by item id.</summary>
    Task<IReadOnlyList<ViewFaceScanCandidate>> GetUnscannedPhotosAsync(
        Guid ownerProfileId, Guid libraryId, Guid? afterItemId, int limit, IReadOnlyCollection<string> imageExtensions, CancellationToken ct = default);

    /// <summary>The looks of this person's saved faces that already belong to a person.</summary>
    Task<IReadOnlyList<ViewFaceLook>> GetAssignedLooksAsync(Guid ownerProfileId, CancellationToken ct = default);

    /// <summary>Saves one photo's faces and marks the photo as looked at, in one step. New people (unnamed groups) are created for ids not seen before.</summary>
    Task SaveScanAsync(
        Guid ownerProfileId, Guid itemId, string model, IReadOnlyList<ViewFaceInsert> faces, IReadOnlyCollection<Guid> newPeople, CancellationToken ct = default);

    /// <summary>How far the scan has got and what it found.</summary>
    Task<ViewFaceScanProgress> GetProgressAsync(Guid ownerProfileId, Guid libraryId, CancellationToken ct = default);

    /// <summary>Removes every face, person and scan record of this profile. Returns how many faces were removed.</summary>
    Task<int> DeleteAllAsync(Guid ownerProfileId, CancellationToken ct = default);

    /// <summary>The people and unnamed groups of this profile, biggest first. <paramref name="named"/> picks only named people (true), only unnamed groups (false) or both (null).</summary>
    Task<IReadOnlyList<ViewFaceGroup>> GetGroupsAsync(Guid ownerProfileId, bool? named, bool includeHidden, int limit, CancellationToken ct = default);

    /// <summary>One person or group of this profile, or null when it is not theirs.</summary>
    Task<ViewFaceGroup?> GetGroupAsync(Guid ownerProfileId, Guid personId, CancellationToken ct = default);

    /// <summary>The faces of one person or group, best quality first.</summary>
    Task<IReadOnlyList<ViewFaceBox>> GetFacesOfPersonAsync(Guid ownerProfileId, Guid personId, int limit, CancellationToken ct = default);

    /// <summary>The faces found in one photo of this profile.</summary>
    Task<IReadOnlyList<ViewFaceBox>> GetFacesOfItemAsync(Guid ownerProfileId, Guid itemId, CancellationToken ct = default);

    /// <summary>One face of this profile, or null when it is not theirs.</summary>
    Task<ViewFaceBox?> GetFaceAsync(Guid ownerProfileId, Guid faceId, CancellationToken ct = default);

    /// <summary>The faces this profile said are not the given person.</summary>
    Task<ISet<Guid>> GetRejectedPeopleAsync(Guid ownerProfileId, IReadOnlyCollection<Guid> faceIds, CancellationToken ct = default);

    /// <summary>Names a group. When this profile already has someone with that name the group joins them instead. Null when the person is not theirs.</summary>
    Task<ViewNamedPerson?> NamePersonAsync(Guid ownerProfileId, Guid personId, string name, CancellationToken ct = default);

    /// <summary>Moves every face of <paramref name="fromPersonId"/> to <paramref name="intoPersonId"/> and removes the emptied group.</summary>
    Task<bool> MergeAsync(Guid ownerProfileId, Guid fromPersonId, Guid intoPersonId, CancellationToken ct = default);

    /// <summary>"Yes, that is them": the accepted faces join the person (confirmed); the rest are marked as not that person and stay where they were.</summary>
    Task<bool> ConfirmFacesAsync(Guid ownerProfileId, Guid personId, IReadOnlyCollection<Guid> acceptedFaceIds, IReadOnlyCollection<Guid> rejectedFaceIds, CancellationToken ct = default);

    /// <summary>"Not Sam": the faces may never be put with that person again.</summary>
    Task<bool> RejectFacesAsync(Guid ownerProfileId, Guid personId, IReadOnlyCollection<Guid> faceIds, CancellationToken ct = default);

    Task<bool> SetHiddenAsync(Guid ownerProfileId, Guid personId, bool hidden, CancellationToken ct = default);

    Task<bool> SetCoverAsync(Guid ownerProfileId, Guid personId, Guid faceId, CancellationToken ct = default);

    /// <summary>Removes one face (it was not a face, or should not be tracked). The photo stays marked as looked at, so it is not found again.</summary>
    Task<bool> RemoveFaceAsync(Guid ownerProfileId, Guid faceId, CancellationToken ct = default);

    /// <summary>"Remove from all photos": deletes the person and every face matched to them.</summary>
    Task<bool> DeletePersonAsync(Guid ownerProfileId, Guid personId, CancellationToken ct = default);
}
