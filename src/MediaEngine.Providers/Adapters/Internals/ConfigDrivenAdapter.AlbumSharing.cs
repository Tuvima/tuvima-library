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
                async () => (await FetchCoreAsync(request, CancellationToken.None).ConfigureAwait(false))
                    .Where(claim => shared.Contains(claim.Key))
                    .ToList(),
                claims => claims.Count > 0,
                ct).ConfigureAwait(false);
            // The retail match only keeps an extra source's claims when its title agrees with the song, so the answer carries this
            // song's own title. The album and artist were already matched by the answer itself.
            return string.IsNullOrWhiteSpace(request.Title)
                ? albumClaims
                : [.. albumClaims, new ProviderClaim("title", request.Title, SongTitleConfidence)];
        }

        return await FetchCoreAsync(request, ct).ConfigureAwait(false);
    }

    internal bool SharesAlbumAnswer(ProviderLookupRequest request) =>
        request.MediaType == MediaType.Music
        && _config.SharedAlbumClaimKeys.Count > 0
        && !string.IsNullOrWhiteSpace(request.Artist)
        && !string.IsNullOrWhiteSpace(request.Album);
}
