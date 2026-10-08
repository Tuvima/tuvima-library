using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Resolves each writable field from its declared owner in the asset lineage.
/// Asset values remain a compatibility fallback for libraries populated before
/// work and edition scoped canonical values were available.
/// </summary>
public static class EffectiveWritebackMetadataComposer
{
    public static IReadOnlyDictionary<string, string> Compose(
        WorkLineage lineage,
        IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>> valuesByEntity,
        IReadOnlySet<string> allowedFields,
        IReadOnlySet<string> excludedFields)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        valuesByEntity.TryGetValue(lineage.AssetId, out var assetValues);
        var assetByKey = (assetValues ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value.Key))
            .GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);

        foreach (var key in allowedFields)
        {
            if (excludedFields.Contains(key))
            {
                continue;
            }

            var ownerId = ClaimScopeCatalog.GetScope(key, lineage.MediaType) switch
            {
                ClaimScope.Parent => lineage.TargetForParentScope,
                ClaimScope.Edition => lineage.EditionId,
                ClaimScope.Asset => lineage.AssetId,
                _ => lineage.TargetForSelfScope,
            };

            string? value = null;
            if (valuesByEntity.TryGetValue(ownerId, out var ownerValues))
            {
                value = ownerValues.FirstOrDefault(candidate =>
                    string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
            }

            if (string.IsNullOrWhiteSpace(value) && assetByKey.TryGetValue(key, out var legacyValue))
            {
                value = legacyValue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                result[key] = value;
            }
        }

        return result;
    }
}
