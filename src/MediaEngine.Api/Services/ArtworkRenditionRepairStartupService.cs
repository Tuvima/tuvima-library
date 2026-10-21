using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;

namespace MediaEngine.Api.Services;

public sealed class ArtworkRenditionRepairStartupService : BackgroundService
{
    private readonly IEntityAssetRepository _assetRepository;
    private readonly ICanonicalValueRepository _canonicalRepository;
    private readonly IPersonRepository _personRepository;
    private readonly AssetPathService _assetPathService;
    private readonly ILogger<ArtworkRenditionRepairStartupService> _logger;

    public ArtworkRenditionRepairStartupService(
        IEntityAssetRepository assetRepository,
        ICanonicalValueRepository canonicalRepository,
        IPersonRepository personRepository,
        AssetPathService assetPathService,
        ILogger<ArtworkRenditionRepairStartupService> logger)
    {
        _assetRepository = assetRepository;
        _canonicalRepository = canonicalRepository;
        _personRepository = personRepository;
        _assetPathService = assetPathService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            await RepairAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Artwork rendition repair failed.");
        }
    }

    private async Task RepairAsync(CancellationToken ct)
    {
        var adoptedFaces = await AdoptLegacyFacesAsync(ct);
        var assets = await _assetRepository.GetPreferredArtworkAsync(ct);
        var repairedRenditions = 0;
        var repairedCanonicals = 0;

        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();

            if (!Guid.TryParse(asset.EntityId, out var entityId))
            {
                continue;
            }

            var repairedAsset = false;
            if (NeedsRenditionRepair(asset)
                && !string.IsNullOrWhiteSpace(asset.LocalImagePath)
                && File.Exists(asset.LocalImagePath)
                && ArtworkVariantHelper.EnsureMissingRenditions(asset, _assetPathService))
            {
                await _assetRepository.UpdateRenditionPathsAsync(
                    asset.Id,
                    asset.LocalImagePathTiny,
                    asset.LocalImagePathSmall,
                    asset.LocalImagePathMedium,
                    asset.LocalImagePathLarge,
                    ct);
                repairedRenditions++;
                repairedAsset = true;
            }

            var canonicalValues = ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(
                entityId,
                asset,
                DateTimeOffset.UtcNow);

            if (canonicalValues.Count == 0)
            {
                continue;
            }

            if (repairedAsset || await HasMissingOrStaleCanonicalAsync(entityId, canonicalValues, ct))
            {
                await _canonicalRepository.UpsertBatchAsync(canonicalValues, ct);
                repairedCanonicals++;
            }
        }

        if (adoptedFaces > 0 || repairedRenditions > 0 || repairedCanonicals > 0)
        {
            _logger.LogInformation(
                "Artwork rendition repair completed: {FaceCount} faces registered, {RenditionCount} assets repaired, {CanonicalCount} canonical sets refreshed.",
                adoptedFaces,
                repairedRenditions,
                repairedCanonicals);
        }
    }

    /// <summary>
    /// Registers faces saved before faces were managed pictures (a face file with no Headshot asset) so they get
    /// all their sized copies like any other artwork.
    /// </summary>
    private async Task<int> AdoptLegacyFacesAsync(CancellationToken ct)
    {
        var adopted = 0;
        foreach (var person in await _personRepository.GetWithLocalHeadshotWithoutManagedAssetAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(person.LocalHeadshotPath))
            {
                continue;
            }

            try
            {
                var asset = await PersonHeadshotAssets.EnsureManagedAsync(
                    person.Id, person.LocalHeadshotPath, "wikidata", person.HeadshotUrl, _assetRepository, _assetPathService, ct);
                if (asset is not null)
                {
                    adopted++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not register the face of person {PersonId} as a managed picture.", person.Id);
            }
        }

        return adopted;
    }

    /// <summary>True when the asset should have copies and at least one of the four (tiny, small, medium, large) is missing.</summary>
    internal static bool NeedsRenditionRepair(EntityAsset asset) => ArtworkVariantHelper.HasMissingRenditions(asset);

    private async Task<bool> HasMissingOrStaleCanonicalAsync(
        Guid entityId,
        IReadOnlyList<CanonicalValue> expectedValues,
        CancellationToken ct)
    {
        var existing = await _canonicalRepository.GetByEntityAsync(entityId, ct);
        var valuesByKey = existing.ToDictionary(
            value => value.Key,
            value => value.Value,
            StringComparer.OrdinalIgnoreCase);

        return expectedValues.Any(expected =>
            !valuesByKey.TryGetValue(expected.Key, out var existingValue)
            || !string.Equals(existingValue, expected.Value, StringComparison.Ordinal));
    }
}
