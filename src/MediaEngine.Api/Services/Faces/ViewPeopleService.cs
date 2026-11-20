using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.Faces;

/// <summary>Suggestion-first naming (spec view-people-search D3): after someone is named, groups that look like them are offered for a yes or no.</summary>
public sealed class ViewPeopleService(IViewFaceRepository faces)
{
    /// <summary>A little below <see cref="FaceIndex.SameFaceThreshold"/>: close enough to ask, not close enough to decide.</summary>
    public const double AskThreshold = 0.28;

    private const int MaxSuggestions = 5;
    private const int FacesShown = 12;

    /// <summary>The unnamed groups most like this person, closest first, never one that already contains a face marked "not them".</summary>
    public async Task<IReadOnlyList<(ViewFaceGroup Group, IReadOnlyList<ViewFaceBox> Faces, double Similarity)>> SuggestAsync(
        Guid ownerProfileId, Guid personId, CancellationToken ct)
    {
        var looks = await faces.GetAssignedLooksAsync(ownerProfileId, ct).ConfigureAwait(false);
        var own = looks.Where(look => look.PersonId == personId).ToList();
        if (own.Count == 0)
        {
            return [];
        }

        var centroid = FaceIndex.Normalize(Sum(own.Select(look => FaceIndex.Normalize(look.Embedding))));
        var groups = (await faces.GetGroupsAsync(ownerProfileId, false, false, 200, ct).ConfigureAwait(false))
            .Where(group => group.PersonId != personId)
            .ToDictionary(group => group.PersonId);
        var scored = looks
            .Where(look => look.PersonId is { } id && groups.ContainsKey(id))
            .GroupBy(look => look.PersonId!.Value)
            .Select(group => (Id: group.Key, Faces: group.ToList(), Score: FaceIndex.Similarity(
                centroid, FaceIndex.Normalize(Sum(group.Select(look => FaceIndex.Normalize(look.Embedding)))))))
            .Where(candidate => candidate.Score >= AskThreshold)
            .OrderByDescending(candidate => candidate.Score)
            .Take(MaxSuggestions * 2)
            .ToList();

        var result = new List<(ViewFaceGroup, IReadOnlyList<ViewFaceBox>, double)>();
        foreach (var candidate in scored)
        {
            var rejected = await faces.GetRejectedPeopleAsync(ownerProfileId, candidate.Faces.Select(look => look.FaceId).ToList(), ct).ConfigureAwait(false);
            if (rejected.Contains(personId))
            {
                continue;
            }

            var shown = await faces.GetFacesOfPersonAsync(ownerProfileId, candidate.Id, FacesShown, ct).ConfigureAwait(false);
            result.Add((groups[candidate.Id], shown, candidate.Score));
            if (result.Count == MaxSuggestions)
            {
                break;
            }
        }

        return result;
    }

    private static float[] Sum(IEnumerable<float[]> looks)
    {
        float[]? sum = null;
        foreach (var look in looks)
        {
            sum ??= new float[look.Length];
            for (var i = 0; i < look.Length && i < sum.Length; i++)
            {
                sum[i] += look[i];
            }
        }

        return sum ?? [];
    }
}
