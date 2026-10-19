using System.Collections.Concurrent;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;
using Tuvima.Wikidata.Graph;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Local in-memory graph query service using Tuvima.Wikidata.Graph.
/// Loads universe data from SQLite into an EntityGraph,
/// then runs queries locally with zero network calls.
/// </summary>
public sealed class UniverseGraphQueryService : IUniverseGraphQueryService
{
    private readonly IFictionalEntityRepository _entityRepo;
    private readonly IEntityRelationshipRepository _relRepo;
    private readonly ILogger<UniverseGraphQueryService> _logger;

    private readonly ConcurrentDictionary<string, EntityGraph> _graphCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadLocks = new(StringComparer.OrdinalIgnoreCase);

    public UniverseGraphQueryService(
        IFictionalEntityRepository entityRepo,
        IEntityRelationshipRepository relRepo,
        ILogger<UniverseGraphQueryService> logger)
    {
        ArgumentNullException.ThrowIfNull(entityRepo);
        ArgumentNullException.ThrowIfNull(relRepo);
        ArgumentNullException.ThrowIfNull(logger);
        _entityRepo = entityRepo;
        _relRepo = relRepo;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IReadOnlyList<string>>> FindPathsAsync(
        string universeQid, string fromQid, string toQid,
        int maxHops = 4, CancellationToken ct = default)
    {
        var graph = await GetOrLoadGraphAsync(universeQid, ct).ConfigureAwait(false);
        var paths = graph.FindPaths(fromQid, toQid, maxHops);

        _logger.LogDebug("FindPaths {From}→{To}: found {Count} paths (max {MaxHops} hops)",
            fromQid, toQid, paths.Count, maxHops);
        return paths;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetFamilyTreeAsync(
        string universeQid, string characterQid,
        int generations = 3, CancellationToken ct = default)
    {
        var graph = await GetOrLoadGraphAsync(universeQid, ct).ConfigureAwait(false);
        return graph.GetFamilyTree(
            characterQid,
            generations,
            parentRelationships: new HashSet<string> { "father", "mother" },
            childRelationships: new HashSet<string> { "child" });
    }

    /// <inheritdoc/>
    public void InvalidateCache(string universeQid)
    {
        if (_graphCache.TryRemove(universeQid, out _))
        {
            _logger.LogDebug("Invalidated graph cache for universe {Qid}", universeQid);
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<EntityGraph> GetOrLoadGraphAsync(string universeQid, CancellationToken ct)
    {
        if (_graphCache.TryGetValue(universeQid, out var cached))
        {
            return cached;
        }

        var loadLock = _loadLocks.GetOrAdd(universeQid, _ => new SemaphoreSlim(1, 1));
        await loadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock.
            if (_graphCache.TryGetValue(universeQid, out cached))
            {
                return cached;
            }

            var graph = await BuildGraphFromSqliteAsync(universeQid, ct).ConfigureAwait(false);
            _graphCache[universeQid] = graph;

            _logger.LogInformation(
                "Built in-memory graph for universe {Qid}: {NodeCount} nodes, {EdgeCount} edges",
                universeQid, graph.NodeCount, graph.EdgeCount);

            return graph;
        }
        finally
        {
            loadLock.Release();
        }
    }

    private async Task<EntityGraph> BuildGraphFromSqliteAsync(string universeQid, CancellationToken ct)
    {
        var entities = await _entityRepo.GetByUniverseAsync(universeQid, ct).ConfigureAwait(false);
        var entityQids = entities.Select(e => e.WikidataQid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        foreach (var entity in entities)
        {
            ct.ThrowIfCancellationRequested();

            var workLinks = await _entityRepo.GetWorkLinksAsync(entity.Id, ct).ConfigureAwait(false);
            var workQids = workLinks.Select(l => l.WorkQid).ToList();

            nodes.Add(new GraphNode
            {
                Qid = entity.WikidataQid,
                Label = entity.Label,
                Type = entity.EntitySubType,
                WorkQids = workQids,
            });
        }

        var relationships = await _relRepo.GetByUniverseAsync(entityQids, ct).ConfigureAwait(false);

        foreach (var rel in relationships)
        {
            ct.ThrowIfCancellationRequested();

            edges.Add(new GraphEdge
            {
                SubjectQid = rel.SubjectQid,
                Relationship = rel.RelationshipTypeValue,
                ObjectQid = rel.ObjectQid,
                Confidence = rel.Confidence,
                ContextWorkQid = rel.ContextWorkQid,
            });
        }

        return new EntityGraph(nodes, edges);
    }
}
