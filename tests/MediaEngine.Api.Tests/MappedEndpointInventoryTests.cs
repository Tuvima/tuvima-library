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
            [ApplicationPermissionIds.ReviewRead] = ["GetPendingReviews", "GetReviewCount", "GetReviewItem"],
            [ApplicationPermissionIds.ReviewResolve] = ["ResolveReviewItem", "DismissReviewItem", "SkipUniverseMatch", "MoveReviewItemToTv"],
            [ApplicationPermissionIds.ProvidersStatusRead] = ["GetProviderHealth", "GetProviderIcon"],
            [ApplicationPermissionIds.ProvidersConfigRead] = ["GetProviderStatus", "GetProviderCatalogue", "GetStashBoxSettings"],
            [ApplicationPermissionIds.ProvidersConfigWrite] = ["UpdateProvider", "TestProviderCredentials", "SaveProviderCredentials", "RemoveProviderCredentials", "TestProvider", "SampleProvider", "UpdateProviderConfig", "DeleteProvider", "UploadProviderIcon", "AddStashBoxServer", "RemoveStashBoxServer", "SetStashBoxServerOrder", "SaveStashBoxServerKey", "RemoveStashBoxServerKey", "TestStashBoxConnection", "SetStashBoxFingerprintPermission"],
            [ApplicationPermissionIds.AiStatusRead] = ["GetAiStatus", "GetAiModelStatuses", "GetAiHardwareProfile", "GetAiResourceSnapshot"],
            [ApplicationPermissionIds.AiManage] = ["StartAiModelDownload", "CancelAiModelDownload", "LoadAiModel", "UnloadAiModel", "GetAiConfig", "SaveAiConfig", "RunAiHardwareBenchmark", "InvalidateAiHardwareBenchmark"],
            [ApplicationPermissionIds.NetworkStatusRead] = ["GetNetworkSettings", "GetNetworkRuntimeStatus"],
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

        string[] personal = ["GetNetworkAppAddresses", "GetUIDeviceProfile", "GetUIProfileSettings", "UpdateUIProfileSettings", "GetResolvedUISettings", "GetLibraryPreferences", "GetMediaTypes", "GetProfilePlaybackSettings", "UpdateProfilePlaybackSettings"];
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
            [ApplicationPermissionIds.IngestionStatusRead] = ["GetIngestionPresentation", "GetIngestionQueueProgress", "GetCurrentIngestionMediaGroups", "GetImportFolderWaiting", "ListWatchFolder", "ListMediaOperations", "GetMediaOperationsSummary"],
            [ApplicationPermissionIds.IngestionHistoryRead] = ["GetRecentIngestionAdditions", "GetIngestionMediaGroup", "GetIngestionMediaGroupChildren", "GetMediaOperation", "GetActivityHistorySummary"],
            [ApplicationPermissionIds.IngestionRun] = ["TriggerScan", "TriggerLibraryScan", "TriggerRescan", "TriggerReconciliation", "UploadMedia"],
            [ApplicationPermissionIds.IngestionRetry] = ["RereadAssetMetadata", "RetryMediaOperation"],
            [ApplicationPermissionIds.IngestionCancel] = ["CancelMediaOperation"],
            [ApplicationPermissionIds.SystemActivityRead] = ["GetRecentActivity", "GetActivityStats", "GetActivityByTypes", "GetActivityLog"],
        };
        string[] administratorOnly = ["PruneActivity"];
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
    public async Task MediaEditorContextRoute_DoesNotAdmitHouseholdAdministrators()
    {
        await using var app = BuildEndpointOnlyApplication();
        var inventory = EndpointInventory.From(app);

        var route = Assert.Single(
            inventory.Endpoints,
            endpoint => endpoint.Name == "GetMediaEditorContext");

        route.RequiresAdministratorOrApplication(ApplicationPermissionIds.MetadataRead);
        var requirement = Assert.Single(
            Assert.Single(route.Policies).Requirements.OfType<AdministratorOrApplicationRequirement>());
        Assert.False(requirement.AllowHouseholdAdministrator);
    }

    [Fact]
    public async Task MetadataEndpoints_SeparateReadMatchWriteAndEnrichmentPermissions()
    {
        await using var app = BuildEndpointOnlyApplication();
        var inventory = EndpointInventory.From(app);
        var expected = new Dictionary<ApplicationPermissionId, string[]>
        {
            [ApplicationPermissionIds.MetadataRead] = ["GetClaimHistory", "GetSeriesPlacement", "GetMediaEditorContext", "GetScopedArtworkEditor", "GetArtworkEditor", "GetArtworkWritebackStatus", "GetArtworkWritebackStatuses", "GetEditionRenditions", "GetSearchResultsCache", "GetCanonicalValues", "ResolveLabels", "GetWikidataAliases", "GetMediaEditorNavigator", "GetMediaEditorWorkVersions", "SearchMediaEditorOwnedChildren", "SnapshotMediaEditorOwnedChildSelection", "PreviewParentFirstMediaEditorPairing", "SearchReviewedTvPairingChildren", "GetMediaEditorFileCoverage", "PreviewMediaEditorMembershipChange", "GetCanonDiscrepancies", "GetTvdbMatchPreview", "GetTvdbScopedMatchCandidates", "PreviewTvdbShowOrder"],
            [ApplicationPermissionIds.MetadataWrite] = ["LockClaim", "SetSeriesPlacement", "OverrideMetadata", "ReclassifyMediaType", "UploadCover", "UploadScopedArtwork", "UploadScopedArtworkFromUrl", "UploadEntityArtwork", "SetPreferredArtwork", "DeleteArtworkVariant", "CoverFromUrl", "ApplyTvdbScopedMatch", "ApplyTvdbShowOrder", "ImportSelectedProviderArtwork", "RetryArtworkWriteback", "UpdateAssetRendition", "PreviewEditionCoverPreference", "SaveEditionCoverPreference", "SaveMediaEditorFileCoverage", "PreviewReviewedTvEpisodeStill", "PreviewReviewedTvSharedArtwork"],
            [ApplicationPermissionIds.MetadataMatch] = ["SearchMetadata", "SearchMetadataFanOut", "PutSearchResultsCache", "WikidataTest", "SaveReviewedParentFirstTvPairing"],
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

        // Households: anyone eligible may ask for the list (the service limits a household administrator to their own);
        // adding, renaming and removing are for server administrators and trusted applications only.
        inventory.Require("/access/households", HttpMethods.Get)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersRead, allowHouseholdAdministrator: true);
        inventory.Require("/access/households", HttpMethods.Post)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite, allowHouseholdAdministrator: false);
        inventory.Require("/access/households/{householdId:guid}", HttpMethods.Put)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite, allowHouseholdAdministrator: false);
        inventory.Require("/access/households/{householdId:guid}", HttpMethods.Delete)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite, allowHouseholdAdministrator: false);

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

        // The delivery history and Send test sit in the webhook group, so they need exactly what the other webhook actions need.
        inventory.Require("/access/applications/{applicationId:guid}/webhooks/{webhookId:guid}/attempts", HttpMethods.Get)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityApplicationsWrite);
        inventory.Require("/access/applications/{applicationId:guid}/webhooks/{webhookId:guid}/test", HttpMethods.Post)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.IdentityApplicationsWrite);

        inventory.Require("/auth/login", HttpMethods.Post)
            .RequiresNamedPolicy(AuthPolicies.DashboardService);
        inventory.Require("/auth/session/validate", HttpMethods.Post)
            .RequiresNamedPolicy(AuthPolicies.DashboardService);
        inventory.Require("/auth/sessions/others", HttpMethods.Delete)
            .RequiresNamedPolicy(AuthPolicies.HumanSelfService);

        inventory.Require("/system/activity-status", HttpMethods.Get)
            .RequiresHumanOrApplicationPermission(ApplicationPermissionIds.SystemActivityRead);

        inventory.Require("/system/scheduled-tasks", HttpMethods.Get)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.SystemStatusRead);

        inventory.Require("/system/resources", HttpMethods.Get)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.SystemStatusRead);

        inventory.Require("/system/data-store-version", HttpMethods.Get)
            .RequiresAdministratorOrApplication(ApplicationPermissionIds.BackupRead);

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
        // Cast receivers (a TV) cannot send a bearer token: each route is a secret link carrying its own signed grant, read-only.
        foreach (var method in new[] { HttpMethods.Get, HttpMethods.Head, HttpMethods.Options })
        {
            inventory.Require("/cast/v1/{grant}/media", method).AllowsAnonymous();
            inventory.Require("/cast/v1/{grant}/hls/{packageId:guid}/{**resourcePath}", method).AllowsAnonymous();
            inventory.Require("/cast/v1/{grant}/text/{trackId:guid}", method).AllowsAnonymous();
            inventory.Require("/cast/v1/{grant}/poster", method).AllowsAnonymous();
        }

        inventory.Require("/intercom").AllowsAnonymous();
        inventory.Require("/intercom/negotiate").AllowsAnonymous();

        Assert.False(inventory.Require("/access/accounts", HttpMethods.Get).AllowsAnonymousEndpoint);
        Assert.False(inventory.Require("/access/applications", HttpMethods.Get).AllowsAnonymousEndpoint);
        Assert.False(inventory.Require("/auth/login", HttpMethods.Post).AllowsAnonymousEndpoint);

        Assert.Equal([
            "/api/v1/oauth/device_authorization",
            "/api/v1/oauth/token",
            "/cast/v1/{grant}/hls/{packageId:guid}/{**resourcePath}",
            "/cast/v1/{grant}/media",
            "/cast/v1/{grant}/poster",
            "/cast/v1/{grant}/text/{trackId:guid}",
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

    [Fact]
    public async Task DeleteFromLibrary_HasOneInPersonAdministratorDeleteRoute_AndTheOldBatchDeleteIsGone()
    {
        await using var app = BuildEndpointOnlyApplication();
        var inventory = EndpointInventory.From(app);

        // The old batch delete (and its unsafe file removal) is gone for good; the preview-and-confirm pair replaces it.
        Assert.DoesNotContain(inventory.Endpoints, endpoint =>
            endpoint.Pattern.Equals("/library/items/batch/delete", StringComparison.OrdinalIgnoreCase));
        var delete = inventory.Require("/library/items/delete", "POST");
        Assert.Equal("DeleteLibraryItems", delete.Name);
        var policy = Assert.Single(delete.Policies);
        var requirement = Assert.Single(policy.Requirements.OfType<EffectiveAdministratorRequirement>());
        Assert.True(requirement.HumanOnly);
        Assert.True(requirement.SurfaceUnlock);
    }

    [Fact]
    public async Task LibraryItemMoves_AreAuthorisedLikeTheOtherStructuralEdits_AndTheOldRoutesAreGone()
    {
        await using var app = BuildEndpointOnlyApplication();
        var inventory = EndpointInventory.From(app);

        // Moving changes library structure, so each action needs the metadata write permission.
        foreach (var (pattern, method, name) in new[]
        {
            ("/library/items/{id:guid}/move-destinations", "GET", "GetLibraryItemMoveDestinations"),
            ("/library/items/move-preview", "POST", "PreviewLibraryItemMove"),
            ("/library/items/move", "POST", "MoveLibraryItems"),
        })
        {
            var route = inventory.Require(pattern, method);
            Assert.Equal(name, route.Name);
            route.RequiresAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite);
            Assert.False(route.AllowsAnonymousEndpoint);
        }

        // The old move actions are retired: a title moves only through the Match step. The membership preview stays because the
        // retail rematch asks for the hierarchy impact of a candidate with it.
        Assert.DoesNotContain(inventory.Endpoints, endpoint => endpoint.Name is "ApplyMediaEditorMembershipChange" or "GetMediaEditorMembershipSuggestions"
            or "PreviewMusicTrackMove" or "SaveMusicTrackMove");
        Assert.Contains(inventory.Endpoints, endpoint => endpoint.Name == "PreviewMediaEditorMembershipChange");
    }

    [Fact]
    public async Task PluginDirectory_IsReadWithPluginsRead_AndEveryActionIsForAdministratorsOnly()
    {
        await using var app = BuildEndpointOnlyApplication();
        var inventory = EndpointInventory.From(app);

        var read = inventory.Require("/plugins/directory", HttpMethods.Get);
        Assert.Equal("GetPluginDirectory", read.Name);
        read.RequiresAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        // Checking, marking seen, the switch and the install preview change what is remembered, switched or asked of
        // GitHub, so a signed-in administrator is needed; no application permission stands in.
        foreach (var (pattern, method, name) in new[]
        {
            ("/plugins/directory/check", HttpMethods.Post, "CheckPluginDirectory"),
            ("/plugins/directory/seen", HttpMethods.Post, "MarkPluginDirectorySeen"),
            ("/plugins/directory/settings", HttpMethods.Put, "SavePluginDirectorySettings"),
            ("/plugins/directory/{pluginId}/approval-preview", HttpMethods.Post, "PreviewPluginDirectoryInstall"),
        })
        {
            var route = inventory.Require(pattern, method);
            Assert.Equal(name, route.Name);
            route.RequiresNamedPolicy(AuthPolicies.Administrator);
            Assert.Empty(route.Policies);
        }

        Assert.DoesNotContain(inventory.Endpoints, endpoint => endpoint.Pattern == "/plugins/approved");
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
        builder.Services.AddSingleton<ProviderApiClient>(_ => throw new InvalidOperationException(
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
        builder.Services.AddSingleton<IVideoOnlineLookup>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not contact an online video lookup."));
        builder.Services.AddSingleton<IStashBoxSettings>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not read the Stash-box settings."));
        builder.Services.AddSingleton<MediaEngine.Providers.Services.StashBox.StashBoxClient>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not contact a stash-box server."));
        builder.Services.AddSingleton<IProviderAudienceGate>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not ask the provider audience gate."));
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
        builder.Services.AddSingleton<SystemResourcesService>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not sample machine resources."));
        builder.Services.AddSingleton<ModelInventory>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not initialize the AI model inventory."));
        builder.Services.AddSingleton<IModelLifecycleManager, ModelLifecycleManager>();
        builder.Services.AddSingleton<IModelDownloadManager>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not resolve AI model downloads."));
        builder.Services.AddApiReadServices();
        builder.Services.AddTuvimaHostedServices();
        builder.Services.AddTuvimaScheduledTasks();
        builder.Services.AddTuvimaToolRequirements();
        builder.Services.AddTuvimaPlugins();
        builder.Services.AddPluginApplicationServices();
        builder.Services.AddApplicationEvents();
        builder.Services.AddApplicationWebhooks();
        builder.Services.AddTuvimaApprovals();
        builder.Services.AddSingleton<AppleRetailClient>();
        builder.Services.AddSingleton<MusicBrainzReleaseClient>();
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
        builder.Services.AddSingleton<MediaEngine.Providers.Services.SeriesPlacementChoiceService>(_ => throw new InvalidOperationException(
            "Endpoint inventory must not resolve collection placement."));
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

        public EndpointRecord RequiresAdministratorOrApplication(ApplicationPermissionId permission, bool allowHouseholdAdministrator)
        {
            RequiresAdministratorOrApplication(permission);
            var requirement = Assert.Single(Policies[0].Requirements.OfType<AdministratorOrApplicationRequirement>());
            Assert.Equal(allowHouseholdAdministrator, requirement.AllowHouseholdAdministrator);
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
