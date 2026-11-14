using MediaEngine.Api.Services.Activity;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.Taste;
using MediaEngine.Domain.Contracts;
using MediaEngine.Processors;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Extractors;
using MediaEngine.Processors.Processors;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.DataProtection;

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
        // What the Dashboard's browsers measured about themselves this session, and what failed for them (playback-path D12, D13).
        services.AddSingleton<BrowserPlaybackAbilityRegistryService>();
        // The one "Video quality and devices" list and its saved values (playback-path D15).
        services.AddSingleton<VideoOptionsService>();
        services.AddSingleton<PlaybackActivityRegistry>();
        services.AddSingleton<IEncodeLoadSampler, FfmpegProcessLoadSampler>();
        services.AddSingleton<EncodeSlotGate>(sp => new EncodeSlotGate(() => Math.Clamp(sp.GetRequiredService<IConfigurationLoader>().LoadTranscoding().MaxConcurrentTranscodes, 1, 8)));
        services.AddSingleton<IPlayingNowNames, PlayingNowNames>();
        services.AddSingleton<PlayingNowReadService>();
        services.AddSingleton<AdaptiveHlsService>();
        // Scrub previews (Unified player v2 D9a): frames made ahead of time, in the Library's own cache.
        services.AddSingleton<MediaEngine.Processors.VideoFrameExtractor>();
        services.AddSingleton<ScrubPreviewCache>();
        services.AddSingleton<ScrubPreviewQueue>();
        services.AddSingleton<ScrubPreviewGenerator>();
        services.AddSingleton<ScrubPreviewSettingsService>();
        services.AddSingleton<MediaEngine.Storage.ScrubPreviewCandidateRepository>();
        services.AddSingleton<ITranscodeActivitySignal, TranscodeActivitySignal>();
        // Bookmark frames (Unified player v2 D21a): one still per video bookmark, made right after the save, in the same cache family.
        services.AddSingleton<BookmarkFrameCache>();
        services.AddSingleton<BookmarkFrameService>();
        services.AddSingleton<HlsAccessGrantService>();
        // Casting: a separate grant kind (its own Data Protection purpose) so HLS and cast grants can never be swapped.
        // Built by hand so the identity store (which a signed-in Dashboard session's grant is checked against on every request) can
        // never be silently left out; a missing registration fails here, at first use, rather than quietly refusing every session grant.
        services.AddSingleton(provider => new CastAccessGrantService(
            provider.GetRequiredService<IDataProtectionProvider>(),
            provider.GetRequiredService<IAccountRepository>(),
            provider.GetRequiredService<IApplicationRepository>(),
            provider.GetRequiredService<IClientAuthorizationRepository>(),
            provider.GetRequiredService<IPermissionRegistry>(),
            provider.GetService<TimeProvider>(),
            provider.GetRequiredService<IIdentityRepository>()));
        services.AddSingleton<CastSessionService>();
        services.AddScoped<ICastAssetAuthorizer, CatalogueCastAssetAuthorizer>();
        services.AddSingleton<AdaptiveHlsCleanupService>();
        services.AddSingleton<PlayerSessionRepository>();
        services.AddSingleton<PlayerDeviceStateRepository>();
        services.AddSingleton<CompanionSessionRepository>();
        services.AddSingleton<AudiobookListenHistoryRepository>();
        services.AddSingleton<MusicPlayStatsRepository>();
        services.AddSingleton<AudiobookBookmarkRepository>();
        services.AddSingleton<BookmarkRecognitionRepository>();
        services.AddSingleton<BookmarkClosenessProvider>();
        // Page reader (PDF and comic reader spec WP1): comic page lists and page pictures, limits from config/reader.json.
        services.AddSingleton<MediaEngine.Api.Services.Reading.ReaderConfigurationProvider>();
        services.AddSingleton<MediaEngine.Api.Services.Reading.IReaderTitleService, MediaEngine.Api.Services.Reading.ReaderTitleService>();
        services.AddScoped<MediaEngine.Api.Services.Reading.IReaderNextIssueService, MediaEngine.Api.Services.Reading.ReaderNextIssueService>();
        services.AddSingleton<MediaEngine.Api.Services.Reading.IComicPageService, MediaEngine.Api.Services.Reading.ComicPageService>();
        services.AddSingleton<BookmarkSetService>();
        services.AddSingleton<EditorBookmarkRepository>();
        services.AddSingleton<PlayerBookmarkVisibility>();
        services.AddSingleton<EditorBookmarksService>();
        services.AddScoped<EditorBookmarkOverviewService>();
        services.AddSingleton<ReaderBookmarkSetService>();
        services.AddSingleton<PageBookmarkSetService>();
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
