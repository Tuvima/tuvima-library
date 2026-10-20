using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

public interface IPluginLoreRepository
{
    Task<IReadOnlyList<PluginLoreSourceRecord>> GetSourcesAsync(
        string universeQid,
        CancellationToken ct = default);

    Task<IReadOnlyList<PluginLoreSourceRecord>> GetApprovedSourcesAsync(
        string universeQid,
        CancellationToken ct = default);

    Task<PluginLoreSourceRecord?> FindSourceAsync(
        Guid sourceId,
        CancellationToken ct = default);

    Task UpsertSourceCandidateAsync(
        string universeQid,
        string pluginId,
        PluginLoreSourceCandidateRecord candidate,
        CancellationToken ct = default);

    Task<PluginLoreSourceRecord> AddManualSourceAsync(
        string universeQid,
        string pluginId,
        string sourceName,
        string baseUrl,
        string apiUrl,
        CancellationToken ct = default);

    Task SetSourceStatusAsync(
        Guid sourceId,
        string status,
        string? actor,
        CancellationToken ct = default);

    Task UpsertExtractionResultAsync(
        PluginLoreSourceRecord source,
        IReadOnlyList<PluginLoreEntityRecord> entities,
        IReadOnlyList<PluginLoreRelationshipRecord> relationships,
        CancellationToken ct = default);

    Task<IReadOnlyList<PluginLoreEntityRecord>> GetEntitiesAsync(
        string universeQid,
        bool approvedOnly = true,
        CancellationToken ct = default);

    Task<IReadOnlyList<PluginLoreRelationshipRecord>> GetRelationshipsAsync(
        string universeQid,
        bool approvedOnly = true,
        CancellationToken ct = default);

    /// <summary>
    /// For each of the given Wikidata items, the names of the approved lore sources of this Universe that hold an entity for
    /// it (one row per item and source). Reads only those two columns, so a large lore source costs little.
    /// </summary>
    Task<IReadOnlyList<PluginLoreItemSource>> GetApprovedSourceNamesForItemsAsync(
        string universeQid,
        IReadOnlyCollection<string> wikidataQids,
        CancellationToken ct = default);
}

/// <summary>An approved lore source that holds an entity for one Wikidata item.</summary>
public sealed record PluginLoreItemSource(string WikidataQid, string SourceName);
