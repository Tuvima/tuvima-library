using System.Text.Json;
using MediaEngine.Contracts.Ai;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.System;
using MediaEngine.Contracts.WriteBack;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /settings/writeback/schedule: the time between file write runs, how many files are waiting or failed, a run's progress and the last and next run. Null when the Engine could not be asked.</summary>
    Task<FileWriteScheduleDto?> GetFileWriteScheduleAsync(CancellationToken ct = default);

    /// <summary>PUT /settings/writeback/schedule: saves the minutes between runs (1 to 1440) and returns the fresh status; null when it was not saved.</summary>
    Task<FileWriteScheduleDto?> UpdateFileWriteScheduleAsync(int intervalMinutes, CancellationToken ct = default);

    /// <summary>POST /settings/writeback/run: Write now. A refusal (a run is already going, write-back is off, not an administrator) comes back as a plain sentence, not an error.</summary>
    Task<FileWriteRunResult> StartFileWriteRunAsync(CancellationToken ct = default);

    /// <summary>GET /settings/writeback/failed: the files that could not be written, with why. Empty when none or the Engine could not be asked.</summary>
    Task<IReadOnlyList<FailedFileWriteDto>> GetFailedFileWritesAsync(CancellationToken ct = default);

    /// <summary>GET /metadata/assets/{assetId}/file-write: whether changes are waiting to be written into this file. Null when the Engine could not be asked.</summary>
    Task<AssetFileWriteStatusDto?> GetAssetFileWriteStatusAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>The Engine's last tools check, or <c>null</c> when the Engine could not be asked.</summary>
    Task<ToolRequirementsDto?> GetToolRequirementsAsync(CancellationToken ct = default);

    /// <summary>Asks the Engine to look for its tools again and returns the fresh result.</summary>
    Task<ToolRequirementsDto?> CheckToolRequirementsAsync(CancellationToken ct = default);

    /// <summary>The Settings attention lines the active profile dismissed, or <c>null</c> when the Engine could not be asked.</summary>
    Task<ProfileNoticesResponse?> GetSettingsNoticesAsync(CancellationToken ct = default);

    /// <summary>Remembers that the active profile dismissed a Settings attention line, with what was true at the time. False when the Engine did not save it.</summary>
    Task<bool> DismissSettingsNoticeAsync(string noticeKey, string fingerprint, CancellationToken ct = default);

    /// <summary>Saves the folder a person chose as a tool's home and returns the fresh tools check, or the Engine's plain reason it was refused.</summary>
    Task<ToolLocationResult> SetToolLocationAsync(string toolId, ToolLocationRequest request, CancellationToken ct = default);

    /// <summary>Starts downloading FFmpeg or Tesseract (Install for me, Download again) and returns the tools list with the download in progress, or the Engine's plain reason it was refused.</summary>
    Task<ToolActionResult> InstallToolAsync(string toolId, CancellationToken ct = default);

    /// <summary>Cancels a tool download in progress and returns the fresh tools list.</summary>
    Task<ToolActionResult> CancelToolInstallAsync(string toolId, CancellationToken ct = default);

    /// <summary>Starts Tesseract's downloaded setup program on the machine running the Engine and returns the tools list.</summary>
    Task<ToolActionResult> RunToolInstallerAsync(string toolId, CancellationToken ct = default);

    Task<IReadOnlyList<BackupArchiveDto>> GetBackupsAsync(CancellationToken ct = default);

    /// <summary>The latest data store upgrade step, or <c>null</c> when the Engine could not be asked.</summary>
    Task<DataStoreVersionDto?> GetDataStoreVersionAsync(CancellationToken ct = default);

    /// <summary>GET /system/resources — Engine version, uptime, processor, memory and disk space; <c>null</c> when the Engine could not be asked.</summary>
    Task<SystemResourcesDto?> GetSystemResourcesAsync(CancellationToken ct = default);

    Task<BackupArchiveDto?> CreateBackupAsync(CancellationToken ct = default);

    Task<byte[]?> DownloadBackupAsync(string fileName, CancellationToken ct = default);

    /// <summary>
    /// Runs a restore test. When the Engine refuses the file (an older layout, a damaged file) the result is not valid and its
    /// <c>Message</c> is the Engine's one sentence; <c>null</c> means the Engine could not be asked.
    /// </summary>
    Task<RestoreValidationResultDto?> ValidateRestoreAsync(string fileName, CancellationToken ct = default);

    /// <summary>GET /system/backups/state: counts for the Backup screen; <c>null</c> when the Engine could not be asked.</summary>
    Task<BackupStateDto?> GetBackupStateAsync(CancellationToken ct = default);

    /// <summary>PUT /system/backups/waiting-period: how many days restored progress waits for its files. Returns the new state, or <c>null</c> when it was not saved.</summary>
    Task<BackupStateDto?> SetBackupWaitingPeriodAsync(int days, CancellationToken ct = default);

    Task<ScheduleRestoreResultDto?> ScheduleRestoreAsync(string fileName, CancellationToken ct = default);

    Task<IReadOnlyList<PluginSummaryResponse>> GetPluginsAsync(CancellationToken ct = default);

    /// <summary>GET /plugins/directory — the saved plugin directory (Available plugins). The Engine checks GitHub first only when the switch is on and the last check is over a day old. Null when the Engine could not be asked.</summary>
    Task<PluginDirectoryResponse?> GetPluginDirectoryAsync(CancellationToken ct = default);

    /// <summary>GET /plugin-sources: every metadata source with what it supplies, and whether the Engine must restart to match the list.</summary>
    Task<PluginSourcesResponse?> GetPluginSourcesAsync(CancellationToken ct = default);

    /// <summary>GET /plugin-sources/{name}/switch-off-impact: what would have no source if this one were switched off. Null when unknown.</summary>
    Task<PluginSourceImpactDto?> GetPluginSourceSwitchOffImpactAsync(string name, CancellationToken ct = default);

    /// <summary>GET /plugin-sources/compare: two sources side by side. Null when either is unknown.</summary>
    Task<PluginSourceComparisonDto?> ComparePluginSourcesAsync(string from, string to, CancellationToken ct = default);

    /// <summary>POST /plugins/directory/check — Check now. Null when the Engine could not be asked.</summary>
    Task<PluginDirectoryResponse?> CheckPluginDirectoryAsync(CancellationToken ct = default);

    /// <summary>POST /plugins/directory/seen — remembers that the listed plugins have been seen. Null when the Engine could not be asked.</summary>
    Task<PluginDirectoryResponse?> MarkPluginDirectorySeenAsync(CancellationToken ct = default);

    /// <summary>PUT /plugins/directory/settings — the plugin directory switch. False when it could not be saved.</summary>
    Task<bool> SavePluginDirectoryEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>POST /plugins/directory/{id}/approval-preview — what to show before installing a listed plugin. Null when the Engine could not be asked or does not list it.</summary>
    Task<ApprovalPreviewResponse?> PreviewPluginDirectoryInstallAsync(string pluginId, CancellationToken ct = default);

    /// <summary>POST /plugins/directory/{id}/install — installs or updates a listed plugin. Carries the Engine's reason when it was refused.</summary>
    Task<PluginInstallOutcome> InstallDirectoryPluginAsync(string pluginId, CancellationToken ct = default);

    /// <summary>GET /plugins/{id}/removal-impact — what would have no source if the plugin were removed. Null when the Engine could not be asked.</summary>
    Task<PluginRemovalImpactResponse?> GetPluginRemovalImpactAsync(string pluginId, CancellationToken ct = default);

    /// <summary>
    /// POST /plugins/{id}/enable|disable. Switching on can answer with <see cref="PluginActionResult.Approval"/> (the Engine
    /// wants the person to see what the plugin can do first); repeat the call with that preview's hash.
    /// </summary>
    Task<PluginActionResult> SetPluginEnabledAsync(string pluginId, bool enabled, string? approvalHash = null, CancellationToken ct = default);

    /// <summary>GET /approvals — the recorded approvals for one item (kind and item id), newest first.</summary>
    Task<IReadOnlyList<ApprovalRecordResponse>> GetApprovalsAsync(string kind, string itemId, CancellationToken ct = default);

    Task<bool> SavePluginSettingsAsync(string pluginId, Dictionary<string, JsonElement> settings, CancellationToken ct = default);

    Task<string?> GetPluginManifestJsonAsync(string pluginId, CancellationToken ct = default);

    Task<bool> SavePluginManifestJsonAsync(string pluginId, string json, CancellationToken ct = default);

    Task<bool> DeletePluginAsync(string pluginId, CancellationToken ct = default);

    Task<PluginHealthResponse?> CheckPluginHealthAsync(string pluginId, CancellationToken ct = default);

    Task<IReadOnlyList<OperationDto>> GetPluginJobsAsync(string pluginId, CancellationToken ct = default);

    Task<IReadOnlyList<PluginJobSnapshot>> RunPluginSegmentDetectionJobsAsync(CancellationToken ct = default);

    /// <summary>GET /system/status — lightweight connectivity probe.</summary>
    Task<SystemStatusViewModel?> GetSystemStatusAsync(CancellationToken ct = default);

    /// <summary>GET /system/development-tools — which development-only Settings pages this Engine offers (administrator only). Null when the Engine could not be asked.</summary>
    Task<DevelopmentToolsResponse?> GetDevelopmentToolsAsync(CancellationToken ct = default);

    /// <summary>GET /system/readiness — structured launch-readiness report.</summary>
    Task<StartupReadinessResponse?> GetStartupReadinessAsync(CancellationToken ct = default)
        => Task.FromResult<StartupReadinessResponse?>(null);

    /// <summary>GET /system/readiness?category=storage — only the library and watch folder checks, without the data store scan.</summary>
    Task<StartupReadinessResponse?> GetStorageReadinessAsync(CancellationToken ct = default)
        => Task.FromResult<StartupReadinessResponse?>(null);

    /// <summary>GET /system/activity-status — sanitized active Engine operations for the global shell.</summary>
    Task<IReadOnlyList<SystemActivityOperationViewModel>> GetSystemActivityOperationsAsync(CancellationToken ct = default);

    /// <summary>GET /settings/security/auth - sign-in and SSO configuration.</summary>
    Task<AuthSettingsDto?> GetAuthSettingsAsync(CancellationToken ct = default);

    /// <summary>PUT /settings/security/auth - persist sign-in and session policy.</summary>
    Task<AuthSettingsDto?> UpdateAuthSettingsAsync(UpdateAuthSettingsRequest request, CancellationToken ct = default);
    Task<AuthSettingsDto?> UpdateExternalAuthProviderAsync(string providerId, UpdateExternalAuthProviderRequest request, CancellationToken ct = default);
    Task<bool> DeleteExternalAuthProviderAsync(string providerId, CancellationToken ct = default);

    // ── Profiles (/profiles) ────────────────────────────────────────────────────

    /// <summary>GET /profiles — list all user profiles.</summary>
    Task<List<ProfileViewModel>> GetProfilesAsync(CancellationToken ct = default);

    /// <summary>PUT /profiles/{id}/experience — update the active profile experience.</summary>
    Task<bool> UpdateProfileAsync(
        Guid id, string displayName, string avatarColor,
        string? navigationConfig = null,
        CancellationToken ct = default);

    /// <summary>GET /profiles/{id}/settings/view — read the administrator-managed View policy.</summary>
    Task<ViewProfilePolicyDto?> GetViewProfilePolicyAsync(Guid id, CancellationToken ct = default);

    /// <summary>PUT /profiles/{id}/settings/view — save independent View access and sharing controls.</summary>
    Task<ViewProfilePolicyDto?> UpdateViewProfilePolicyAsync(
        Guid id,
        UpdateViewProfilePolicyRequest request,
        CancellationToken ct = default);

    /// <summary>POST /profiles/{id}/avatar — upload a persisted profile avatar image.</summary>
    Task<ProfileViewModel?> UploadProfileAvatarAsync(
        Guid id,
        Stream fileStream,
        string fileName,
        double zoom = 1,
        CancellationToken ct = default);

    /// <summary>DELETE /profiles/{id}/avatar - remove a persisted profile avatar image.</summary>
    Task<ProfileViewModel?> RemoveProfileAvatarAsync(Guid id, CancellationToken ct = default);

    /// <summary>GET /profiles/{id}/overview - read user-facing profile details, history, and stats.</summary>
    Task<ProfileOverviewViewModel?> GetProfileOverviewAsync(Guid id, CancellationToken ct = default);

    // ── Metadata claims (/metadata) ─────────────────────────────────────────────

    /// <summary>GET /metadata/claims/{entityId} — claim history for a work/edition.</summary>
    Task<List<ClaimDto>> GetClaimHistoryAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>PATCH /metadata/lock-claim — create a user-locked claim.</summary>
    Task<bool> LockClaimAsync(Guid entityId, string key, string value, CancellationToken ct = default);

    // ── Settings (/settings) ──────────────────────────────────────────────────

    /// <summary>GET /settings/server-general — server name and regional settings.</summary>
    Task<ServerGeneralSettingsDto?> GetServerGeneralAsync(CancellationToken ct = default);

    /// <summary>PUT /settings/server-general — save server name and regional settings.</summary>
    Task<bool> UpdateServerGeneralAsync(ServerGeneralSettingsDto settings, CancellationToken ct = default);

    /// <summary>GET /settings/libraries — complete schema 4 library, storage-root, and incoming-source configuration.</summary>
    Task<LibrariesConfigurationDto?> GetLibrariesAsync(CancellationToken ct = default);
    Task<ViewStorageSummaryDto?> GetViewStorageSummaryAsync(CancellationToken ct = default);

    /// <summary>GET /settings/library-kinds — every library kind (type of library) in order, built-in and from switched-on plugins; null when the Engine could not be read.</summary>
    Task<IReadOnlyList<LibraryKindDto>?> GetLibraryKindsAsync(CancellationToken ct = default);
    Task<LibrariesConfigurationDto?> MutateLibraryAsync(LibraryMutationRequest request, CancellationToken ct = default);

    /// <summary>POST /library/unmatched/libraries/{libraryId}/retry — Retry now for one library's Unmatched files (administrators); the files affected, or null when the Engine refused or could not be read.</summary>
    Task<MediaEngine.Contracts.Library.UnmatchedActionResultDto?> RetryUnmatchedLibraryAsync(string libraryId, CancellationToken ct = default);

    /// <summary>POST /library/unmatched/libraries/{libraryId}/keep — Keep all Unmatched as they are for one library (administrators); the files affected, or null when the Engine refused or could not be read.</summary>
    Task<MediaEngine.Contracts.Library.UnmatchedActionResultDto?> KeepAllUnmatchedLibraryAsync(string libraryId, CancellationToken ct = default);

    /// <summary>GET /library/first-checks — the folders whose first check is running or paused (administrators); null when the Engine refused or could not be read.</summary>
    Task<MediaEngine.Contracts.Library.FolderFirstChecksDto?> GetFirstChecksAsync(CancellationToken ct = default);

    /// <summary>POST /library/first-checks/{sourceId}/keep-going — Keep going on a paused folder (administrators); true when the Engine accepted it, false when it refused (the folder is not being checked) or could not be read.</summary>
    Task<bool> KeepGoingAsync(string sourceId, CancellationToken ct = default);

    /// <summary>PUT /settings/libraries — replace schema 4 library, storage-root, and incoming-source configuration.</summary>
    Task<LibrariesConfigurationDto?> UpdateLibrariesAsync(UpdateLibrariesRequest request, CancellationToken ct = default);

    /// <summary>POST /settings/test-path — probe a directory for existence, read, and write access.</summary>
    Task<PathTestResultDto?> TestPathAsync(string path, CancellationToken ct = default);

    /// <summary>GET /settings/server-folders/roots — approved server/container roots.</summary>
    /// <summary>The server folder roots, or null when the Engine could not be reached (an empty list means there are none).</summary>
    Task<IReadOnlyList<ServerStorageLocationDto>?> GetServerFolderRootsAsync(CancellationToken ct = default);

    /// <summary>POST /settings/server-folders/browse — list folders beneath an approved root.</summary>
    Task<BrowseServerFoldersResultDto?> BrowseServerFoldersAsync(
        BrowseServerFoldersRequest request,
        CancellationToken ct = default);

    /// <summary>POST /settings/server-folders/validate — validate a server folder for a source mode.</summary>
    Task<ServerFolderValidationResultDto?> ValidateServerFolderAsync(
        ValidateServerFolderRequest request,
        CancellationToken ct = default);

    /// <summary>GET /providers/catalogue — consolidated UI metadata for all configured providers.</summary>
    Task<IReadOnlyList<ProviderCatalogueDto>> GetProviderCatalogueAsync(CancellationToken ct = default);

    /// <summary>GET /settings/providers — enabled state and live reachability for all providers.</summary>
    Task<IReadOnlyList<ProviderStatusDto>> GetProviderStatusAsync(CancellationToken ct = default);

    /// <summary>PUT /settings/providers/{name} — toggle a provider's enabled state.</summary>
    Task<bool> UpdateProviderAsync(string name, bool enabled, CancellationToken ct = default);

    /// <summary>GET /settings/providers/health — health status for all tracked providers.</summary>
    Task<List<ProviderHealthStatusResponse>> GetProviderHealthAsync(CancellationToken ct = default);

    /// <summary>POST /settings/providers/{name}/test — test a provider's connectivity.</summary>
    Task<ProviderTestResultDto?> TestProviderAsync(string name, CancellationToken ct = default);

    Task<ProviderCredentialOperationResultDto?> TestProviderCredentialsAsync(
        string name, ProviderCredentialWriteRequest request, CancellationToken ct = default);

    Task<ProviderCredentialOperationResultDto?> SaveProviderCredentialsAsync(
        string name, ProviderCredentialWriteRequest request, CancellationToken ct = default);

    Task<ProviderCredentialOperationResultDto?> RemoveProviderCredentialsAsync(
        string name, CancellationToken ct = default);

    /// <summary>POST /settings/providers/{name}/sample — fetch sample claims from a provider.</summary>
    Task<ProviderSampleResultDto?> FetchProviderSampleAsync(
        string name, string title, string? author = null,
        string? isbn = null, string? asin = null, string? mediaType = null,
        CancellationToken ct = default);

    /// <summary>PUT /settings/providers/{name}/config — save full provider configuration.</summary>
    Task<bool> SaveProviderConfigAsync(string name, ProviderConfigUpdateDto config, CancellationToken ct = default);

    /// <summary>DELETE /settings/providers/{name} — disable/delete a provider.</summary>
    Task<bool> DeleteProviderAsync(string name, CancellationToken ct = default);

    // ── Organization template ─────────────────────────────────────────────────

    /// <summary>GET /settings/organization-template — current file organization template + preview.</summary>
    Task<OrganizationTemplateDto?> GetOrganizationTemplateAsync(CancellationToken ct = default);
    Task<OrganizationTemplateDto?> PreviewOrganizationTemplateAsync(
        string template,
        Dictionary<string, string>? templates = null,
        CancellationToken ct = default);

    /// <summary>PUT /settings/organization-template — save a new file organization template.</summary>
    Task<OrganizationTemplateDto?> UpdateOrganizationTemplateAsync(
        string template,
        Dictionary<string, string>? templates = null,
        CancellationToken ct = default);

    // ── Activity log (/activity) ────────────────────────────────────────────────

    /// <summary>GET /activity/recent?limit= — most recent system activity entries.</summary>
    Task<List<ActivityEntryResponse>> GetRecentActivityAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>GET /activity/log?category=&amp;search=&amp;offset=&amp;limit= — the Events part of System &gt; Logs: one flat, newest-first, paged activity log (administrators only). Null when the Engine could not be asked.</summary>
    Task<PagedResponse<ActivityLogEntryDto>?> GetActivityLogAsync(string category, string? search, int offset, int limit, string? mediaType = null, CancellationToken ct = default);

    /// <summary>GET /activity/stats — total entries and retention setting.</summary>
    Task<ActivityStatsResponse?> GetActivityStatsAsync(CancellationToken ct = default);

    /// <summary>POST /activity/prune — manually prune old activity entries.</summary>
    Task<PruneResponse?> TriggerPruneAsync(CancellationToken ct = default);

    /// <summary>GET /logs/retention — the four Logs page limits plus how much is stored now. Null when the Engine could not be asked.</summary>
    Task<LogRetentionStatusDto?> GetLogRetentionAsync(CancellationToken ct = default);

    /// <summary>PUT /logs/retention — change the four Logs page limits. Null when the Engine refused or could not be asked.</summary>
    Task<LogRetentionStatusDto?> UpdateLogRetentionAsync(LogRetentionSettingsDto settings, CancellationToken ct = default);

    /// <summary>GET /logs/technical — the Engine's technical log, newest first. Null when the Engine could not be asked.</summary>
    Task<TechnicalLogPageDto?> GetTechnicalLogAsync(TechnicalLogRequest request, CancellationToken ct = default);

    /// <summary>POST /logs/export/preview — how many lines and how large the export would be. Null when the Engine refused or could not be asked.</summary>
    Task<LogExportPreviewDto?> PreviewLogExportAsync(LogExportRequest request, CancellationToken ct = default);

    /// <summary>POST /logs/export — the finished export file. Null when the Engine refused or could not be asked.</summary>
    Task<LogExportDownload?> ExportLogsAsync(LogExportRequest request, CancellationToken ct = default);

    /// <summary>GET /system/scheduled-tasks — every recurring Engine task with its last and next run. Null when the Engine could not be asked.</summary>
    Task<ScheduledTaskListResponse?> GetScheduledTasksAsync(CancellationToken ct = default);

    /// <summary>GET /activity/by-types?types=...&amp;limit= — entries filtered by action type for activity summaries.</summary>
    Task<List<ActivityEntryResponse>> GetActivityByTypesAsync(
        string[] actionTypes, int limit = 50, CancellationToken ct = default);

    // ── UI Settings (/settings/ui) ───────────────────────────────────────────────

    /// <summary>
    /// GET /settings/ui/resolved?device={class}&amp;profile={id} — fully cascaded UI settings
    /// for the given device class and optional profile.
    /// </summary>
    Task<ResolvedUISettingsViewModel?> GetResolvedUISettingsAsync(
        string deviceClass = "web",
        string? profileId = null,
        CancellationToken ct = default);

    Task<UIProfileSettingsDto?> GetUIProfileSettingsAsync(string profileId, CancellationToken ct = default);

    Task<UIProfileSettingsDto?> SaveUIProfileSettingsAsync(string profileId, UIProfileSettingsDto settings, CancellationToken ct = default);

    // ── Pipelines (/settings/pipelines) ─────────────────────────────────────

    /// <summary>GET /settings/pipelines — pipeline configuration per media type.</summary>
    Task<PipelineConfiguration?> GetPipelinesAsync(CancellationToken ct = default);

    /// <summary>GET /settings/pipelines/defaults — shipped provider ordering defaults.</summary>
    Task<PipelineConfiguration?> GetDefaultPipelinesAsync(CancellationToken ct = default);

    /// <summary>PUT /settings/pipelines — save pipeline configuration.</summary>
    Task<bool> SavePipelinesAsync(PipelineConfiguration pipelines, CancellationToken ct = default);

    // ── Media types (/settings/media-types) ────────────────────────────────

    /// <summary>GET /settings/media-types — load media type definitions.</summary>
    Task<MediaTypeConfigurationDto?> GetMediaTypesAsync(CancellationToken ct = default);

    /// <summary>PUT /settings/media-types — save all media type definitions.</summary>
    Task<bool> SaveMediaTypesAsync(MediaTypeConfigurationDto config, CancellationToken ct = default);

    /// <summary>POST /settings/media-types/add — add a single custom media type.</summary>
    Task<MediaTypeConfigurationDto?> AddMediaTypeAsync(MediaTypeDefinitionDto newType, CancellationToken ct = default);

    /// <summary>DELETE /settings/media-types/{key} — delete a custom media type.</summary>
    Task<bool> DeleteMediaTypeAsync(string key, CancellationToken ct = default);

    // ── Hydration settings (/settings/hydration) ──────────────────────────

    /// <summary>GET /settings/hydration — load hydration pipeline configuration.</summary>
    Task<HydrationSettingsDto?> GetHydrationSettingsAsync(CancellationToken ct = default);

    /// <summary>PUT /settings/hydration — save hydration pipeline configuration.</summary>
    Task<bool> UpdateHydrationSettingsAsync(HydrationSettingsDto settings, CancellationToken ct = default);

    // ── Provider Icons ───────────────────────────────────────────────────────

    /// <summary>POST /settings/providers/{name}/icon — upload a provider icon.</summary>
    Task<bool> UploadProviderIconAsync(string name, Stream fileStream, string fileName, CancellationToken ct = default);

    /// <summary>Returns the URL path for a provider's icon, or null if none exists.</summary>
    string GetProviderIconUrl(string name);

    // ── Local AI (/ai) ───────────────────────────────────────────────────────

    /// <summary>GET /ai/status — returns AI subsystem health.</summary>
    Task<AiHealthStatusDto?> GetAiStatusAsync(CancellationToken ct = default);

    /// <summary>GET /ai/models — returns configured model inventory and lifecycle status.</summary>
    Task<IReadOnlyList<AiModelStatusDto>> GetAiModelStatusesAsync(CancellationToken ct = default);

    /// <summary>POST /ai/models/{role}/download — starts model download.</summary>
    Task<AiOperationResultDto> StartAiModelDownloadAsync(string role, CancellationToken ct = default);

    /// <summary>DELETE /ai/models/{role}/download — cancels model download.</summary>
    Task<AiOperationResultDto> CancelAiModelDownloadAsync(string role, CancellationToken ct = default);

    /// <summary>POST /ai/models/{role}/load — loads model into memory.</summary>
    Task<AiOperationResultDto> LoadAiModelAsync(string role, CancellationToken ct = default);

    /// <summary>POST /ai/models/{role}/unload — unloads model from memory.</summary>
    Task<AiOperationResultDto> UnloadAiModelAsync(string role, CancellationToken ct = default);

    /// <summary>GET /ai/config — returns persisted AI configuration.</summary>
    Task<AiConfigDto?> GetAiConfigAsync(CancellationToken ct = default);

    /// <summary>GET /ai/usage — who used which Local AI ability over the last days. Null when the Engine could not be asked.</summary>
    Task<AiUsageResponse?> GetAiUsageAsync(int days = 7, CancellationToken ct = default);

    /// <summary>PUT /ai/config — saves persisted AI configuration.</summary>
    Task<bool> SaveAiConfigAsync(AiConfigDto config, CancellationToken ct = default);

    // ── AI Hardware Profile (/ai/profile, /ai/benchmark) ────────────────────

    /// <summary>GET /ai/profile — returns the cached hardware profile and performance tier.</summary>
    Task<HardwareProfileDto?> GetAiProfileAsync(CancellationToken ct = default);

    /// <summary>POST /ai/benchmark — re-runs the hardware benchmark and returns the updated profile.</summary>
    Task<AiOperationResultDto<HardwareProfileDto>> RunBenchmarkAsync(CancellationToken ct = default);

    /// <summary>DELETE /ai/benchmark — invalidates the machine-local result.</summary>
    Task<AiOperationResultDto<HardwareProfileDto>> InvalidateBenchmarkAsync(CancellationToken ct = default);

    /// <summary>GET /ai/enrichment/progress — pending and completed AI enrichment counts.</summary>
    Task<EnrichmentProgressDto?> GetEnrichmentProgressAsync(CancellationToken ct = default);

    /// <summary>GET /ai/resources — live RAM, CPU pressure, and transcoding status.</summary>
    Task<ResourceSnapshotDto?> GetResourceSnapshotAsync(CancellationToken ct = default);

    // ── Library Preferences (/settings/ui/library-preferences) ──────────────────

    /// <summary>GET /settings/ui/library-preferences - library display preferences, including per-media missing-item policies.</summary>
    Task<LibraryPreferencesSettings?> GetLibraryPreferencesAsync();

    /// <summary>GET the active profile's explicit missing-item override for one series. Null ShowMissing means inherit the media default.</summary>
    Task<SeriesMissingItemPreferenceDto?> GetSeriesMissingItemPreferenceAsync(
        Guid profileId,
        string mediaType,
        string containerKey,
        CancellationToken ct = default);

    /// <summary>PUT an explicit missing-item visibility override for one profile and series.</summary>
    Task<SeriesMissingItemPreferenceDto?> SaveSeriesMissingItemPreferenceAsync(
        Guid profileId,
        string mediaType,
        string containerKey,
        bool showMissing,
        CancellationToken ct = default);

    /// <summary>DELETE a per-series override so the series inherits its media configuration default.</summary>
    Task<SeriesMissingItemPreferenceDto?> ResetSeriesMissingItemPreferenceAsync(
        Guid profileId,
        string mediaType,
        string containerKey,
        CancellationToken ct = default);

}

/// <summary>The outcome of saving a tool's location: the fresh tools check, or a short plain reason it was refused.</summary>
public sealed record ToolLocationResult(bool Success, ToolRequirementsDto? Tools = null, string? ErrorMessage = null);

/// <summary>The outcome of an install, cancel, run-installer or decline request: the fresh tools list, or a short plain reason it was refused.</summary>
public sealed record ToolActionResult(bool Success, ToolRequirementsDto? Tools = null, string? ErrorMessage = null);

/// <summary>The outcome of Write now: the fresh status when a run started, or a short plain reason it did not.</summary>
public sealed record FileWriteRunResult(bool Started, FileWriteScheduleDto? Schedule = null, string? Message = null);
