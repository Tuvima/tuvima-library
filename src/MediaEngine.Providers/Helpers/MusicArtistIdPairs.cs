using MediaEngine.Domain.Entities;

namespace MediaEngine.Providers.Helpers;

/// <summary>
/// Pairs each artist named on a song with the MusicBrainz artist ID that came with it, so the artist can be found on Wikidata by
/// that exact ID instead of by name (#1360). MusicBrainz sends the names and the IDs in the same order from one credit list; the
/// pairing is used only when both lists come from the same source and have the same length.
/// </summary>
public static class MusicArtistIdPairs
{
    public const string ArtistIdsClaimKey = "artist_mbids";
    private const string ArtistClaimKey = "artist";

    public static IReadOnlyDictionary<string, string> Build(IEnumerable<MetadataClaim> claims)
    {
        var current = claims.Where(claim => claim.IsCurrent).ToList();
        var idClaim = current
            .Where(claim => string.Equals(claim.ClaimKey, ArtistIdsClaimKey, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(claim.ClaimValue))
            .OrderByDescending(claim => claim.Confidence)
            .FirstOrDefault();
        if (idClaim is null)
        {
            return new Dictionary<string, string>();
        }

        var ids = Split(idClaim.ClaimValue);
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artistClaim in current.Where(claim =>
                     claim.ProviderId == idClaim.ProviderId
                     && string.Equals(claim.ClaimKey, ArtistClaimKey, StringComparison.OrdinalIgnoreCase)))
        {
            var names = Split(artistClaim.ClaimValue);
            if (names.Count != ids.Count)
            {
                continue;
            }

            for (var i = 0; i < names.Count; i++)
            {
                if (Guid.TryParse(ids[i], out _))
                {
                    pairs.TryAdd(names[i], ids[i]);
                }
            }
        }

        return pairs;
    }

    private static List<string> Split(string value) =>
        value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
}
