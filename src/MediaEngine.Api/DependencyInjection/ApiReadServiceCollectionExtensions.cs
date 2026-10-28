using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Artwork;
using MediaEngine.Api.Services.BatchEdit;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Storage;

namespace MediaEngine.Api.DependencyInjection;

public static class ApiReadServiceCollectionExtensions
{
    public static IServiceCollection AddApiReadServices(this IServiceCollection services)
    {
        services.AddSingleton<IActivityLogReadService, ActivityLogReadService>();
        services.AddSingleton<LibraryActivityReadService>();
        services.AddSingleton<IVisibilityAfterMoveReadService, VisibilityAfterMoveReadService>();
        services.AddSingleton<IIngestionPresentationReadService, IngestionPresentationReadService>();
        services.AddSingleton<JourneyReadService>();
        services.AddSingleton<IJourneyReadService>(sp => sp.GetRequiredService<JourneyReadService>());
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
        services.AddSingleton<ILibraryBatchEditService, LibraryBatchEditService>();
        services.AddScoped<LibraryBatchEditAccessService>();
        services.AddScoped<EditSessionAccessService>();
        services.AddSingleton<IEditSubject, PersonEditSubject>();
        services.AddSingleton<IEditSubject, SeriesEditSubject>();
        services.AddSingleton<IEditSubject, UniverseEditSubject>();
        services.AddSingleton<IEditSubject, CuratedCollectionEditSubject>();
        services.AddSingleton<IEditSubject, FictionalEntityEditSubject>();
        services.AddSingleton<EditSubjectDirectory>();
        services.AddSingleton<IEditSessionChangeHandler, DetailsEditSessionChangeHandler>();
        services.AddScoped<RetailMatchWriter>();
        services.AddScoped<IRetailMatchWriter>(sp => sp.GetRequiredService<RetailMatchWriter>());
        services.AddSingleton<EditSessionCallerAccessor>();
        services.AddSingleton<IEditSessionChangeHandler, IdentityEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionFileWrites, EditSessionFileWrites>();
        services.AddSingleton<IEditSessionService, EditSessionService>();
        services.AddSingleton<IMetadataClaimHistoryReadService, MetadataClaimHistoryReadService>();
        services.AddSingleton<ICollectionBrowseReadService, CollectionBrowseReadService>();
        services.AddSingleton<ICollectionSearchReadService, CollectionSearchReadService>();
        services.AddScoped<IUniversalSearchReadService, UniversalSearchReadService>();
        services.AddSingleton<ICollectionMediaLookupReadService, CollectionMediaLookupReadService>();
        services.AddSingleton<CollectionCatalogReadService>();
        services.AddScoped<CharacterFirstSeenReader>();
        services.AddScoped<CharacterSpoilerService>();
        services.AddScoped<CharacterCreditSpoilerFilter>();
        services.AddScoped<UniverseConnectionsReadService>();
        services.AddScoped<UniverseClientReadService>();
        services.AddScoped<UniversePageReadService>();
        services.AddSingleton<UniverseLinkCache>();
        services.AddScoped<EntityAppearanceReadService>();
        services.AddSingleton<ICompanionChapterSource, CompanionChapterSource>();
        services.AddScoped<CompanionContextReadService>();
        services.AddScoped<CollectionPersonalMediaService>();
        services.AddSingleton<IReviewQueueReadService, ReviewQueueReadService>();
        services.AddSingleton<EditorSuggestionReadService>();
        services.AddSingleton<PersonEditorReadService>();
        services.AddSingleton<ArtworkLibraryReadService>();
        services.AddSingleton<ArtworkAssetService>();
        services.AddSingleton<IEditSessionChangeHandler, ArtworkEditSessionChangeHandler>();
        services.AddSingleton<ArtworkRenditionQueue>();
        services.AddSingleton<IArtworkRenditionService, ArtworkRenditionService>();
        services.AddSingleton<MediaEditorNavigationReadService>();
        services.AddSingleton<IMediaEditorNavigationReadService>(sp => sp.GetRequiredService<MediaEditorNavigationReadService>());
        services.AddSingleton<IMediaEditorMembershipReadService>(sp => sp.GetRequiredService<MediaEditorNavigationReadService>());
        services.AddSingleton<IMediaEditorOwnedChildReadService, MediaEditorOwnedChildReadService>();
        services.AddSingleton<MediaEditorWorkVersionReadRepository>();
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
