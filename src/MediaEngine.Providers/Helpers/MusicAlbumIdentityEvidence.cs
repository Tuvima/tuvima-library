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
        var matchingRelease = false;
        foreach (var key in new[] { "musicbrainz_release_group_id", "musicbrainz_release_id" })
        {
            if (!sourceIds.TryGetValue(key, out var source) || string.IsNullOrWhiteSpace(source))
            {
                continue;
            }
            var returnedIds = evidence.Where(c => c.Key == key).Select(c => c.Value).ToList();
            if (returnedIds.Any(value => !string.Equals(value, source, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            matchingRelease |= returnedIds.Any(value => string.Equals(value, source, StringComparison.OrdinalIgnoreCase));
        }
        var performers = evidence.Where(c => c.Key is "artist" or "album_artist" or "performer")
            .Select(c => c.Value.Trim()).Where(value => value.Length > 0).ToList();
        if (sourceIds.TryGetValue("musicbrainz_artist_id", out var artistId))
        {
            var candidateArtists = evidence.Where(c => c.Key == "musicbrainz_artist_id").Select(c => c.Value).ToList();
            if (candidateArtists.Count > 0)
            {
                return candidateArtists.Contains(artistId, StringComparer.OrdinalIgnoreCase);
            }
        }
        if (performers.Count == 0)
        {
            return matchingRelease;
        }
        if (string.IsNullOrWhiteSpace(artist))
        {
            return false;
        }
        // Missing credit can be supported by an exact release bridge. An explicit
        // conflicting credit cannot: leave aliases for verified identity evidence.
        return performers.Any(value => string.Equals(value, artist.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
