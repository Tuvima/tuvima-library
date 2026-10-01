using MediaEngine.Contracts.Search;

namespace MediaEngine.Web.Components.MediaEditor;

public static class MediaEditorPairingTargetSelector
{
    public static string? GetTargetId(SearchRetailCandidateDto candidate, string mediaType)
    {
        if (string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase))
            return candidate.ProviderName?.Contains("tvdb", StringComparison.OrdinalIgnoreCase) == true
                && !string.IsNullOrWhiteSpace(candidate.ProviderItemId)
                && candidate.ProviderItemId.All(char.IsDigit)
                ? candidate.ProviderItemId : null;

        if (!string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
            || candidate.ProviderName?.Contains("musicbrainz", StringComparison.OrdinalIgnoreCase) != true)
            return null;

        var scopedReleaseId = (candidate.ExtraFields ?? [])
            .FirstOrDefault(pair => pair.Key.Equals("musicbrainz_release_id", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("release_id", StringComparison.OrdinalIgnoreCase)).Value;
        return Guid.TryParse(scopedReleaseId, out var parsed) ? parsed.ToString("D") : null;
    }

    public static bool IsValidTargetId(string? id, string mediaType) =>
        string.IsNullOrWhiteSpace(id)
        || (string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase)
            ? id.All(char.IsDigit)
            : string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(id, out _));
}
