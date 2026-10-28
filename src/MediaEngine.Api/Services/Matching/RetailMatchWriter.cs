using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Api.Services.Matching;

/// <summary>What the edit session needs from the shared retail-match write (kept small so the identity handler can be tested with a spy).</summary>
public interface IRetailMatchWriter
{
    /// <summary>Identity and details only: the item stays in its album, season or series (universal editor spec D6).</summary>
    Task<IResult> ReplaceWithoutMovingAsync(Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct);

    /// <summary>The cheap local checks of a retail match, with nothing written and no provider called.</summary>
    Task<IResult> CheckAsync(Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct);
}

/// <summary>
/// The Engine's one way to replace an item's retail match: the services the write needs, gathered so the editor's retail-match
/// action and the edit session's identity change run the very same code (<c>ItemCanonicalEndpoints.ReplaceRetailMatchCoreAsync</c>).
/// </summary>
public sealed class RetailMatchWriter(
    IMetadataClaimRepository claimRepo,
    ICanonicalValueRepository canonicalRepo,
    IBridgeIdRepository bridgeIdRepo,
    ISystemActivityRepository activityRepo,
    IEventPublisher publisher,
    ICollectionRepository collectionRepo,
    IWorkRepository workRepo,
    IReviewQueueRepository reviewRepo,
    IHydrationPipelineService pipeline,
    AlbumTrackManifestService albumTrackManifestService,
    CoverArtWorker coverArtWorker,
    TimelineRecorder timeline,
    IItemCanonicalRepository itemCanonicalData,
    IHierarchyAlignmentService hierarchyAlignment,
    ProviderApiClient tvdb,
    MusicBrainzReleaseClient musicBrainz,
    AppleRetailClient apple,
    IEnumerable<IExternalMetadataProvider> externalProviders,
    ILoggerFactory loggerFactory,
    IExternalLookupGuard lookupGuard) : IRetailMatchWriter
{
    internal IMetadataClaimRepository ClaimRepo => claimRepo;
    internal ICanonicalValueRepository CanonicalRepo => canonicalRepo;
    internal IBridgeIdRepository BridgeIdRepo => bridgeIdRepo;
    internal ISystemActivityRepository ActivityRepo => activityRepo;
    internal IEventPublisher Publisher => publisher;
    internal ICollectionRepository CollectionRepo => collectionRepo;
    internal IWorkRepository WorkRepo => workRepo;
    internal IReviewQueueRepository ReviewRepo => reviewRepo;
    internal IHydrationPipelineService Pipeline => pipeline;
    internal AlbumTrackManifestService AlbumTrackManifestService => albumTrackManifestService;
    internal CoverArtWorker CoverArtWorker => coverArtWorker;
    internal TimelineRecorder Timeline => timeline;
    internal IItemCanonicalRepository ItemCanonicalData => itemCanonicalData;
    internal IHierarchyAlignmentService HierarchyAlignment => hierarchyAlignment;
    internal ProviderApiClient Tvdb => tvdb;
    internal MusicBrainzReleaseClient MusicBrainz => musicBrainz;
    internal AppleRetailClient Apple => apple;
    internal IEnumerable<IExternalMetadataProvider> ExternalProviders => externalProviders;
    internal ILoggerFactory LoggerFactory => loggerFactory;
    internal IExternalLookupGuard LookupGuard => lookupGuard;

    /// <summary>The endpoint's own retail-match action: identity, details and, where the match names a different parent, the move.</summary>
    internal Task<IResult> ReplaceAsync(Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct) =>
        ItemCanonicalEndpoints.ReplaceRetailMatchCoreAsync(this, entityId, request, allowParentMove: true, validateOnly: false, ct);

    /// <summary>
    /// The edit session's identity change (universal editor spec D6): identity and details only. The item stays in its album,
    /// season or series; moving it is a separate relationship change.
    /// </summary>
    public Task<IResult> ReplaceWithoutMovingAsync(Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct) =>
        ItemCanonicalEndpoints.ReplaceRetailMatchCoreAsync(this, entityId, request, allowParentMove: false, validateOnly: false, ct);

    /// <summary>The cheap local checks of a retail match, with nothing written and no provider called.</summary>
    public Task<IResult> CheckAsync(Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct) =>
        ItemCanonicalEndpoints.ReplaceRetailMatchCoreAsync(this, entityId, request, allowParentMove: false, validateOnly: true, ct);
}
