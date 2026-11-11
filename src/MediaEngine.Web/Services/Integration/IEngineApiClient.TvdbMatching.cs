using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<TvdbScopedMatchCandidatesDto?> GetTvdbScopedMatchCandidatesAsync(
        Guid entityId, string scopeId, int? seasonNumber, string? seasonType = null,
        CancellationToken ct = default, string? seriesId = null);

    Task<TvdbScopedMatchResultDto?> ApplyTvdbScopedMatchAsync(
        Guid entityId, string scopeId, ApplyTvdbScopedMatchDto request, CancellationToken ct = default);

    Task<TvdbShowOrderPreviewDto?> GetTvdbShowOrderPreviewAsync(
        Guid entityId, string seasonType, CancellationToken ct = default);

    Task<TvdbShowOrderResultDto?> ApplyTvdbShowOrderAsync(
        Guid entityId, ApplyTvdbShowOrderDto request, CancellationToken ct = default);

    /// <summary>The Match tool's Show column: any TheTVDB show whose name fits the words typed.</summary>
    Task<TvMatchBrowserShowsDto?> SearchTvMatchBrowserShowsAsync(Guid entityId, string query, CancellationToken ct = default);

    /// <summary>The Match tool's Season column: the seasons of one show, in TheTVDB's default order.</summary>
    Task<TvMatchBrowserSeasonsDto?> GetTvMatchBrowserSeasonsAsync(Guid entityId, string seriesId, CancellationToken ct = default);

    /// <summary>The Match tool's Episode column: the real episode records of one season, each saying whether a library file holds it.</summary>
    Task<TvMatchBrowserEpisodesDto?> GetTvMatchBrowserEpisodesAsync(Guid entityId, string seriesId, int seasonNumber, CancellationToken ct = default);

    /// <summary>The one episode of a show that carries an absolute number (a file named for the show's 125th episode), as the provider's season and episode. Null when the show has none or the Engine could not be reached.</summary>
    Task<TvMatchBrowserEpisodeDto?> GetTvMatchBrowserAbsoluteEpisodeAsync(Guid entityId, string seriesId, int absoluteNumber, CancellationToken ct = default);

    /// <summary>The Match tool's starting point: what the item's file name says (show, season, episode) and the provider's best guess for it.</summary>
    Task<EditorMatchContextDto?> GetEditorMatchContextAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>"What is this file?": whether the item is one file, the guess its name gives for an Extra's kind and place, and the other unsorted files in its folder. A read; null when the Engine could not be reached.</summary>
    Task<EditorFileSortingDto?> GetEditorFileSortingAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>"Match the other episodes in this season by number": which other episodes of the folder season the chosen season would take. A read.</summary>
    Task<EpisodeSiblingMatchPreviewDto?> PreviewEpisodeSiblingMatchAsync(Guid entityId, string seriesId, int seasonNumber, CancellationToken ct = default);

    /// <summary>"Re-match seasons and episodes": how many of the show's episodes the chosen TheTVDB show has a slot for, and how many it lacks. A read.</summary>
    Task<ShowChildRematchPreviewDto?> PreviewShowChildRematchAsync(Guid entityId, string seriesId, CancellationToken ct = default);

    /// <summary>The Engine's finishing work for a saved item by named step (the saved ribbon). A read; null when the Engine could not be reached.</summary>
    Task<EditorFollowUpDto?> GetEditorFollowUpAsync(Guid entityId, CancellationToken ct = default);
}
