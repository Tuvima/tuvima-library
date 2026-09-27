using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Helpers;

public static class MusicAlbumIdentityEvidence
{
    // A shared album title is not proof of identity. Release IDs or the credited
    // artist must corroborate a text-based album reconciliation.
    public static bool Corroborates(string? artist, IReadOnlyDictionary<string, string> sourceIds,
        IEnumerable<ProviderClaim> claims)
    {
        var evidence = claims.ToList();
        foreach (var key in new[] { "musicbrainz_release_group_id", "musicbrainz_release_id" })
        {
            if (sourceIds.TryGetValue(key, out var source)
                && evidence.Any(c => c.Key == key && string.Equals(c.Value, source, StringComparison.OrdinalIgnoreCase))) return true;
        }
        if (string.IsNullOrWhiteSpace(artist)) return false;
        return evidence.Where(c => c.Key is "artist" or "album_artist" or "performer")
            .Any(c => string.Equals(c.Value.Trim(), artist.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
