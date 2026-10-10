using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.DependencyInjection;
using MediaEngine.Ingestion.Services;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Maps the real production endpoint extension on a bare application. This is
/// deliberately registration-only: it never runs Program, hosted services,
/// watchers, storage initialization, or request handlers.
/// </summary>
public sealed class MappedEndpointInventoryTests
{
    [Fact]
    public async Task AdministrationServices_UsePreciseReadAndMutationPermissions()
    {
        await using var app = BuildEndpointOnlyApplication();
        var inventory = EndpointInventory.From(app);
        var expected = new Dictionary<ApplicationPermissionId, string[]>
        {
            [ApplicationPermissionIds.ReviewRead] = ["GetPendingReviews", "GetReviewCount", "GetReviewItem", "GetReportsForEntity"],
            [ApplicationPermissionIds.ReviewResolve] = ["ResolveReviewItem", "DismissReviewItem", "SkipUniverseMatch", "MoveReviewItemToTv", "ResolveReport", "DismissReport"],
            [ApplicationPermissionIds.ProvidersStatusRead] = ["GetProviderHealth", "GetProviderIcon"],
            [ApplicationPermissionIds.ProvidersConfigRead] = ["GetProviderStatus", "GetProviderCatalogue"],
            [ApplicationPermissionIds.ProvidersConfigWrite] = ["UpdateProvider", "TestProviderCredentials", "SaveProviderCredentials", "RemoveProviderCredentials", "TestProvider", "SampleProvider", "UpdateProviderConfig", "DeleteProvider", "UploadProviderIcon"],
            [ApplicationPermissionIds.AiStatusRead] = ["GetAiStatus", "GetAiModelStatuses", "GetAiHardwareProfile", "GetAiResourceSnapshot"],
            [ApplicationPermissionIds.AiManage] = ["StartAiModelDownload", "CancelAiModelDownload", "LoadAiModel", "UnloadAiModel", "GetAiConfig", "SaveAiConfig", "RunAiHardwareBenchmark", "InvalidateAiHardwareBenchmark"],
            [ApplicationPermissionIds.NetworkStatusRead] = ["GetNetworkSettings", "GetNetworkRuntimeStatus", "GetRemoteAccessReadiness"],
            [ApplicationPermissionIds.NetworkConfigWrite] = ["UpdateNetworkSettings", "TestLocalNetworkConnection", "TestRemoteNetworkConnection", "TestNetworkUploadBandwidth", "CheckNetworkPortAvailability", "ApplyNetworkPortChange", "RenewNetworkRouterMapping", "ResetNetworkSettings"],
            [ApplicationPermissionIds.StorageStatusRead] = ["GetLibraries", "GetOrganizationTemplate"],
            [ApplicationPermissionIds.StorageConfigWrite] = ["UpdateLibraries", "TestPath", "PreviewOrganizationTemplate", "UpdateOrganizationTemplate", "RunStorageMaintenance"],
            [ApplicationPermissionIds.MetadataEnrichmentRead] = ["GetHydrationSettings", "GetAiEnrichmentProgress", "GetAssetCapabilities", "GetCapabilitySummary", "GetRetagSweepState"],
            [ApplicationPermissionIds.MetadataWrite] = ["ApplyRetagSweepPending", "RunRetagSweepNow", "RetryRetagForAsset"],
            [ApplicationPermissionIds.IngestionRun] = ["RunInitialSweep"],
        };
        foreach (var (permission, names) in expected)
        {
            foreach (var name in names)
            {
                var endpoint = Assert.Single(inventory.Endpoints, endpoint => endpoint.Name == name);
                endpoint.RequiresAdministratorOrApplication(permission);
                Assert.DoesNotContain(AuthPolicies.Administrator, endpoint.NamedPolicies);
            }
        }

        string[] personal = ["GetUIDeviceProfile", "GetUIProfileSettings", "UpdateUIProfileSettings", "GetResolvedUISettings", "GetLibraryPreferences", "SubmitReport", "GetMediaTypes"];
        foreach (var name in personal)
        {
            Assert.Single(inventory.Endpoints, endpoint => endpoint.Name == name).RequiresNamedPolicy(AuthPolicies.HumanSelfService);
        }
    }

    [Fact]
    public async Task OperationsEndpoints_SeparateMonitoringHistoryAndEachControl()
    {
        await using var app = BuildEndpointOnlyApplication();
        var inventory = EndpointInventory.From(app);
        var expected = new Dictionary<ApplicationPermissionId, string[]>
        {
            [ApplicationPermissionIds.IngestionStatusRead] = ["GetIngestionNotificationStatus", "GetIngestionOperationsSnapshot", "GetIngestionPresentation", "GetCurrentIngestionMediaGroups", "ListWatchFolder", "ListMediaOperations", "GetMediaOperationsSummary"],
            [ApplicationPermissionIds.IngestionHistoryRead] = ["GetRecentIngestionAdditions", "GetIngestionMediaGroup", "GetIngestionMediaGroupChildren", "GetRecentBatches", "GetBatchAttentionCount", "GetBatchItems", "GetBatchById", "GetMediaOperation", "GetActivityHistorySummary", "GetActivityBatches", "GetActivityBatch", "GetActivityBatchPresentation", "GetActivityBatchMediaGroups", "GetActivityBatchGroups", "GetActivityBatchInsights", "GetActivityBatchItems", "GetActivityBatchEvents", "GetActivityBatchItemDetail", "GetActivityByRunId"],
            [ApplicationPermissionIds.IngestionRun] = ["TriggerScan", "TriggerLibraryScan", "TriggerRescan", "TriggerReconciliation", "UploadMedia"],
            [ApplicationPermissionIds.IngestionRetry] = ["RereadAssetMetadata", "RetryMediaOperation"],
            [ApplicationPermissionIds.IngestionCancel] = ["CancelMediaOperation"],
            [ApplicationPermissionIds.SystemActivityRead] = ["GetActivityPeopleAudit", "GetRecentActivity", "GetActivityStats", "GetActivityByTypes"],
        };
        string[] administratorOnly = ["PruneActivity", "UpdateActivityRetention"];
        var operations = inventory.Endpoints.Where(endpoint =>
            (endpoint.Pattern == "/operations" || endpoint.Pattern.StartsWith("/operations/", StringComparison.Ordinal) ||
             endpoint.Pattern == "/activity" || endpoint.Pattern.StartsWith("/activity/", StringComparison.Ordinal) ||
             endpoint.Pattern.StartsWith("/ingestion/", StringComparison.Ordinal)) &&
            !endpoint.Pattern.StartsWith("/ingestion/refresh-schedule", StringComparison.Ordinal)).ToArray();
        Assert.Equal(expected.Values.SelectMany(names => names).Concat(administratorOnly).Order(), operations.Select(endpoint => endpoint.Name ?? endpoint.Pattern).Order());
        foreach (var (permission, names) in expected)
        {
            foreach (var name in names)
            {
                Assert.Single(operations, endpoint => endpoint.Name == name).RequiresAdministratorOrApplication(permission);
            }
        }

        foreach (var name in administratorOnly)
        {
            Assert.Single(operations, endpoint => endpoint.Name == name).RequiresNamedPolicy(AuthPolicies.Administrator);
        }
    }

    [Fact]
    public async Task MetadataEndpoints_SeparateReadMatchWriteAndEnrichmentPermissions()
    {
        await using var app = BuildEndpointOnlyApplication();
        var inventory = EndpointInventory.From(app);
        var expected = new Dictionary<ApplicationPermissionId, string[]>
        {
            [ApplicationPermissionIds.MetadataRead] = ["GetClaimHistory", "GetMediaEditorContext", "GetScopedArtworkEditor", "GetArtworkEditor", "GetArtworkWritebackStatus", "GetArtworkWritebackStatuses", "GetEditionRenditions", "GetSearchResultsCache", "GetCanonicalValues", "ResolveLabels", "GetWikidataAliases", "GetMediaEditorNavigator", "GetMediaEditorWorkVersions", "SearchMediaEditorOwnedChildren", "SnapshotMediaEditorOwnedChildSelection", "PreviewParentFirstMediaEditorPairing", "SearchReviewedTvPairingChildren", "GetMediaEditorFileCoverage", "GetMediaEditorMembershipSuggestions", "PreviewMediaEditorMembershipChange", "GetCanonDiscrepancies", "GetTvdbMatchPreview", "GetTvdbScopedMatchCandidates", "PreviewTvdbShowOrder"],
            [ApplicationPermissionIds.MetadataWrite] = ["LockClaim", "OverrideMetadata", "ReclassifyMediaType", "UploadCover", "UploadScopedArtwork", "UploadScopedArtworkFromUrl", "UploadEntityArtwork", "SetPreferredArtwork", "DeleteArtworkVariant", "CoverFromUrl", "ApplyMediaEditorMembershipChange", "ApplyTvdbScopedMatch", "ApplyTvdbShowOrder", "ImportSelectedProviderArtwork", "RetryArtworkWriteback", "UpdateAssetRendition", "PreviewEditionCoverPreference", "SaveEditionCoverPreference", "SaveMediaEditorFileCoverage", "PreviewReviewedTvEpisodeStill", "PreviewReviewedTvSharedArtwork"],
            [ApplicationPermissionIds.MetadataMatch] = ["SearchMetadata", "SearchMetadataFanOut", "PutSearchResultsCache", "WikidataTest", "PreviewMusicTrackMove", "SaveMusicTrackMove", "SaveReviewedParentFirstTvPairing"],
            [ApplicationPermissionIds.MetadataEnrichmentRun] = ["HydrateEntity", "RefreshScopedProviderArtwork", "DiscoverScopedProviderArtwork", "TriggerPass2"],
            [ApplicationPermissionIds.MetadataEnrichmentRead] = ["GetPass2Status"],
            [ApplicationPermissionIds.ReviewRead] = ["GetMediaEditorSelectionHistory"],
        };
        var metadata = inventory.Endpoints.Where(endpoint => endpoint.Pattern.StartsWith("/metadata/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(
            expected.Values.SelectMany(names => names).Append("GetConflicts").Order(),
            metadata.Select(endpoint => endpoint.Name ?? endpoint.Pattern).Order());
        foreach (var (permission, names) in expected)
        {
            foreach (var name in names)
            {
                Assert.Single(metadata, endpoint => endpoint.Name == name).RequiresAdministratorOrApplication(permission);
            }
        }
        Assert.Single(metadata, endpoint => endpoint.Name == "GetConflicts")
            .RequiresNamedPolicy(AuthPolicies.Administrator);

        inventory.Require("/ingestion/refresh-schedule", HttpMethods.Get).RequiresAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRead);
        inventory.Require("/ingestion/refresh-schedule/{entityType}/{entityId:guid}", HttpMethods.Get).RequiresAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRead);
        inventory.Require("/ingestion/refresh-schedule/{entityType}/{entityId:guid}/run-now", HttpMethods.Post).RequiresAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRun);
    }

    [Fact]
    public async Task ProductionEndpointInventory_RecordsAccessAndApplicationPolicies()
    {
        await using var app = BuildEndpointOnlyApplication();
        var inventory = EndpointInventory.From(app);

        inventory.Require("/access/accounts", HttpMethods.Get)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersRead);
        inventory.Require("/access/accounts/{accountId:guid}", HttpMethods.Put)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite);
        inventory.Require("/access/accounts/{accountId:guid}/access", HttpMethods.Put)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite);
        inventory.Require("/access/accounts/{accountId:guid}/grants/{profileId:guid}/admin-protection", HttpMethods.Put)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite);

        // Server administrators and household administrators (who may turn on their own PIN) use the unlock routes.
        inventory.Require("/access/admin-unlock", HttpMethods.Get)
            .RequiresEffectiveAdministratorOrHousehold(surfaceUnlock: false);
        inventory.Require("/access/admin-unlock", HttpMethods.Post)
            .RequiresEffectiveAdministratorOrHousehold(surfaceUnlock: false);
        inventory.Require("/access/self-service", HttpMethods.Get)
            .RequiresNamedPolicy(AuthPolicies.HumanSelfService);

        inventory.Require("/profiles/{id:guid}/overview", HttpMethods.Get)
            .RequiresNamedPolicy(AuthPolicies.HumanSelfService);

        inventory.Require("/access/applications", HttpMethods.Get)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityApplicationsWrite);
        inventory.Require("/access/applications/{applicationId:guid}/client-bindings", HttpMethods.Put)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityApplicationsWrite);
        inventory.Require("/access/applications/{applicationId:guid}/credentials/{credentialId:guid}/rotate", HttpMethods.Post)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityApplicationsWrite);

        inventory.Require("/auth/login", HttpMethods.Post)
            .RequiresNamedPolicy(AuthPolicies.DashboardService);
        inventory.Require("/auth/session/validate", HttpMethods.Post)
            .RequiresNamedPolicy(AuthPolicies.DashboardService);
        inventory.Require("/auth/sessions/others", HttpMethods.Delete)
            .RequiresNamedPolicy(AuthPolicies.HumanSelfService);

        inventory.Require("/system/activity-status", HttpMethods.Get)
            .RequiresHumanOrApplicationPermission(ApplicationPermissionIds.SystemActivityRead);

        inventory.Require("/system/backups/validate", HttpMethods.Post)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.BackupRead);
        inventory.Require("/system/backups/restore", HttpMethods.Post)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.BackupRestore);
    }

    [Fact]
    public async Task ProductionEndpointInventory_RecordsExplicitPublicExemptionAllowlist()
    {
        await using var app = BuildEndpointOnlyApplication();
        var inventory = EndpointInventory.From(app);

        inventory.Require("/system/status", HttpMethods.Get).AllowsAnonymous();
        inventory.Require("/api/v1/oauth/device_authorization", HttpMethods.Post).AllowsAnonymous();
        inventory.Require("/api/v1/oauth/token", HttpMethods.Post).AllowsAnonymous();
        inventory.Require("/stream/hls/{grant}/{packageId:guid}/{**resourcePath}", HttpMethods.Get).AllowsAnonymous();
        inventory.Require("/stream/hls/{grant}/{packageId:guid}/{**resourcePath}", HttpMethods.Head).AllowsAnonymous();
        inventory.Require("/intercom").AllowsAnonymous();
        inventory.Require("/intercom/negotiate").AllowsAnonymous();

        Assert.False(inventory.Require("/access/accounts", HttpMethods.Get).AllowsAnonymousEndpoint);
        Assert.False(inventory.Require("/access/applications", HttpMethods.Get).AllowsAnonymousEndpoint);
        Assert.False(inventory.Require("/auth/login", HttpMethods.Post).AllowsAnonymousEndpoint);

        Assert.Equal([
            "/api/v1/oauth/device_authorization",
            "/api/v1/oauth/token",
            "/intercom",
            "/intercom/negotiate",
            "/stream/hls/{grant}/{packageId:guid}/{**resourcePath}",
            "/system/status",
        ], inventory.AnonymousPatterns);
    }

    [Fact]
    public async Task ProductionEndpointInventory_RequiresExplicitAuthorizationOutsideReviewedTransportExemptions()
    {
        await using var app = BuildEndpointOnlyApplication();
        var inventory = EndpointInventory.From(app);

        Assert.DoesNotContain(inventory.Endpoints, endpoint =>
            !endpoint.AllowsAnonymousEndpoint && !endpoint.HasExplicitAuthorization);
    }

    private static WebApplication BuildEndpointOnlyApplication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.Services.AddSingleton<IConfigurationLoader>(_ => throw new InvalidOperationException("Endpoint inventory must not resolve runtime configuration."));
        builder.Services.AddSingleton<ProviderCredentialService>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not resolve provider credentials."));
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<IEventPublisher, SignalREventPublisher>();
        builder.Services.AddHttpClient();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<TvdbRetailClient>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not contact TheTVDB."));
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddTuvimaStorage();
        builder.Services.AddSingleton<IDatabaseConnection>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not open the database."));
        builder.Services.AddSingleton<StartupReadinessService>();
        builder.Services.AddSingleton<DatabaseBackupService>();
        builder.Services.AddSingleton<IFileWatcher, FileWatcher>();
        builder.Services.AddSingleton<IFileOrganizer, FileOrganizer>();
        builder.Services.AddSingleton<WritebackConfigState>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not resolve runtime ingestion state."));
        builder.Services.AddTuvimaPlayback();
        builder.Services.AddTuvimaNetworking();
        builder.Services.AddMediaEngineIngestion(builder.Configuration, null!, options =>
        {
            options.ConfigureOptions = false;
            options.CreateConfiguredDirectories = false;
            options.RegisterHostedService = false;
        });
        builder.Services.AddTuvimaDisplay();
        builder.Services.AddTuvimaIntelligence();
        builder.Services.AddSingleton<AiSettings>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not load AI configuration."));
        builder.Services.AddSingleton<AiConfigurationService>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not load mutable AI configuration."));
        builder.Services.AddSingleton<HardwareBenchmarkService>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not benchmark hardware."));
        builder.Services.AddSingleton<ResourceMonitorService>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not sample machine resources."));
        builder.Services.AddSingleton<ModelInventory>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not initialize the AI model inventory."));
        builder.Services.AddSingleton<IModelLifecycleManager, ModelLifecycleManager>();
        builder.Services.AddSingleton<IModelDownloadManager>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not resolve AI model downloads."));
        builder.Services.AddApiReadServices();
        builder.Services.AddTuvimaHostedServices();
        builder.Services.AddTuvimaPlugins();
        builder.Services.AddPluginApplicationServices();
        builder.Services.AddApplicationEvents();
        builder.Services.AddApplicationWebhooks();
        builder.Services.AddSingleton<AppleRetailClient>();
        builder.Services.AddSingleton<MusicBrainzReleaseClient>();
        builder.Services.AddSingleton<IIngestionOperationsStatusService, IngestionOperationsStatusService>();
        builder.Services.AddSingleton<IngestionNotificationReadService>();
        builder.Services.AddSingleton<IIngestionBatchResponseService, IngestionBatchResponseService>();
        builder.Services.AddSingleton<EnrichmentRefreshScheduleService>();
        builder.Services.AddSingleton<IHydrationPipelineService, SynchronousIdentityPipelineService>();
        builder.Services.AddSingleton<IImageEnrichmentService, ImageEnrichmentService>();
        builder.Services.AddSingleton<ImageEnrichmentService>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not download provider artwork."));
        builder.Services.AddSingleton<CoverArtWorker>();
        builder.Services.AddSingleton<LoreDeltaService>();
        builder.Services.AddSingleton<ILoreDeltaService>(provider => provider.GetRequiredService<LoreDeltaService>());
        builder.Services.AddSingleton<EraActorResolverService>();
        builder.Services.AddSingleton<IEraActorResolverService>(provider => provider.GetRequiredService<EraActorResolverService>());
        builder.Services.AddSingleton<IUniverseGraphQueryService, UniverseGraphQueryService>();
        builder.Services.AddSingleton<ICanonDiscrepancyService, CanonDiscrepancyService>();
        builder.Services.AddSingleton<IDeferredEnrichmentService, DeferredEnrichmentService>();
        builder.Services.AddSingleton<WikidataMatchPreviewService>();
        builder.Services.AddSingleton<TimelineRecorder>();
        builder.Services.AddSingleton<Tuvima.Wikidata.WikidataReconciler>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not resolve external reconciliation."));
        builder.Services.AddScoped<DashboardAuthorityProjector>();
        builder.Services.AddScoped<IRequestAuthorityResolver, RequestAuthorityResolver>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddScoped<IAccountAccessDecisionService, AccountAccessDecisionService>();
        builder.Services.AddScoped<ISelfServiceAuthorizationService>(provider =>
            (AccountAccessDecisionService)provider.GetRequiredService<IAccountAccessDecisionService>());
        builder.Services.AddSingleton<IAuthorizationAuditWriter, AuthorizationAuditWriter>();
        builder.Services.AddScoped<IGrantAdminUnlockService, GrantAdminUnlockService>();
        builder.Services.AddScoped<IAccountAccessMutationService, AccountAccessMutationService>();
        builder.Services.AddScoped<ApplicationAdministrationService>();
        builder.Services.AddSingleton<ClientAuthorizationService>();
        builder.Services.AddSingleton<AuthenticationPolicyMutationGate>();
        builder.Services.AddSingleton<AuthenticationProviderConfigurationService>();
        builder.Services.AddSingleton<ExternalIdentityTransactionService>();
        var app = builder.Build();
        // Use the real production mapper, including nested route groups and the
        // Intercom hub. Mapping is metadata-only: no handler or hosted service runs.
        app.MapEngineEndpoints();
        return app;
    }

    private sealed class EndpointInventory(IReadOnlyList<EndpointRecord> endpoints)
    {
        public IReadOnlyList<EndpointRecord> Endpoints => endpoints;
        public IReadOnlyList<string> AnonymousPatterns => endpoints
            .Where(endpoint => endpoint.AllowsAnonymousEndpoint)
            .Select(endpoint => endpoint.Pattern)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        public static EndpointInventory From(WebApplication app) => new(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(EndpointRecord.From)
                .ToArray());

        public EndpointRecord Require(string pattern, string method) => Assert.Single(endpoints, endpoint =>
            endpoint.Pattern == pattern && endpoint.Methods.Contains(method, StringComparer.OrdinalIgnoreCase));

        public EndpointRecord Require(string pattern) => Assert.Single(endpoints, endpoint => endpoint.Pattern == pattern);
    }

    private sealed record EndpointRecord(
        string? Name,
        string Pattern,
        IReadOnlyList<string> Methods,
        IReadOnlyList<string> NamedPolicies,
        IReadOnlyList<AuthorizationPolicy> Policies,
        bool AllowsAnonymousEndpoint)
    {
        public bool HasExplicitAuthorization => NamedPolicies.Count > 0 || Policies.Count > 0;

        public static EndpointRecord From(RouteEndpoint endpoint) => new(
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
            (endpoint.RoutePattern.RawText ?? string.Empty).TrimEnd('/'),
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
            endpoint.Metadata.OfType<IAuthorizeData>()
                .Select(data => data.Policy)
                .Where(policy => !string.IsNullOrWhiteSpace(policy))
                .Cast<string>()
                .ToArray(),
            endpoint.Metadata.OfType<AuthorizationPolicy>().ToArray(),
            endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null);

        public EndpointRecord RequiresNamedPolicy(string policy)
        {
            Assert.Contains(policy, NamedPolicies);
            return this;
        }

        public EndpointRecord RequiresAdministratorOrApplication(ApplicationPermissionId permission)
        {
            var policy = Assert.Single(Policies);
            var requirement = Assert.Single(policy.Requirements.OfType<AdministratorOrApplicationRequirement>());
            Assert.Equal(permission, requirement.Permission);
            return this;
        }

        public EndpointRecord RequiresEffectiveAdministratorOrHousehold(bool surfaceUnlock)
        {
            var policy = Assert.Single(Policies);
            var requirement = Assert.Single(policy.Requirements.OfType<EffectiveAdministratorRequirement>());
            Assert.Equal(surfaceUnlock, requirement.SurfaceUnlock);
            Assert.True(requirement.AllowHouseholdAdministrator);
            return this;
        }

        public EndpointRecord RequiresHumanOrApplicationPermission(ApplicationPermissionId permission)
        {
            var policy = Assert.Single(Policies);
            var requirement = Assert.Single(policy.Requirements.OfType<HumanOrApplicationPermissionRequirement>());
            Assert.Equal(permission, requirement.Permission);
            return this;
        }

        public EndpointRecord AllowsAnonymous()
        {
            Assert.True(AllowsAnonymousEndpoint);
            return this;
        }
    }
}
