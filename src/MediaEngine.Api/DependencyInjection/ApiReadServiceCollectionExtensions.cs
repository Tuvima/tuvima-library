using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Artwork;
using MediaEngine.Api.Services.BatchEdit;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.Collections.Packs;
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
        services.AddSingleton<ActivityRunReadService>();
        services.AddSingleton<IVisibilityAfterMoveReadService, VisibilityAfterMoveReadService>();
        services.AddSingleton<IIngestionPresentationReadService, IngestionPresentationReadService>();
        services.AddSingleton<JourneyReadService>();
        services.AddSingleton<IJourneyReadService>(sp => sp.GetRequiredService<JourneyReadService>());
        services.AddSingleton<IPersonAliasReadService, PersonAliasReadService>();
        services.AddSingleton<IPersonPresenceReadService, PersonPresenceReadService>();
        services.AddSingleton<IPersonWorksReadService, PersonWorksReadService>();
        services.AddSingleton(sp => new MediaEngine.Api.Services.Display.SharedListCache(
            sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger<MediaEngine.Api.Services.Display.SharedListCache>()));
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
        services.AddSingleton<EditorSeriesGuard>();
        services.AddSingleton<IEditSessionChangeHandler, DetailsEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionChangeHandler, ThingAddEditSessionChangeHandler>();
        services.AddScoped<EditorUniverseThingsService>();
        services.AddScoped<RetailMatchWriter>();
        services.AddScoped<IRetailMatchWriter>(sp => sp.GetRequiredService<RetailMatchWriter>());
        services.AddSingleton<EditSessionCallerAccessor>();
        services.AddSingleton<IEditSessionChangeHandler, IdentityEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionChangeHandler, MatchRemovalEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionChangeHandler, WikidataLinkEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionChangeHandler, WikidataRemovalEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionChangeHandler, ExtraMoveEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionChangeHandler, SupportingFilesEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionChangeHandler, BookmarksEditSessionChangeHandler>();
        services.AddSingleton<VideoOrderWriter>();
        services.AddSingleton<IEditSessionChangeHandler, VideoSeriesEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionChangeHandler, VideoOrderEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionChangeHandler, VideoRenameEditSessionChangeHandler>();
        services.AddScoped<IEpisodeSiblingMatcher, EpisodeSiblingMatcher>();
        services.AddScoped<IShowChildRematcher, ShowChildRematcher>();
        services.AddSingleton<IEditSessionChangeHandler, SiblingMatchEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionFileWrites, EditSessionFileWrites>();
        services.AddSingleton<IEditSessionService, EditSessionService>();
        services.AddSingleton<IMetadataClaimHistoryReadService, MetadataClaimHistoryReadService>();
        services.AddSingleton<ICollectionBrowseReadService, CollectionBrowseReadService>();
        services.AddSingleton<ICollectionSearchReadService, CollectionSearchReadService>();
        services.AddScoped<IUniversalSearchReadService, UniversalSearchReadService>();
        services.AddSingleton<ICollectionMediaLookupReadService, CollectionMediaLookupReadService>();
        services.AddSingleton<CollectionCatalogReadService>();
        services.AddSingleton<CollectionLaneReadService>();
        services.AddScoped<CharacterFirstSeenReader>();
        services.AddScoped<CharacterSpoilerService>();
        services.AddScoped<CharacterCreditSpoilerFilter>();
        services.AddScoped<UniverseConnectionsReadService>();
        services.AddScoped<UniverseClientReadService>();
        services.AddScoped<UniverseContentsReadService>();
        services.AddScoped<UniversePageReadService>();
        services.AddSingleton<UniverseLinkCache>();
        services.AddScoped<EntityAppearanceReadService>();
        services.AddSingleton<ICompanionChapterSource, CompanionChapterSource>();
        services.AddScoped<CompanionContextReadService>();
        services.AddScoped<CollectionPersonalMediaService>();
        services.AddScoped<CollectionPackPlanner>();
        services.AddScoped<CollectionPackService>();
        services.AddScoped<IPackFileImporter>(sp => sp.GetRequiredService<CollectionPackService>());
        services.AddSingleton<IReviewQueueReadService, ReviewQueueReadService>();
        services.AddSingleton<INeedsReviewRowContextReadService, NeedsReviewRowContextReadService>();
        services.AddSingleton<INeedsReviewListReadService, NeedsReviewListReadService>();
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
        services.AddSingleton<ItemQuickFactsRepository>();
        services.AddSingleton<ItemQuickFactsService>();
        // The universal editor's v5 header, current match, document details and folder tree, and the numbers they read (config/editor.json).
        services.AddSingleton<EditorConfigurationProvider>();
        services.AddSingleton<MediaEngine.Domain.Contracts.IEditorConfigurationSource>(sp => sp.GetRequiredService<EditorConfigurationProvider>());
        services.AddScoped<EditorHeaderReadService>();
        services.AddScoped<IEditorFamilySource>(sp => sp.GetRequiredService<EditorHeaderReadService>());
        services.AddScoped<EditorCurrentMatchService>();
        // The Match tab's search: the providers and boxes for an item, and results judged against the file (config/scoring.json match_verdict).
        services.AddScoped(sp => new MediaEngine.Api.Services.Matching.EditorMatchVerdictCalculator(
            sp.GetRequiredService<MediaEngine.Domain.Contracts.IConfigurationLoader>().LoadScoring().MatchVerdict));
        services.AddScoped(sp => sp.GetRequiredService<MediaEngine.Domain.Contracts.IConfigurationLoader>().LoadScoring().MusicMatch);
        services.AddScoped<MediaEngine.Api.Services.Matching.EditorMatchLocalEvidenceReader>();
        services.AddScoped<MediaEngine.Api.Services.Matching.IVideoIdentifyWriter, MediaEngine.Api.Services.Matching.VideoIdentifyWriter>();
        services.AddScoped<MediaEngine.Api.Services.Matching.IEditorMatchKindResolver, MediaEngine.Api.Services.Matching.HeaderEditorMatchKindResolver>();
        services.AddScoped<MediaEngine.Api.Services.Matching.IEditorMatchProviderCatalog, MediaEngine.Api.Services.Matching.RetailEditorMatchProviderCatalog>();
        services.AddScoped<MediaEngine.Api.Services.Matching.IEditorMatchCandidateSearch, MediaEngine.Api.Services.Matching.CanonicalEditorMatchCandidateSearch>();
        services.AddScoped<MediaEngine.Api.Services.Matching.EditorMatchSearchService>();
        services.AddScoped<MediaEngine.Api.Services.Matching.EditorMusicMatchService>();
        // The Match tab's Wikidata side: search, pasted link, evidence and conflict (config/wikidata_match.json), and the link's own write.
        services.AddSingleton<MediaEngine.Api.Services.Matching.WikidataMatchConfigurationProvider>();
        services.AddScoped<MediaEngine.Api.Services.Matching.EditorWikidataMatchService>();
        services.AddScoped<MediaEngine.Api.Services.Matching.IWikidataIdentityWriter, MediaEngine.Api.Services.Matching.WikidataIdentityWriter>();
        services.AddScoped<EditorSuggestDetailsService>();
        services.AddScoped<MediaEngine.Api.Services.Extras.SupportingFileEditorReadService>();
        services.AddScoped<EditorFolderTreeService>();
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
