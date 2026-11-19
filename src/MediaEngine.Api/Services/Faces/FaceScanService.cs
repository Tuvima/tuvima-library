using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.Faces;

/// <summary>
/// Looks through one person's own photos for faces and puts look-alikes together (spec view-people-search WP7). It only ever reads the
/// photos of the library that profile owns, never the Vault, a shared library or anyone else's photos, and a photo is looked at once.
/// </summary>
public sealed class FaceScanService(
    IViewFaceRepository faces,
    IViewProfileRepository profiles,
    IViewPersonalSpaceRepository spaces,
    IFaceEngine engine,
    ILogger<FaceScanService> logger)
{
    private const int PageSize = 50;

    /// <summary>True when this person turned recognition off (or it cannot run yet), so nothing is looked at.</summary>
    public async Task<bool> CanScanAsync(Guid ownerProfileId, CancellationToken ct) =>
        engine.IsAvailable && (await profiles.GetPreferencesAsync(ownerProfileId, ct).ConfigureAwait(false)).RecognizePeople;

    /// <summary>
    /// Looks at up to <paramref name="maxPhotos"/> photos that were not looked at yet and returns how many it handled. Safe to call again at any
    /// time (after a restart it simply carries on), and a photo that fails is recorded as looked at with no faces so it never blocks the rest.
    /// </summary>
    public async Task<int> ScanBatchAsync(Guid ownerProfileId, int maxPhotos, CancellationToken ct)
    {
        if (!await CanScanAsync(ownerProfileId, ct).ConfigureAwait(false))
        {
            return 0;
        }

        var space = await spaces.GetByOwnerAsync(ownerProfileId, ct).ConfigureAwait(false);
        if (space is null)
        {
            return 0;
        }

        var candidates = await faces.GetUnscannedPhotosAsync(
            ownerProfileId, space.LibraryId, null, Math.Min(maxPhotos, PageSize), FaceImageFormats.Readable, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var index = new FaceIndex();
        foreach (var look in await faces.GetAssignedLooksAsync(ownerProfileId, ct).ConfigureAwait(false))
        {
            if (look.PersonId is { } personId)
            {
                index.Add(personId, FaceIndex.Normalize(look.Embedding));
            }
        }

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            await ScanPhotoAsync(ownerProfileId, candidate, index, ct).ConfigureAwait(false);
        }

        return candidates.Count;
    }

    private async Task ScanPhotoAsync(Guid ownerProfileId, ViewFaceScanCandidate photo, FaceIndex index, CancellationToken ct)
    {
        IReadOnlyList<ViewDetectedFace> found;
        try
        {
            found = await engine.AnalyseAsync(photo.FilePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreadable photo must not stop the rest; it is recorded as looked at with no faces. The file name is not logged.
            logger.LogWarning(ex, "A photo could not be looked at for faces.");
            found = [];
        }

        var inserts = new List<ViewFaceInsert>();
        var newPeople = new List<Guid>();
        foreach (var face in found)
        {
            var look = FaceIndex.Normalize(face.Embedding);
            // Two faces in one photo are two different people, so a person already used in this photo is not offered again.
            var taken = inserts.Select(insert => insert.PersonId).ToHashSet();
            var personId = index.Closest(look, taken);
            if (personId is null)
            {
                personId = Guid.NewGuid();
                newPeople.Add(personId.Value);
            }

            index.Add(personId.Value, look);
            inserts.Add(new ViewFaceInsert(Guid.NewGuid(), personId.Value, face with { Embedding = look }));
        }

        await faces.SaveScanAsync(ownerProfileId, photo.ItemId, engine.ModelId, inserts, newPeople, ct).ConfigureAwait(false);
    }
}

/// <summary>The picture types the face scan reads.</summary>
public static class FaceImageFormats
{
    public static IReadOnlyCollection<string> Readable { get; } = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".heic", ".heif"];
}
