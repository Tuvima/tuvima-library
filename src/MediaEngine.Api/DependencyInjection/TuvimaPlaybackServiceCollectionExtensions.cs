using MediaEngine.Api.Services.Activity;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.Taste;
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
        services.AddSingleton<DeviceDownloadRepository>();
        services.AddScoped<IPhotoDownloadAccess, ViewPhotoDownloadAccess>();
        services.AddScoped<DeviceDownloadService>();
        services.AddSingleton<AdaptiveHlsPackageRepository>();
        services.AddSingleton<PlaybackCapabilitiesService>();
        services.AddSingleton<AdaptiveHlsService>();
        services.AddSingleton<HlsAccessGrantService>();
        // Casting: a separate grant kind (its own Data Protection purpose) so HLS and cast grants can never be swapped.
        services.AddSingleton<CastAccessGrantService>();
        services.AddSingleton<CastSessionService>();
        services.AddScoped<ICastAssetAuthorizer, CatalogueCastAssetAuthorizer>();
        services.AddSingleton<AdaptiveHlsCleanupService>();
        services.AddSingleton<PlayerSessionRepository>();
        services.AddSingleton<PlayerDeviceStateRepository>();
        services.AddSingleton<CompanionSessionRepository>();
        services.AddSingleton<AudiobookListenHistoryRepository>();
        services.AddSingleton<MusicPlayStatsRepository>();
        services.AddSingleton<AudiobookBookmarkRepository>();
        services.AddSingleton<BookmarkPackRepository>();
        services.AddSingleton<BookmarkPackService>();
        services.AddSingleton<AudiobookChapterTitleOverrideRepository>();
        services.AddSingleton<AudiobookChapterNamingService>();
        services.AddSingleton<IPlaybackTelemetryRepository, PlaybackTelemetryRepository>();
        services.AddSingleton<IPlaybackActivityProbe, PlaybackActivityProbe>();
        // Each person's own activity record (Settings > Personal > Activity) follows playback and My list.
        services.AddSingleton<PersonalActivityArtworkService>();
        services.AddSingleton<PersonalActivityRecorder>();
        services.AddScoped<PersonalActivityReadService>();
        services.AddScoped<PersonalTasteService>();
        services.AddSingleton<PlaybackTelemetryLifecycleService>();
        services.AddScoped<PlaybackTelemetryReadService>();
        services.AddHostedService<PlaybackTelemetryMaintenanceService>();
        services.AddSingleton<PlayerService>();
        services.AddSingleton<ICompanionSessionService, CompanionSessionService>();
        services.AddSingleton<MediaEngine.Api.Security.PlayerCatalogueScope>();
        services.AddSingleton<IUserPlaybackSettingsService, UserPlaybackSettingsService>();
        services.AddSingleton<IVideoMetadataExtractor, FFmpegVideoMetadataExtractor>();
        services.AddSingleton<IEpubContentService, EpubContentService>();
        return services;
    }
}
