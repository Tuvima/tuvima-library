using System.Net.Sockets;
using System.Text.Json;
using MediaEngine.Contracts.Ai;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Maintenance;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Progress;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Contracts.Reports;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain;
using MediaEngine.Web.Models.ViewDTOs;
using Microsoft.AspNetCore.SignalR.Client;

namespace MediaEngine.Web.Services.Integration;

public enum EngineConnectionState
{
    Unknown = 0,
    Checking = 1,
    Online = 2,
    Offline = 3,
    LiveUpdatesDisconnected = 4,
    Degraded = 5,
    Reconnecting = 6,
}

/// <summary>
/// Scoped orchestrator: the single bridge between <see cref="IEngineApiClient"/>,
/// the <see cref="UniverseStateContainer"/>, and the Engine API Intercom SignalR collection.
///
/// <para>
/// <b>Lifecycle:</b> one instance per Blazor Server circuit.  Components call
/// <see cref="StartSignalRAsync"/> during their first <c>OnInitializedAsync</c>
/// to activate the real-time channel for that circuit.
/// </para>
///
/// <para>
/// <b>SignalR events handled:</b>
/// <list type="bullet">
///   <item><c>"MediaAdded"</c>  -  invalidates the collection cache; next navigation triggers a fresh load.</item>
///   <item><c>"IngestionProgress"</c>  -  updates progress state in the container for live UI feedback.</item>
///   <item><c>"IngestionItemProgress"</c>  -  updates per-file stage progress for the ingestion dashboard.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Non-fatal connection failure:</b> if the API is offline when
/// <see cref="StartSignalRAsync"/> is called, the connection attempt is swallowed
/// and logged at Warning level.  The UI degrades gracefully to HTTP-only mode.
/// </para>
/// </summary>
public sealed class UIOrchestratorService : IAsyncDisposable
{
    private readonly IEngineApiClient _api;
    private readonly UniverseStateContainer _state;
    private readonly ActiveProfileSessionService _activeProfileSession;
    private readonly IConfiguration _config;
    private readonly DashboardIdentityClient? _identityClient;
    private readonly DashboardSessionAccessor? _dashboardSession;
    private readonly ILogger<UIOrchestratorService> _logger;

    private HubConnection? _hubConnection;
    private EngineConnectionState _engineConnectionState = EngineConnectionState.Unknown;

    public UIOrchestratorService(
        IEngineApiClient api,
        UniverseStateContainer state,
        ActiveProfileSessionService activeProfileSession,
        IConfiguration config,
        ILogger<UIOrchestratorService> logger,
        DashboardIdentityClient? identityClient = null,
        DashboardSessionAccessor? dashboardSession = null)
    {
        _api = api;
        _state = state;
        _activeProfileSession = activeProfileSession;
        _identityClient = identityClient;
        _dashboardSession = dashboardSession;
        _config = config;
        _logger = logger;
    }

    // -- Collections ------------------------------------------------------------------

    /// <summary>
    /// Returns the collection list, using the state container cache when available.
    /// Pass <paramref name="forceRefresh"/> = <see langword="true"/> to bypass the cache.
    /// </summary>
    public async Task<List<CollectionViewModel>> GetCollectionsAsync(
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        if (_state.IsLoaded && !forceRefresh)
        {
            return [.. _state.Collections];
        }

        var collections = await _api.GetCollectionsAsync(ct);
        _state.SetCollections(collections);   // also rebuilds UniverseViewModel via UniverseMapper
        return collections;
    }

    /// <summary>
    /// Returns the flattened <see cref="UniverseViewModel"/>, loading collection data
    /// from the API if not already cached.
    /// </summary>
    public async Task<UniverseViewModel> GetUniverseAsync(
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        // GetCollectionsAsync populates _state.Universe via UniverseMapper inside SetCollections.
        await GetCollectionsAsync(forceRefresh, ct);
        return _state.Universe ?? UniverseMapper.MapFromCollections([]);
    }

    // -- Library works --------------------------------------------------------

    public Task<List<WorkViewModel>> GetLibraryWorksAsync(CancellationToken ct = default)
        => _api.GetLibraryWorksAsync(ct: ct);

    public Task<WorkDetailViewModel?> GetWorkDetailAsync(Guid workId, CancellationToken ct = default)
        => _api.GetWorkDetailAsync(workId, ct);

    public Task<List<EditionViewModel>> GetWorkEditionsAsync(Guid workId, CancellationToken ct = default)
        => _api.GetWorkEditionsAsync(workId, ct);
    // -- System status ---------------------------------------------------------

    public async Task<SystemStatusViewModel?> GetSystemStatusAsync(CancellationToken ct = default)
    {
        SetEngineConnectionState(EngineConnectionState.Checking);
        var status = await _api.GetSystemStatusAsync(ct);
        if (status is not null)
        {
            _state.Language = status.Language;
            if (status.IsHealthy && !IsIntercomConnected)
            {
                await StartSignalRAsync(ct);
            }

            SetEngineConnectionState(status.IsHealthy
                ? (IsIntercomConnected ? EngineConnectionState.Online : EngineConnectionState.LiveUpdatesDisconnected)
                : EngineConnectionState.Degraded);
        }
        else
        {
            SetEngineConnectionState(EngineConnectionState.Offline);
        }
        return status;
    }

    public Task<AuthSettingsDto?> GetAuthSettingsAsync(CancellationToken ct = default)
        => _api.GetAuthSettingsAsync(ct);

    public Task<AuthSettingsDto?> UpdateAuthSettingsAsync(UpdateAuthSettingsRequest request, CancellationToken ct = default)
        => _api.UpdateAuthSettingsAsync(request, ct);
    public Task<AuthSettingsDto?> UpdateExternalAuthProviderAsync(string providerId, UpdateExternalAuthProviderRequest request, CancellationToken ct = default)
        => _api.UpdateExternalAuthProviderAsync(providerId, request, ct);
    public Task<bool> DeleteExternalAuthProviderAsync(string providerId, CancellationToken ct = default)
        => _api.DeleteExternalAuthProviderAsync(providerId, ct);

    // -- Ingestion -------------------------------------------------------------

    /// <summary>Triggers a dry-run scan and invalidates the collection cache on success.</summary>
    public async Task<ScanResponse?> ScanAndRefreshAsync(
        string? rootPath = null,
        CancellationToken ct = default)
    {
        var result = await _api.TriggerScanAsync(rootPath, ct);
        if (result is not null)
        {
            _state.Invalidate();
        }

        return result;
    }

    // -- Search ----------------------------------------------------------------

    /// <summary>
    /// Searches works across all collections.  Returns an empty list on failure or when
    /// the query is shorter than 2 characters (enforced server-side too).
    /// </summary>
    public Task<List<SearchResultDto>> SearchWorksAsync(
        string query,
        CancellationToken ct = default)
        => _api.SearchWorksAsync(query, ct);

    // -- Profile Management ------------------------------------------------------

    /// <summary>Lists all user profiles.</summary>
    public Task<List<ProfileViewModel>> GetProfilesAsync(CancellationToken ct = default)
        => _activeProfileSession.GetProfilesAsync(ct);

    /// <summary>
    /// Returns the browser/session-selected active profile, falling back to the seed Owner.
    /// </summary>
    public Task<ProfileViewModel?> GetActiveProfileAsync(CancellationToken ct = default) =>
        _activeProfileSession.GetActiveProfileAsync(ct);

    /// <summary>Persists the active browser/session profile and notifies layout consumers.</summary>
    public async Task<ProfileSwitchOutcome> SetActiveProfileAsync(
        Guid profileId,
        string? secret = null,
        CancellationToken ct = default)
    {
        var outcome = await _activeProfileSession.SetActiveProfileAsync(profileId, secret, ct);
        if (!outcome.Succeeded)
        {
            return outcome;
        }

        OnProfileChanged?.Invoke();
        return outcome;
    }
    /// <summary>Updates an existing user profile and notifies the layout to refresh.</summary>
    public async Task<bool> UpdateProfileAsync(
        Guid id, string displayName, string avatarColor,
        string? navigationConfig = null,
        CancellationToken ct = default)
    {
        var ok = await _api.UpdateProfileAsync(id, displayName, avatarColor, navigationConfig, ct);
        if (ok)
        {
            await _activeProfileSession.RefreshProfilesAsync(ct);
            OnProfileChanged?.Invoke();
        }
        return ok;
    }

    public Task<ViewProfilePolicyDto?> GetViewProfilePolicyAsync(
        Guid id,
        CancellationToken ct = default) =>
        _api.GetViewProfilePolicyAsync(id, ct);

    public Task<ViewProfilePolicyDto?> UpdateViewProfilePolicyAsync(
        Guid id,
        UpdateViewProfilePolicyRequest request,
        CancellationToken ct = default) =>
        _api.UpdateViewProfilePolicyAsync(id, request, ct);

    public Task<ViewPersonalSpaceAdminReviewDto?> GetViewProfileSourcesAsync(
        Guid id,
        CancellationToken ct = default) =>
        _api.GetViewProfileSourcesAsync(id, ct);

    public Task<ViewSourceAdminDto?> CreateViewProfileSourceAsync(
        Guid id, CreateViewSourceRequest request, CancellationToken ct = default) =>
        _api.CreateViewProfileSourceAsync(id, request, ct);

    public Task<ViewSourceAdminDto?> UpdateViewProfileSourceAsync(
        Guid id, Guid sourceId, UpdateViewSourceRequest request, CancellationToken ct = default) =>
        _api.UpdateViewProfileSourceAsync(id, sourceId, request, ct);

    public Task<bool> DeleteViewProfileSourceAsync(
        Guid id, Guid sourceId, CancellationToken ct = default) =>
        _api.DeleteViewProfileSourceAsync(id, sourceId, ct);

    public Task<LocalAssetScanResultDto?> ReconcileViewPersonalSpaceAsync(
        Guid id, CancellationToken ct = default) =>
        _api.ReconcileViewPersonalSpaceAsync(id, ct);

    public async Task<ProfileViewModel?> UploadProfileAvatarAsync(
        Guid id,
        Stream fileStream,
        string fileName,
        double zoom = 1,
        CancellationToken ct = default)
    {
        var profile = await _api.UploadProfileAvatarAsync(id, fileStream, fileName, zoom, ct);
        if (profile is not null)
        {
            _activeProfileSession.UpsertProfile(profile);
            OnProfileChanged?.Invoke();
        }
        return profile;
    }

    public async Task<ProfileViewModel?> RemoveProfileAvatarAsync(Guid id, CancellationToken ct = default)
    {
        var profile = await _api.RemoveProfileAvatarAsync(id, ct);
        if (profile is not null)
        {
            _activeProfileSession.UpsertProfile(profile);
            OnProfileChanged?.Invoke();
        }
        return profile;
    }
    public async Task<UserPlaybackSettingsDto?> GetPlaybackSettingsAsync(CancellationToken ct = default)
    {
        var profile = await GetActiveProfileAsync(ct);
        if (profile is null)
        {
            return null;
        }
        var result = await _api.GetPlaybackSettingsAsync(profile.Id, ct);
        return _activeProfileSession.CurrentProfile?.Id == profile.Id && result?.ProfileId == profile.Id ? result : null;
    }

    public Guid? ActivePlaybackProfileId => _activeProfileSession.CurrentProfile?.Id;

    public async Task<UserPlaybackSettingsDto?> SavePlaybackSettingsAsync(
        UserPlaybackSettingsDto settings,
        CancellationToken ct = default)
    {
        var targetProfileId = settings.ProfileId;
        var profile = await GetActiveProfileAsync(ct);
        if (profile is null || profile.Id != targetProfileId
            || _activeProfileSession.CurrentProfile?.Id != targetProfileId)
        {
            return null;
        }
        var result = await _api.UpdatePlaybackSettingsAsync(targetProfileId, settings, ct);
        return _activeProfileSession.CurrentProfile?.Id == targetProfileId && result?.ProfileId == targetProfileId ? result : null;
    }

    public Task<IReadOnlyList<PluginSummaryResponse>> GetPluginsAsync(CancellationToken ct = default) =>
        _api.GetPluginsAsync(ct);

    public Task<ApprovedPluginCatalogDto?> GetApprovedPluginCatalogAsync(CancellationToken ct = default) =>
        _api.GetApprovedPluginCatalogAsync(ct);

    public Task<bool> SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default) =>
        _api.SetPluginEnabledAsync(pluginId, enabled, ct);

    public Task<bool> SavePluginSettingsAsync(string pluginId, Dictionary<string, JsonElement> settings, CancellationToken ct = default) =>
        _api.SavePluginSettingsAsync(pluginId, settings, ct);

    public Task<string?> GetPluginManifestJsonAsync(string pluginId, CancellationToken ct = default) =>
        _api.GetPluginManifestJsonAsync(pluginId, ct);

    public Task<bool> SavePluginManifestJsonAsync(string pluginId, string json, CancellationToken ct = default) =>
        _api.SavePluginManifestJsonAsync(pluginId, json, ct);

    public Task<bool> DeletePluginAsync(string pluginId, CancellationToken ct = default) =>
        _api.DeletePluginAsync(pluginId, ct);

    public Task<PluginHealthResponse?> CheckPluginHealthAsync(string pluginId, CancellationToken ct = default) =>
        _api.CheckPluginHealthAsync(pluginId, ct);

    public Task<IReadOnlyList<OperationDto>> GetPluginJobsAsync(string pluginId, CancellationToken ct = default) =>
        _api.GetPluginJobsAsync(pluginId, ct);

    public Task<IReadOnlyList<PluginJobSnapshot>> RunPluginSegmentDetectionJobsAsync(CancellationToken ct = default) =>
        _api.RunPluginSegmentDetectionJobsAsync(ct);
    // -- Metadata Claims ---------------------------------------------------------

    /// <summary>Returns claim history for a given entity (Work or Edition).</summary>
    public Task<List<ClaimDto>> GetClaimHistoryAsync(
        Guid entityId, CancellationToken ct = default)
        => _api.GetClaimHistoryAsync(entityId, ct);

    /// <summary>Creates a user-locked claim and invalidates the collection cache.</summary>
    public async Task<bool> LockClaimAsync(
        Guid entityId, string key, string value,
        CancellationToken ct = default)
    {
        var ok = await _api.LockClaimAsync(entityId, key, value, ct);
        if (ok)
        {
            _state.Invalidate();
        }

        return ok;
    }

    // -- Settings --------------------------------------------------------------

    /// <summary>Returns the server name and regional settings.</summary>
    public Task<ServerGeneralSettingsDto?> GetServerGeneralAsync(CancellationToken ct = default)
        => _api.GetServerGeneralAsync(ct);

    /// <summary>Saves server name and regional settings.</summary>
    public Task<bool> UpdateServerGeneralAsync(ServerGeneralSettingsDto settings, CancellationToken ct = default)
        => _api.UpdateServerGeneralAsync(settings, ct);

    /// <summary>Returns complete schema 4 library, server-root, and incoming-source configuration.</summary>
    public Task<LibrariesConfigurationDto?> GetLibrariesAsync(CancellationToken ct = default)
        => _api.GetLibrariesAsync(ct);

    /// <summary>Replaces complete schema 4 library, server-root, and incoming-source configuration.</summary>
    public Task<LibrariesConfigurationDto?> UpdateLibrariesAsync(UpdateLibrariesRequest request, CancellationToken ct = default)
        => _api.UpdateLibrariesAsync(request, ct);

    /// <summary>Probes a directory path for existence, read, and write access.</summary>
    public Task<PathTestResultDto?> TestPathAsync(string path, CancellationToken ct = default)
        => _api.TestPathAsync(path, ct);

    /// <summary>Returns enabled state and live reachability for all registered metadata providers.</summary>
    public Task<IReadOnlyList<ProviderStatusDto>> GetProviderStatusAsync(CancellationToken ct = default)
        => _api.GetProviderStatusAsync(ct);

    /// <summary>Toggles a provider's enabled state in the Engine manifest.</summary>
    public Task<bool> UpdateProviderAsync(string name, bool enabled, CancellationToken ct = default)
        => _api.UpdateProviderAsync(name, enabled, ct);

    /// <summary>Tests a provider's connectivity by sending a real request.</summary>
    public Task<ProviderTestResultDto?> TestProviderAsync(string name, CancellationToken ct = default)
        => _api.TestProviderAsync(name, ct);

    public Task<ProviderCredentialOperationResultDto?> TestProviderCredentialsAsync(
        string name, ProviderCredentialWriteRequest request, CancellationToken ct = default)
        => _api.TestProviderCredentialsAsync(name, request, ct);

    public Task<ProviderCredentialOperationResultDto?> SaveProviderCredentialsAsync(
        string name, ProviderCredentialWriteRequest request, CancellationToken ct = default)
        => _api.SaveProviderCredentialsAsync(name, request, ct);

    public Task<ProviderCredentialOperationResultDto?> RemoveProviderCredentialsAsync(
        string name, CancellationToken ct = default)
        => _api.RemoveProviderCredentialsAsync(name, ct);

    /// <summary>Fetches sample claims from a provider for the property picker.</summary>
    public Task<ProviderSampleResultDto?> FetchProviderSampleAsync(
        string name, string title, string? author = null,
        string? isbn = null, string? asin = null, string? mediaType = null,
        CancellationToken ct = default)
        => _api.FetchProviderSampleAsync(name, title, author, isbn, asin, mediaType, ct);

    /// <summary>Saves a provider's full configuration.</summary>
    public Task<bool> SaveProviderConfigAsync(string name, ProviderConfigUpdateDto config, CancellationToken ct = default)
        => _api.SaveProviderConfigAsync(name, config, ct);

    /// <summary>Disables/deletes a provider.</summary>
    public Task<bool> DeleteProviderAsync(string name, CancellationToken ct = default)
        => _api.DeleteProviderAsync(name, ct);

    /// <summary>Most recent error detail from the last failed API call.</summary>
    public string? LastApiError => _api.LastError;

    public int? LastApiStatusCode => _api.LastStatusCode;

    public string? LastApiFailedEndpoint => _api.LastFailedEndpoint;

    public string? LastApiFailureKind => _api.LastFailureKind;

    // -- Activity Log (local in-memory) --------------------------------------

    /// <summary>Returns the current activity log (most recent first).</summary>
    public IReadOnlyList<ActivityEntry> GetActivityLog() => _state.ActivityLog;

    // -- System Activity (persistent Engine ledger) ---------------------------

    /// <summary>Returns the most recent system activity entries from the Engine's ledger.</summary>
    public Task<List<ActivityEntryResponse>> GetRecentActivityAsync(
        int limit = 50, CancellationToken ct = default)
        => _api.GetRecentActivityAsync(limit, ct);

    /// <summary>Returns total entries and retention setting from the Engine's activity ledger.</summary>
    public Task<ActivityStatsResponse?> GetActivityStatsAsync(CancellationToken ct = default)
        => _api.GetActivityStatsAsync(ct);

    /// <summary>Triggers a manual prune of activity entries older than the retention period.</summary>
    public Task<PruneResponse?> TriggerPruneAsync(CancellationToken ct = default)
        => _api.TriggerPruneAsync(ct);

    /// <summary>Updates the activity retention period in days.</summary>
    public Task<bool> UpdateRetentionAsync(int days, CancellationToken ct = default)
        => _api.UpdateRetentionAsync(days, ct);

    /// <summary>Returns activity entries filtered by action types for activity summaries.</summary>
    public Task<List<ActivityEntryResponse>> GetActivityByTypesAsync(
        string[] actionTypes, int limit = 50, CancellationToken ct = default)
        => _api.GetActivityByTypesAsync(actionTypes, limit, ct);

    /// <summary>Returns activity entries for a single ingestion run.</summary>
    public Task<List<ActivityEntryResponse>> GetActivityByRunIdAsync(
        Guid runId, CancellationToken ct = default)
        => _api.GetActivityByRunIdAsync(runId, ct);

    public Task<PagedResponse<ActivityBatchSummaryDto>?> GetActivityBatchesAsync(
        ActivityAuditQuery query, CancellationToken ct = default)
        => _api.GetActivityBatchesAsync(query, ct);

    public Task<ActivityBatchSummaryDto?> GetActivityBatchAsync(
        Guid batchId, CancellationToken ct = default)
        => _api.GetActivityBatchAsync(batchId, ct);

    public Task<List<ActivityMediaTypeGroupDto>> GetActivityBatchGroupsAsync(
        Guid batchId, CancellationToken ct = default)
        => _api.GetActivityBatchGroupsAsync(batchId, ct);

    public Task<ActivityBatchInsightsDto?> GetActivityBatchInsightsAsync(
        Guid batchId,
        CancellationToken ct = default)
        => _api.GetActivityBatchInsightsAsync(batchId, ct);

    public Task<PagedResponse<ActivityBatchItemDto>?> GetActivityBatchItemsAsync(
        Guid batchId,
        string? mediaType = null,
        string? search = null,
        string? status = null,
        string? source = null,
        int offset = 0,
        int limit = 25,
        string? sort = null,
        string? sortDirection = null,
        CancellationToken ct = default)
        => _api.GetActivityBatchItemsAsync(batchId, mediaType, search, status, source, offset, limit, sort, sortDirection, ct);

    public Task<PagedResponse<ActivityTechnicalEventDto>?> GetActivityBatchEventsAsync(
        Guid batchId,
        string? search = null,
        string? eventType = null,
        string? result = null,
        int offset = 0,
        int limit = 25,
        CancellationToken ct = default)
        => _api.GetActivityBatchEventsAsync(batchId, search, eventType, result, offset, limit, ct);

    public Task<ActivityBatchItemDetailDto?> GetActivityBatchItemDetailAsync(
        Guid batchId,
        Guid assetId,
        CancellationToken ct = default)
        => _api.GetActivityBatchItemDetailAsync(batchId, assetId, ct);

    public Task<PagedResponse<ActivityPersonAuditDto>?> GetActivityPeopleAsync(
        ActivityAuditQuery query,
        CancellationToken ct = default)
        => _api.GetActivityPeopleAsync(query, ct);

    /// <summary>Triggers a library reconciliation scan for missing files.</summary>
    public Task<ReconciliationResultResponse?> TriggerReconciliationAsync(CancellationToken ct = default)
        => _api.TriggerReconciliationAsync(ct);

    /// <summary>Fires when the activity log changes. Components should use InvokeAsync(StateHasChanged).</summary>
    public event Action? OnActivityChanged
    {
        add => _state.OnStateChanged += value;
        remove => _state.OnStateChanged -= value;
    }

    /// <summary>
    /// Fires when the Engine reports a folder health change via SignalR.
    /// Parameters: (path, isHealthy).
    /// Components should call <c>InvokeAsync(StateHasChanged)</c> in their handler.
    /// </summary>
    public event Action<string, bool>? OnFolderHealthChanged;

    /// <summary>
    /// Fires when the Engine reports that fictional entities in a universe have
    /// Wikidata revisions newer than the library's cached data.
    /// Parameters: (universeQid, changedCount).
    /// Components should call <c>InvokeAsync(StateHasChanged)</c> in their handler.
    /// </summary>
    public event Action<string, int>? OnLoreDeltaDiscovered;

    // -- Watch Folder ---------------------------------------------------------

    /// <summary>Returns files currently sitting in the Watch Folder.</summary>
    public Task<List<WatchFolderFileDto>> GetWatchFolderAsync(CancellationToken ct = default)
        => _api.GetWatchFolderAsync(ct);

    /// <summary>Triggers a re-scan of the Watch Folder, feeding all files into the pipeline.</summary>
    public Task<bool> TriggerRescanAsync(CancellationToken ct = default)
        => _api.TriggerRescanAsync(ct: ct);

    // -- Hydration ----------------------------------------------------------

    /// <summary>
    /// Triggers Wikidata SPARQL deep hydration for a given entity.
    /// Returns the hydration result, or null on failure.
    /// </summary>
    public async Task<HydrateResultViewModel?> TriggerHydrationAsync(
        Guid entityId, CancellationToken ct = default)
    {
        var result = await _api.TriggerHydrationAsync(entityId, ct);
        if (result is { Success: true })
        {
            _state.Invalidate();
        }

        return result;
    }

    // -- Review Queue ---------------------------------------------------------

    private int _reviewCount;

    /// <summary>Returns the cached pending review count. Call <see cref="RefreshReviewCountAsync"/> first.</summary>
    public int ReviewCount => _reviewCount;

    /// <summary>Fetches the pending review count from the Engine and caches it.</summary>
    public async Task<int> RefreshReviewCountAsync(CancellationToken ct = default)
    {
        await RefreshReviewCountSnapshotAsync(ct);
        return _reviewCount;
    }

    /// <summary>Returns pending review queue items.</summary>
    public Task<List<ReviewItemViewModel>> GetPendingReviewsAsync(
        int limit = 50, CancellationToken ct = default)
        => _api.GetPendingReviewsAsync(limit, ct);

    /// <summary>Returns a single review item.</summary>
    public Task<ReviewItemViewModel?> GetReviewItemAsync(
        Guid id, CancellationToken ct = default)
        => _api.GetReviewItemAsync(id, ct);

    /// <summary>Resolves a review item and invalidates the collection cache.</summary>
    public async Task<bool> ResolveReviewAsync(
        Guid id, ReviewResolveRequestDto request, CancellationToken ct = default)
    {
        var ok = await _api.ResolveReviewItemAsync(id, request, ct);
        if (ok)
        {
            await RefreshReviewCountSnapshotAsync(ct);
            _state.Invalidate();
        }
        return ok;
    }

    /// <summary>Permanently removes a work and all its files from the library.</summary>
    public async Task<bool> DeleteLibraryCatalogItemAsync(Guid entityId, CancellationToken ct = default)
    {
        return await _api.DeleteLibraryCatalogItemAsync(entityId, ct);
    }

    /// <summary>Dismisses a review item.</summary>
    public async Task<bool> DismissReviewAsync(Guid id, CancellationToken ct = default)
    {
        var ok = await _api.DismissReviewItemAsync(id, ct);
        if (ok)
        {
            await RefreshReviewCountSnapshotAsync(ct);
        }

        return ok;
    }

    /// <summary>Skips Universe matching for a review item and dismisses it.</summary>
    public async Task<bool> SkipUniverseAsync(Guid id, CancellationToken ct = default)
    {
        var ok = await _api.SkipUniverseAsync(id, ct);
        if (ok)
        {
            await RefreshReviewCountSnapshotAsync(ct);
            _state.Invalidate();
        }
        return ok;
    }

    /// <summary>Reclassifies a media asset to a different media type.</summary>
    public async Task<bool> ReclassifyMediaTypeAsync(
        Guid entityId, string mediaType, CancellationToken ct = default)
    {
        var ok = await _api.ReclassifyMediaTypeAsync(entityId, mediaType, ct);
        if (ok)
        {
            await RefreshReviewCountSnapshotAsync(ct);
            _state.Invalidate();
        }
        return ok;
    }

    private async Task RefreshReviewCountSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            var activeProfile = _activeProfileSession.CurrentProfile
                ?? await _activeProfileSession.GetActiveProfileAsync(ct);
            if (activeProfile is null || _dashboardSession?.HasNavigation("settings.administration") != true)
            {
                _reviewCount = 0;
                OnReviewCountChanged?.Invoke();
                return;
            }

            _reviewCount = await _api.GetReviewCountAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh review count snapshot");
        }

        OnReviewCountChanged?.Invoke();
    }

    /// <summary>Fires when the review count changes (from SignalR events or explicit actions).</summary>
    public event Action? OnReviewCountChanged;

    /// <summary>Fires when the active profile is updated (display name or avatar colour).</summary>
    public event Action? OnProfileChanged;

    /// <summary>Fires on every re-tag sweep progress tick broadcast by the Engine.</summary>
    public event Action<RetagSweepProgressEvent>? OnRetagSweepProgress;

    /// <summary>Fires once per sweep pass after the final progress event.</summary>
    public event Action<RetagSweepCompletedEvent>? OnRetagSweepCompleted;

    /// <summary>Fires on initial sweep progress ticks (plan section M).</summary>
    public event Action<InitialSweepProgressEvent>? OnInitialSweepProgress;

    /// <summary>Fires when the initial sweep finishes.</summary>
    public event Action<InitialSweepCompletedEvent>? OnInitialSweepCompleted;

    // -- Metadata Override ------------------------------------------------

    /// <summary>Overrides metadata fields for an entity and invalidates the collection cache.</summary>
    public async Task<bool> SaveItemPreferencesAsync(
        Guid entityId, Dictionary<string, string> fields, CancellationToken ct = default)
    {
        var ok = await _api.SaveItemPreferencesAsync(entityId, fields, ct);
        if (ok)
        {
            _state.Invalidate();
        }

        return ok;
    }

    // -- Hydration Settings ------------------------------------------------

    /// <summary>Returns hydration pipeline settings.</summary>
    public Task<HydrationSettingsDto?> GetHydrationSettingsAsync(CancellationToken ct = default)
        => _api.GetHydrationSettingsAsync(ct);

    /// <summary>Saves hydration pipeline settings.</summary>
    public Task<bool> UpdateHydrationSettingsAsync(
        HydrationSettingsDto settings, CancellationToken ct = default)
        => _api.UpdateHydrationSettingsAsync(settings, ct);

    // -- Pass 2 (Universe Lookup) ------------------------------------------

    /// <summary>Returns the pending count and enabled state for the Pass 2 deferred enrichment queue.</summary>
    public Task<DeferredEnrichmentStatusResponse?> GetPass2StatusAsync(CancellationToken ct = default)
        => _api.GetPass2StatusAsync(ct);

    /// <summary>Triggers immediate Pass 2 (Universe Lookup) processing for all pending items.</summary>
    public Task<DeferredEnrichmentTriggerResponse?> TriggerPass2NowAsync(CancellationToken ct = default)
        => _api.TriggerPass2NowAsync(ct);

    // -- Retag Sweep (auto re-tag) -------------------------------------------

    /// <summary>Returns the current re-tag sweep state (pending diff + hashes).</summary>
    public Task<RetagSweepStateResponse?> GetRetagSweepStateAsync(CancellationToken ct = default)
        => _api.GetRetagSweepStateAsync(ct);

    /// <summary>Commits the staged pending diff so the sweep worker picks it up.</summary>
    public Task<bool> ApplyRetagSweepPendingAsync(CancellationToken ct = default)
        => _api.ApplyRetagSweepPendingAsync(ct);

    /// <summary>Wakes the sweep worker immediately for an out-of-band pass.</summary>
    public Task<bool> RunRetagSweepNowAsync(CancellationToken ct = default)
        => _api.RunRetagSweepNowAsync(ct);

    /// <summary>Re-queues a single asset after terminal writeback failure.</summary>
    public Task<bool> RetryRetagForAssetAsync(Guid assetId, CancellationToken ct = default)
        => _api.RetryRetagForAssetAsync(assetId, ct);

    // -- Initial Sweep (side-by-side-with-Plex plan section M) --------------------

    /// <summary>Triggers the fire-and-forget initial hash sweep.</summary>
    public Task<bool> RunInitialSweepAsync(CancellationToken ct = default)
        => _api.RunInitialSweepAsync(ct);

    // -- Provider slots ------------------------------------------------------

    // -- Pipelines -----------------------------------------------------------

    public Task<PipelineConfiguration?> GetPipelinesAsync(CancellationToken ct = default)
        => _api.GetPipelinesAsync(ct);

    public Task<PipelineConfiguration?> GetDefaultPipelinesAsync(CancellationToken ct = default)
        => _api.GetDefaultPipelinesAsync(ct);

    public Task<bool> SavePipelinesAsync(PipelineConfiguration pipelines, CancellationToken ct = default)
        => _api.SavePipelinesAsync(pipelines, ct);

    // -- Media types ---------------------------------------------------------

    /// <summary>Returns media type definitions from the Engine.</summary>
    public Task<MediaTypeConfigurationDto?> GetMediaTypesAsync(CancellationToken ct = default)
        => _api.GetMediaTypesAsync(ct);

    /// <summary>Saves all media type definitions. Returns true on success.</summary>
    public Task<bool> SaveMediaTypesAsync(
        MediaTypeConfigurationDto config, CancellationToken ct = default)
        => _api.SaveMediaTypesAsync(config, ct);

    /// <summary>Adds a single custom media type. Returns updated config on success.</summary>
    public Task<MediaTypeConfigurationDto?> AddMediaTypeAsync(
        MediaTypeDefinitionDto newType, CancellationToken ct = default)
        => _api.AddMediaTypeAsync(newType, ct);

    /// <summary>Deletes a custom media type by key. Returns true on success.</summary>
    public Task<bool> DeleteMediaTypeAsync(string key, CancellationToken ct = default)
        => _api.DeleteMediaTypeAsync(key, ct);

    // -- Cover Art Upload -------------------------------------------------

    /// <summary>Uploads cover art for a media asset.</summary>
    public Task<bool> UploadCoverAsync(Guid entityId, Stream fileStream, string fileName, CancellationToken ct = default)
        => _api.UploadCoverAsync(entityId, fileStream, fileName, ct);

    // -- Provider Icons -----------------------------------------------------

    /// <summary>Uploads a custom icon for a provider.</summary>
    public Task<bool> UploadProviderIconAsync(string name, Stream fileStream, string fileName, CancellationToken ct = default)
        => _api.UploadProviderIconAsync(name, fileStream, fileName, ct);

    /// <summary>Returns the Engine URL path for a provider's icon.</summary>
    public string GetProviderIconUrl(string name) => _api.GetProviderIconUrl(name);

    // -- Metadata search -----------------------------------------------------

    /// <summary>Searches a specific provider for metadata results.</summary>
    public async Task<List<MetadataSearchResultDto>> SearchMetadataAsync(
        string providerName, string query, string? mediaType = null,
        int limit = 25, CancellationToken ct = default)
        => (await _api.SearchMetadataAsync(providerName, query, mediaType, limit, ct))?.Results ?? [];

    // -- Development Seed ------------------------------------------------

    /// <summary>Seeds the library with test EPUBs (dev only).</summary>
    public Task<bool> SeedLibraryAsync(CancellationToken ct = default)
        => _api.SeedLibraryAsync(ct);

    // -- Progress & Journey ------------------------------------------------

    /// <summary>Returns incomplete journey items for the "Continue your Journey" hero.
    /// Pass collectionId to get server-filtered results for a specific collection (no client-side matching needed).</summary>
    public Task<List<JourneyItemViewModel>> GetJourneyAsync(
        Guid? userId = null, int limit = 5, Guid? collectionId = null, CancellationToken ct = default)
        => _api.GetJourneyAsync(userId, limit, collectionId, ct);

    /// <summary>Retrieves the current progress state for a media asset.</summary>
    public Task<UserStateResponse?> GetProgressAsync(
        Guid assetId, CancellationToken ct = default)
        => _api.GetProgressAsync(assetId, ct);

    /// <summary>Saves progress for a media asset.</summary>
    public Task<bool> SaveProgressAsync(
        Guid assetId, Guid? userId = null, double progressPct = 0,
        Dictionary<string, string>? extendedProperties = null,
        CancellationToken ct = default)
        => _api.SaveProgressAsync(assetId, userId, progressPct, extendedProperties, ct);

    /// <summary>Resolves a work ID to its primary media asset ID for playback.</summary>
    public Task<Guid?> ResolveWorkToAssetAsync(
        Guid workId, CancellationToken ct = default)
        => _api.ResolveWorkToAssetAsync(workId, ct);

    // -- Persons ----------------------------------------------------------

    /// <summary>Returns persons as libraryItem list items (for People filter in LibraryItem Library view).</summary>
    public Task<IReadOnlyList<MediaEngine.Contracts.Persons.PersonListItemResponse>?> GetPersonsAsync(
        string? role = null, int limit = 200, CancellationToken ct = default)
        => _api.GetPersonsAsync(role: role, limit: limit, ct: ct);

    /// <summary>Returns all persons linked to works in a collection.</summary>
    public Task<List<PersonViewModel>> GetPersonsByCollectionAsync(
        Guid collectionId, CancellationToken ct = default)
        => _api.GetPersonsByCollectionAsync(collectionId, ct);

    /// <summary>Returns all persons linked to a specific work.</summary>
    public Task<List<PersonViewModel>> GetPersonsByWorkAsync(
        Guid workId, CancellationToken ct = default)
        => _api.GetPersonsByWorkAsync(workId, ct);

    /// <summary>Returns persons filtered by role (e.g. "Author") for batch headshot loading.</summary>
    public Task<List<PersonViewModel>> GetPersonsByRoleAsync(
        string role, int limit = 50, CancellationToken ct = default)
        => _api.GetPersonsByRoleAsync(role, limit, ct);

    /// <summary>Returns count of persons per role.</summary>
    public Task<Dictionary<string, int>> GetPersonRoleCountsAsync(CancellationToken ct = default)
        => _api.GetPersonRoleCountsAsync(ct);

    /// <summary>Returns media type presence counts per person.</summary>
    public Task<Dictionary<string, Dictionary<string, int>>> GetPersonPresenceAsync(
        IEnumerable<Guid> personIds, CancellationToken ct = default)
        => _api.GetPersonPresenceAsync(personIds, ct);

    /// <summary>Returns related collections (series/author/genre/explore cascade).</summary>
    public Task<RelatedCollectionsViewModel?> GetRelatedCollectionsAsync(
        Guid collectionId, int limit = 20, CancellationToken ct = default)
        => _api.GetRelatedCollectionsAsync(collectionId, limit, ct);

    /// <summary>Returns full person detail with social links.</summary>
    public Task<PersonDetailViewModel?> GetPersonDetailAsync(
        Guid personId, CancellationToken ct = default)
        => _api.GetPersonDetailAsync(personId, ct);

    /// <summary>Returns role-aware owned work credits for a person.</summary>
    public Task<List<PersonLibraryCreditViewModel>> GetPersonLibraryCreditsAsync(
        Guid personId, CancellationToken ct = default)
        => _api.GetPersonLibraryCreditsAsync(personId, ct);

    /// <summary>Returns work-scoped portrayed character credits for a person.</summary>
    public Task<IReadOnlyList<CharacterRoleDto>> GetPersonCharacterRolesAsync(
        Guid personId, CancellationToken ct = default)
        => _api.GetPersonCharacterRolesAsync(personId, ct);

    /// <summary>Returns all collections linked to works by a person.</summary>
    public Task<List<CollectionViewModel>> GetWorksByPersonAsync(
        Guid personId, CancellationToken ct = default)
        => _api.GetWorksByPersonAsync(personId, ct);

    /// <summary>Returns actor and character credits for a single work.</summary>
    public Task<List<CastCreditDto>> GetWorkCastAsync(
        Guid workId, CancellationToken ct = default)
        => _api.GetWorkCastAsync(workId, ct);

    // -- Parent Collection hierarchy --------------------------------------------------

    /// <summary>Returns all Parent Collections (franchise-level groupings).</summary>
    public Task<List<CollectionViewModel>> GetParentCollectionsAsync(CancellationToken ct = default)
        => _api.GetParentCollectionsAsync(ct);

    /// <summary>Returns child Collections of the given Parent Collection.</summary>
    public Task<List<CollectionViewModel>> GetChildCollectionsAsync(
        Guid parentCollectionId, CancellationToken ct = default)
        => _api.GetChildCollectionsAsync(parentCollectionId, ct);

    /// <summary>Returns the Parent Collection of the given Collection, if any.</summary>
    public Task<CollectionViewModel?> GetParentCollectionAsync(
        Guid collectionId, CancellationToken ct = default)
        => _api.GetParentCollectionAsync(collectionId, ct);

    // -- Conflicts ------------------------------------------------------------

    /// <summary>
    /// Returns all canonical values that have unresolved metadata conflicts.
    /// Spec: Phase B  -  Conflict Surfacing (B-05).
    /// </summary>
    public Task<List<ConflictViewModel>> GetConflictsAsync(CancellationToken ct = default)
        => _api.GetConflictsAsync(ct);

    // -- Organization Template -------------------------------------------------

    /// <summary>Gets the current file organization template and sample preview.</summary>
    public Task<OrganizationTemplateDto?> GetOrganizationTemplateAsync(CancellationToken ct = default)
        => _api.GetOrganizationTemplateAsync(ct);

    /// <summary>Saves a new file organization template. Returns the result with preview, or null on failure.</summary>
    public Task<OrganizationTemplateDto?> UpdateOrganizationTemplateAsync(string template, CancellationToken ct = default)
        => _api.UpdateOrganizationTemplateAsync(template, ct: ct);

    /// <summary>Saves default and per-media-type file organization templates.</summary>
    public Task<OrganizationTemplateDto?> UpdateOrganizationTemplateAsync(
        string template,
        Dictionary<string, string>? templates,
        CancellationToken ct = default)
        => _api.UpdateOrganizationTemplateAsync(template, templates, ct);

    // -- LibraryItem ------------------------------------------------------------

    /// <summary>Returns paginated libraryItem items.</summary>
    public Task<LibraryCatalogPageResponse?> GetLibraryCatalogItemsAsync(
        int offset = 0, int limit = 50,
        string? search = null, string? type = null, string? status = null,
        double? minConfidence = null, string? matchSource = null,
        bool? duplicatesOnly = null, bool? missingUniverseOnly = null,
        string? sort = null, int? maxDays = null,
        CancellationToken ct = default)
        => _api.GetLibraryCatalogItemsAsync(offset, limit, search, type, status, minConfidence, matchSource, duplicatesOnly, missingUniverseOnly, sort, maxDays, ct);

    /// <summary>Bulk-approves libraryItem items by entity ID.</summary>
    public Task<BatchLibraryItemResponse?> BatchApproveLibraryCatalogItemsAsync(Guid[] entityIds, CancellationToken ct = default)
        => _api.BatchApproveLibraryCatalogItemsAsync(entityIds, ct);

    /// <summary>Bulk-deletes libraryItem items by entity ID.</summary>
    public Task<BatchLibraryItemResponse?> BatchDeleteLibraryCatalogItemsAsync(Guid[] entityIds, CancellationToken ct = default)
        => _api.BatchDeleteLibraryCatalogItemsAsync(entityIds, ct);

    /// <summary>Rejects a single libraryItem item.</summary>
    public Task<BatchLibraryItemResponse?> RejectLibraryCatalogItemAsync(Guid entityId, CancellationToken ct = default)
        => _api.RejectLibraryCatalogItemAsync(entityId, ct);

    /// <summary>Bulk-rejects libraryItem items by entity ID.</summary>
    public Task<BatchLibraryItemResponse?> BatchRejectLibraryCatalogItemsAsync(Guid[] entityIds, CancellationToken ct = default)
        => _api.BatchRejectLibraryCatalogItemsAsync(entityIds, ct);

    /// <summary>Returns full detail for a single libraryItem item.</summary>
    public Task<LibraryItemDetailViewModel?> GetLibraryItemDetailAsync(Guid entityId, CancellationToken ct = default)
        => _api.GetLibraryItemDetailAsync(entityId, ct);

    /// <summary>Returns status counts for libraryItem tab badges.</summary>
    public Task<LibraryItemStatusCountsDto?> GetLibraryItemStatusCountsAsync(CancellationToken ct = default)
        => _api.GetLibraryItemStatusCountsAsync(ct);

    /// <summary>Returns four-state counts (Registered, NeedsReview, NoMatch, Failed) with trigger breakdown.</summary>
    public Task<LibraryItemLifecycleCountsDto?> GetLibraryItemLifecycleCountsAsync(
        Guid? batchId = null, CancellationToken ct = default)
        => _api.GetLibraryItemLifecycleCountsAsync(batchId, ct);

    /// <summary>Fetches recent ingestion batches from the Engine.</summary>
    public async Task<IReadOnlyList<IngestionBatchResponse>> GetIngestionBatchesAsync(int limit = 20)
        => await _api.GetIngestionBatchesAsync(limit);

    /// <summary>Fetches a single ingestion batch by ID.</summary>
    public async Task<IngestionBatchResponse?> GetIngestionBatchByIdAsync(Guid id)
        => await _api.GetIngestionBatchByIdAsync(id);

    /// <summary>Fetches the count of items needing curator attention across all batches.</summary>
    public async Task<int> GetBatchAttentionCountAsync()
        => await _api.GetBatchAttentionCountAsync();

    // -- Search ----------------------------------------------------------------

    /// <summary>POST /search/universe  -  Wikidata candidate search enriched with retail cover art.</summary>
    public async Task<List<UniverseCandidateDto>> SearchUniverseAsync(
        string query, string mediaType, int maxCandidates = 5,
        string? localAuthor = null, CancellationToken ct = default)
    {
        var result = await _api.SearchUniverseAsync(query, mediaType, maxCandidates, localAuthor, ct);
        return result?.Candidates ?? [];
    }

    /// <summary>POST /search/retail  -  retail provider candidate search with optional file hints for description scoring.</summary>
    public async Task<List<SearchRetailCandidateDto>> SearchRetailAsync(
        string query, string mediaType, int maxCandidates = 5,
        string? localTitle = null, string? localAuthor = null, string? localYear = null,
        Dictionary<string, string>? fileHints = null,
        Dictionary<string, string>? searchFields = null,
        CancellationToken ct = default)
    {
        var result = await _api.SearchRetailAsync(
            query, mediaType, maxCandidates,
            localTitle, localAuthor, localYear,
            fileHints, searchFields, ct);
        return result?.Candidates ?? [];
    }

    /// <summary>POST /search/resolve  -  unified resolve search with retail identification and description-based scoring.</summary>
    public async Task<List<SearchResolveCandidateDto>> SearchResolveAsync(
        string query, string mediaType, int maxCandidates = 5,
        Dictionary<string, string>? fileHints = null,
        CancellationToken ct = default)
    {
        var result = await _api.SearchResolveAsync(query, mediaType, maxCandidates, fileHints, ct);
        return result?.Candidates ?? [];
    }

    public Task<RetailCandidateDetailDto?> GetRetailCandidateDetailAsync(
        RetailCandidateDetailRequestDto request, CancellationToken ct = default)
        => _api.GetRetailCandidateDetailAsync(request, ct);

    /// <summary>
    /// GET /metadata/{qid}/aliases  -  fetches Wikidata aliases (alternative titles) for the given QID.
    /// If <paramref name="canonicalTitle"/> is provided and is not already in the aliases list,
    /// it is prepended so the canonical title is always the first/default choice.
    /// Returns an empty list when the Engine returns no data or an error occurs.
    /// </summary>
    public async Task<List<string>> GetAliasesAsync(
        string qid, string? canonicalTitle = null, CancellationToken ct = default)
    {
        var response = await _api.GetAliasesAsync(qid, ct);
        if (response is null)
        {
            return [];
        }

        var aliases = response.Aliases?.ToList() ?? [];

        if (!string.IsNullOrWhiteSpace(canonicalTitle) &&
            !aliases.Contains(canonicalTitle, StringComparer.OrdinalIgnoreCase))
        {
            aliases = [canonicalTitle, .. aliases];
        }

        return aliases;
    }

    /// <summary>POST /library/items/{entityId}/apply-match  -  apply a selected match.</summary>
    public async Task<ApplyMatchResponseDto?> ApplyLibraryItemMatchAsync(
        Guid entityId, ApplyMatchRequestDto request,
        CancellationToken ct = default)
    {
        var result = await _api.ApplyLibraryItemMatchAsync(entityId, request, ct);
        if (result is not null)
        {
            _state.Invalidate(); // refresh collection list since metadata changed
        }

        return result;
    }

    public async Task<ItemCanonicalSearchResponseDto?> SearchItemCanonicalAsync(
        Guid entityId, ItemCanonicalSearchRequestDto request, CancellationToken ct = default)
        => await _api.SearchItemCanonicalAsync(entityId, request, ct);

    public async Task<ItemCanonicalApplyResponseDto?> ApplyItemCanonicalAsync(
        Guid entityId, ItemCanonicalApplyRequestDto request, CancellationToken ct = default)
    {
        var result = await _api.ApplyItemCanonicalAsync(entityId, request, ct);
        if (result is not null)
        {
            _state.Invalidate();
        }

        return result;
    }

    /// <summary>POST /library/items/{entityId}/create-manual  -  create manual metadata entry.</summary>
    public Task<CreateManualResponseDto?> CreateManualEntryAsync(
        Guid entityId, CreateManualRequestDto request,
        CancellationToken ct = default)
        => _api.CreateManualEntryAsync(entityId, request, ct);

    /// <summary>Mark a libraryItem item as provisional with curator-entered metadata.</summary>
    public Task<bool> MarkProvisionalAsync(Guid entityId, ProvisionalMetadataRequestDto metadata, CancellationToken ct = default)
        => _api.MarkProvisionalAsync(entityId, metadata, ct);

    // -- SignalR Intercom -------------------------------------------------------

    /// <summary>
    /// Starts the SignalR connection to the Engine API Intercom collection at
    /// <c>{Engine:BaseUrl}/intercom</c>.
    ///
    /// <para>Idempotent  -  calling this multiple times is safe; the connection
    /// is only created and started once per circuit lifetime.</para>
    ///
    /// <para>Connection failure is non-fatal: the warning is logged and the
    /// UI continues in HTTP-only mode.</para>
    /// </summary>
    public async Task StartSignalRAsync(CancellationToken ct = default)
    {
        if (_hubConnection is not null)
        {
            if (_hubConnection.State is HubConnectionState.Connected
                or HubConnectionState.Connecting
                or HubConnectionState.Reconnecting)
            {
                return;
            }

            if (_hubConnection.State != HubConnectionState.Disconnected)
            {
                return;
            }

            await TryStartSignalRConnectionAsync(_hubConnection, null, ct);
            return;
        }

        var baseUrl = Environment.GetEnvironmentVariable("TUVIMA_ENGINE_URL")
            ?? _config["Engine:BaseUrl"]
            ?? "http://localhost:61495";
        var collectionUrl = $"{baseUrl.TrimEnd('/')}{SignalREvents.IntercomPath}";
        if (_identityClient is null)
        {
            _logger.LogDebug("Intercom is unavailable because the first-party identity client is not registered.");
            return;
        }

        var intercomToken = await _identityClient.GetIntercomTokenAsync(ct).ConfigureAwait(false);
        if (intercomToken is null)
        {
            _logger.LogWarning("Could not mint a short-lived Intercom token; real-time updates remain disabled.");
            SetEngineConnectionState(EngineConnectionState.LiveUpdatesDisconnected);
            return;
        }

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(collectionUrl, options =>
            {
                // The standard bearer-token hook is applied consistently to the
                // negotiate request and every selected SignalR transport.
                options.AccessTokenProvider = () => Task.FromResult<string?>(intercomToken.Token);
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
            })
            .Build();

        // -- "MediaAdded" ------------------------------------------------------
        // A new Work has been committed to the library.
        // Invalidate the cache so the grid refreshes on next render.
        _hubConnection.On<MediaAddedEvent>(SignalREvents.MediaAdded, ev =>
        {
            _logger.LogInformation(
                "Intercom ? MediaAdded: WorkId={WorkId} Title=\"{Title}\" Type={MediaType}",
                ev.WorkId, ev.Title, ev.MediaType);
            _state.PushMediaAdded(ev);
        });

        // -- "IngestionCompleted" ------------------------------------------------
        // A file has been fully ingested (hashed, scored, and optionally moved).
        // The Engine publishes this event; invalidate the cache so the grid refreshes.
        _hubConnection.On<IngestionCompletedEvent>(SignalREvents.IngestionCompleted, ev =>
        {
            _logger.LogInformation(
                "Intercom ? IngestionCompleted: File=\"{File}\" Type={MediaType}",
                ev.FilePath, ev.MediaType);
            _state.PushIngestionCompleted(ev);
        });

        // -- "IngestionProgress" -----------------------------------------------
        // Active ingestion tick  -  update the progress indicator.
        _hubConnection.On<IngestionProgressEvent>(SignalREvents.IngestionProgress, ev =>
        {
            _logger.LogDebug(
                "Intercom ? IngestionProgress: [{Stage}] {Done}/{Total}  -  {File}",
                ev.Stage, ev.ProcessedCount, ev.TotalCount, ev.CurrentFile);
            _state.PushIngestionProgress(ev);
        });

        // -- "IngestionItemProgress" ------------------------------------------
        // Per-file lifecycle tick, emitted for stages such as detected, hashing,
        // processed, scored, registered, and queued_identity.
        _hubConnection.On<IngestionItemProgressEvent>(SignalREvents.IngestionItemProgress, ev =>
        {
            _logger.LogDebug(
                "Intercom ? IngestionItemProgress: [{Stage}] {Percent}% - {File}",
                ev.Stage, ev.ProgressPercent, ev.FileName);
            _state.PushIngestionItemProgress(ev);
        });

        // -- "BatchProgress" -------------------------------------------------
        // Per-file progress tick during an ingestion batch  -  carries running
        // counters and estimated time remaining for the active batch card.
        _hubConnection.On<BatchProgressEvent>(SignalREvents.BatchProgress, ev =>
        {
            _logger.LogDebug(
                "Intercom ? BatchProgress: {Done}/{Total} ({Pct}%) ~{Eta}s remaining",
                ev.FilesProcessed, ev.FilesTotal, ev.ProgressPercent, ev.EstimatedSecondsRemaining);
            _state.PushBatchProgress(ev);
        });

        _hubConnection.On<ProviderActivityEvent>(SignalREvents.ProviderActivity, ev =>
        {
            _logger.LogDebug(
                "Intercom ? ProviderActivity: {ProviderCount} providers at {CapturedAt}",
                ev.Providers.Count, ev.CapturedAt);
            _state.PushProviderActivity(ev);
        });

        // -- "UniverseEnrichmentProgress" ------------------------------------
        // Live Stage 3 progress for inline or maintenance universe enrichment.
        _hubConnection.On<UniverseEnrichmentProgressEvent>(SignalREvents.UniverseEnrichmentProgress, ev =>
        {
            _logger.LogDebug(
                "Intercom ? UniverseEnrichmentProgress: {Step} {Done}/{Total}  -  {Title} ({Qid})",
                ev.CurrentStep, ev.ProcessedCount, ev.TotalCount, ev.WorkTitle, ev.WorkQid);
            _state.PushUniverseEnrichmentProgress(ev);
        });

        _hubConnection.On<ModelDownloadProgressEvent>(SignalREvents.ModelDownloadProgress, ev =>
        {
            _logger.LogDebug(
                "Intercom - ModelDownloadProgress: {Role} {Percent}%",
                ev.Role, ev.Percent);
            _state.PushModelDownloadProgress(ev);
        });

        _hubConnection.On<ModelStateChangedEvent>(SignalREvents.ModelStateChanged, ev =>
        {
            _logger.LogDebug(
                "Intercom - ModelStateChanged: {Role} {OldState} -> {NewState}",
                ev.Role, ev.OldState, ev.NewState);
            _state.PushModelStateChanged(ev);
        });

        _hubConnection.On<MediaOperationChangedEvent>(SignalREvents.MediaOperationChanged, ev =>
        {
            _logger.LogDebug(
                "Intercom - MediaOperationChanged: {OperationType} {Status} {Progress}%",
                ev.OperationType, ev.Status, ev.ProgressPercent);
            _state.PushMediaOperationChanged(ev);
        });

        // -- "RetagSweepProgress" ---------------------------------------------
        // Periodic progress tick while the auto re-tag sweep is running.
        _hubConnection.On<RetagSweepProgressEvent>(SignalREvents.RetagSweepProgress, ev =>
        {
            _logger.LogDebug(
                "Intercom ? RetagSweepProgress: {Processed} processed ({Ok} ok, {Retry} retry, {Fail} failed){Final}",
                ev.Processed, ev.Succeeded, ev.Transient, ev.Terminal, ev.IsFinal ? " [final]" : string.Empty);
            OnRetagSweepProgress?.Invoke(ev);
        });

        // -- "RetagSweepCompleted" --------------------------------------------
        _hubConnection.On<RetagSweepCompletedEvent>(SignalREvents.RetagSweepCompleted, ev =>
        {
            _logger.LogInformation(
                "Intercom ? RetagSweepCompleted: {Processed} processed, {Ok} ok, {Retry} retry, {Fail} failed",
                ev.Processed, ev.Succeeded, ev.Transient, ev.Terminal);
            OnRetagSweepCompleted?.Invoke(ev);
        });

        // -- "InitialSweepProgress" --------------------------------------------
        _hubConnection.On<InitialSweepProgressEvent>(SignalREvents.InitialSweepProgress, ev =>
        {
            _logger.LogDebug(
                "Intercom ? InitialSweepProgress: {Processed}/{Discovered} ({Hashed} hashed, {Cached} cached)",
                ev.Processed, ev.Discovered, ev.Hashed, ev.Cached);
            OnInitialSweepProgress?.Invoke(ev);
        });

        // -- "InitialSweepCompleted" -------------------------------------------
        _hubConnection.On<InitialSweepCompletedEvent>(SignalREvents.InitialSweepCompleted, ev =>
        {
            _logger.LogInformation(
                "Intercom ? InitialSweepCompleted: {Discovered} discovered, {Hashed} hashed, {Cached} cached, {Failed} failed",
                ev.Discovered, ev.Hashed, ev.Cached, ev.Failed);
            OnInitialSweepCompleted?.Invoke(ev);
        });

        // -- "MetadataHarvested" -----------------------------------------------
        // An external provider updated cover art / description / narrator etc.
        // Invalidate the state cache so cards re-render with the new data.
        _hubConnection.On<MetadataHarvestedEvent>(SignalREvents.MetadataHarvested, ev =>
        {
            _logger.LogDebug(
                "Intercom ? MetadataHarvested: EntityId={Id} Provider={Provider} Fields=[{Fields}]",
                ev.EntityId, ev.ProviderName, string.Join(",", ev.UpdatedFields));
            _state.PushMetadataHarvested(ev);
        });

        // -- "PersonEnriched" --------------------------------------------------
        // Wikidata has enriched an author/narrator with a headshot + biography.
        _hubConnection.On<PersonEnrichedEvent>(SignalREvents.PersonEnriched, ev =>
        {
            _logger.LogDebug(
                "Intercom ? PersonEnriched: PersonId={Id} Name={Name}",
                ev.PersonId, ev.Name);
            _state.PushPersonEnriched(ev);
        });

        // -- "MediaRemoved" ----------------------------------------------------
        // A file was removed (orphaned during ingestion scan or reconciliation).
        // Invalidate the collection cache so the home page refreshes on next render.
        _hubConnection.On<LibraryChangedEvent>(SignalREvents.MediaRemoved, _ =>
        {
            _logger.LogInformation("Intercom ? MediaRemoved: invalidating collection cache");
            _state.Invalidate();
        });

        // -- "WatchFolderActive" -----------------------------------------------
        // The Watch Folder has been updated; notify state container so interested
        // components (e.g. Settings page connection indicator) can react.
        _hubConnection.On<WatchFolderActiveEvent>(SignalREvents.WatchFolderActive, ev =>
        {
            _logger.LogInformation(
                "Intercom ? WatchFolderActive: Dir={Dir} At={At}",
                ev.WatchDirectory, ev.ActivatedAt);
            _state.PushWatchFolderActive(ev);
        });

        // -- "ReviewItemCreated" ----------------------------------------------
        // A new review item was created by the hydration pipeline.
        _hubConnection.On<ReviewItemCreatedEvent>(SignalREvents.ReviewItemCreated, ev =>
        {
            _logger.LogDebug(
                "Intercom ? ReviewItemCreated: ReviewId={Id} EntityId={EntityId} Trigger={Trigger}",
                ev.ReviewItemId, ev.EntityId, ev.Trigger);
            _ = RefreshReviewCountSnapshotAsync();
        });

        // -- "ReviewItemResolved" ---------------------------------------------
        // A review item was resolved or dismissed.
        _hubConnection.On<ReviewItemResolvedEvent>(SignalREvents.ReviewItemResolved, ev =>
        {
            _logger.LogDebug(
                "Intercom ? ReviewItemResolved: ReviewId={Id} Status={Status}",
                ev.ReviewItemId, ev.Status);
            _ = RefreshReviewCountSnapshotAsync();
            _state.Invalidate();
        });

        // -- "HydrationStageCompleted" ----------------------------------------
        // A pipeline stage completed  -  metadata may have changed.
        _hubConnection.On<HydrationStageCompletedEvent>(SignalREvents.HydrationStageCompleted, ev =>
        {
            _logger.LogDebug(
                "Intercom ? HydrationStageCompleted: EntityId={Id} Stage={Stage} Claims={Claims}",
                ev.EntityId, ev.Stage, ev.ClaimsAdded);
            _state.Invalidate();
        });

        // -- "FolderHealthChanged" -------------------------------------------
        // Periodic health check reports whether Watch/Library folders are accessible.
        // LibrariesTab subscribes to OnFolderHealthChanged to update status dots.
        _hubConnection.On<FolderHealthChangedEvent>(SignalREvents.FolderHealthChanged, ev =>
        {
            _logger.LogDebug(
                "Intercom ? FolderHealthChanged: Path={Path} Accessible={Ok}",
                ev.Path, ev.IsAccessible);

            // Determine folder type by comparing with current known paths.
            var healthy = ev.IsAccessible && ev.HasRead && ev.HasWrite;
            OnFolderHealthChanged?.Invoke(ev.Path, healthy);
        });

        // -- "LoreDeltaDiscovered" ---------------------------------------------
        // Stage 3 Lore Delta sweep found entities with updated Wikidata revisions.
        // Dashboard surfaces subscribe to OnLoreDeltaDiscovered to show an amber banner.
        _hubConnection.On<LoreDeltaDiscoveredEvent>(SignalREvents.LoreDeltaDiscovered, ev =>
        {
            _logger.LogDebug(
                "Intercom ? LoreDeltaDiscovered: Universe={Qid} Changed={Count}",
                ev.UniverseQid, ev.ChangedCount);

            OnLoreDeltaDiscovered?.Invoke(ev.UniverseQid, ev.ChangedCount);
        });

        // -- Connection lifecycle logging --------------------------------------
        _hubConnection.Reconnecting += ex =>
        {
            _logger.LogWarning("Intercom reconnecting: {Message}", ex?.Message);
            SetEngineConnectionState(EngineConnectionState.Reconnecting);
            return Task.CompletedTask;
        };
        _hubConnection.Reconnected += connectionId =>
        {
            _logger.LogInformation("Intercom reconnected (connectionId={Id})", connectionId);
            SetEngineConnectionState(EngineConnectionState.Online);
            return Task.CompletedTask;
        };
        _hubConnection.Closed += ex =>
        {
            _logger.LogWarning("Intercom closed: {Message}", ex?.Message);
            SetEngineConnectionState(EngineConnectionState.LiveUpdatesDisconnected);
            return Task.CompletedTask;
        };

        await TryStartSignalRConnectionAsync(_hubConnection, collectionUrl, ct);
    }

    private async Task TryStartSignalRConnectionAsync(HubConnection connection, string? collectionUrl, CancellationToken ct)
    {
        try
        {
            await connection.StartAsync(ct);
            _logger.LogInformation("Intercom connected ? {Url}", collectionUrl ?? connection.ConnectionId);
            SetEngineConnectionState(EngineConnectionState.Online);
            _state.PushServerStarted();
        }
        catch (Exception ex)
        {
            // Non-fatal: degrade gracefully to HTTP-only mode. A later call can retry.
            if (IsConnectionRefused(ex))
            {
                _logger.LogWarning(
                    "Could not connect to Intercom collection at {Url}; real-time updates are disabled until the Engine is reachable.",
                    collectionUrl ?? "configured Engine URL");
                _logger.LogDebug(ex, "Intercom connection refused.");
            }
            else
            {
                _logger.LogWarning(ex, "Could not connect to Intercom collection; real-time updates disabled.");
            }
            SetEngineConnectionState(EngineConnectionState.LiveUpdatesDisconnected);
        }
    }

    private static bool IsConnectionRefused(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the SignalR connection is currently established.
    /// Useful for rendering a live-indicator badge in the app bar.
    /// </summary>
    public bool IsIntercomConnected =>
        _hubConnection?.State == HubConnectionState.Connected;

    public EngineConnectionState EngineConnectionState => _engineConnectionState;

    public event Action<EngineConnectionState>? OnEngineConnectionStateChanged;

    private void SetEngineConnectionState(EngineConnectionState state)
    {
        if (_engineConnectionState == state)
        {
            return;
        }

        _engineConnectionState = state;
        OnEngineConnectionStateChanged?.Invoke(state);
    }

    // -- IAsyncDisposable ------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }
    }

    // Fan-out search

    public Task<FanOutSearchResponse?> SearchMetadataFanOutAsync(
        string query, string? mediaType = null, string? providerId = null,
        int maxResultsPerProvider = 5, CancellationToken ct = default)
        => _api.SearchMetadataFanOutAsync(query, mediaType, providerId, maxResultsPerProvider, ct);

    // -- Search results cache --------------------------------------------

    public Task<string?> GetSearchResultsCacheAsync(Guid entityId, CancellationToken ct = default)
        => _api.GetSearchResultsCacheAsync(entityId, ct);

    public Task SaveSearchResultsCacheAsync(Guid entityId, string resultsJson, CancellationToken ct = default)
        => _api.SaveSearchResultsCacheAsync(entityId, resultsJson, ct);


    // Canonical values

    public Task<List<CanonicalFieldDto>> GetCanonicalValuesAsync(
        Guid entityId, CancellationToken ct = default)
        => _api.GetCanonicalValuesAsync(entityId, ct);

    // Cover from URL

    public Task<bool> ApplyCoverFromUrlAsync(
        Guid entityId, string imageUrl, CancellationToken ct = default)
        => _api.ApplyCoverFromUrlAsync(entityId, imageUrl, ct);

    public Task<SubmitReportResponse?> SubmitReportAsync(SubmitReportRequest request, CancellationToken ct = default)
        => _api.SubmitReportAsync(request, ct);

    public Task<List<ReportEntryResponse>> GetReportsForEntityAsync(Guid entityId, CancellationToken ct = default)
        => _api.GetReportsForEntityAsync(entityId, ct);

    public Task<bool> ResolveReportAsync(long activityId, CancellationToken ct = default)
        => _api.ResolveReportAsync(activityId, ct);

    public Task<bool> DismissReportAsync(long activityId, CancellationToken ct = default)
        => _api.DismissReportAsync(activityId, ct);

    // -- AI Hardware Profile ---------------------------------------------------

    public Task<AiHealthStatusDto?> GetAiStatusAsync(CancellationToken ct = default)
        => _api.GetAiStatusAsync(ct);

    public Task<IReadOnlyList<AiModelStatusDto>> GetAiModelStatusesAsync(CancellationToken ct = default)
        => _api.GetAiModelStatusesAsync(ct);

    public Task<AiOperationResultDto> StartAiModelDownloadAsync(string role, CancellationToken ct = default)
        => _api.StartAiModelDownloadAsync(role, ct);

    public Task<AiOperationResultDto> CancelAiModelDownloadAsync(string role, CancellationToken ct = default)
        => _api.CancelAiModelDownloadAsync(role, ct);

    public Task<AiOperationResultDto> LoadAiModelAsync(string role, CancellationToken ct = default)
        => _api.LoadAiModelAsync(role, ct);

    public Task<AiOperationResultDto> UnloadAiModelAsync(string role, CancellationToken ct = default)
        => _api.UnloadAiModelAsync(role, ct);

    public Task<AiConfigDto?> GetAiConfigAsync(CancellationToken ct = default)
        => _api.GetAiConfigAsync(ct);

    public Task<bool> SaveAiConfigAsync(AiConfigDto config, CancellationToken ct = default)
        => _api.SaveAiConfigAsync(config, ct);

    public Task<HardwareProfileDto?> GetAiProfileAsync(CancellationToken ct = default)
        => _api.GetAiProfileAsync(ct);

    public Task<AiOperationResultDto<HardwareProfileDto>> RunBenchmarkAsync(CancellationToken ct = default)
        => _api.RunBenchmarkAsync(ct);

    public Task<AiOperationResultDto<HardwareProfileDto>> InvalidateBenchmarkAsync(CancellationToken ct = default)
        => _api.InvalidateBenchmarkAsync(ct);

    public Task<EnrichmentProgressDto?> GetEnrichmentProgressAsync(CancellationToken ct = default)
        => _api.GetEnrichmentProgressAsync(ct);

    public Task<ResourceSnapshotDto?> GetResourceSnapshotAsync(CancellationToken ct = default)
        => _api.GetResourceSnapshotAsync(ct);
}


