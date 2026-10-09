using System.Security.Cryptography;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Services.Matching;

public sealed record TvPairingReviewSnapshot(
    Guid RouteEntityId,
    RequestAuthority Actor,
    Guid? ApplicationCredentialId,
    string TvdbSeriesId,
    Guid ShowWorkId,
    IReadOnlyDictionary<Guid, PairingAssetRow> SelectedAssets,
    IReadOnlyDictionary<Guid, string> SelectionRevisions,
    IReadOnlyDictionary<string, TvPairingLocalTarget> Targets,
    IReadOnlyList<PairingCatalogueChild> Catalogue,
    DateTimeOffset ExpiresAt);

/// <summary>An opaque, short-lived review receipt; it is not a mutation capability by itself.</summary>
public sealed class TvPairingReviewTokenService(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public (string Token, DateTimeOffset ExpiresAt) Store(
        Guid routeEntityId, RequestAuthority actor, Guid? credentialId,
        string tvdbSeriesId, Guid showWorkId,
        IReadOnlyDictionary<Guid, PairingAssetRow> selectedAssets,
        IReadOnlyDictionary<string, TvPairingLocalTarget> targets,
        IReadOnlyList<PairingCatalogueChild> catalogue,
        IReadOnlyDictionary<Guid, string>? selectionRevisions = null)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        cache.Set(Key(token), new TvPairingReviewSnapshot(routeEntityId, actor,
            credentialId, tvdbSeriesId, showWorkId,
            new Dictionary<Guid, PairingAssetRow>(selectedAssets),
            new Dictionary<Guid, string>(selectionRevisions ?? new Dictionary<Guid, string>()),
            new Dictionary<string, TvPairingLocalTarget>(targets, StringComparer.Ordinal),
            catalogue.ToArray(),
            expires), expires);
        return (token, expires);
    }

    public TvPairingReviewSnapshot? Get(string token)
    {
        if (token.Length != 48 || !token.All(Uri.IsHexDigit))
        {
            return null;
        }
        return cache.TryGetValue<TvPairingReviewSnapshot>(Key(token), out var snapshot)
            && snapshot?.ExpiresAt > DateTimeOffset.UtcNow ? snapshot : null;
    }

    public static bool TryBindActor(HttpContext http, RequestAuthority actor, out Guid? credentialId)
    {
        credentialId = Guid.TryParse(http.User.FindFirst(TuvimaClaimTypes.ApplicationCredentialId)?.Value,
            out var parsedCredential) && parsedCredential != Guid.Empty ? parsedCredential : null;
        return actor.IsAuthenticated && (actor.PrincipalKind switch
        {
            PrincipalKind.Human => actor.AccountEnabled && actor.GrantEnabled
                && actor.AccountId.HasValue && actor.ActiveProfileId.HasValue && actor.SessionId.HasValue,
            PrincipalKind.DelegatedUserClient => actor.AccountEnabled && actor.GrantEnabled && actor.ApplicationEnabled
                && actor.AccountId.HasValue && actor.ActiveProfileId.HasValue
                && actor.ApplicationId.HasValue && actor.DeviceId.HasValue,
            PrincipalKind.ServiceApplication => actor.ApplicationEnabled
                && actor.ApplicationId.HasValue && credentialId.HasValue,
            _ => false,
        });
    }

    private static string Key(string token) => $"media-editor:tv-review:{token}";
}
