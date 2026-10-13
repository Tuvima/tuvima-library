using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Processors;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Extractors;
using MediaEngine.Processors.Processors;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaPlaybackServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaPlayback(this IServiceCollection services)
    {
        services.AddSingleton<IFFmpegService, FFmpegService>();
        services.AddSingleton<PlaybackStateRepository>();
        services.AddSingleton<AdaptiveHlsPackageRepository>();
        services.AddSingleton<PlaybackCapabilitiesService>();
        services.AddSingleton<AdaptiveHlsService>();
        services.AddSingleton<HlsAccessGrantService>();
        services.AddSingleton<AdaptiveHlsCleanupService>();
        services.AddSingleton<PlayerSessionRepository>();
        services.AddSingleton<AudiobookListenHistoryRepository>();
        services.AddSingleton<MusicPlayStatsRepository>();
        services.AddSingleton<AudiobookBookmarkRepository>();
        services.AddSingleton<AudiobookChapterTitleOverrideRepository>();
        services.AddSingleton<AudiobookChapterNamingService>();
        services.AddSingleton<IPlaybackTelemetryRepository, PlaybackTelemetryRepository>();
        services.AddSingleton<IPlaybackActivityProbe, PlaybackActivityProbe>();
        services.AddSingleton<PlaybackTelemetryLifecycleService>();
        services.AddScoped<PlaybackTelemetryReadService>();
        services.AddHostedService<PlaybackTelemetryMaintenanceService>();
        services.AddSingleton<PlayerService>();
        services.AddSingleton<MediaEngine.Api.Security.PlayerCatalogueScope>();
        services.AddSingleton<IUserPlaybackSettingsService, UserPlaybackSettingsService>();
        services.AddSingleton<IVideoMetadataExtractor, FFmpegVideoMetadataExtractor>();
        services.AddSingleton<IEpubContentService, EpubContentService>();
        return services;
    }
}
