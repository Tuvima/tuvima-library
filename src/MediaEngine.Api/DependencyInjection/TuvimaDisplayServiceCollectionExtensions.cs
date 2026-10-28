using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Canonical;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Domain.Contracts;
using MediaEngine.Processors;
using MediaEngine.Processors.Contracts;
using MediaEngine.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaDisplayServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaDisplay(this IServiceCollection services)
    {
        services.AddSingleton<IByteStreamer, ByteStreamer>();
        services.AddApiReadServices();
        services.AddSingleton<ILibraryItemCurationRepository, LibraryItemCurationRepository>();
        services.AddSingleton<ILibraryItemDeletionRepository, LibraryItemDeletionRepository>();
        services.AddSingleton<IMetadataEditorRepository, MetadataEditorRepository>();
        services.AddSingleton<IItemCanonicalRepository, ItemCanonicalRepository>();
        services.AddSingleton<AlbumTrackManifestService>();
        services.AddSingleton<ArtworkScopeService>();
        services.AddSingleton<CanonicalCandidateBuilder>();

        // Raw projections remain shared and cacheable. The authorization projection
        // and composers are request scoped so every response uses current authority.
        services.AddSingleton<IRawDisplayProjectionReadService, DisplayProjectionReadService>();
        services.AddScoped<AuthorizedDisplayProjectionReadService>();
        services.AddScoped<IDisplayProjectionReadService>(services =>
            services.GetRequiredService<AuthorizedDisplayProjectionReadService>());
        // The one access service every viewer read goes through (Vault sections spec D32): people, counts and anything that is not a list of works
        // start from its catalogue, built from the same authorized projection.
        services.AddScoped<IViewerAccessService, ViewerAccessService>();
        services.AddSingleton<DisplayWorkProjectionReader>();
        services.AddSingleton<DisplayVisibleWorkReader>();
        services.AddScoped<ContributorShelfReadService>();
        services.AddSingleton<DisplayJourneyProjectionReader>();
        services.AddSingleton<DisplayFavoriteProjectionReader>();
        services.AddSingleton<DisplayHomeCollectionProjectionReader>();
        services.AddSingleton<DisplayLaneGroupPolicy>();
        services.AddSingleton<DisplayCardBuilder>();
        services.AddSingleton<DisplayShelfBuilder>();
        services.AddSingleton<DisplayFormatReader>();
        services.AddSingleton<DisplayGridPersonalReader>();
        services.TryAddSingleton<IHeroOrderer, DefaultHeroOrderer>();
        services.TryAddSingleton<IHeroPluginSlides, HeroPluginSlides>();
        services.AddScoped<DisplayComposerService>();
        services.AddScoped<BrowseLibraryListService>();
        services.AddScoped<RecentCatalogueReadService>();
        services.AddScoped<DisplayRecentComposerService>();
        services.AddScoped<ProfileStateAccessService>();
        services.AddSingleton<DetailComposerService>();
        services.AddSingleton<DetailUnmatchedStateService>();
        services.AddSingleton<ContributorReadiness>();
        return services;
    }
}
