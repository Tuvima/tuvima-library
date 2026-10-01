using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
    private string _tvdbLocalFilter = string.Empty;

    private ItemCanonicalRetailCandidateDto? SelectedRetailMatchCandidate =>
        ActiveMatchSearchResponse?.RetailCandidates.FirstOrDefault(candidate =>
            IsCandidateSelected(GetCandidateId(candidate)));

    private ItemCanonicalLinkedCandidateDto? SelectedCanonicalMatchCandidate =>
        ActiveMatchSearchResponse?.LinkedCandidates.FirstOrDefault(candidate =>
            IsCandidateSelected(GetCandidateId(candidate)));

    private IReadOnlyList<TvdbMatchCandidateDto> FilteredTvdbPresentationCandidates
    {
        get
        {
            var candidates = FilteredTvdbCandidates;
            if (string.IsNullOrWhiteSpace(_tvdbLocalFilter))
                return candidates;

            return candidates.Where(candidate =>
            {
                var episode = candidate.EpisodeNumber is { } episodeNumber
                    ? $"S{candidate.SeasonNumber}E{episodeNumber}"
                    : $"Season {candidate.SeasonNumber}";
                return candidate.Title.Contains(_tvdbLocalFilter, StringComparison.OrdinalIgnoreCase)
                    || episode.Contains(_tvdbLocalFilter, StringComparison.OrdinalIgnoreCase)
                    || (candidate.Date?.Contains(_tvdbLocalFilter, StringComparison.OrdinalIgnoreCase) ?? false);
            }).ToArray();
        }
    }

    private void SetTvdbLocalFilter(string? value) => _tvdbLocalFilter = value?.Trim() ?? string.Empty;

    private string GetRetailCandidateArtworkShape(ItemCanonicalRetailCandidateDto candidate)
    {
        var type = (candidate.ExtraFields.GetValueOrDefault("artwork_shape")
                    ?? candidate.ExtraFields.GetValueOrDefault("image_shape")
                    ?? string.Empty).Trim().ToLowerInvariant();
        if (type is "landscape" or "wide")
            return "sme-match-result-art--wide";
        if (type is "square")
            return "sme-match-result-art--square";

        return EditorMediaType switch
        {
            "Music" => "sme-match-result-art--square",
            "TV" => "sme-match-result-art--wide",
            _ => "sme-match-result-art--portrait",
        };
    }

    private static string CanonicalConfidencePercentage(ItemCanonicalLinkedCandidateDto candidate) =>
        candidate.Confidence > 0
            ? candidate.Confidence.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)
            : "Not scored";
}
