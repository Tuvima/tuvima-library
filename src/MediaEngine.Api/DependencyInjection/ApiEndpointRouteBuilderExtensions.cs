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
        app.MapUserTransferEndpoints();
        app.MapApplicationEndpoints();
        app.MapApplicationWebhookEndpoints();
        app.MapClientAuthorizationEndpoints();
        app.MapMaintenanceEndpoints();
        app.MapAdminEndpoints();
        app.MapCollectionEndpoints();
        app.MapLibraryEndpoints();
        app.MapUnmatchedEndpoints();
        app.MapFolderFirstCheckEndpoints();
        app.MapStreamEndpoints();
        app.MapHlsStreamEndpoints();
        app.MapCastEndpoints();
        app.MapViewEndpoints();
        app.MapViewDiscoveryEndpoints();
        app.MapViewClientEndpoints();
        app.MapPlaybackEndpoints();
        app.MapPlayerEndpoints();
        app.MapCompanionEndpoints();
        app.MapPlaybackTelemetryEndpoints();
        app.MapPlaybackSegmentEndpoints();
        app.MapReadEndpoints();
        app.MapReaderEndpoints();
        app.MapIngestionEndpoints();
        app.MapImportUploadEndpoints();
        app.MapEnrichmentRefreshEndpoints();
        app.MapOperationsEndpoints();
        app.MapCapabilityEndpoints();
        app.MapMetadataEndpoints();
        app.MapReviewEndpoints();
        app.MapSettingsEndpoints();
        app.MapStashBoxEndpoints();
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
        app.MapVaultEndpoints();
        app.MapPersonEndpoints();
        app.MapWorkEndpoints();
        app.MapProgressEndpoints();
        app.MapActivityEndpoints();
        app.MapPersonalActivityEndpoints();
        app.MapPersonalTasteEndpoints();
        app.MapLogEndpoints();
        app.MapDisplayEndpoints();
        app.MapDisplayGridPreferenceEndpoints();
        app.MapCollectionClientEndpoints();
        app.MapDetailEndpoints();
        app.MapUniverseGraphEndpoints();
        app.MapUniverseAppearanceEndpoints();
        app.MapSharedEntityEditorEndpoints();
        app.MapUniverseLoreEndpoints();
        app.MapCharacterEndpoints();
        app.MapCanonEndpoints();
        app.MapDeferredEnrichmentEndpoints();
        app.MapLibraryItemEndpoints();
        app.MapExcludedFileEndpoints();
        app.MapItemCanonicalEndpoints();
        app.MapTimelineEndpoints();
        app.MapSearchEndpoints();
        app.MapAiEndpoints();
        app.MapPluginEndpoints();
        app.MapPluginSourceEndpoints();
        app.MapNotificationEndpoints();
        app.MapBackupDestinationEndpoints();
        app.MapPluginHostEndpoints();
        app.MapPluginPageEndpoints();
        app.MapApprovalEndpoints();
        app.MapPluginApplicationServiceEndpoints();

        return app;
    }

    /// <summary>
    /// Whether this Engine maps the development-only actions (<c>/debug/*</c> and <c>/dev/*</c>): only a DEBUG build
    /// running in the Development environment does. <see cref="MapDevelopmentEngineEndpoints"/> and the
    /// <c>GET /system/development-tools</c> answer both read this, so what the Dashboard is told can never drift from
    /// what is actually mapped.
    /// </summary>
    public static bool OffersDevelopmentEndpoints(this IHostEnvironment environment)
    {
#if DEBUG
        return environment.IsDevelopment();
#else
        ArgumentNullException.ThrowIfNull(environment);
        return false;
#endif
    }

    public static WebApplication MapDevelopmentEngineEndpoints(this WebApplication app)
    {
        if (app.Environment.OffersDevelopmentEndpoints())
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
