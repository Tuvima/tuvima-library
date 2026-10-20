using System.Collections.Concurrent;
using System.Globalization;
using MediaEngine.Domain;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Several children share one parent Work (an album's tracks, a show's episodes) and each child's provider lookup
/// reports the parent's identity. When two reports from one provider name different identities (two MusicBrainz
/// releases of one album, because one track only matched a reissue's recording), the parent keeps the
/// earliest-dated report, then the lowest identity: the same answer whichever child finished first. A report the
/// user decided (a rematch in the editor) is never replaced by an automatic one, and a new user decision always wins.
/// </summary>
public static class SharedParentIdentity
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ParentLocks = new();

    // Person ids also travel with a track's parent-scope claims (each track of a compilation names its own
    // artist), so they say nothing about which container the children share.
    private static readonly HashSet<string> PersonIdentityKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        BridgeIdKeys.AppleArtistId,
        BridgeIdKeys.MusicBrainzId,
        BridgeIdKeys.MusicBrainzArtistId,
        BridgeIdKeys.TmdbPersonId,
        BridgeIdKeys.TvdbPersonId,
    };

    public enum Outcome
    {
        /// <summary>No earlier report names a different identity; the incoming report is written as is.</summary>
        Agree,

        /// <summary>The incoming report wins; the provider's earlier reports for the parent are superseded.</summary>
        IncomingWins,

        /// <summary>An earlier report wins; the incoming child adopts its parent claims.</summary>
        ExistingWins,
    }

    /// <param name="ParentClaims">The parent-scope claims the child should use: its own, or the winning report's.</param>
    /// <param name="SupersededKeys">With <see cref="Outcome.IncomingWins"/>, every key of the reports it replaces.</param>
    public sealed record Decision(
        Outcome Outcome,
        IReadOnlyList<ProviderClaim> ParentClaims,
        IReadOnlyCollection<string> SupersededKeys);

    /// <summary>
    /// Serialises the writes of one provider report for a shared parent: comparing with the stored reports and
    /// writing the claims, bridge ids and external identifiers must not interleave with a sibling's.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(Guid parentWorkId, CancellationToken ct)
    {
        var gate = ParentLocks.GetOrAdd(parentWorkId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Release(gate);
    }

    /// <summary>
    /// Compares an incoming parent-scope report with the same provider's current claims on the parent.
    /// A report is one observation set (or, for claims stored without one, one claim time); its identity is its
    /// container bridge-id claims.
    /// </summary>
    /// <param name="incomingIsUserDecision">The incoming report is the user's own choice (decided by user_manual).</param>
    public static Decision Decide(
        IReadOnlyList<ProviderClaim> incomingParentClaims,
        IReadOnlyList<MetadataClaim> existingProviderClaims,
        bool incomingIsUserDecision = false)
    {
        var incomingIdentity = Identity(incomingParentClaims.Select(claim => (claim.Key, claim.Value)));
        if (incomingIdentity.Count == 0)
        {
            return new Decision(Outcome.Agree, incomingParentClaims, []);
        }

        var reports = existingProviderClaims
            .Where(claim => claim.IsCurrent)
            .GroupBy(claim => claim.ObservationSetId?.ToString() ?? claim.ClaimedAt.ToString("O", CultureInfo.InvariantCulture))
            .Select(group => group.ToList())
            .Select(claims => (Claims: claims, Identity: Identity(claims.Select(claim => (claim.ClaimKey, claim.ClaimValue)))))
            .Where(report => report.Identity.Count > 0)
            .ToList();

        if (!reports.Any(report => Conflicts(report.Identity, incomingIdentity)))
        {
            return new Decision(Outcome.Agree, incomingParentClaims, []);
        }

        var incomingKey = SortKey(incomingParentClaims.Select(claim => (claim.Key, claim.Value)), incomingIdentity);
        var userReports = reports
            .Where(report => report.Claims.Any(claim => claim.DecisionSourceProviderId == WellKnownProviders.UserManual))
            .ToList();
        var best = userReports.Count > 0
            ? userReports
                .OrderByDescending(report => report.Claims.Max(claim => claim.ClaimedAt))
                .Select(report => (report.Claims, Key: SortKey(report.Claims.Select(claim => (claim.ClaimKey, claim.ClaimValue)), report.Identity)))
                .First()
            : reports
                .Select(report => (report.Claims, Key: SortKey(report.Claims.Select(claim => (claim.ClaimKey, claim.ClaimValue)), report.Identity)))
                .OrderBy(report => report.Key.Year)
                .ThenBy(report => report.Key.Signature, StringComparer.Ordinal)
                .First();

        var incomingWins = incomingIsUserDecision
            || (userReports.Count == 0 && Compare(incomingKey, best.Key) < 0);
        if (incomingWins)
        {
            var superseded = reports
                .SelectMany(report => report.Claims.Select(claim => claim.ClaimKey))
                .Concat(incomingParentClaims.Select(claim => claim.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new Decision(Outcome.IncomingWins, incomingParentClaims, superseded);
        }

        var adopted = best.Claims
            .Select(claim => new ProviderClaim(claim.ClaimKey, claim.ClaimValue, claim.Confidence))
            .ToList();
        return new Decision(Outcome.ExistingWins, adopted, []);
    }

    private static Dictionary<string, string> Identity(IEnumerable<(string Key, string Value)> claims)
    {
        var identity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in claims)
        {
            if (BridgeIdHelper.IsBridgeId(key) && !PersonIdentityKeys.Contains(key) && !string.IsNullOrWhiteSpace(value))
            {
                identity.TryAdd(key, value.Trim());
            }
        }

        return identity;
    }

    private static bool Conflicts(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Any(pair => right.TryGetValue(pair.Key, out var other)
            && !string.Equals(pair.Value, other, StringComparison.OrdinalIgnoreCase));

    private static (int Year, string Signature) SortKey(
        IEnumerable<(string Key, string Value)> claims,
        IReadOnlyDictionary<string, string> identity)
    {
        // Undated reports sort after dated ones, as release selection does.
        var year = int.MaxValue;
        foreach (var (key, value) in claims)
        {
            if (string.Equals(key, MetadataFieldConstants.Year, StringComparison.OrdinalIgnoreCase)
                && value.Trim() is { Length: >= 4 } text
                && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                year = Math.Min(year, parsed);
            }
        }

        var signature = string.Join(
            '\u001f',
            identity
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key.ToLowerInvariant() + "=" + pair.Value.ToLowerInvariant()));
        return (year, signature);
    }

    private static int Compare((int Year, string Signature) left, (int Year, string Signature) right)
    {
        var byYear = left.Year.CompareTo(right.Year);
        return byYear != 0 ? byYear : string.CompareOrdinal(left.Signature, right.Signature);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
