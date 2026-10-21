using System.Globalization;
using System.Security.Cryptography;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services.StashBox;

/// <summary>What happened when a Vault video was looked up. "Filled" lists the fields that were blank and now have a value.</summary>
public sealed record VaultVideoDetailsOutcome(string Status, IReadOnlyList<string> FilledFields)
{
    /// <summary>Not a Vault item, the library's lookup is None, or the plugin is off: nothing was asked.</summary>
    public const string Skipped = "skipped";

    public const string NotFound = "not_found";
    public const string NothingNew = "nothing_new";
    public const string Filled = "filled";

    public static VaultVideoDetailsOutcome Of(string status) => new(status, []);
}

/// <summary>
/// Asks the adult-only providers about one Vault video and fills only its blank fields (title, description, date, cover), plus the
/// Vault's own performer, studio and tag names. A value already there, and above all anything the person edited, is never replaced.
/// Nothing here reaches the shared People, Tags, Series or artwork records. This is the hook the Vault ingestion path (WP12) calls;
/// it does not compute fingerprints or decide how strong a match must be.
/// </summary>
public sealed class VaultVideoDetailsService
{
    private readonly IProviderAudienceGate _gate;
    private readonly IVaultLibraryRepository _vault;
    private readonly IMediaAssetRepository _assets;
    private readonly ICanonicalValueRepository _canonical;
    private readonly IMetadataClaimRepository _claims;
    private readonly IVaultNameRepository _names;
    private readonly IVaultArtworkRepository _artwork;
    private readonly IEnumerable<IAdultVideoLookup> _lookups;
    private readonly TuvimaDataPaths _paths;
    private readonly ILogger<VaultVideoDetailsService> _logger;

    public VaultVideoDetailsService(
        IProviderAudienceGate gate,
        IVaultLibraryRepository vault,
        IMediaAssetRepository assets,
        ICanonicalValueRepository canonical,
        IMetadataClaimRepository claims,
        IVaultNameRepository names,
        IVaultArtworkRepository artwork,
        IEnumerable<IAdultVideoLookup> lookups,
        TuvimaDataPaths paths,
        ILogger<VaultVideoDetailsService> logger)
    {
        _gate = gate;
        _vault = vault;
        _assets = assets;
        _canonical = canonical;
        _claims = claims;
        _names = names;
        _artwork = artwork;
        _lookups = lookups;
        _paths = paths;
        _logger = logger;
    }

    public async Task<VaultVideoDetailsOutcome> LookupAndFillAsync(
        Guid assetId, IReadOnlyList<AdultFingerprint> fingerprints, CancellationToken ct = default)
    {
        var asset = await _assets.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset?.LibraryId is not { } libraryId
            || await _vault.GetAsync(libraryId, ct).ConfigureAwait(false) is not { } library
            || library.EffectiveLookupSource != VaultLookupSources.StashBox)
        {
            return VaultVideoDetailsOutcome.Of(VaultVideoDetailsOutcome.Skipped);
        }

        var existing = await _canonical.GetByEntityAsync(assetId, ct).ConfigureAwait(false);
        var title = existing.FirstOrDefault(v => v.Key == MetadataFieldConstants.Title)?.Value;
        var query = new AdultLookupQuery(title, fingerprints, library.LookupServerId);

        foreach (var lookup in _lookups)
        {
            // The gate is asked per item, every time: an adult-only provider is allowed here only because this is a Vault item.
            if (!await _gate.MayAskAboutItemAsync(lookup.ProviderName, assetId, ct).ConfigureAwait(false))
            {
                continue;
            }

            if (await lookup.LookupAsync(query, ct).ConfigureAwait(false) is { } info)
            {
                return await ApplyAsync(assetId, libraryId, info, existing, ct).ConfigureAwait(false);
            }
        }

        return VaultVideoDetailsOutcome.Of(VaultVideoDetailsOutcome.NotFound);
    }

    private async Task<VaultVideoDetailsOutcome> ApplyAsync(
        Guid assetId, string libraryId, AdultVideoInfo info, IReadOnlyList<CanonicalValue> existing, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var filled = new List<string>();
        var claims = new List<MetadataClaim>();
        var canonicals = new List<CanonicalValue>();

        bool Blank(string key) => !existing.Any(v => v.Key == key && !string.IsNullOrWhiteSpace(v.Value));

        void Fill(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Blank(key))
            {
                return;
            }

            claims.Add(new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = assetId,
                ProviderId = WellKnownProviders.StashBox,
                ClaimKey = key,
                ClaimValue = value,
                ClaimedAt = now,
                Confidence = 0.9,
            });
            canonicals.Add(new CanonicalValue
            {
                EntityId = assetId,
                Key = key,
                Value = value,
                LastScoredAt = now,
                WinningProviderId = WellKnownProviders.StashBox,
            });
            filled.Add(key);
        }

        Fill(MetadataFieldConstants.Title, info.Title);
        Fill(MetadataFieldConstants.Description, info.Description);
        if (info.Date is { } date)
        {
            Fill(MetadataFieldConstants.OriginalReleaseDate, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Fill(MetadataFieldConstants.Year, date.Year.ToString(CultureInfo.InvariantCulture));
        }

        if (filled.Count > 0)
        {
            Fill(MetadataFieldConstants.VideoLookupSource, VaultLookupSources.StashBox);
            await _claims.InsertBatchAsync(claims, ct).ConfigureAwait(false);
            await _canonical.UpsertBatchAsync(canonicals, ct).ConfigureAwait(false);
        }

        // The Vault's own names (D27, D28): created or found in the Vault tables only, added to and never taken away.
        foreach (var performer in info.Performers)
        {
            var id = await _names.EnsurePerformerAsync(libraryId, performer, null, StashBoxSettings.ProviderName, now, ct).ConfigureAwait(false);
            await _names.LinkPerformerAsync(assetId, id, ct).ConfigureAwait(false);
        }

        if (info.Studio is { } studio)
        {
            var id = await _names.EnsureStudioAsync(libraryId, studio, StashBoxSettings.ProviderName, now, ct).ConfigureAwait(false);
            await _names.LinkStudioAsync(assetId, id, ct).ConfigureAwait(false);
        }

        foreach (var tag in info.Tags)
        {
            var id = await _names.EnsureTagAsync(libraryId, tag, now, ct).ConfigureAwait(false);
            await _names.LinkTagAsync(assetId, id, ct).ConfigureAwait(false);
        }

        if (info.Performers.Count + info.Tags.Count > 0 || info.Studio is not null)
        {
            filled.Add("vault_names");
        }

        if (info.Cover is { Length: > 0 } cover && info.CoverMimeType is { } mime
            && (await _artwork.GetForOwnerAsync(VaultArtworkOwnerKinds.Asset, assetId, ct).ConfigureAwait(false))
                .All(a => a.Kind != VaultArtworkKinds.Cover))
        {
            var extension = mime switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
            var folder = Path.Combine(_paths.Root, "vault-artwork", assetId.ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "cover" + extension);
            await File.WriteAllBytesAsync(path, cover, ct).ConfigureAwait(false);
            await _artwork.AddAsync(
                VaultArtworkOwnerKinds.Asset, assetId, VaultArtworkKinds.Cover, path,
                Convert.ToHexString(SHA256.HashData(cover)), null, null, StashBoxSettings.ProviderName, now, ct).ConfigureAwait(false);
            filled.Add("cover");
        }

        _logger.LogInformation("A Vault video was matched by {Signal}; {Count} blank fields were filled", info.MatchedBy, filled.Count);
        return new VaultVideoDetailsOutcome(filled.Count > 0 ? VaultVideoDetailsOutcome.Filled : VaultVideoDetailsOutcome.NothingNew, filled);
    }
}
