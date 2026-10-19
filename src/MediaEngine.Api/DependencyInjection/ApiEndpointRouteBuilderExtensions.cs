#if DEBUG
using MediaEngine.Api.DevSupport;
#endif
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Security;
using MediaEngine.Domain;

namespace MediaEngine.Api.DependencyInjection;

public static class ApiEndpointRouteBuilderExtensions
{
    public static WebApplication MapEngineEndpoints(this WebApplication app)
    {
        app.MapHub<Intercom>(SignalREvents.IntercomPath)
            .RequireRateLimiting(LiveUpdatesRateLimitPolicies.IntercomHub)
            .AllowAnonymous();
        app.MapHub<ApplicationEventsHub>(MediaEngine.Contracts.Authentication.ApplicationEventClientMethods.HubPath)
            .RequireRateLimiting(LiveUpdatesRateLimitPolicies.ApplicationEventsHub)
            .RequireAuthorization(AuthPolicies.Authenticated);
        app.MapSystemEndpoints();
        app.MapSetupEndpoints();
        app.MapAuthenticationEndpoints();
        app.MapAccountEndpoints();
        app.MapApplicationEndpoints();
        app.MapApplicationWebhookEndpoints();
        app.MapClientAuthorizationEndpoints();
        app.MapMaintenanceEndpoints();
        app.MapAdminEndpoints();
        app.MapCollectionEndpoints();
        app.MapLibraryEndpoints();
        app.MapStreamEndpoints();
        app.MapHlsStreamEndpoints();
        app.MapCastEndpoints();
        app.MapViewEndpoints();
        app.MapViewDiscoveryEndpoints();
        app.MapViewClientEndpoints();
        app.MapPlaybackEndpoints();
        app.MapPlayerEndpoints();
        app.MapPlaybackTelemetryEndpoints();
        app.MapPlaybackSegmentEndpoints();
        app.MapReadEndpoints();
        app.MapReaderEndpoints();
        app.MapIngestionEndpoints();
        app.MapEnrichmentRefreshEndpoints();
        app.MapOperationsEndpoints();
        app.MapCapabilityEndpoints();
        app.MapMetadataEndpoints();
        app.MapReviewEndpoints();
        app.MapSettingsEndpoints();
        app.MapOcrSettingsEndpoints();
        app.MapToolRequirementsEndpoints();
        app.MapSettingsNoticeEndpoints();
        app.MapNetworkEndpoints();
        app.MapServerFolderEndpoints();
        app.MapLibraryReorganizationEndpoints();
        app.MapFolderJobEndpoints();
        app.MapProviderCatalogueEndpoints();
        app.MapUISettingsEndpoints();
        app.MapProfileEndpoints();
        app.MapProfileStateEndpoints();
        app.MapPersonEndpoints();
        app.MapWorkEndpoints();
        app.MapProgressEndpoints();
        app.MapActivityEndpoints();
        app.MapPersonalActivityEndpoints();
        app.MapPersonalTasteEndpoints();
        app.MapLogEndpoints();
        app.MapDisplayEndpoints();
        app.MapCollectionClientEndpoints();
        app.MapDetailEndpoints();
        app.MapUniverseGraphEndpoints();
        app.MapSharedEntityEditorEndpoints();
        app.MapUniverseLoreEndpoints();
        app.MapCharacterEndpoints();
        app.MapCanonEndpoints();
        app.MapDeferredEnrichmentEndpoints();
        app.MapLibraryItemEndpoints();
        app.MapItemCanonicalEndpoints();
        app.MapTimelineEndpoints();
        app.MapSearchEndpoints();
        app.MapAiEndpoints();
        app.MapPluginEndpoints();
        app.MapPluginApplicationServiceEndpoints();

        return app;
    }

    public static WebApplication MapDevelopmentEngineEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
#if DEBUG
            app.MapDebugEndpoints();
            app.MapDevSeedEndpoints();
            app.MapIntegrationTestEndpoints();
            app.MapRealMediaEndpoints();
#endif
        }

        return app;
    }
}
