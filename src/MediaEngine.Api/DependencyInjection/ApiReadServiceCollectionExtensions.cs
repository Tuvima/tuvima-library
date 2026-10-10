using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Storage;

namespace MediaEngine.Api.DependencyInjection;

public static class ApiReadServiceCollectionExtensions
{
    public static IServiceCollection AddApiReadServices(this IServiceCollection services)
    {
        services.AddSingleton<IActivityBatchReadService, ActivityBatchReadService>();
        services.AddSingleton<IIngestionPresentationReadService, IngestionPresentationReadService>();
        services.AddSingleton<JourneyReadService>();
        services.AddSingleton<IJourneyReadService>(sp => sp.GetRequiredService<JourneyReadService>());
        services.AddSingleton<IIngestionBatchReadService, IngestionBatchReadService>();
        services.AddSingleton<IPersonAliasReadService, PersonAliasReadService>();
        services.AddSingleton<IPersonPresenceReadService, PersonPresenceReadService>();
        services.AddSingleton<IPersonWorksReadService, PersonWorksReadService>();
        services.AddSingleton<IPersonAssetScopeReadService, PersonAssetScopeReadService>();
        services.AddSingleton<IOrphanImageReferenceReadService, OrphanImageReferenceReadService>();
        services.AddSingleton<IProfileOverviewReadService, ProfileOverviewReadService>();
        services.AddSingleton<IPersonCreditReadService, PersonCreditReadService>();
        services.AddSingleton<IWorkDetailReadService, WorkDetailReadService>();
        services.AddSingleton<ILibraryOverviewReadService, LibraryOverviewReadService>();
        services.AddSingleton<ILibraryWorkFeedReadService, LibraryWorkFeedReadService>();
        services.AddSingleton<ILibraryCurationReadService, LibraryCurationReadService>();
        services.AddSingleton<IMetadataClaimHistoryReadService, MetadataClaimHistoryReadService>();
        services.AddSingleton<ICollectionBrowseReadService, CollectionBrowseReadService>();
        services.AddSingleton<ICollectionSearchReadService, CollectionSearchReadService>();
        services.AddScoped<IUniversalSearchReadService, UniversalSearchReadService>();
        services.AddSingleton<ICollectionMediaLookupReadService, CollectionMediaLookupReadService>();
        services.AddSingleton<CollectionCatalogReadService>();
        services.AddScoped<CollectionPersonalMediaService>();
        services.AddSingleton<IReviewQueueReadService, ReviewQueueReadService>();
        services.AddSingleton<EditorSuggestionReadService>();
        services.AddSingleton<PersonEditorReadService>();
        services.AddSingleton<ArtworkLibraryReadService>();
        services.AddSingleton<ArtworkAssetService>();
        services.AddSingleton<MediaEditorNavigationReadService>();
        services.AddSingleton<IMediaEditorNavigationReadService>(sp => sp.GetRequiredService<MediaEditorNavigationReadService>());
        services.AddSingleton<IMediaEditorMembershipReadService>(sp => sp.GetRequiredService<MediaEditorNavigationReadService>());
        services.AddSingleton<IMediaEditorOwnedChildReadService, MediaEditorOwnedChildReadService>();
        services.AddSingleton<MediaEditorWorkVersionReadRepository>();
        services.AddSingleton<MusicTrackRelocationRepository>();
        services.AddSingleton<MediaEditorFileCoverageRepository>();
        services.AddSingleton<MediaEditorSelectionHistoryReadService>();
        services.AddSingleton<PairingAssetReadService>();
        services.AddSingleton<TvPairingLocalTargetReadService>();
        services.AddSingleton<EpisodeStillReviewReadService>();
        services.AddSingleton<HierarchyAlignmentService>();
        services.AddSingleton<IHierarchyAlignmentService>(sp => sp.GetRequiredService<HierarchyAlignmentService>());
        return services;
    }
}
