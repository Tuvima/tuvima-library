using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    // One provider answer per album, shared by every song of it (see ProviderConfiguration.SharedAlbumClaimKeys).
    private readonly SharedWalkMemo<IReadOnlyList<ProviderClaim>> _albumAnswers = new(TimeSpan.FromMinutes(30));

    public async Task<IReadOnlyList<ProviderClaim>> FetchAsync(
        ProviderLookupRequest request,
        CancellationToken ct = default)
    {
        if (_flows?.HasFetch != true && SharesAlbumAnswer(request))
        {
            var shared = _config.SharedAlbumClaimKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var key = LookupMemo<int>.Key(Name, request.Artist, request.Album, request.Country, request.Language);
            return await _albumAnswers.GetAsync(
                key,
                async () => (await FetchCoreAsync(request, CancellationToken.None).ConfigureAwait(false))
                    .Where(claim => shared.Contains(claim.Key))
                    .ToList(),
                claims => claims.Count > 0,
                ct).ConfigureAwait(false);
        }

        return await FetchCoreAsync(request, ct).ConfigureAwait(false);
    }

    internal bool SharesAlbumAnswer(ProviderLookupRequest request) =>
        request.MediaType == MediaType.Music
        && _config.SharedAlbumClaimKeys.Count > 0
        && !string.IsNullOrWhiteSpace(request.Artist)
        && !string.IsNullOrWhiteSpace(request.Album);
}
