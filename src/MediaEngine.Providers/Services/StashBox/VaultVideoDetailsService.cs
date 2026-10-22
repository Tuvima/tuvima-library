using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services.Vault;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services.StashBox;

/// <summary>What happened when a Vault video was looked up. "FilledFields" lists the fields that were written (<see cref="VaultFieldNames"/>).</summary>
public sealed record VaultVideoDetailsOutcome(string Status, IReadOnlyList<string> FilledFields)
{
    /// <summary>Not a Vault item, the library's lookup is None, or the plugin is off: nothing was asked.</summary>
    public const string Skipped = "skipped";

    /// <summary>No strong match: the video stays Unmatched and nothing is queued anywhere (Videos spec D32).</summary>
    public const string NotFound = "not_found";

    public const string NothingNew = "nothing_new";
    public const string Filled = "filled";

    public static VaultVideoDetailsOutcome Of(string status) => new(status, []);
}

/// <summary>
/// Looks one Vault video up with the adult-only providers and applies a strong match (Videos spec D30, D37). It builds the question
/// from the file itself (name patterns, product code, length, and fingerprints when the separate permission is on), asks only the
/// providers <see cref="IProviderAudienceGate"/> allows for a Vault item, and hands an accepted record to
/// <see cref="VaultRecordApplier"/>, which checks every field and stores where it came from. A weak match, or signals that
/// disagree, change nothing: the video stays Unmatched.
/// </summary>
public sealed class VaultVideoDetailsService
{
    private readonly IProviderAudienceGate _gate;
    private readonly IVaultLibraryRepository _vault;
    private readonly IMediaAssetRepository _assets;
    private readonly ICanonicalValueRepository _canonical;
    private readonly IVaultMatchRepository _matches;
    private readonly IEnumerable<IAdultVideoLookup> _lookups;
    private readonly IStashBoxSettings _settings;
    private readonly IConfigurationLoader _configuration;
    private readonly VaultFileNameReader _names;
    private readonly VaultRecordApplier _applier;
    private readonly TimeProvider _time;
    private readonly ILogger<VaultVideoDetailsService> _logger;

    public VaultVideoDetailsService(
        IProviderAudienceGate gate,
        IVaultLibraryRepository vault,
        IMediaAssetRepository assets,
        ICanonicalValueRepository canonical,
        IVaultMatchRepository matches,
        IEnumerable<IAdultVideoLookup> lookups,
        IStashBoxSettings settings,
        IConfigurationLoader configuration,
        VaultFileNameReader names,
        VaultRecordApplier applier,
        TimeProvider time,
        ILogger<VaultVideoDetailsService> logger)
    {
        _gate = gate;
        _vault = vault;
        _assets = assets;
        _canonical = canonical;
        _matches = matches;
        _lookups = lookups;
        _settings = settings;
        _configuration = configuration;
        _names = names;
        _applier = applier;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Looks a newly registered Vault file up. Fingerprints are computed (and kept) only while the administrator's separate fingerprint
    /// permission is on and the provider declares it uses them; otherwise nothing about the file's content is read or sent.
    /// </summary>
    public async Task<VaultVideoDetailsOutcome> LookupFileAsync(Guid assetId, CancellationToken ct = default)
    {
        var fingerprints = await FingerprintsForAsync(assetId, ct).ConfigureAwait(false);
        return await LookupAndFillAsync(assetId, fingerprints, ct).ConfigureAwait(false);
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

        var facts = await _matches.GetFileFactsAsync(assetId, ct).ConfigureAwait(false);
        var parts = _names.Read(facts?.FilePath ?? asset.FilePathRoot);
        var existing = await _canonical.GetByEntityAsync(assetId, ct).ConfigureAwait(false);
        var storedTitle = existing.FirstOrDefault(v => v.Key == MetadataFieldConstants.Title)?.Value;
        var duration = VaultFieldValidator.IsSensibleDuration(facts?.DurationSeconds) ? facts!.DurationSeconds : null;

        // The name patterns read the title more exactly than the cleaned file name does; the stored title is the fallback.
        var query = new AdultLookupQuery(
            parts.PatternMatched && !string.IsNullOrWhiteSpace(parts.Title) ? parts.Title : storedTitle ?? parts.Title,
            fingerprints,
            library.LookupServerId,
            parts.ProductCode,
            duration,
            parts.Date);

        foreach (var lookup in _lookups)
        {
            // The gate is asked per item, every time: an adult-only provider is allowed here only because this is a Vault item.
            if (!await _gate.MayAskAboutItemAsync(lookup.ProviderName, assetId, ct).ConfigureAwait(false))
            {
                continue;
            }

            if (await lookup.LookupAsync(query, ct).ConfigureAwait(false) is not { } info)
            {
                continue;
            }

            var result = await _applier.ApplyAsync(new VaultApplyRequest(assetId, libraryId, info, VaultAppliedVia.Automatic, null), ct).ConfigureAwait(false);
            return new VaultVideoDetailsOutcome(
                result.Applied.Count > 0 ? VaultVideoDetailsOutcome.Filled : VaultVideoDetailsOutcome.NothingNew, result.Applied);
        }

        return VaultVideoDetailsOutcome.Of(VaultVideoDetailsOutcome.NotFound);
    }

    /// <summary>The file's exact-copy fingerprints, or none when the permission is off or the provider does not use them.</summary>
    private async Task<IReadOnlyList<AdultFingerprint>> FingerprintsForAsync(Guid assetId, CancellationToken ct)
    {
        var provider = _configuration.LoadProvider(StashBoxSettings.ProviderName);
        if (provider is not { Enabled: true, AdultOnly: true, FileFingerprint: true }
            || !(await _settings.GetAsync(ct).ConfigureAwait(false)).FingerprintPermission
            || await _matches.GetFileFactsAsync(assetId, ct).ConfigureAwait(false) is not { } facts)
        {
            return [];
        }

        try
        {
            var stored = await _matches.GetFingerprintsAsync(assetId, ct).ConfigureAwait(false);
            var size = new FileInfo(facts.FilePath).Length;
            if (stored.Count == 0 || stored.Any(f => f.FileSize != size))
            {
                stored = await VaultFileFingerprinter.ComputeAsync(facts.FilePath, _time, ct).ConfigureAwait(false);
                await _matches.SaveFingerprintsAsync(assetId, stored, ct).ConfigureAwait(false);
            }

            return stored.Select(f => new AdultFingerprint(f.Algorithm, f.Hash)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogInformation("A Vault file could not be read for its fingerprints ({Type}); it is looked up without them", ex.GetType().Name);
            return [];
        }
    }
}
