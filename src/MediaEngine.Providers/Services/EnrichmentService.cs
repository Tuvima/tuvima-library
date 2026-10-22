using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Workers;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Dispatches enrichment work to specialized workers based on pass type.
/// Thin orchestrator — no enrichment logic lives here.
/// </summary>
public sealed class EnrichmentService : IEnrichmentService
{
    private readonly CoverArtWorker _coverArt;
    private readonly PersonEnrichmentWorker _persons;
    private readonly ChildEntityWorker _children;
    private readonly FictionalEntityWorker _fictional;
    private readonly DescriptionEnrichmentWorker _descriptions;
    private readonly TextTrackEnrichmentWorker _textTracks;
    private readonly IImageEnrichmentService _images;
    private readonly IFileWriteQueue _fileWrites;
    private readonly ICollectionRepository _collectionRepo;
    private readonly IParentCollectionResolver _parentCollectionResolver;
    private readonly IEnrichmentConcurrencyLimiter _concurrency;
    private readonly ILogger<EnrichmentService> _logger;

    public EnrichmentService(
        CoverArtWorker coverArt,
        PersonEnrichmentWorker persons,
        ChildEntityWorker children,
        FictionalEntityWorker fictional,
        DescriptionEnrichmentWorker descriptions,
        TextTrackEnrichmentWorker textTracks,
        IImageEnrichmentService images,
        IFileWriteQueue fileWrites,
        ICollectionRepository collectionRepo,
        IParentCollectionResolver parentCollectionResolver,
        ILogger<EnrichmentService> logger,
        IEnrichmentConcurrencyLimiter? concurrencyLimiter = null)
    {
        _coverArt = coverArt;
        _persons = persons;
        _children = children;
        _fictional = fictional;
        _descriptions = descriptions;
        _textTracks = textTracks;
        _images = images;
        _fileWrites = fileWrites;
        _collectionRepo = collectionRepo;
        _parentCollectionResolver = parentCollectionResolver;
        _concurrency = concurrencyLimiter ?? NoopEnrichmentConcurrencyLimiter.Instance;
        _logger = logger;
    }

    public async Task RunQuickPassAsync(Guid entityId, string qid, CancellationToken ct = default)
    {
        _logger.LogInformation("Quick pass starting for entity {Id} (QID {Qid})", entityId, qid);
        await _coverArt.DownloadAndPersistAsync(entityId, qid, ct);
        try
        {
            await _concurrency.RunAsync(
                EnrichmentWorkKind.Wikidata,
                token => _persons.EnrichFromClaimsAsync(entityId, token),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Quick pass people enrichment was partial for entity {Id}; continuing with quick hydration",
                entityId);
        }

        await _fileWrites.EnqueueAssetAsync(entityId, "quick_hydration", ct);
        _logger.LogInformation("Quick pass completed for entity {Id}", entityId);
    }

    public async Task RunUniversePassAsync(Guid entityId, string qid, CancellationToken ct = default)
    {
        _logger.LogInformation("Universe pass starting for entity {Id} (QID {Qid})", entityId, qid);
        await RunWorkScopedPassAsync(entityId, qid, ct);
        await RunUniverseCorePassAsync(entityId, qid, ct);
        await RunUniverseEnhancerPassAsync(entityId, qid, ct);
        _logger.LogInformation("Universe pass completed for entity {Id}", entityId);
    }

    public async Task RunWorkScopedPassAsync(Guid entityId, string qid, CancellationToken ct = default)
    {
        await _concurrency.RunAsync(
            EnrichmentWorkKind.Wikidata,
            token => _persons.EnrichActorCharacterMappingsAsync(entityId, qid, token),
            ct);
    }

    public async Task RunUniverseCorePassAsync(Guid entityId, string qid, CancellationToken ct = default)
    {
        await _concurrency.RunAsync(
            EnrichmentWorkKind.Wikidata,
            async token =>
            {
                await _children.DiscoverAsync(entityId, qid, token);
                await _fictional.EnrichAsync(entityId, qid, token);
                await ResolveParentCollectionAsync(entityId, token);
            },
            ct);
    }

    public async Task RunUniverseEnhancerPassAsync(Guid entityId, string qid, CancellationToken ct = default)
    {
        await _concurrency.RunAsync(
            EnrichmentWorkKind.TmdbArtwork,
            async token => await _images.EnrichWorkImagesAsync(entityId, qid, token),
            ct);
        await _concurrency.RunAsync(
            EnrichmentWorkKind.Wikidata,
            token => _descriptions.EnrichAsync(entityId, qid, token),
            ct);
        await _fileWrites.EnqueueAssetAsync(entityId, "universe_enrichment", ct);
    }

    public async Task RunSingleEnrichmentAsync(Guid entityId, string qid, EnrichmentType type, CancellationToken ct = default)
    {
        _logger.LogInformation("Single enrichment {Type} starting for entity {Id}", type, entityId);
        switch (type)
        {
            case EnrichmentType.CoverArt:
                await _coverArt.DownloadAndPersistAsync(entityId, qid, ct);
                break;
            case EnrichmentType.Persons:
                await _concurrency.RunAsync(
                    EnrichmentWorkKind.Wikidata,
                    token => _persons.EnrichFromClaimsAsync(entityId, token),
                    ct);
                break;
            case EnrichmentType.Children:
                await _concurrency.RunAsync(
                    EnrichmentWorkKind.Wikidata,
                    token => _children.DiscoverAsync(entityId, qid, token),
                    ct);
                break;
            case EnrichmentType.Images:
                await _concurrency.RunAsync(
                    EnrichmentWorkKind.TmdbArtwork,
                    async token => await _images.EnrichWorkImagesAsync(entityId, qid, token),
                    ct);
                break;
            case EnrichmentType.Fictional:
                await _concurrency.RunAsync(
                    EnrichmentWorkKind.Wikidata,
                    token => _fictional.EnrichAsync(entityId, qid, token),
                    ct);
                break;
            case EnrichmentType.Descriptions:
                await _concurrency.RunAsync(
                    EnrichmentWorkKind.Wikidata,
                    token => _descriptions.EnrichAsync(entityId, qid, token),
                    ct);
                break;
            case EnrichmentType.WriteBack:
                await _fileWrites.EnqueueAssetAsync(entityId, "manual_enrichment", ct);
                break;
            case EnrichmentType.TimedLyrics:
                await _textTracks.EnrichAsync(entityId, TextTrackKind.Lyrics, ct);
                break;
            case EnrichmentType.Subtitles:
                await _textTracks.EnrichAsync(entityId, TextTrackKind.Subtitles, ct);
                break;
        }
    }

    private async Task ResolveParentCollectionAsync(Guid entityId, CancellationToken ct)
    {
        var workId = await _collectionRepo.GetWorkIdByMediaAssetAsync(entityId, ct);
        if (!workId.HasValue)
        {
            return;
        }

        var collectionId = await _collectionRepo.GetCollectionIdByWorkIdAsync(workId.Value, ct);
        if (!collectionId.HasValue)
        {
            return;
        }

        await _parentCollectionResolver.ResolveParentCollectionAsync(collectionId.Value, ct);
    }
}
