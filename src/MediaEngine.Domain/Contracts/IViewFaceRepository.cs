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
}
