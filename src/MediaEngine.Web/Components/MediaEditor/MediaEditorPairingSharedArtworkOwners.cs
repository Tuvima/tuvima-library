using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Components.MediaEditor;

public sealed record MediaEditorPairingSharedArtworkOwner(Guid Id, string Scope, string Label);

/// <summary>Owner choices come only from the server-reviewed show and accepted saveable episodes.</summary>
public static class MediaEditorPairingSharedArtworkOwners
{
    public static IReadOnlyList<MediaEditorPairingSharedArtworkOwner> Build(
        MediaEditorPairingPreviewDto? preview, MediaEditorPairingReviewState state,
        string showLabel)
    {
        if (preview is not { MediaKind: "tv_episode", ReviewToken: not null,
                LocalParentWorkId: { } showId }
            || showId == Guid.Empty || state.AcceptedCount == 0)
            return [];

        var owners = new List<MediaEditorPairingSharedArtworkOwner>
        {
            new(showId, "TvShow", $"{(string.IsNullOrWhiteSpace(showLabel) ? "Reviewed show" : showLabel)} · TV show"),
        };
        foreach (var row in preview.Rows)
        {
            var acceptedId = state.AcceptedCandidateId(row.AssetId);
            if (acceptedId is null) continue;
            var candidate = row.Proposed?.Child.ChildId == acceptedId ? row.Proposed
                : row.Alternatives.FirstOrDefault(item => item.Child.ChildId == acceptedId);
            var child = candidate?.CanSave == true ? candidate.Child
                : state.SearchedAcceptedChild(row.AssetId);
            if (child?.LocalSeasonWorkId is not { } seasonId || seasonId == Guid.Empty
                || owners.Any(owner => owner.Id == seasonId))
                continue;
            owners.Add(new(seasonId, "TvSeason",
                child.SeasonNumber is { } number ? $"Season {number}" : "Reviewed season"));
        }
        return owners;
    }
}
