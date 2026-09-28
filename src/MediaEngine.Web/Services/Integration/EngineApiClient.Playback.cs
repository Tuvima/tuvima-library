using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Progress;
using MediaEngine.Contracts.Reading;
using MediaEngine.Contracts.Reports;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Branding;
using MediaEngine.Web.Services.Integration.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<PlaybackManifestDto?> GetPlaybackManifestAsync(
        Guid assetId,
        string client = "web",
        Guid? profileId = null,
        CancellationToken ct = default,
        PlaybackConnectionContextDto? connection = null)
    {
        var endpoint = $"GET /playback/{assetId}/manifest";
        try
        {
            var query = new List<string>
            {
                $"client={Uri.EscapeDataString(string.IsNullOrWhiteSpace(client) ? "web" : client)}",
            };
            if (profileId.HasValue)
            {
                query.Add($"profileId={profileId.Value:D}");
            }

            if (connection is not null)
            {
                query.Add($"connectionPath={Uri.EscapeDataString(connection.ConnectionPath)}");
                if (!string.IsNullOrWhiteSpace(connection.RemoteConnectivityProvider))
                {
                    query.Add($"provider={Uri.EscapeDataString(connection.RemoteConnectivityProvider)}");
                }
                if (connection.EstimatedBandwidthMbps.HasValue)
                {
                    query.Add($"bandwidthMbps={connection.EstimatedBandwidthMbps.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
                if (connection.LatencyMs.HasValue)
                {
                    query.Add($"latencyMs={connection.LatencyMs.Value}");
                }
                if (connection.RoomId.HasValue)
                {
                    query.Add($"roomId={connection.RoomId.Value:D}");
                }
            }

            var response = await _http.GetAsync($"/api/v1/playback/{assetId}/manifest?{string.Join("&", query)}", ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return null;
            }

            var manifest = await response.Content.ReadFromJsonAsync<PlaybackManifestDto>(cancellationToken: ct);
            ClearFailure(endpoint);
            return manifest;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /playback/{AssetId}/manifest failed", assetId);
            RecordExceptionFailure(endpoint, ex);
            return null;
        }
    }

    public Task<PlayerStateDto?> GetPlayerStateAsync(
        Guid? profileId = null,
        string? deviceId = null,
        string client = "web",
        CancellationToken ct = default) =>
        GetAsync<PlayerStateDto?>(
            "GET /player/state",
            "/api/v1/player/state",
            () => null,
            new Dictionary<string, string?>
            {
                ["profileId"] = profileId?.ToString("D"),
                ["deviceId"] = deviceId,
                ["client"] = string.IsNullOrWhiteSpace(client) ? "web" : client,
            },
            ct: ct);

    public Task<PlayerCapabilitiesDto?> GetPlayerCapabilitiesAsync(CancellationToken ct = default) =>
        GetAsync<PlayerCapabilitiesDto?>(
            "GET /player/capabilities",
            "/api/v1/player/capabilities",
            () => null,
            ct: ct);

    // Migrated to the shared PostAsync<TReq,TRes> helper (stage 5B wave 1 proof).
    public Task<PlayerStateDto?> ReplacePlayerQueueAsync(PlayerQueueMutationDto request, CancellationToken ct = default) =>
        PostAsync<PlayerQueueMutationDto, PlayerStateDto>("POST /api/v1/player/queue/replace", "/api/v1/player/queue/replace", request, ct: ct);

    // Migrated to the shared PostAsync<TReq,TRes> helper (stage 5B wave 2). The hand-written
    // PostPlayerMutationAsync helper this used to call is now dead code and has been removed.
    public Task<PlayerStateDto?> AddPlayerQueueItemsAsync(PlayerQueueMutationDto request, CancellationToken ct = default) =>
        PostAsync<PlayerQueueMutationDto, PlayerStateDto>("POST /api/v1/player/queue/items", "/api/v1/player/queue/items", request, ct: ct);

    // Migrated to the shared PostAsync<TReq,TRes> helper (stage 5B wave 1 proof).
    public Task<PlayerStateDto?> SendPlayerCommandAsync(PlayerCommandRequestDto request, CancellationToken ct = default) =>
        PostAsync<PlayerCommandRequestDto, PlayerStateDto>("POST /api/v1/player/command", "/api/v1/player/command", request, ct: ct);

    // Migrated to the shared PostAsync<TReq,TRes> helper (stage 5B wave 1 proof).
    public Task<PlayerStateDto?> PostPlayerHeartbeatAsync(PlayerHeartbeatDto request, CancellationToken ct = default) =>
        PostAsync<PlayerHeartbeatDto, PlayerStateDto>("POST /api/v1/player/heartbeat", "/api/v1/player/heartbeat", request, ct: ct);

    public Task<PlayerStateDto?> TakeOverPlayerSessionAsync(PlayerSessionTakeoverRequestDto request, CancellationToken ct = default) =>
        PostAsync<PlayerSessionTakeoverRequestDto, PlayerStateDto>("POST /api/v1/player/session/takeover", "/api/v1/player/session/takeover", request, ct: ct);

    // Migrated to the shared GetAsync<T> fallback-overload helper (stage 5B wave 2).
    public Task<IReadOnlyList<AudiobookListenHistoryItemDto>> GetAudiobookListenHistoryAsync(Guid workId, Guid? profileId = null, int limit = 25, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AudiobookListenHistoryItemDto>>(
            "GET /player/audiobooks/{workId}/history",
            $"/api/v1/player/audiobooks/{workId:D}/history",
            () => [],
            new Dictionary<string, string?>
            {
                ["limit"] = Math.Clamp(limit, 1, 50).ToString(),
                ["profileId"] = profileId?.ToString("D"),
            },
            ct: ct);

    // Migrated to the shared GetAsync<T> fallback-overload helper (stage 5B wave 2).
    public Task<IReadOnlyList<AudiobookBookmarkDto>> GetAudiobookBookmarksAsync(Guid workId, Guid? profileId = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AudiobookBookmarkDto>>(
            "GET /player/audiobooks/{workId}/bookmarks",
            $"/api/v1/player/audiobooks/{workId:D}/bookmarks",
            () => [],
            new Dictionary<string, string?> { ["profileId"] = profileId?.ToString("D") },
            ct: ct);

    // Migrated to the shared PostAsync<TReq,TRes> helper (stage 5B wave 2). The optional profileId
    // query parameter now routes through BuildEndpointPath instead of a hand-built suffix string.
    public Task<AudiobookBookmarkDto?> CreateAudiobookBookmarkAsync(Guid workId, CreateAudiobookBookmarkRequestDto request, Guid? profileId = null, CancellationToken ct = default) =>
        PostAsync<CreateAudiobookBookmarkRequestDto, AudiobookBookmarkDto>(
            "POST /player/audiobooks/{workId}/bookmarks",
            BuildEndpointPath($"/api/v1/player/audiobooks/{workId:D}/bookmarks", new Dictionary<string, string?> { ["profileId"] = profileId?.ToString("D") }),
            request,
            ct: ct);

    // Migrated to the shared DeleteAsync helper (stage 5B wave 2).
    public Task<bool> DeleteAudiobookBookmarkAsync(Guid bookmarkId, Guid? profileId = null, CancellationToken ct = default) =>
        DeleteAsync(
            "DELETE /player/audiobooks/bookmarks/{bookmarkId}",
            BuildEndpointPath($"/api/v1/player/audiobooks/bookmarks/{bookmarkId:D}", new Dictionary<string, string?> { ["profileId"] = profileId?.ToString("D") }),
            ct: ct);

    // Migrated to the shared GetAsync<T> fallback-overload helper (stage 5B wave 2).
    public Task<IReadOnlyList<AudiobookChapterTitleOverrideDto>> GetAudiobookChapterTitleOverridesAsync(Guid workId, Guid? assetId = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AudiobookChapterTitleOverrideDto>>(
            "GET /player/audiobooks/{workId}/chapter-overrides",
            $"/api/v1/player/audiobooks/{workId:D}/chapter-overrides",
            () => [],
            new Dictionary<string, string?> { ["assetId"] = assetId?.ToString("D") },
            ct: ct);

    // Migrated to the shared PostAsync<TReq,TRes> helper (stage 5B wave 2).
    public Task<AudiobookChapterTitleOverrideDto?> UpsertAudiobookChapterTitleOverrideAsync(Guid workId, UpsertAudiobookChapterTitleOverrideRequestDto request, CancellationToken ct = default) =>
        PostAsync<UpsertAudiobookChapterTitleOverrideRequestDto, AudiobookChapterTitleOverrideDto>(
            "POST /player/audiobooks/{workId}/chapter-overrides",
            $"/api/v1/player/audiobooks/{workId:D}/chapter-overrides",
            request,
            ct: ct);

    // Migrated to the shared DeleteAsync helper (stage 5B wave 2).
    public Task<bool> DeleteAudiobookChapterTitleOverrideAsync(Guid workId, Guid assetId, int chapterIndex, CancellationToken ct = default) =>
        DeleteAsync(
            "DELETE /player/audiobooks/{workId}/chapter-overrides/{assetId}/{chapterIndex}",
            $"/api/v1/player/audiobooks/{workId:D}/chapter-overrides/{assetId:D}/{chapterIndex}",
            ct: ct);

    public async Task<IReadOnlyList<TextTrackDto>> GetTextTracksAsync(Guid assetId, CancellationToken ct = default)
    {
        try
        {
            var tracks = await _http.GetFromJsonAsync<List<TextTrackDto>>($"/stream/{assetId}/text-tracks", ct);
            if (tracks is null)
            {
                return [];
            }

            foreach (var track in tracks)
            {
                track.Url = AbsoluteUrl(track.Url);
            }

            return tracks;
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GET /stream/{AssetId}/text-tracks failed", assetId);
            return [];
        }
    }

    public Task<string?> GetLyricsAsync(Guid assetId, CancellationToken ct = default) =>
        GetRawTextAsync("GET /stream/{assetId}/lyrics", $"/stream/{assetId:D}/lyrics", ct);

    public Task<string?> GetTextTrackContentAsync(Guid assetId, Guid trackId, CancellationToken ct = default) =>
        GetRawTextAsync("GET /stream/{assetId}/text-tracks/{trackId}", $"/stream/{assetId:D}/text-tracks/{trackId:D}", ct);

    public async Task<RefreshTextTracksResponse?> RefreshTextTracksAsync(Guid assetId, string kind, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/stream/{assetId}/text-tracks/refresh?kind={Uri.EscapeDataString(kind)}");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var queued = await response.Content.ReadFromJsonAsync<RefreshTextTracksResponse>(cancellationToken: ct);
            if (queued?.operation_id is not { } operationId)
            {
                return queued;
            }

            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                var detail = await GetMediaOperationAsync(operationId, ct);
                var operation = detail?.Operation;
                if (operation is null || operation.Status is "queued" or "leased" or "running" or "pending" or "interrupted")
                {
                    continue;
                }

                return queued with
                {
                    refreshed = operation.Status == "succeeded",
                    status = operation.Status,
                    message = operation.ResultSummary
                        ?? operation.LastError
                        ?? operation.MissingReason
                        ?? "The text-track refresh finished.",
                };
            }

            return queued with
            {
                status = "queued",
                message = "The text-track refresh is still running in the background. Its result will appear in activity history.",
            };
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /stream/{AssetId}/text-tracks/refresh failed", assetId);
            return null;
        }
    }

    public async Task<RefreshTextTracksResponse?> ImportTextTrackAsync(
        Guid assetId,
        string kind,
        string language,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken ct = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(kind), "kind");
            form.Add(new StringContent(language), "language");
            var fileContent = new StreamContent(content);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "text/plain" : contentType);
            form.Add(fileContent, "file", fileName);
            using var response = await _http.PostAsync($"/stream/{assetId:D}/text-tracks/import", form, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<RefreshTextTracksResponse>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /stream/{AssetId}/text-tracks/import failed", assetId);
            return null;
        }
    }

    public async Task<bool> SetPreferredTextTrackAsync(Guid assetId, Guid trackId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsync(
                $"/stream/{assetId:D}/text-tracks/{trackId:D}/preferred",
                content: null,
                ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST preferred text track failed for {AssetId}/{TrackId}", assetId, trackId);
            return false;
        }
    }

    public async Task<List<EncodeJobDto>> GetEncodeJobsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<EncodeJobDto>>("/api/v1/playback/encode/jobs", ct) ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /playback/encode/jobs failed");
            return [];
        }
    }

    public async Task<bool> CancelEncodeJobAsync(Guid jobId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync($"/api/v1/playback/encode/jobs/{jobId}/cancel", new { }, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /playback/encode/jobs/{JobId}/cancel failed", jobId);
            return false;
        }
    }

    public async Task<PlaybackDiagnosticsDto?> GetPlaybackDiagnosticsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<PlaybackDiagnosticsDto>("/api/v1/playback/diagnostics", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /playback/diagnostics failed");
            return null;
        }
    }

    // -- GET /system/status ----------------------------------------------------

    public async Task<TranscodingSettings?> GetTranscodingSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TranscodingSettings>("/settings/transcoding", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /settings/transcoding failed");
            return null;
        }
    }

    public async Task<TranscodingSettings?> SaveTranscodingSettingsAsync(TranscodingSettings settings, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PutAsJsonAsync("/settings/transcoding", settings, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TranscodingSettings>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /settings/transcoding failed");
            return null;
        }
    }

    public async Task<UserPlaybackSettingsDto?> GetPlaybackSettingsAsync(Guid profileId, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<UserPlaybackSettingsDto>(
                $"/profiles/{profileId:D}/settings/playback", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "GET /profiles/{ProfileId}/settings/playback failed", profileId);
            return null;
        }
    }

    public async Task<UserPlaybackSettingsDto?> UpdatePlaybackSettingsAsync(
        Guid profileId,
        UserPlaybackSettingsDto settings,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PutAsJsonAsync(
                $"/profiles/{profileId:D}/settings/playback", settings, ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                LastError = $"HTTP {(int)response.StatusCode}: {detail}";
                return null;
            }

            return await response.Content.ReadFromJsonAsync<UserPlaybackSettingsDto>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "PUT /profiles/{ProfileId}/settings/playback failed", profileId);
            return null;
        }
    }

    // -- Progress & Journey (/progress) ----------------------------------

    public async Task<List<JourneyItemViewModel>> GetJourneyAsync(
        Guid? userId = null, int limit = 5, Guid? collectionId = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"/api/v1/progress/journey?limit={limit}";
            if (userId.HasValue)
            {
                url += $"&userId={userId.Value}";
            }

            if (collectionId.HasValue)
            {
                url += $"&collectionId={collectionId.Value}";
            }

            var raw = await _http.GetFromJsonAsync<List<JourneyItemDto>>(url, ct);
            return raw?.Select(j => new JourneyItemViewModel
            {
                AssetId = j.AssetId,
                WorkId = j.WorkId,
                CollectionId = j.CollectionId,
                Title = j.Title,
                Author = j.Author,
                CoverUrl = j.CoverUrl is not null ? AbsoluteUrl(j.CoverUrl) : null,
                BackgroundUrl = j.BackgroundUrl is not null ? AbsoluteUrl(j.BackgroundUrl) : null,
                BannerUrl = j.BannerUrl is not null ? AbsoluteUrl(j.BannerUrl) : null,
                HeroUrl = j.HeroUrl is not null ? AbsoluteUrl(j.HeroUrl) : null,
                LogoUrl = j.LogoUrl is not null ? AbsoluteUrl(j.LogoUrl) : null,
                CoverWidthPx = j.CoverWidthPx,
                CoverHeightPx = j.CoverHeightPx,
                BackgroundWidthPx = j.BackgroundWidthPx,
                BackgroundHeightPx = j.BackgroundHeightPx,
                BannerWidthPx = j.BannerWidthPx,
                BannerHeightPx = j.BannerHeightPx,
                Narrator = j.Narrator,
                Series = j.Series,
                SeriesPosition = j.SeriesPosition,
                Description = j.Description,
                MediaType = j.MediaType,
                ProgressPct = j.ProgressPct,
                LastAccessed = j.LastAccessed,
                CollectionDisplayName = j.CollectionDisplayName,
                ExtendedProperties = j.ExtendedProperties,
            }).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /progress/journey failed");
            LastError = ex.Message;
            return [];
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Profile, Guid Asset), long> _progressRevisions = new();

    public async Task<bool> SaveProgressAsync(
        Guid assetId, Guid? userId = null, double progressPct = 0,
        Dictionary<string, string>? extendedProperties = null,
        CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                expected_revision = _progressRevisions.GetValueOrDefault((_progressProfile?.ProfileId ?? Guid.Empty, assetId)),
                user_id = userId?.ToString(),
                progress_pct = progressPct,
                extended_properties = extendedProperties,
            };
            var resp = await _http.PutAsJsonAsync($"/api/v1/progress/{assetId}", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            var saved = await resp.Content.ReadFromJsonAsync<UserStateResponse>(ct);
            if (saved is not null)
            {
                _progressRevisions[(_progressProfile?.ProfileId ?? Guid.Empty, assetId)] = saved.Revision;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /progress/{AssetId} failed", assetId);
            LastError = ex.Message;
            return false;
        }
    }

    // -- EPUB Reader (/read, /reader) ----------------------------------

    public async Task<UserStateResponse?> GetProgressAsync(Guid assetId, CancellationToken ct = default)
    {
        try
        {
            // Use GetAsync + manual deserialization so that 404 (no progress recorded)
            // returns null cleanly without throwing HttpRequestException.
            var resp = await _http.GetAsync($"/api/v1/progress/{assetId}", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _progressRevisions.TryRemove((_progressProfile?.ProfileId ?? Guid.Empty, assetId), out _);
                return null;
            }

            resp.EnsureSuccessStatusCode();
            var state = await resp.Content.ReadFromJsonAsync<UserStateResponse>(ct);
            if (state is not null)
            {
                _progressRevisions[(_progressProfile?.ProfileId ?? Guid.Empty, assetId)] = state.Revision;
            }

            return state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /progress/{AssetId} failed", assetId);
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<EpubBookMetadataDto?> GetBookMetadataAsync(Guid assetId, CancellationToken ct = default)
    {
        var endpoint = $"GET /read/{assetId}/metadata";
        try
        {
            var response = await _http.GetAsync($"read/{assetId}/metadata", ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return null;
            }

            var metadata = await response.Content.ReadFromJsonAsync<EpubBookMetadataDto>(cancellationToken: ct);
            ClearFailure(endpoint);
            return metadata;
        }
        catch (Exception ex) { RecordExceptionFailure(endpoint, ex); return null; }
    }

    public async Task<List<EpubTocEntryDto>> GetTableOfContentsAsync(Guid assetId, CancellationToken ct = default)
    {
        var endpoint = $"GET /read/{assetId}/toc";
        try
        {
            var response = await _http.GetAsync($"read/{assetId}/toc", ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return [];
            }

            var toc = await response.Content.ReadFromJsonAsync<List<EpubTocEntryDto>>(cancellationToken: ct) ?? [];
            ClearFailure(endpoint);
            return toc;
        }
        catch (Exception ex) { RecordExceptionFailure(endpoint, ex); return []; }
    }

    public async Task<EpubChapterContentDto?> GetChapterContentAsync(Guid assetId, int chapterIndex, CancellationToken ct = default)
    {
        var endpoint = $"GET /read/{assetId}/chapter/{chapterIndex}";
        try
        {
            var response = await _http.GetAsync($"read/{assetId}/chapter/{chapterIndex}", ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return null;
            }

            var chapter = await response.Content.ReadFromJsonAsync<EpubChapterContentDto>(cancellationToken: ct);
            ClearFailure(endpoint);
            return chapter;
        }
        catch (Exception ex) { RecordExceptionFailure(endpoint, ex); return null; }
    }

    public async Task<List<EpubSearchHitDto>> SearchEpubAsync(Guid assetId, string query, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            return await _http.GetFromJsonAsync<List<EpubSearchHitDto>>($"read/{assetId}/search?q={encoded}", ct) ?? [];
        }
        catch (Exception ex) { LastError = ex.Message; return []; }
    }

    public async Task<Guid?> ResolveWorkToAssetAsync(Guid workId, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<System.Text.Json.JsonElement>($"read/resolve/{workId}", ct);
            if (result.TryGetProperty("assetId", out var prop) && Guid.TryParse(prop.GetString(), out var id))
            {
                return id;
            }

            return null;
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    public async Task<List<ReaderBookmarkDto>> GetBookmarksAsync(Guid assetId, CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync<List<ReaderBookmarkDto>>($"reader/{assetId}/bookmarks", ct) ?? []; }
        catch (Exception ex) { LastError = ex.Message; return []; }
    }

    public async Task<ReaderBookmarkDto?> CreateBookmarkAsync(Guid assetId, int chapterIndex, string? cfiPosition, string? label, CancellationToken ct = default)
    {
        try
        {
            var body = new CreateReaderBookmarkRequestDto(chapterIndex, cfiPosition, label);
            var resp = await _http.PostAsJsonAsync($"reader/{assetId}/bookmarks", body, ct);
            return resp.IsSuccessStatusCode
                ? await resp.Content.ReadFromJsonAsync<ReaderBookmarkDto>(cancellationToken: ct)
                : null;
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    public async Task<bool> DeleteBookmarkAsync(Guid bookmarkId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.DeleteAsync($"reader/bookmarks/{bookmarkId}", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    public async Task<List<ReaderHighlightDto>> GetHighlightsAsync(Guid assetId, CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync<List<ReaderHighlightDto>>($"reader/{assetId}/highlights", ct) ?? []; }
        catch (Exception ex) { LastError = ex.Message; return []; }
    }

    public async Task<ReaderStatisticsDto?> GetReadingStatisticsAsync(Guid assetId, CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync<ReaderStatisticsDto>($"reader/{assetId}/statistics", ct); }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    public async Task<bool> UpdateReadingStatisticsAsync(Guid assetId, UpdateReaderStatisticsRequestDto stats, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync($"reader/{assetId}/statistics", stats, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    public async Task<SubmitReportResponse?> SubmitReportAsync(SubmitReportRequest request, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/reports", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SubmitReportResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /reports failed");
            return null;
        }
    }

    public async Task<List<ReportEntryResponse>> GetReportsForEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<ReportEntryResponse>>($"/reports/entity/{entityId}", ct) ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /reports/entity/{EntityId} failed", entityId);
            return [];
        }
    }

    public async Task<bool> ResolveReportAsync(long activityId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync($"/reports/{activityId}/resolve", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /reports/{ActivityId}/resolve failed", activityId);
            return false;
        }
    }

    public async Task<bool> DismissReportAsync(long activityId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync($"/reports/{activityId}/dismiss", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /reports/{ActivityId}/dismiss failed", activityId);
            return false;
        }
    }

}
