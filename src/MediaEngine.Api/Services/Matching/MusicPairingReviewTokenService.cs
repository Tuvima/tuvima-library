using System.Security.Cryptography;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Services.Matching;

public sealed record MusicPairingReviewSnapshot(
    Guid RouteEntityId,
    RequestAuthority Actor,
    Guid? ApplicationCredentialId,
    string ReleaseId,
    IReadOnlyDictionary<Guid, PairingAssetRow> SelectedAssets,
    IReadOnlyDictionary<Guid, string> SelectionRevisions,
    IReadOnlyDictionary<string, PairingCatalogueChild> ReleaseTracks,
    DateTimeOffset ExpiresAt);

/// <summary>Frozen proof of one exact MusicBrainz release catalogue and its reviewed local files.</summary>
public sealed class MusicPairingReviewTokenService(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public (string Token, DateTimeOffset ExpiresAt) Store(Guid routeEntityId,
        RequestAuthority actor, Guid? credentialId, string releaseId,
        IReadOnlyDictionary<Guid, PairingAssetRow> selectedAssets,
        IReadOnlyDictionary<Guid, string> selectionRevisions,
        IReadOnlyList<PairingCatalogueChild> catalogue)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        cache.Set(Key(token), new MusicPairingReviewSnapshot(routeEntityId, actor,
            credentialId, releaseId, new Dictionary<Guid, PairingAssetRow>(selectedAssets),
            new Dictionary<Guid, string>(selectionRevisions),
            catalogue.ToDictionary(item => item.ChildId, StringComparer.OrdinalIgnoreCase), expires), expires);
        return (token, expires);
    }

    public MusicPairingReviewSnapshot? Get(string token) =>
        token.Length == 48 && token.All(Uri.IsHexDigit)
        && cache.TryGetValue<MusicPairingReviewSnapshot>(Key(token), out var snapshot)
        && snapshot?.ExpiresAt > DateTimeOffset.UtcNow ? snapshot : null;

    private static string Key(string token) => $"media-editor:music-review:{token}";
}
