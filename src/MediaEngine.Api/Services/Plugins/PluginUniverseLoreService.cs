using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

public sealed class PluginUniverseLoreService
{
    public const string FandomPluginId = "tuvima.fandom-lore";
    public const string NotAFandomAddressMessage = "Only Fandom wiki addresses can be added.";

    private readonly PluginCatalog _catalog;
    private readonly IPluginLoreRepository _loreRepository;
    private readonly INarrativeRootRepository _roots;
    private readonly ILibraryUniverseQidGate _libraryQids;
    private readonly IPluginExecutionContextFactory _contexts;
    private readonly ILogger<PluginUniverseLoreService> _logger;

    public PluginUniverseLoreService(
        PluginCatalog catalog,
        IPluginLoreRepository loreRepository,
        INarrativeRootRepository roots,
        ILibraryUniverseQidGate libraryQids,
        IPluginExecutionContextFactory contexts,
        ILogger<PluginUniverseLoreService> logger)
    {
        _catalog = catalog;
        _loreRepository = loreRepository;
        _roots = roots;
        _libraryQids = libraryQids;
        _contexts = contexts;
        _logger = logger;
    }

    public Task<IReadOnlyList<PluginLoreSourceRecord>> GetSourcesAsync(
        string universeQid,
        CancellationToken ct = default)
        => _loreRepository.GetSourcesAsync(universeQid, ct);

    /// <summary>
    /// Asks the lore plugins for sources for a Universe. Returns null, and sends nothing out, when the QID is not a
    /// Universe this Library holds.
    /// </summary>
    public async Task<IReadOnlyList<PluginLoreSourceRecord>?> DiscoverSourcesAsync(
        string universeQid,
        CancellationToken ct = default)
    {
        if (!await _libraryQids.HoldsAsync(universeQid, ct).ConfigureAwait(false))
        {
            return null;
        }

        var context = await BuildContextAsync(universeQid, ct).ConfigureAwait(false);
        foreach (var registration in GetEnabledLoreRegistrations())
        {
            await using var executionContext = _contexts.Create(registration.Manifest.Id, "source-discovery");
            foreach (var provider in registration.Capabilities.OfType<IUniverseLoreProvider>())
            {
                try
                {
                    var candidates = await provider.DiscoverSourcesAsync(context, executionContext, ct).ConfigureAwait(false);
                    foreach (var candidate in candidates)
                    {
                        if (registration.Manifest.Id == FandomPluginId
                            && !(FandomHostRule.IsFandomAddress(candidate.BaseUrl) && FandomHostRule.IsFandomAddress(candidate.ApiUrl)))
                        {
                            _logger.LogInformation(
                                "Ignored lore source {SourceKey} for {UniverseQid}: it is not on a Fandom address",
                                LogSafe(candidate.SourceKey), LogSafe(universeQid));
                            continue;
                        }

                        await _loreRepository.UpsertSourceCandidateAsync(
                            universeQid,
                            registration.Manifest.Id,
                            MapCandidate(candidate),
                            ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Plugin {PluginId} failed lore source discovery for {UniverseQid}", registration.Manifest.Id, universeQid);
                }
            }
        }

        return await _loreRepository.GetSourcesAsync(universeQid, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a wiki an administrator typed in. Returns null when the QID is not a Universe this Library holds; throws
    /// <see cref="UriFormatException"/> when the address is not a Fandom wiki.
    /// </summary>
    public async Task<PluginLoreSourceRecord?> AddManualSourceAsync(
        string universeQid,
        string sourceName,
        string baseUrl,
        string? apiUrl = null,
        CancellationToken ct = default)
    {
        if (!await _libraryQids.HoldsAsync(universeQid, ct).ConfigureAwait(false))
        {
            return null;
        }

        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var normalizedApiUrl = string.IsNullOrWhiteSpace(apiUrl)
            ? $"{normalizedBaseUrl.TrimEnd('/')}/api.php"
            : apiUrl.Trim();
        if (!FandomHostRule.IsFandomAddress(normalizedBaseUrl) || !FandomHostRule.IsFandomAddress(normalizedApiUrl))
        {
            throw new UriFormatException(NotAFandomAddressMessage);
        }

        return await _loreRepository.AddManualSourceAsync(
            universeQid,
            FandomPluginId,
            string.IsNullOrWhiteSpace(sourceName) ? new Uri(normalizedBaseUrl).Host : sourceName.Trim(),
            normalizedBaseUrl,
            normalizedApiUrl,
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PluginLoreSourceRecord>> SetSourceStatusAsync(
        string universeQid,
        Guid sourceId,
        string status,
        string? actor,
        CancellationToken ct = default)
    {
        var source = await _loreRepository.FindSourceAsync(sourceId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Lore source '{sourceId}' was not found.");
        if (!string.Equals(source.UniverseQid, universeQid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Lore source does not belong to this universe.");
        }

        await _loreRepository.SetSourceStatusAsync(sourceId, status, actor, ct).ConfigureAwait(false);
        return await _loreRepository.GetSourcesAsync(universeQid, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls structured lore from the approved sources of a Universe. Returns null, and sends nothing out, when the QID
    /// is not a Universe this Library holds.
    /// </summary>
    public async Task<PluginLoreEnrichmentSummary?> EnrichUniverseAsync(
        string universeQid,
        CancellationToken ct = default)
    {
        if (!await _libraryQids.HoldsAsync(universeQid, ct).ConfigureAwait(false))
        {
            return null;
        }

        var context = await BuildContextAsync(universeQid, ct).ConfigureAwait(false);
        var approvedSources = await _loreRepository.GetApprovedSourcesAsync(universeQid, ct).ConfigureAwait(false);
        var totalEntities = 0;
        var totalRelationships = 0;
        var sourceCount = 0;

        foreach (var source in approvedSources)
        {
            var registration = _catalog.Get(source.PluginId);
            if (registration is null || !registration.Enabled || registration.LoadError is not null)
            {
                continue;
            }

            var provider = registration.Capabilities.OfType<IUniverseLoreProvider>().FirstOrDefault();
            if (provider is null)
            {
                continue;
            }

            if (source.PluginId == FandomPluginId && !FandomHostRule.IsFandomAddress(source.ApiUrl))
            {
                _logger.LogInformation(
                    "Skipped lore source {SourceKey} for {UniverseQid}: it is not on a Fandom address",
                    LogSafe(source.SourceKey), LogSafe(universeQid));
                continue;
            }

            await using var executionContext = _contexts.Create(registration.Manifest.Id, "enrichment");
            try
            {
                var result = await provider.EnrichUniverseAsync(context, MapSource(source), executionContext, ct).ConfigureAwait(false);
                var entities = result.Entities.Select(entity => MapEntity(source, entity)).ToList();
                var relationships = result.Relationships.Select(relationship => MapRelationship(source, relationship)).ToList();
                await _loreRepository.UpsertExtractionResultAsync(source, entities, relationships, ct).ConfigureAwait(false);
                totalEntities += entities.Count;
                totalRelationships += relationships.Count;
                sourceCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Plugin {PluginId} failed lore enrichment for {UniverseQid} source {SourceKey}", source.PluginId, universeQid, source.SourceKey);
            }
        }

        return new PluginLoreEnrichmentSummary(universeQid, sourceCount, totalEntities, totalRelationships);
    }

    private IEnumerable<PluginRegistration> GetEnabledLoreRegistrations() =>
        _catalog.List()
            .Where(registration => registration.Enabled
                                   && registration.LoadError is null
                                   && registration.Capabilities.OfType<IUniverseLoreProvider>().Any());

    private async Task<PluginUniverseLoreContext> BuildContextAsync(string universeQid, CancellationToken ct)
    {
        var root = await _roots.FindByQidAsync(universeQid, ct).ConfigureAwait(false);
        return new PluginUniverseLoreContext(universeQid, root?.Label);
    }

    private static PluginLoreSourceCandidateRecord MapCandidate(PluginLoreSourceCandidate candidate) => new()
    {
        SourceKey = candidate.SourceKey,
        SourceName = candidate.SourceName,
        BaseUrl = candidate.BaseUrl,
        ApiUrl = candidate.ApiUrl,
        License = candidate.License,
        Confidence = candidate.Confidence,
        EvidenceJson = ToJson(candidate.Evidence),
    };

    private static PluginLoreSource MapSource(PluginLoreSourceRecord source)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(source.EvidenceJson) ? "{}" : source.EvidenceJson);
        return new PluginLoreSource(
            source.Id,
            source.UniverseQid,
            source.PluginId,
            source.SourceKey,
            source.SourceName,
            source.BaseUrl,
            source.ApiUrl,
            source.License,
            source.Confidence,
            document.RootElement.Clone());
    }

    private static PluginLoreEntityRecord MapEntity(PluginLoreSourceRecord source, PluginLoreEntity entity) => new()
    {
        Id = Guid.NewGuid(),
        SourceId = source.Id,
        UniverseQid = source.UniverseQid,
        PluginId = source.PluginId,
        ExternalKey = entity.ExternalKey,
        WikidataQid = string.IsNullOrWhiteSpace(entity.WikidataQid) ? null : entity.WikidataQid,
        Label = entity.Label,
        Description = entity.Description,
        EntityType = NormalizeEntityType(entity.EntityType),
        AliasesJson = JsonSerializer.Serialize(entity.Aliases),
        SourceUrl = entity.SourceUrl,
        Confidence = entity.Confidence,
        EvidenceJson = ToJson(entity.Evidence),
    };

    private static PluginLoreRelationshipRecord MapRelationship(PluginLoreSourceRecord source, PluginLoreRelationship relationship) => new()
    {
        Id = Guid.NewGuid(),
        SourceId = source.Id,
        UniverseQid = source.UniverseQid,
        PluginId = source.PluginId,
        SubjectExternalKey = relationship.SubjectExternalKey,
        SubjectQid = relationship.SubjectQid,
        ObjectExternalKey = relationship.ObjectExternalKey,
        ObjectQid = relationship.ObjectQid,
        RelationshipType = relationship.RelationshipType,
        SourceUrl = relationship.SourceUrl,
        Confidence = relationship.Confidence,
        EvidenceJson = ToJson(relationship.Evidence),
    };

    private static string ToJson(JsonElement element) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : element.GetRawText();

    private static string NormalizeEntityType(string value) =>
        value switch
        {
            "Character" or "Location" or "Organization" or "Event" => value,
            _ => "Unknown",
        };

    private static string NormalizeBaseUrl(string value)
    {
        var raw = value.Trim();
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = $"https://{raw}";
        }

        // Wikis are only ever reached over https; a Fandom wiki typed with http is kept on its https address.
        var uri = new Uri(raw);
        return $"https://{uri.Host}";
    }

    // Source keys come from wiki addresses an administrator or Wikidata supplied; line breaks are removed so a value can
    // never start a forged log line.
    private static string LogSafe(string? value) =>
        (value ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
}

public sealed record PluginLoreEnrichmentSummary(
    string UniverseQid,
    int SourcesEnriched,
    int EntitiesWritten,
    int RelationshipsWritten);
