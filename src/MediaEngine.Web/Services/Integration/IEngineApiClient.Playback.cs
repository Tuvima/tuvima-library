using System.Text.Json;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Progress;
using MediaEngine.Contracts.Reading;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<PlayerStateDto?> ReorderPlayerQueueAsync(PlayerQueueMutationDto request, CancellationToken ct = default);
    Task<bool> RemovePlayerQueueItemAsync(Guid queueItemId, PlayerQueueMutationDto request, CancellationToken ct = default);

    Task<PlaybackManifestDto?> GetPlaybackManifestAsync(
        Guid assetId,
        string client = "web",
        Guid? profileId = null,
        CancellationToken ct = default,
        PlaybackConnectionContextDto? connection = null);

    Task<PlayerStateDto?> GetPlayerStateAsync(Guid? profileId = null, string? deviceId = null, string client = "web", CancellationToken ct = default);

    Task<PlayerCapabilitiesDto?> GetPlayerCapabilitiesAsync(CancellationToken ct = default);

    Task<PlayerStateDto?> ReplacePlayerQueueAsync(PlayerQueueMutationDto request, CancellationToken ct = default);

    Task<PlayerStateDto?> AddPlayerQueueItemsAsync(PlayerQueueMutationDto request, CancellationToken ct = default);

    Task<PlayerStateDto?> SendPlayerCommandAsync(PlayerCommandRequestDto request, CancellationToken ct = default);

    Task<PlayerStateDto?> PostPlayerHeartbeatAsync(PlayerHeartbeatDto request, CancellationToken ct = default);

    Task<PlayerStateDto?> TakeOverPlayerSessionAsync(PlayerSessionTakeoverRequestDto request, CancellationToken ct = default);

    Task<IReadOnlyList<AudiobookListenHistoryItemDto>> GetAudiobookListenHistoryAsync(Guid workId, Guid? profileId = null, int limit = 25, CancellationToken ct = default);

    Task<AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>> GetAudiobookBookmarksWithOutcomeAsync(Guid workId, Guid profileId, CancellationToken ct = default);

    Task<AudiobookBookmarkOperationResult<AudiobookBookmarkDto>> CreateAudiobookBookmarkWithOutcomeAsync(Guid workId, CreateAudiobookBookmarkRequestDto request, Guid profileId, CancellationToken ct = default);

    Task<AudiobookBookmarkOperationResult<bool>> DeleteAudiobookBookmarkWithOutcomeAsync(Guid bookmarkId, Guid profileId, CancellationToken ct = default);

    /// <summary>The bookmark packs the profile imported on a title, with their read-only pack bookmarks.</summary>
    Task<IReadOnlyList<BookmarkPackDto>> GetBookmarkPacksAsync(Guid workId, Guid profileId, CancellationToken ct = default);

    /// <summary>Builds a pack file from some of the profile's own bookmarks. Null when the Engine refused; <see cref="LastError"/> says why.</summary>
    Task<BookmarkPackFileDto?> ExportBookmarkPackAsync(Guid workId, Guid profileId, BookmarkPackExportRequestDto request, CancellationToken ct = default);

    /// <summary>Reads a pack file and says whether it is for this title. Null when the Engine refused; <see cref="LastError"/> says why.</summary>
    Task<BookmarkPackPreviewDto?> InspectBookmarkPackAsync(Guid workId, Guid profileId, BookmarkPackImportRequestDto request, CancellationToken ct = default);

    /// <summary>Adds a pack's bookmarks to the title. Null when the Engine refused; <see cref="LastError"/> says why.</summary>
    Task<BookmarkPackDto?> ImportBookmarkPackAsync(Guid workId, Guid profileId, BookmarkPackImportRequestDto request, CancellationToken ct = default);

    Task<bool> SetBookmarkPackEnabledAsync(Guid packId, Guid profileId, bool isEnabled, CancellationToken ct = default);

    Task<bool> DeleteBookmarkPackAsync(Guid packId, Guid profileId, CancellationToken ct = default);

    Task<IReadOnlyList<AudiobookChapterTitleOverrideDto>> GetAudiobookChapterTitleOverridesAsync(Guid workId, Guid? assetId = null, CancellationToken ct = default);

    Task<AudiobookChapterTitleOverrideDto?> UpsertAudiobookChapterTitleOverrideAsync(Guid workId, UpsertAudiobookChapterTitleOverrideRequestDto request, CancellationToken ct = default);

    Task<bool> DeleteAudiobookChapterTitleOverrideAsync(Guid workId, Guid assetId, int chapterIndex, CancellationToken ct = default);

    Task<IReadOnlyList<TextTrackDto>> GetTextTracksAsync(Guid assetId, CancellationToken ct = default);

    Task<RefreshTextTracksResponse?> RefreshTextTracksAsync(Guid assetId, string kind, CancellationToken ct = default);

    Task<RefreshTextTracksResponse?> ImportTextTrackAsync(
        Guid assetId, string kind, string language, string fileName, string contentType, Stream content, CancellationToken ct = default);

    Task<bool> SetPreferredTextTrackAsync(Guid assetId, Guid trackId, CancellationToken ct = default);

    Task<string?> GetLyricsAsync(Guid assetId, CancellationToken ct = default);
    Task<string?> GetTextTrackContentAsync(Guid assetId, Guid trackId, CancellationToken ct = default);

    Task<List<EncodeJobDto>> GetEncodeJobsAsync(CancellationToken ct = default);

    Task<bool> CancelEncodeJobAsync(Guid jobId, CancellationToken ct = default);

    Task<PlaybackDiagnosticsDto?> GetPlaybackDiagnosticsAsync(CancellationToken ct = default);

    Task<TranscodingSettings?> GetTranscodingSettingsAsync(CancellationToken ct = default);

    Task<TranscodingSettings?> SaveTranscodingSettingsAsync(TranscodingSettings settings, CancellationToken ct = default);

    Task<UserPlaybackSettingsDto?> GetPlaybackSettingsAsync(Guid profileId, CancellationToken ct = default);

    Task<UserPlaybackSettingsDto?> UpdatePlaybackSettingsAsync(Guid profileId, UserPlaybackSettingsDto settings, CancellationToken ct = default);

    /// <summary>Chooses the signed-in profile's spoiler level (<c>up_to_progress</c> or <c>everything</c>); null when the Engine did not accept it.</summary>
    Task<ProfileSpoilerPreferenceDto?> SaveSpoilerLevelAsync(Guid profileId, string spoilerLevel, CancellationToken ct = default);

    // ── Progress & Journey (/progress) ─────────────────────────────────

    /// <summary>GET /progress/journey?userId={id}&amp;limit= — incomplete items with Work+Collection context.
    /// Pass collectionId to filter server-side to assets belonging to a specific collection.</summary>
    Task<List<JourneyItemViewModel>> GetJourneyAsync(Guid? userId = null, int limit = 5, Guid? collectionId = null, CancellationToken ct = default);

    /// <summary>GET /progress/{assetId} - current progress for an asset.</summary>
    Task<UserStateResponse?> GetProgressAsync(Guid assetId, CancellationToken ct = default);
    /// <summary>PUT /progress/{assetId} — upsert progress for a media asset.</summary>
    Task<bool> SaveProgressAsync(Guid assetId, Guid? userId = null, double progressPct = 0,
        Dictionary<string, string>? extendedProperties = null, CancellationToken ct = default);

    // -- EPUB Reader (/read, /reader) ----------------------------------

    /// <summary>GET /read/{assetId}/metadata  -  book metadata.</summary>
    Task<EpubBookMetadataDto?> GetBookMetadataAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>GET /read/{assetId}/toc  -  table of contents.</summary>
    Task<List<EpubTocEntryDto>> GetTableOfContentsAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>GET /read/{assetId}/chapter/{index}  -  chapter HTML.</summary>
    Task<EpubChapterContentDto?> GetChapterContentAsync(Guid assetId, int chapterIndex, CancellationToken ct = default);

    /// <summary>GET /read/{assetId}/search?q={query}  -  full-text search.</summary>
    Task<List<EpubSearchHitDto>> SearchEpubAsync(Guid assetId, string query, CancellationToken ct = default);

    /// <summary>GET /read/resolve/{workId}  -  resolve Work ID to Asset ID.</summary>
    Task<Guid?> ResolveWorkToAssetAsync(Guid workId, CancellationToken ct = default);

    /// <summary>GET /reader/{assetId}/bookmarks  -  list bookmarks.</summary>
    Task<List<ReaderBookmarkDto>> GetBookmarksAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>POST /reader/{assetId}/bookmarks  -  create bookmark.</summary>
    Task<ReaderBookmarkDto?> CreateBookmarkAsync(Guid assetId, int chapterIndex, string? cfiPosition, string? label, CancellationToken ct = default);

    /// <summary>DELETE /reader/bookmarks/{id}  -  delete bookmark.</summary>
    Task<bool> DeleteBookmarkAsync(Guid bookmarkId, CancellationToken ct = default);

    /// <summary>GET /reader/{assetId}/highlights  -  list highlights.</summary>
    Task<List<ReaderHighlightDto>> GetHighlightsAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>POST /reader/{assetId}/highlights  -  create a highlight anchored by an EPUB CFI range.</summary>
    Task<ReaderHighlightDto?> CreateHighlightAsync(Guid assetId, string cfiRange, string selectedText, string? color, string? noteText, CancellationToken ct = default);

    /// <summary>PUT /reader/highlights/{id}  -  change a highlight's colour and/or note.</summary>
    Task<bool> UpdateHighlightAsync(Guid highlightId, string? color, string? noteText, CancellationToken ct = default);

    /// <summary>DELETE /reader/highlights/{id}  -  delete a highlight.</summary>
    Task<bool> DeleteHighlightAsync(Guid highlightId, CancellationToken ct = default);

    /// <summary>GET /reader/{assetId}/statistics  -  reading statistics.</summary>
    Task<ReaderStatisticsDto?> GetReadingStatisticsAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>PUT /reader/{assetId}/statistics  -  update reading statistics.</summary>
    Task<bool> UpdateReadingStatisticsAsync(Guid assetId, UpdateReaderStatisticsRequestDto stats, CancellationToken ct = default);

    /// <summary>

}
