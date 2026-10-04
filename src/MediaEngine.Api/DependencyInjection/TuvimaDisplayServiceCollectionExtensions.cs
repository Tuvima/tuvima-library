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

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaDisplayServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaDisplay(this IServiceCollection services)
    {
        services.AddSingleton<IByteStreamer, ByteStreamer>();
        services.AddApiReadServices();
        services.AddSingleton<ILibraryItemCurationRepository, LibraryItemCurationRepository>();
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
        services.AddSingleton<DisplayWorkProjectionReader>();
        services.AddScoped<ContributorShelfReadService>();
        services.AddSingleton<DisplayJourneyProjectionReader>();
        services.AddSingleton<DisplayFavoriteProjectionReader>();
        services.AddSingleton<DisplayHomeCollectionProjectionReader>();
        services.AddSingleton<DisplayLaneGroupPolicy>();
        services.AddSingleton<DisplayCardBuilder>();
        services.AddSingleton<DisplayShelfBuilder>();
        services.AddScoped<DisplayComposerService>();
        services.AddScoped<RecentCatalogueReadService>();
        services.AddScoped<DisplayRecentComposerService>();
        services.AddScoped<ProfileStateAccessService>();
        services.AddSingleton<DetailRecommendationService>();
        services.AddSingleton<DetailComposerService>();
        services.AddSingleton<ContributorReadiness>();
        return services;
    }
}
