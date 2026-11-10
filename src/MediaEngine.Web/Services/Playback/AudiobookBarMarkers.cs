using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// The bookmark markers on an audiobook's progress bar (unified player D10: "Show bookmarks on the bar" in the audiobook Settings menu, the same
/// per-profile setting the video player and Settings > Playback use). The markers are off by default and drawn only while the setting is on, and they
/// follow the list the Bookmarks panel shows (Everyone or Mine; mockup screen 11).
/// </summary>
public static class AudiobookBarMarkers
{
    /// <summary>The bookmarks the Engine lets the profile see on the book's current file (their own and Everyone's). A failed load leaves the bar without markers.</summary>
    public static async Task<IReadOnlyList<AudiobookBookmarkDto>> LoadAsync(
        IEngineApiClient? api, Guid workId, Guid profileId, Guid assetId, CancellationToken ct)
    {
        if (api is null || workId == Guid.Empty || profileId == Guid.Empty || assetId == Guid.Empty)
        {
            return [];
        }

        var result = await api.GetAudiobookBookmarksWithOutcomeAsync(workId, profileId, ct).ConfigureAwait(false);
        return result is { Outcome: AudiobookBookmarkOperationOutcome.Success, Value: { } saved }
            ? saved.Where(bookmark => bookmark.AssetId == assetId).ToList()
            : [];
    }

    /// <summary>
    /// The markers to draw: the list the panel shows (<paramref name="choice"/>, Everyone by default). When the bar shows one chapter (<paramref name="scope"/>), only that chapter's markers are kept and their
    /// positions are moved to time into the chapter; use <see cref="ToAbsolute"/> to turn a chosen marker back into a book position.
    /// </summary>
    public static IReadOnlyList<PlaybackRailBookmark> ForBar(
        IReadOnlyList<AudiobookBookmarkDto> saved, bool show, string? choice, double duration, AudiobookChapterWindow? scope)
    {
        if (!show || saved.Count == 0)
        {
            return [];
        }

        var markers = PlaybackRailBookmark.ForChoice(PlaybackRailBookmark.From(saved, duration), choice);
        if (scope is null)
        {
            return markers;
        }

        return markers
            .Where(marker => marker.Seconds >= scope.Start && marker.Seconds <= scope.End)
            .Select(marker => marker with { Seconds = scope.Elapsed(marker.Seconds) })
            .ToList();
    }

    /// <summary>The book position of a marker taken from <see cref="ForBar"/>.</summary>
    public static double ToAbsolute(PlaybackRailBookmark marker, AudiobookChapterWindow? scope) =>
        scope is null ? marker.Seconds : scope.ToAbsolute(marker.Seconds);
}
