using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Ai;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.WriteBack;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Branding;
using MediaEngine.Web.Services.Integration.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    // -- /settings/tools ---------------------------------------------------------

    public Task<ToolRequirementsDto?> GetToolRequirementsAsync(CancellationToken ct = default) =>
        GetAsync<ToolRequirementsDto>("GET /settings/tools", "/settings/tools", ct: ct);

    public Task<ToolRequirementsDto?> CheckToolRequirementsAsync(CancellationToken ct = default) =>
        PostAsync<object?, ToolRequirementsDto>("POST /settings/tools/check", "/settings/tools/check", null, ct: ct);

    // -- /settings/writeback (batched file writes) ----------------------------------

    public Task<FileWriteScheduleDto?> GetFileWriteScheduleAsync(CancellationToken ct = default) =>
        GetAsync<FileWriteScheduleDto>("GET /settings/writeback/schedule", "/settings/writeback/schedule", ct: ct);

    public Task<FileWriteScheduleDto?> UpdateFileWriteScheduleAsync(int intervalMinutes, CancellationToken ct = default) =>
        PutAsync<UpdateFileWriteScheduleDto, FileWriteScheduleDto>(
            "PUT /settings/writeback/schedule", "/settings/writeback/schedule",
            new UpdateFileWriteScheduleDto { IntervalMinutes = intervalMinutes }, ct: ct);

    public async Task<FileWriteRunResult> StartFileWriteRunAsync(CancellationToken ct = default)
    {
        const string path = "/settings/writeback/run";
        try
        {
            using var response = await _http.PostAsync(path, null, ct);
            if (response.IsSuccessStatusCode)
            {
                return new(true, await response.Content.ReadFromJsonAsync<FileWriteScheduleDto>(cancellationToken: ct));
            }

            // 409 (already running, or write-back off) carries a plain sentence for the person; 403 means not an administrator.
            return new(false, Message: response.StatusCode == HttpStatusCode.Forbidden
                ? "Only an administrator can write changes into files now."
                : await ReadViewErrorAsync(response, ct));
        }
        catch (OperationCanceledException) { return new(false, Message: "That was canceled."); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST {Path} failed", path);
            return new(false, Message: "The Library could not start writing. Check that the server is online and try again.");
        }
    }

    public Task<IReadOnlyList<FailedFileWriteDto>> GetFailedFileWritesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FailedFileWriteDto>>("GET /settings/writeback/failed", "/settings/writeback/failed", () => [], ct: ct);

    public Task<AssetFileWriteStatusDto?> GetAssetFileWriteStatusAsync(Guid assetId, CancellationToken ct = default) =>
        GetAsync<AssetFileWriteStatusDto>("GET /metadata/assets/{assetId}/file-write", $"/metadata/assets/{assetId}/file-write", ct: ct);

    // -- /settings/notices -------------------------------------------------------

    public Task<ProfileNoticesResponse?> GetSettingsNoticesAsync(CancellationToken ct = default) =>
        GetAsync<ProfileNoticesResponse>("GET /settings/notices", "/settings/notices", ct: ct);

    public async Task<bool> DismissSettingsNoticeAsync(string noticeKey, string fingerprint, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noticeKey);
        ArgumentNullException.ThrowIfNull(fingerprint);
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"/settings/notices/{Uri.EscapeDataString(noticeKey)}/dismiss",
                new DismissProfileNoticeRequest(fingerprint), ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /settings/notices/{NoticeKey}/dismiss failed", noticeKey);
            return false;
        }
    }

    public async Task<ToolLocationResult> SetToolLocationAsync(string toolId, ToolLocationRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            // The Engine's refusal is a short plain sentence meant for the person, so it is passed on as it is
            // rather than wrapped in the shared failure state's "HTTP 400: ..." summary.
            using var message = new HttpRequestMessage(HttpMethod.Put, $"/settings/tools/{Uri.EscapeDataString(toolId)}/location")
            {
                Content = JsonContent.Create(request),
            };
            using var response = await _http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, ErrorMessage: await ReadViewErrorAsync(response, ct));
            }

            var tools = await response.Content.ReadFromJsonAsync<ToolRequirementsDto>(cancellationToken: ct);
            return tools is null ? new(false, ErrorMessage: "The Engine returned no result.") : new(true, tools);
        }
        catch (OperationCanceledException) { return new(false, ErrorMessage: "That was canceled."); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /settings/tools/{ToolId}/location failed", toolId);
            return new(false, ErrorMessage: "The Library could not save that location. Check that the server is online and try again.");
        }
    }

    public Task<ToolActionResult> InstallToolAsync(string toolId, CancellationToken ct = default) =>
        SendToolActionAsync(HttpMethod.Post, toolId, "install",
            "The Library could not start that download. Check that the server is online and try again.", ct);

    public Task<ToolActionResult> CancelToolInstallAsync(string toolId, CancellationToken ct = default) =>
        SendToolActionAsync(HttpMethod.Delete, toolId, "install",
            "The Library could not cancel that download. Check that the server is online and try again.", ct);

    public Task<ToolActionResult> RunToolInstallerAsync(string toolId, CancellationToken ct = default) =>
        SendToolActionAsync(HttpMethod.Post, toolId, "run-installer",
            "The Library could not start the installer. Check that the server is online and try again.", ct);

    // The Engine's refusal is a short plain sentence meant for the person, so it is passed on as it is.
    private async Task<ToolActionResult> SendToolActionAsync(
        HttpMethod method, string toolId, string action, string failure, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        var path = $"/settings/tools/{Uri.EscapeDataString(toolId)}/{action}";
        try
        {
            using var message = new HttpRequestMessage(method, path);
            using var response = await _http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, ErrorMessage: await ReadViewErrorAsync(response, ct));
            }

            var tools = await response.Content.ReadFromJsonAsync<ToolRequirementsDto>(cancellationToken: ct);
            return tools is null ? new(false, ErrorMessage: "The Engine returned no result.") : new(true, tools);
        }
        catch (OperationCanceledException) { return new(false, ErrorMessage: "That was canceled."); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Method} {Path} failed", method, path);
            return new(false, ErrorMessage: failure);
        }
    }

    // -- /profiles ---------------------------------------------------------------

    public async Task<List<ProfileViewModel>> GetProfilesAsync(CancellationToken ct = default)
    {
        const string endpoint = "GET /profiles";
        try
        {
            var response = await _http.GetAsync("/profiles", ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return [];
            }

            var raw = await response.Content.ReadFromJsonAsync<List<ProfileResponseDto>>(cancellationToken: ct);
            ClearFailure(endpoint);
            return raw?.Select(MapProfile).ToList() ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /profiles failed");
            RecordExceptionFailure(endpoint, ex);
            return [];
        }
    }

    public Task<bool> UpdateProfileAsync(
        Guid id, string displayName, string avatarColor,
        string? navigationConfig = null,
        CancellationToken ct = default) =>
        PutAsync("UpdateProfileExperience", $"/profiles/{id:D}/experience",
            new UpdateProfileExperienceRequest(displayName, avatarColor, navigationConfig), ct: ct);

    public async Task<ViewProfilePolicyDto?> GetViewProfilePolicyAsync(
        Guid id,
        CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ViewProfilePolicyDto>(
                $"/profiles/{id:D}/settings/view", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /profiles/{Id}/settings/view failed", id);
            return null;
        }
    }

    public async Task<ViewProfilePolicyDto?> UpdateViewProfilePolicyAsync(
        Guid id,
        UpdateViewProfilePolicyRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PutAsJsonAsync(
                $"/profiles/{id:D}/settings/view", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ViewProfilePolicyDto>(
                cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /profiles/{Id}/settings/view failed", id);
            return null;
        }
    }

    public async Task<ProfileViewModel?> UploadProfileAvatarAsync(
        Guid id,
        Stream fileStream,
        string fileName,
        double zoom = 1,
        CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GetImageContentType(fileName));
            content.Add(fileContent, "file", fileName);
            content.Add(new StringContent(Math.Clamp(zoom, 1d, 3d).ToString(System.Globalization.CultureInfo.InvariantCulture)), "zoom");

            var resp = await _http.PostAsync($"/profiles/{id}/avatar", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var profile = await resp.Content.ReadFromJsonAsync<ProfileResponseDto>(ct);
            return profile is null ? null : MapProfile(profile);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /profiles/{Id}/avatar failed", id);
            return null;
        }
    }

    public async Task<ProfileViewModel?> RemoveProfileAvatarAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.DeleteAsync($"/profiles/{id}/avatar", ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var profile = await resp.Content.ReadFromJsonAsync<ProfileResponseDto>(ct);
            return profile is null ? null : MapProfile(profile);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DELETE /profiles/{Id}/avatar failed", id);
            return null;
        }
    }

    private ProfileViewModel MapProfile(ProfileResponseDto profile) => new(
        profile.Id,
        profile.DisplayName,
        profile.AvatarColor,
        profile.Role,
        profile.CreatedAt,
        profile.NavigationConfig,
        NormalizeOptionalUrl(profile.AvatarImageUrl),
        profile.HasPin,
        profile.AvatarIcon);

    private ProfileOverviewViewModel MapProfileOverview(ProfileOverviewResponseDto overview) => new()
    {
        Profile = MapProfile(overview.Profile),
        Stats = new ProfileOverviewStatsViewModel
        {
            TotalItems = overview.Stats.TotalItems,
            InProgress = overview.Stats.InProgress,
            Completed = overview.Stats.Completed,
            RecentActivity = overview.Stats.RecentActivity,
            MediaTypeMix = overview.Stats.MediaTypeMix,
            LibraryCounts = overview.Stats.LibraryCounts,
            ActivityBuckets = overview.Stats.ActivityBuckets,
            TopGenres = overview.Stats.TopGenres,
            ConsumedSeconds = overview.Stats.ConsumedSeconds,
            ConsumedSecondsByMediaType = overview.Stats.ConsumedSecondsByMediaType,
        },
        RecentItems = overview.RecentItems.Select(MapProfileOverviewItem).ToList(),
        ContinueItems = overview.ContinueItems.Select(MapProfileOverviewItem).ToList(),
        CompletedItems = overview.CompletedItems.Select(MapProfileOverviewItem).ToList(),
        RecentlyAddedItems = overview.RecentlyAddedItems.Select(MapProfileOverviewItem).ToList(),
        Activity = overview.Activity.Select(activity => new ProfileOverviewActivityViewModel
        {
            Id = activity.Id,
            OccurredAt = activity.OccurredAt,
            ActionType = activity.ActionType,
            Detail = activity.Detail,
            EntityId = activity.EntityId,
        }).ToList(),
        Taste = overview.Taste,
    };

    private ProfileOverviewItemViewModel MapProfileOverviewItem(ProfileOverviewItemDto item) => new()
    {
        AssetId = item.AssetId,
        WorkId = item.WorkId,
        Title = item.Title,
        Subtitle = item.Subtitle,
        MediaType = item.MediaType,
        CoverUrl = NormalizeOptionalUrl(item.CoverUrl),
        CollectionName = item.CollectionName,
        Genre = item.Genre,
        Route = item.Route,
        PositionSeconds = item.PositionSeconds,
        DurationSeconds = item.DurationSeconds,
        ProgressPct = item.ProgressPct,
        LastAccessed = item.LastAccessed,
        AddedAt = item.AddedAt,
    };

    // -- /metadata/claims + lock-claim -------------------------------------------

    public async Task<ProfileOverviewViewModel?> GetProfileOverviewAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var overview = await _http.GetFromJsonAsync<ProfileOverviewResponseDto>($"/profiles/{id}/overview", ct);
            return overview is null ? null : MapProfileOverview(overview);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /profiles/{Id}/overview failed", id);
            return null;
        }
    }

    public async Task<List<ClaimDto>> GetClaimHistoryAsync(
        Guid entityId, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<ClaimDto>>(
                $"/metadata/claims/{entityId}", ct);
            return raw ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /metadata/claims/{EntityId} failed", entityId);
            return [];
        }
    }

    public async Task<bool> LockClaimAsync(
        Guid entityId, string key, string value, CancellationToken ct = default)
    {
        try
        {
            var body = new { entity_id = entityId, claim_key = key, chosen_value = value };
            using var req = new HttpRequestMessage(new HttpMethod("PATCH"), "/metadata/lock-claim")
            {
                Content = JsonContent.Create(body),
            };
            var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PATCH /metadata/lock-claim failed");
            return false;
        }
    }

    // -- /settings -------------------------------------------------------------

    public async Task<ServerGeneralSettingsDto?> GetServerGeneralAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ServerGeneralSettingsDto>("/settings/server-general", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /settings/server-general failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<bool> UpdateServerGeneralAsync(ServerGeneralSettingsDto settings, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync("/settings/server-general", settings, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("PUT /settings/server-general returned {Status}: {Detail}", (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /settings/server-general failed");
            LastError = ex.Message;
            return false;
        }
    }

    public Task<LibrariesConfigurationDto?> MutateLibraryAsync(LibraryMutationRequest request, CancellationToken ct = default) =>
        PostAsync<LibraryMutationRequest, LibrariesConfigurationDto>("POST /settings/libraries/mutations", "/settings/libraries/mutations", request, ct: ct);

    public Task<MediaEngine.Contracts.Library.LibraryActivityDto?> GetLibraryActivityAsync(string libraryId, int take = 50, CancellationToken ct = default) =>
        GetAsync<MediaEngine.Contracts.Library.LibraryActivityDto>(
            "GET /library/libraries/{libraryId}/activity", $"/library/libraries/{Uri.EscapeDataString(libraryId)}/activity?take={take}", ct: ct);

    public Task<IReadOnlyList<LibraryFolderDto>?> GetVaultSiblingLibrariesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LibraryFolderDto>>("GET /settings/vault-libraries", "/settings/vault-libraries", logAsWarning: false, ct: ct);

    public Task<VaultLibraryActionResultDto?> CreateVaultLibraryAsync(CreateVaultLibraryFromKindRequest request, CancellationToken ct = default) =>
        PostAsync<CreateVaultLibraryFromKindRequest, VaultLibraryActionResultDto>("POST /settings/vault-libraries", "/settings/vault-libraries", request, ct: ct);

    public Task<VaultLibraryActionResultDto?> AddVaultFolderAsync(AddVaultFolderRequest request, CancellationToken ct = default) =>
        PostAsync<AddVaultFolderRequest, VaultLibraryActionResultDto>("POST /settings/vault-libraries/folders", "/settings/vault-libraries/folders", request, ct: ct);

    public Task<LibraryFolderDto?> SetVaultOnlineLookupAsync(string libraryId, bool enabled, CancellationToken ct = default) =>
        PutAsync<SetVaultOnlineLookupRequest, LibraryFolderDto>(
            "PUT /settings/vault-libraries/{libraryId}/online-lookup",
            $"/settings/vault-libraries/{Uri.EscapeDataString(libraryId)}/online-lookup",
            new SetVaultOnlineLookupRequest(enabled), ct: ct);

    public Task<VaultMoveDestinationDto?> GetVaultMoveDestinationAsync(string kindKey, CancellationToken ct = default) =>
        GetAsync<VaultMoveDestinationDto>(
            "GET /settings/vault-libraries/destination",
            $"/settings/vault-libraries/destination?kind_key={Uri.EscapeDataString(kindKey)}", logAsWarning: false, ct: ct);

    public Task<MediaEngine.Contracts.Library.UnmatchedActionResultDto?> RetryUnmatchedLibraryAsync(string libraryId, CancellationToken ct = default) =>
        PostAsync<object?, MediaEngine.Contracts.Library.UnmatchedActionResultDto>(
            "POST /library/unmatched/libraries/{libraryId}/retry", $"/library/unmatched/libraries/{Uri.EscapeDataString(libraryId)}/retry", null, ct: ct);

    public Task<MediaEngine.Contracts.Ingestion.RescanAcceptedResponse?> ScanLibraryAsync(string libraryId, CancellationToken ct = default) =>
        PostAsync<object?, MediaEngine.Contracts.Ingestion.RescanAcceptedResponse>(
            "POST /library/libraries/{libraryId}/scan", $"/library/libraries/{Uri.EscapeDataString(libraryId)}/scan", null, ct: ct);

    public Task<MediaEngine.Contracts.Library.LibraryFolderTitlesDto?> GetLibraryFolderTitlesAsync(string libraryId, CancellationToken ct = default) =>
        GetAsync<MediaEngine.Contracts.Library.LibraryFolderTitlesDto>(
            "GET /library/libraries/{libraryId}/folder-titles", $"/library/libraries/{Uri.EscapeDataString(libraryId)}/folder-titles", ct: ct);

    public Task<MediaEngine.Contracts.Library.LibraryAddFilesResultDto?> AddFilesToLibraryAsync(string libraryId, MediaEngine.Contracts.Library.LibraryAddFilesRequest request, CancellationToken ct = default) =>
        PostAsync<MediaEngine.Contracts.Library.LibraryAddFilesRequest, MediaEngine.Contracts.Library.LibraryAddFilesResultDto>(
            "POST /library/libraries/{libraryId}/add-files", $"/library/libraries/{Uri.EscapeDataString(libraryId)}/add-files", request, ct: ct);

    public Task<MediaEngine.Contracts.Library.UnmatchedPageDto?> GetUnmatchedItemsAsync(string libraryId, int skip = 0, int take = 50, CancellationToken ct = default) =>
        GetAsync<MediaEngine.Contracts.Library.UnmatchedPageDto>(
            "GET /library/unmatched/libraries/{libraryId}", $"/library/unmatched/libraries/{Uri.EscapeDataString(libraryId)}?skip={skip}&take={take}", ct: ct);

    public Task<MediaEngine.Contracts.Library.UnmatchedActionResultDto?> KeepAllUnmatchedLibraryAsync(string libraryId, CancellationToken ct = default) =>
        PostAsync<object?, MediaEngine.Contracts.Library.UnmatchedActionResultDto>(
            "POST /library/unmatched/libraries/{libraryId}/keep", $"/library/unmatched/libraries/{Uri.EscapeDataString(libraryId)}/keep", null, ct: ct);

    public Task<MediaEngine.Contracts.Library.UnmatchedActionResultDto?> RetryUnmatchedItemAsync(Guid assetId, CancellationToken ct = default) =>
        PostAsync<object?, MediaEngine.Contracts.Library.UnmatchedActionResultDto>(
            "POST /library/unmatched/items/{assetId}/retry", $"/library/unmatched/items/{assetId:D}/retry", null, ct: ct);

    public Task<MediaEngine.Contracts.Library.UnmatchedActionResultDto?> KeepUnmatchedItemAsync(Guid assetId, CancellationToken ct = default) =>
        PostAsync<object?, MediaEngine.Contracts.Library.UnmatchedActionResultDto>(
            "POST /library/unmatched/items/{assetId}/keep", $"/library/unmatched/items/{assetId:D}/keep", null, ct: ct);

    public Task<MediaEngine.Contracts.Library.UnmatchedActionResultDto?> LookUpUnmatchedItemAgainAsync(Guid assetId, CancellationToken ct = default) =>
        PostAsync<object?, MediaEngine.Contracts.Library.UnmatchedActionResultDto>(
            "POST /library/unmatched/items/{assetId}/look-up-again", $"/library/unmatched/items/{assetId:D}/look-up-again", null, ct: ct);

    public Task<MediaEngine.Contracts.Library.FolderFirstChecksDto?> GetFirstChecksAsync(CancellationToken ct = default) =>
        GetAsync<MediaEngine.Contracts.Library.FolderFirstChecksDto>("GET /library/first-checks", "/library/first-checks", ct: ct);

    public Task<bool> KeepGoingAsync(string sourceId, CancellationToken ct = default) =>
        PostAsync<object?>("POST /library/first-checks/{sourceId}/keep-going", $"/library/first-checks/{Uri.EscapeDataString(sourceId)}/keep-going", null, ct: ct);

    public async Task<IReadOnlyList<LibraryKindDto>?> GetLibraryKindsAsync(CancellationToken ct = default) =>
        await GetAsync<List<LibraryKindDto>>("GET /settings/library-kinds", "/settings/library-kinds", ct: ct);

    public Task<ViewStorageSummaryDto?> GetViewStorageSummaryAsync(CancellationToken ct = default) =>
        GetAsync<ViewStorageSummaryDto>("GET /settings/libraries/view-summary", "/settings/libraries/view-summary", ct: ct);

    public async Task<LibrariesConfigurationDto?> GetLibrariesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<LibrariesConfigurationDto>("/settings/libraries", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /settings/libraries failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<LibrariesConfigurationDto?> UpdateLibrariesAsync(
        UpdateLibrariesRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync("/settings/libraries", request, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "PUT /settings/libraries returned {Status}: {Detail}",
                    (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<LibrariesConfigurationDto>(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /settings/libraries failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<PathTestResultDto?> TestPathAsync(
        string path,
        CancellationToken ct = default)
    {
        try
        {
            var body = new { path };
            var resp = await _http.PostAsJsonAsync("/settings/test-path", body, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "POST /settings/test-path returned {Status}: {Detail}",
                    (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<PathTestResultDto>(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /settings/test-path failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<IReadOnlyList<ServerStorageLocationDto>?> GetServerFolderRootsAsync(
        CancellationToken ct = default)
    {
        return await GetAsync<List<ServerStorageLocationDto>>(
            "GET /settings/server-folders/roots",
            "/settings/server-folders/roots",
            ct: ct);
    }

    public async Task<BrowseServerFoldersResultDto?> BrowseServerFoldersAsync(
        BrowseServerFoldersRequest request,
        CancellationToken ct = default)
        => await PostAsync<BrowseServerFoldersRequest, BrowseServerFoldersResultDto>(
            "POST /settings/server-folders/browse",
            "/settings/server-folders/browse",
            request,
            ct: ct);

    public async Task<ServerFolderValidationResultDto?> ValidateServerFolderAsync(
        ValidateServerFolderRequest request,
        CancellationToken ct = default)
        => await PostAsync<ValidateServerFolderRequest, ServerFolderValidationResultDto>(
            "POST /settings/server-folders/validate",
            "/settings/server-folders/validate",
            request,
            ct: ct);

    // -- Provider catalogue (/providers/catalogue) ----------------------------

    public async Task<IReadOnlyList<ProviderCatalogueDto>> GetProviderCatalogueAsync(
        CancellationToken ct = default)
        => await _providerClient.GetProviderCatalogueAsync(ct);

    public async Task<IReadOnlyList<ProviderStatusDto>> GetProviderStatusAsync(
        CancellationToken ct = default)
        => await _providerClient.GetProviderStatusAsync(ct);

    public async Task<bool> UpdateProviderAsync(
        string name,
        bool enabled,
        CancellationToken ct = default)
        => await _providerClient.UpdateProviderAsync(name, enabled, ct);

    public async Task<List<ProviderHealthStatusResponse>> GetProviderHealthAsync(
        CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<ProviderHealthStatusResponse>>(
                "/settings/providers/health", ct) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /settings/providers/health failed");
            LastError = ex.Message;
            return [];
        }
    }

    // -- Provider management -------------------------------------------------

    public async Task<ProviderCredentialOperationResultDto?> TestProviderCredentialsAsync(
        string name,
        ProviderCredentialWriteRequest request,
        CancellationToken ct = default)
        => await SendProviderCredentialRequestAsync(
            HttpMethod.Post, name, "credentials/test", request, ct);

    public async Task<ProviderCredentialOperationResultDto?> SaveProviderCredentialsAsync(
        string name,
        ProviderCredentialWriteRequest request,
        CancellationToken ct = default)
        => await SendProviderCredentialRequestAsync(
            HttpMethod.Put, name, "credentials", request, ct);

    public async Task<ProviderCredentialOperationResultDto?> RemoveProviderCredentialsAsync(
        string name,
        CancellationToken ct = default)
        => await SendProviderCredentialRequestAsync(
            HttpMethod.Delete, name, "credentials", request: null, ct);

    private async Task<ProviderCredentialOperationResultDto?> SendProviderCredentialRequestAsync(
        HttpMethod method,
        string name,
        string suffix,
        ProviderCredentialWriteRequest? request,
        CancellationToken ct)
    {
        var encoded = WebUtility.UrlEncode(name);
        using var message = new HttpRequestMessage(
            method,
            $"/settings/providers/{encoded}/{suffix}");
        if (request is not null)
        {
            message.Content = JsonContent.Create(request);
        }

        using var response = await _http.SendAsync(message, ct);
        return await ReadProviderOperationResultAsync(response, setup: false, ct);
    }

    private static async Task<ProviderCredentialOperationResultDto> ReadProviderOperationResultAsync(
        HttpResponseMessage response, bool setup, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ProviderCredentialOperationResultDto
            {
                Status = "engine_unauthorized",
                Message = setup
                    ? "The setup session expired. Reopen setup and try again."
                    : "Your administrator session cannot manage providers. Sign in again and retry.",
            };
        }
        if (!response.IsSuccessStatusCode)
        {
            return new ProviderCredentialOperationResultDto
            {
                Status = "engine_error",
                Message = $"The Engine could not complete this provider request (HTTP {(int)response.StatusCode}).",
            };
        }
        try
        {
            return await response.Content.ReadFromJsonAsync<ProviderCredentialOperationResultDto>(
                cancellationToken: ct) ?? new ProviderCredentialOperationResultDto
                {
                    Status = "engine_error",
                    Message = "The Engine returned an empty provider result.",
                };
        }
        catch (JsonException)
        {
            return new ProviderCredentialOperationResultDto
            {
                Status = "engine_error",
                Message = "The Engine returned an unreadable provider result.",
            };
        }
    }

    public async Task<ProviderTestResultDto?> TestProviderAsync(
        string name, CancellationToken ct = default)
    {
        try
        {
            var encoded = WebUtility.UrlEncode(name);
            var resp = await _http.PostAsync($"/settings/providers/{encoded}/test", null, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var unauthorized = resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                var message = unauthorized
                    ? "Your administrator session cannot test providers. Sign in again and retry."
                    : $"The Engine could not complete this provider test (HTTP {(int)resp.StatusCode}).";
                _logger.LogWarning("POST /settings/providers/{Name}/test returned {Status}",
                    name, (int)resp.StatusCode);
                LastError = message;
                return new ProviderTestResultDto(false, 0, [], message)
                {
                    Status = unauthorized ? "engine_unauthorized" : "engine_error",
                };
            }
            return await resp.Content.ReadFromJsonAsync<ProviderTestResultDto>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "POST /settings/providers/{Name}/test failed", name);
            LastError = "The Dashboard could not reach the Engine. Check that it is running and retry.";
            return new ProviderTestResultDto(false, 0, [], LastError)
            {
                Status = "engine_unavailable",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /settings/providers/{Name}/test failed", name);
            LastError = "The Dashboard could not read the Engine's provider test result.";
            return new ProviderTestResultDto(false, 0, [], LastError)
            {
                Status = "engine_error",
            };
        }
    }

    public async Task<ProviderSampleResultDto?> FetchProviderSampleAsync(
        string name, string title, string? author = null,
        string? isbn = null, string? asin = null, string? mediaType = null,
        CancellationToken ct = default)
    {
        try
        {
            var encoded = WebUtility.UrlEncode(name);
            var body = new { title, author, isbn, asin, media_type = mediaType };
            var resp = await _http.PostAsJsonAsync($"/settings/providers/{encoded}/sample", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("POST /settings/providers/{Name}/sample returned {Status}: {Detail}",
                    name, (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<ProviderSampleResultDto>(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /settings/providers/{Name}/sample failed", name);
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<bool> SaveProviderConfigAsync(
        string name, ProviderConfigUpdateDto config, CancellationToken ct = default)
    {
        try
        {
            var encoded = WebUtility.UrlEncode(name);
            var resp = await _http.PutAsJsonAsync($"/settings/providers/{encoded}/config", config, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("PUT /settings/providers/{Name}/config returned {Status}: {Detail}",
                    name, (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /settings/providers/{Name}/config failed", name);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<bool> DeleteProviderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var encoded = WebUtility.UrlEncode(name);
            var resp = await _http.DeleteAsync($"/settings/providers/{encoded}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("DELETE /settings/providers/{Name} returned {Status}: {Detail}",
                    name, (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DELETE /settings/providers/{Name} failed", name);
            LastError = ex.Message;
            return false;
        }
    }

    // -- Activity log (/activity) -------------------------------------------

    public async Task<List<ActivityEntryResponse>> GetRecentActivityAsync(
        int limit = 50, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<ActivityEntryResponse>>(
                $"/activity/recent?limit={limit}", ct);
            return raw ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /activity/recent failed");
            return [];
        }
    }

    public async Task<PagedResponse<ActivityLogEntryDto>?> GetActivityLogAsync(
        string category, string? search, int offset, int limit, string? mediaType = null, CancellationToken ct = default)
    {
        try
        {
            var path = $"/activity/log?category={Uri.EscapeDataString(category)}&offset={offset}&limit={limit}";
            if (!string.IsNullOrWhiteSpace(search))
            {
                path += $"&search={Uri.EscapeDataString(search)}";
            }

            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                path += $"&mediaType={Uri.EscapeDataString(mediaType)}";
            }

            var page = await _http.GetFromJsonAsync<PagedResponse<ActivityLogEntryDto>>(path, ct);
            if (page is not null)
            {
                // Covers are Engine addresses; the browser reads them through the Dashboard's own artwork route.
                foreach (var entry in page.Items.Where(entry => !string.IsNullOrWhiteSpace(entry.CoverUrl)))
                {
                    entry.CoverUrl = AbsoluteUrl(entry.CoverUrl!);
                }
            }

            return page;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /activity/log failed");
            return null;
        }
    }

    public async Task<ActivityStatsResponse?> GetActivityStatsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ActivityStatsResponse>("/activity/stats", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /activity/stats failed");
            return null;
        }
    }

    public async Task<PruneResponse?> TriggerPruneAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/activity/prune", new { }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<PruneResponse>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /activity/prune failed");
            return null;
        }
    }

    public async Task<List<ActivityEntryResponse>> GetActivityByTypesAsync(
        string[] actionTypes, int limit = 50, CancellationToken ct = default)
    {
        try
        {
            var typesParam = string.Join(",", actionTypes);
            var raw = await _http.GetFromJsonAsync<List<ActivityEntryResponse>>(
                $"/activity/by-types?types={Uri.EscapeDataString(typesParam)}&limit={limit}", ct);
            return raw ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /activity/by-types failed");
            return [];
        }
    }

    // -- Organization template ------------------------------------------------

    public async Task<OrganizationTemplateDto?> GetOrganizationTemplateAsync(
        CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<OrganizationTemplateDto>(
                "/settings/organization-template", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /settings/organization-template failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<OrganizationTemplateDto?> PreviewOrganizationTemplateAsync(
        string template,
        Dictionary<string, string>? templates = null,
        CancellationToken ct = default)
    {
        try
        {
            var body = new { template, templates };
            var resp = await _http.PostAsJsonAsync("/settings/organization-template/preview", body, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "POST /settings/organization-template/preview returned {Status}: {Detail}",
                    (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<OrganizationTemplateDto>(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /settings/organization-template/preview failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<OrganizationTemplateDto?> UpdateOrganizationTemplateAsync(
        string template,
        Dictionary<string, string>? templates = null,
        CancellationToken ct = default)
    {
        try
        {
            var body = new { template, templates };
            var resp = await _http.PutAsJsonAsync("/settings/organization-template", body, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "PUT /settings/organization-template returned {Status}: {Detail}",
                    (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<OrganizationTemplateDto>(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /settings/organization-template failed");
            LastError = ex.Message;
            return null;
        }
    }

    // -- Pipelines (/settings/pipelines) ----------------------------------

    public async Task<PipelineConfiguration?> GetPipelinesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<PipelineConfiguration>(
                "/settings/pipelines", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /settings/pipelines failed");
            return null;
        }
    }

    public Task<PipelineConfiguration?> GetDefaultPipelinesAsync(CancellationToken ct = default) =>
        GetAsync<PipelineConfiguration>(
            "GET /settings/pipelines/defaults",
            "/settings/pipelines/defaults",
            ct: ct);

    public async Task<bool> SavePipelinesAsync(PipelineConfiguration pipelines, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync("/settings/pipelines", pipelines, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("PUT /settings/pipelines returned {Status}",
                    resp.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /settings/pipelines failed");
            return false;
        }
    }

    public Task<PipelineFlowOptionsResponse?> GetPipelineFlowOptionsAsync(CancellationToken ct = default) =>
        GetAsync<PipelineFlowOptionsResponse>(
            "GET /settings/pipelines/options",
            "/settings/pipelines/options",
            ct: ct);

    public async Task<PipelineFlowSaveResponse> SavePipelineFlowAsync(PipelineFlowSaveRequest request, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync("/settings/pipelines/flow", request, ct);
            if (resp.IsSuccessStatusCode)
            {
                return new PipelineFlowSaveResponse { Saved = true };
            }

            // A refused edit carries the problems as a "problems" extension on the problem details.
            var failed = new PipelineFlowSaveResponse { Saved = false };
            try
            {
                using var document = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (document.RootElement.TryGetProperty("problems", out var problems))
                {
                    failed.Problems = problems.Deserialize<List<PipelineFlowProblemDto>>() ?? [];
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogDebug(ex, "PUT /settings/pipelines/flow returned a body that was not problem details");
            }

            if (failed.Problems.Count == 0)
            {
                failed.Problems.Add(new PipelineFlowProblemDto { Message = "The flow could not be saved." });
            }

            return failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "PUT /settings/pipelines/flow failed");
            return new PipelineFlowSaveResponse
            {
                Saved = false,
                Problems = [new PipelineFlowProblemDto { Message = "The Engine could not be reached. Nothing was saved." }],
            };
        }
    }

    // -- Media types (/settings/media-types) --------------------------------

    public async Task<MediaTypeConfigurationDto?> GetMediaTypesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<MediaTypeConfigurationDto>(
                "/settings/media-types", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /settings/media-types failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<bool> SaveMediaTypesAsync(MediaTypeConfigurationDto config, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync("/settings/media-types", config, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("PUT /settings/media-types returned {Status}: {Detail}",
                    (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /settings/media-types failed");
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<MediaTypeConfigurationDto?> AddMediaTypeAsync(
        MediaTypeDefinitionDto newType, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/settings/media-types/add", newType, ct);
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadFromJsonAsync<MediaTypeConfigurationDto>(ct);
            }

            var detail = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("POST /settings/media-types/add returned {Status}: {Detail}",
                (int)resp.StatusCode, detail);
            LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /settings/media-types/add failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<bool> DeleteMediaTypeAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.DeleteAsync($"/settings/media-types/{Uri.EscapeDataString(key)}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("DELETE /settings/media-types/{Key} returned {Status}: {Detail}",
                    key, (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DELETE /settings/media-types/{Key} failed", key);
            LastError = ex.Message;
            return false;
        }
    }

    // -- Hydration settings (/settings/hydration) ------------------------

    public async Task<HydrationSettingsDto?> GetHydrationSettingsAsync(
        CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<HydrationSettingsDto>(
                "/settings/hydration", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /settings/hydration failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<bool> UpdateHydrationSettingsAsync(
        HydrationSettingsDto settings, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync("/settings/hydration", settings, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("PUT /settings/hydration returned {Status}: {Detail}",
                    (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /settings/hydration failed");
            LastError = ex.Message;
            return false;
        }
    }

    // -- Provider Icons -----------------------------------------------------

    public async Task<bool> UploadProviderIconAsync(
        string name, Stream fileStream, string fileName, CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(fileStream);
            content.Add(streamContent, "file", fileName);

            var resp = await _http.PostAsync($"/settings/providers/{name}/icon", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("POST /settings/providers/{Name}/icon returned {Status}: {Detail}",
                    name, (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /settings/providers/{Name}/icon failed", name);
            LastError = ex.Message;
            return false;
        }
    }

    public string GetProviderIconUrl(string name) => $"/settings/providers/{name}/icon";

    // -- UI Settings (/settings/ui) ------------------------------------------

    public async Task<ResolvedUISettingsViewModel?> GetResolvedUISettingsAsync(
        string deviceClass = "web",
        string? profileId = null,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"/settings/ui/resolved?device={WebUtility.UrlEncode(deviceClass)}";
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                url += $"&profile={WebUtility.UrlEncode(profileId)}";
            }

            var settings = await _http.GetFromJsonAsync<ResolvedUISettingsDto>(url, ct);
            return settings is null ? null : ResolvedUISettingsViewModel.FromContract(settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /settings/ui/resolved failed");
            LastError = ex.Message;
            return null;
        }
    }

    public Task<UIProfileSettingsDto?> GetUIProfileSettingsAsync(string profileId, CancellationToken ct = default) =>
        GetAsync<UIProfileSettingsDto>(
            "GET /settings/ui/profile/{profileId}",
            $"/settings/ui/profile/{WebUtility.UrlEncode(profileId)}",
            ct: ct);

    public async Task<UIProfileSettingsDto?> SaveUIProfileSettingsAsync(string profileId, UIProfileSettingsDto settings, CancellationToken ct = default)
    {
        var path = $"/settings/ui/profile/{WebUtility.UrlEncode(profileId)}";
        if (!await PutAsync("PUT /settings/ui/profile/{profileId}", path, settings, ct: ct))
        {
            return null;
        }

        return await GetAsync<UIProfileSettingsDto>("GET /settings/ui/profile/{profileId}", path, ct: ct);
    }

    // -- GET /ai/profile -------------------------------------------------------

    public async Task<AiHealthStatusDto?> GetAiStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AiHealthStatusDto>("/ai/status", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /ai/status failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<IReadOnlyList<AiModelStatusDto>> GetAiModelStatusesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<AiModelStatusDto>>("/ai/models", ct) ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /ai/models failed");
            LastError = ex.Message;
            return [];
        }
    }

    public Task<AiOperationResultDto> StartAiModelDownloadAsync(string role, CancellationToken ct = default) =>
        SendAiActionAsync(HttpMethod.Post, $"/ai/models/{Uri.EscapeDataString(role)}/download", "start download", ct);

    public Task<AiOperationResultDto> CancelAiModelDownloadAsync(string role, CancellationToken ct = default) =>
        SendAiActionAsync(HttpMethod.Delete, $"/ai/models/{Uri.EscapeDataString(role)}/download", "cancel download", ct);

    public Task<AiOperationResultDto> LoadAiModelAsync(string role, CancellationToken ct = default) =>
        SendAiActionAsync(HttpMethod.Post, $"/ai/models/{Uri.EscapeDataString(role)}/load", "load model", ct);

    public Task<AiOperationResultDto> UnloadAiModelAsync(string role, CancellationToken ct = default) =>
        SendAiActionAsync(HttpMethod.Post, $"/ai/models/{Uri.EscapeDataString(role)}/unload", "unload model", ct);

    private async Task<AiOperationResultDto> SendAiActionAsync(HttpMethod method, string endpoint, string action, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, endpoint);
            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return AiOperationResultDto.Success();
            }

            var problem = await ReadAiProblemAsync(response, "AI model operation failed", ct);
            LastError = problem.ToUserMessage();
            _logger.LogWarning("AI {Action} failed for {Endpoint}: {Status} {ProblemType}", action, endpoint, response.StatusCode, problem.Type);
            return AiOperationResultDto.Failure(problem);
        }
        catch (OperationCanceledException)
        {
            return AiOperationResultDto.Failure(ClientProblem("AI operation cancelled", "The operation was cancelled before the Engine completed it."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI {Action} failed for {Endpoint}", action, endpoint);
            var problem = ClientProblem("Engine communication failed", "The Dashboard could not reach the Engine for this operation.");
            LastError = problem.ToUserMessage();
            return AiOperationResultDto.Failure(problem);
        }
    }

    private static async Task<AiProblemDetailsDto> ReadAiProblemAsync(
        HttpResponseMessage response,
        string fallbackTitle,
        CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            var type = GetProblemString(root, "type", "about:blank", 300);
            if (!type.StartsWith("https://tuvima.local/problems/", StringComparison.OrdinalIgnoreCase))
            {
                return ClientProblem(
                    fallbackTitle,
                    $"The Engine returned HTTP {(int)response.StatusCode}. Review Engine logs for diagnostic details.",
                    (int)response.StatusCode);
            }
            var reasons = root.TryGetProperty("blockingReasons", out var blockingReasons)
                && blockingReasons.ValueKind == JsonValueKind.Array
                ? blockingReasons.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => SafeProblemText(value.GetString(), 500))
                    .Where(value => value.Length > 0)
                    .ToList()
                : [];
            return new AiProblemDetailsDto
            {
                Type = type,
                Title = GetProblemString(root, "title", fallbackTitle, 200),
                Status = root.TryGetProperty("status", out var status) && status.TryGetInt32(out var value)
                    ? value
                    : (int)response.StatusCode,
                Detail = GetProblemString(root, "detail", $"The Engine returned HTTP {(int)response.StatusCode}.", 1000),
                BlockingReasons = reasons,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return ClientProblem(fallbackTitle, $"The Engine returned HTTP {(int)response.StatusCode} without readable problem details.", (int)response.StatusCode);
        }
    }

    private static string GetProblemString(JsonElement root, string property, string fallback, int maxLength) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? SafeProblemText(value.GetString(), maxLength, fallback)
            : fallback;

    private static string SafeProblemText(string? value, int maxLength, string fallback = "")
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private static AiProblemDetailsDto ClientProblem(string title, string detail, int? status = null) => new()
    {
        Type = "https://tuvima.local/problems/dashboard/engine-communication",
        Title = title,
        Detail = detail,
        Status = status,
    };

    public async Task<AiConfigDto?> GetAiConfigAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AiConfigDto>("/ai/config", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /ai/config failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<AiUsageResponse?> GetAiUsageAsync(int days = 7, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AiUsageResponse>($"/ai/usage?days={days}", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /ai/usage failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<bool> SaveAiConfigAsync(AiConfigDto config, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PutAsJsonAsync("/ai/config", config, ct);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            LastError = await response.Content.ReadAsStringAsync(ct);
            return false;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /ai/config failed");
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<IReadOnlyList<ApprovalRecordResponse>> GetApprovalsAsync(string kind, string itemId, CancellationToken ct = default)
    {
        try
        {
            var url = $"/approvals?kind={Uri.EscapeDataString(kind)}&item_id={Uri.EscapeDataString(itemId)}";
            return await _http.GetFromJsonAsync<List<ApprovalRecordResponse>>(url, ct) ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /approvals failed");
            return [];
        }
    }

    public async Task<HardwareProfileDto?> GetAiProfileAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<HardwareProfileDto>("/ai/profile", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /ai/profile failed");
            LastError = ex.Message;
            return null;
        }
    }

    // -- POST /ai/benchmark ----------------------------------------------------

    public async Task<AiOperationResultDto<HardwareProfileDto>> RunBenchmarkAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsync("/ai/benchmark", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                return AiOperationResultDto<HardwareProfileDto>.Failure(
                    await ReadAiProblemAsync(response, "Hardware benchmark failed", ct));
            }

            var profile = await response.Content.ReadFromJsonAsync<HardwareProfileDto>(cancellationToken: ct);
            return profile is null
                ? AiOperationResultDto<HardwareProfileDto>.Failure(ClientProblem("Invalid Engine response", "The hardware benchmark result was empty."))
                : AiOperationResultDto<HardwareProfileDto>.Success(profile);
        }
        catch (OperationCanceledException)
        {
            return AiOperationResultDto<HardwareProfileDto>.Failure(
                ClientProblem("Hardware benchmark cancelled", "The benchmark was cancelled."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /ai/benchmark failed");
            return AiOperationResultDto<HardwareProfileDto>.Failure(
                ClientProblem("Engine communication failed", "The Dashboard could not reach the Engine for the hardware benchmark."));
        }
    }

    public async Task<AiOperationResultDto<HardwareProfileDto>> InvalidateBenchmarkAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/ai/benchmark");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return AiOperationResultDto<HardwareProfileDto>.Failure(
                    await ReadAiProblemAsync(response, "Benchmark invalidation failed", ct));
            }

            var profile = await response.Content.ReadFromJsonAsync<HardwareProfileDto>(cancellationToken: ct);
            return profile is null
                ? AiOperationResultDto<HardwareProfileDto>.Failure(ClientProblem("Invalid Engine response", "The benchmark state was empty."))
                : AiOperationResultDto<HardwareProfileDto>.Success(profile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DELETE /ai/benchmark failed");
            return AiOperationResultDto<HardwareProfileDto>.Failure(
                ClientProblem("Engine communication failed", "The Dashboard could not invalidate the benchmark."));
        }
    }

    // -- GET /ai/enrichment/progress -------------------------------------------

    public async Task<EnrichmentProgressDto?> GetEnrichmentProgressAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<EnrichmentProgressDto>("/ai/enrichment/progress", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /ai/enrichment/progress failed");
            LastError = ex.Message;
            return null;
        }
    }

    // -- GET /ai/resources -----------------------------------------------------

    public async Task<ResourceSnapshotDto?> GetResourceSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ResourceSnapshotDto>("/ai/resources", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /ai/resources failed");
            LastError = ex.Message;
            return null;
        }
    }

}
