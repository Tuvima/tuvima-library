using MediaEngine.Contracts.Universe;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Turns the provenance recorded on a Universe fact (a work link or a relationship) into the source a person sees
/// ("Wikidata", "Dune Wiki"), and combines several into one stable, de-duplicated list.
/// </summary>
internal static class UniverseSourceLabels
{
    private const string WikidataLabel = "Wikidata";
    private const string LorePluginFallbackLabel = "Lore plugin";

    /// <summary>
    /// A blank or "Wikidata" provenance is Wikidata; "plugin" or anything naming lore/fandom is a lore plugin, labelled with
    /// the provider when known; anything else is shown as it was recorded.
    /// </summary>
    public static UniverseSourceDto FromProvenance(string? provenance, string? sourceProvider)
    {
        var trimmed = provenance?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, WikidataLabel, StringComparison.OrdinalIgnoreCase))
        {
            return new UniverseSourceDto { Kind = UniverseSourceDto.Wikidata, Label = WikidataLabel };
        }

        if (string.Equals(trimmed, "plugin", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("lore", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("fandom", StringComparison.OrdinalIgnoreCase))
        {
            return new UniverseSourceDto
            {
                Kind = UniverseSourceDto.LorePlugin,
                Label = string.IsNullOrWhiteSpace(sourceProvider) ? LorePluginFallbackLabel : sourceProvider.Trim(),
            };
        }

        return new UniverseSourceDto { Kind = UniverseSourceDto.Other, Label = trimmed };
    }

    /// <summary>Distinct by kind and label (case-insensitive); Wikidata first, then by label.</summary>
    public static IReadOnlyList<UniverseSourceDto> Combine(IEnumerable<UniverseSourceDto> sources) =>
        sources
            .DistinctBy(source => (source.Kind, source.Label.ToUpperInvariant()))
            .OrderBy(source => string.Equals(source.Kind, UniverseSourceDto.Wikidata, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(source => source.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Kind, StringComparer.Ordinal)
            .ToList();
}
