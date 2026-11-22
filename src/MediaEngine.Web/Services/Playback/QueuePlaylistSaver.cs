using System.Globalization;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Playback;

/// <summary>The songs a queue would save and how many of its current and upcoming entries were left out.</summary>
public sealed record QueuePlaylistSelection(IReadOnlyList<Guid> SongIds, int Skipped);

/// <summary>
/// "Save queue as playlist" (Listen playlists spec WP6): turns the current song and the songs still to come into a new hand-made playlist,
/// in play order. It only reads the queue snapshot it is given and never sends a playback command, so the queue, its position and whether it
/// is playing are never touched. Only library songs go in (a video or an audiobook in the queue, or an entry with no library work, is
/// skipped and counted); songs already played (before the current one) are not saved. A song queued twice is saved once, as a playlist
/// cannot hold the same song twice.
/// </summary>
public static class QueuePlaylistSaver
{
    /// <summary>The current and upcoming library songs in play order, and how many current or upcoming entries were left out because they are not library songs.</summary>
    public static QueuePlaylistSelection Select(ListenPlaybackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var songs = new List<Guid>();
        var seen = new HashSet<Guid>();
        var skipped = 0;
        if (snapshot.CurrentIndex < 0)
        {
            return new QueuePlaylistSelection(songs, skipped);
        }

        for (var index = snapshot.CurrentIndex; index < snapshot.Queue.Count; index++)
        {
            var item = snapshot.Queue[index];
            if (item.PlaybackExperience != PlaybackExperience.Music || item.WorkId == Guid.Empty)
            {
                skipped++;
                continue;
            }

            if (seen.Add(item.WorkId))
            {
                songs.Add(item.WorkId);
            }
        }

        return new QueuePlaylistSelection(songs, skipped);
    }

    /// <summary>The name offered in the name step, a plain "Queue, 19/11/2026" in the person's own date style.</summary>
    public static string DefaultName(IStringLocalizer<SharedStrings> strings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return strings["QueueSave_DefaultName", now.LocalDateTime.ToString("d", CultureInfo.CurrentCulture)].Value;
    }

    /// <summary>Makes the playlist and says in one sentence how it went. A failure says what failed and that the queue was not changed.</summary>
    public static async Task<PlaylistFlowNotice> SaveAsync(
        IEngineApiClient api, IStringLocalizer<SharedStrings> strings, ListenPlaybackSnapshot snapshot, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(strings);
        var selection = Select(snapshot);
        if (selection.SongIds.Count == 0)
        {
            return new PlaylistFlowNotice(strings["QueueSave_NothingToSave"].Value, AppSeverity.Warning);
        }

        PlaylistAddResult result;
        try
        {
            result = await PlaylistMembership.CreateAsync(api, name, selection.SongIds, snapshot.ProfileId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return Failed(strings);
        }

        return Describe(result, selection, strings);
    }

    /// <summary>"Saved 12 songs as Road trip", then, when some entries were left out, how many.</summary>
    public static PlaylistFlowNotice Describe(PlaylistAddResult result, QueuePlaylistSelection selection, IStringLocalizer<SharedStrings> strings)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(strings);
        if (!result.Created)
        {
            return Failed(strings);
        }

        var saved = result.Added == 1
            ? strings["QueueSave_OneSaved", result.PlaylistName].Value
            : strings["QueueSave_ManySaved", result.Added, result.PlaylistName].Value;
        if (selection.Skipped == 0)
        {
            return new PlaylistFlowNotice(saved, AppSeverity.Success);
        }

        var left = selection.Skipped == 1
            ? strings["QueueSave_SkippedOne"].Value
            : strings["QueueSave_SkippedMany", selection.Skipped].Value;
        return new PlaylistFlowNotice($"{saved}. {left}", AppSeverity.Warning);
    }

    private static PlaylistFlowNotice Failed(IStringLocalizer<SharedStrings> strings) =>
        new(strings["QueueSave_Failed"].Value, AppSeverity.Error);
}
