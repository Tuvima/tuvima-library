using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    // One provider answer per album, shared by every song of it (see ProviderConfiguration.SharedAlbumClaimKeys).
    private readonly SharedWalkMemo<IReadOnlyList<ProviderClaim>> _albumAnswers = new(TimeSpan.FromMinutes(30));
    private const double SongTitleConfidence = 0.8;

    public async Task<IReadOnlyList<ProviderClaim>> FetchAsync(
        ProviderLookupRequest request,
        CancellationToken ct = default)
    {
        if (_flows?.HasFetch != true && SharesAlbumAnswer(request))
        {
            var shared = _config.SharedAlbumClaimKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var key = LookupMemo<int>.Key(Name, request.Artist, request.Album, request.Country, request.Language);
            var albumClaims = await _albumAnswers.GetAsync(
                key,
                async () => (await FetchMatchingArtistAsync(request, CancellationToken.None).ConfigureAwait(false))
                    .Where(claim => shared.Contains(claim.Key))
                    .ToList(),
                claims => claims.Count > 0,
                ct).ConfigureAwait(false);
            // The retail match only keeps an extra source's claims when its title agrees with the song, so the answer carries this
            // song's own title. The album and artist were already matched by the answer itself.
            return albumClaims.Count == 0 || string.IsNullOrWhiteSpace(request.Title)
                ? albumClaims
                : [.. albumClaims, new ProviderClaim("title", request.Title, SongTitleConfidence)];
        }

        return ChecksMusicArtist(request)
            ? await FetchMatchingArtistAsync(request, ct).ConfigureAwait(false)
            : await FetchCoreAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Music answers are only used when the artist they name is the artist the song is tagged with (#1360).</summary>
    internal bool ChecksMusicArtist(ProviderLookupRequest request) =>
        request.MediaType == MediaType.Music
        && _config.SharedAlbumClaimKeys.Count > 0
        && !string.IsNullOrWhiteSpace(request.Artist);

    private async Task<IReadOnlyList<ProviderClaim>> FetchMatchingArtistAsync(ProviderLookupRequest request, CancellationToken ct)
    {
        var claims = await FetchCoreAsync(request, ct).ConfigureAwait(false);
        return !ChecksMusicArtist(request) || ArtistAgrees(request.Artist, claims) ? claims : [];
    }

    /// <summary>
    /// True when the artist in an answer is the requested artist (any one of several credited acts is enough), or when either side
    /// is a placeholder such as "Various Artists". A cover or karaoke act with a matching title no longer passes: the first search
    /// result used to be taken whatever its artist was.
    /// </summary>
    internal static bool ArtistAgrees(string? requestedArtist, IReadOnlyList<ProviderClaim> claims)
    {
        var answered = claims.FirstOrDefault(claim => string.Equals(claim.Key, "artist", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(answered) || string.IsNullOrWhiteSpace(requestedArtist))
        {
            return true;
        }

        var requested = SplitActs(requestedArtist);
        var offered = SplitActs(answered);
        if (requested.Concat(offered).Any(IsPlaceholderAct))
        {
            return true;
        }

        return requested.Any(left => offered.Any(right => RetailTextSimilarity.ComputeWordOverlap(left, right) >= MinimumArtistOverlap));
    }

    // 0.9, not 0.8: "Daft Punk" against "Daft Punk Experience" (a tribute act) scores exactly 0.8.
    private const double MinimumArtistOverlap = 0.9;

    private static List<string> SplitActs(string value) =>
        value.Split([';', '&', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(act => act.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? act[4..] : act)
            .ToList();

    private static bool IsPlaceholderAct(string act) =>
        act.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
        || act.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase)
        || act.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    internal bool SharesAlbumAnswer(ProviderLookupRequest request) =>
        request.MediaType == MediaType.Music
        && _config.SharedAlbumClaimKeys.Count > 0
        && !string.IsNullOrWhiteSpace(request.Artist)
        && !string.IsNullOrWhiteSpace(request.Album);
}
