using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>What the person picked in the playlist list: an existing hand-made playlist, or "New playlist" with the name typed.</summary>
public sealed record PlaylistChoice(Guid? PlaylistId, string Name)
{
    public bool IsNew => PlaylistId is null;
}

/// <summary>
/// How an add to a playlist went: how many songs went in, how many were already there, how many the Engine refused. <c>Creating</c> says the
/// request was "New playlist" (so a failure is worded as "could not be created"); <c>Created</c> says that playlist was made.
/// </summary>
public sealed record PlaylistAddResult(string PlaylistName, int Added, int AlreadyThere, int Failed, bool Created, bool Creating = false);

/// <summary>
/// Putting several songs into a hand-made playlist (Listen music spec D9, D10, D15). One place for the selection bar and, later, drag and
/// drop: songs already in the playlist are skipped (and counted), the rest are added one by one through the Engine's add action, and a
/// new playlist is created already holding the songs. The Engine checks who may edit the playlist on every call.
/// </summary>
public static class PlaylistMembership
{
    /// <summary>
    /// The Engine reads a collection's items 250 at a time at most; a longer playlist is checked against its first 250, and the Engine
    /// itself never stores a song twice.
    /// </summary>
    private const int PlaylistReadLimit = 250;

    /// <summary>The icon a playlist made from Listen carries, as the rail's New playlist gives it.</summary>
    private static readonly string PlaylistIcon = AppMaterialIcons.Outlined.QueueMusic;

    /// <summary>
    /// The songs the ticked rows stand for: a ticked song is itself; a ticked album is every song it holds in disc and track order (spec
    /// D15). An album from the Listen Albums list is found by its name and artist, the way its own page finds it, because that list's id is
    /// not always a collection id; an album row that carries a collection id falls back to that. Each song appears once, in the order the
    /// rows were ticked.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ResolveSongIdsAsync(
        IEngineApiClient api, IReadOnlyList<BulkEditRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(rows);
        var ids = new List<Guid>();
        foreach (var row in rows)
        {
            if (row.Kind != DetailEntityType.MusicAlbum)
            {
                ids.Add(row.Id);
                continue;
            }

            var album = string.IsNullOrWhiteSpace(row.Title)
                ? null
                : await api.GetSystemViewGroupDetailAsync("album", row.Title, "Music", row.Creator, ct);
            album ??= await api.GetCollectionGroupDetailAsync(row.Id, ct);
            if (album is not null)
            {
                // Songs the library does not hold (an album's retail track list) have nothing to add.
                ids.AddRange(album.Works
                    .Where(work => work.IsOwned)
                    .OrderBy(work => work.DiscNumber ?? 0)
                    .ThenBy(work => TrackOf(work.TrackNumber, work.Ordinal))
                    .ThenBy(work => work.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(work => work.WorkId));
            }
        }

        return [.. ids.Where(id => id != Guid.Empty).Distinct()];
    }

    /// <summary>Adds the songs to an existing playlist, skipping the ones it already holds.</summary>
    public static async Task<PlaylistAddResult> AddAsync(
        IEngineApiClient api, Guid playlistId, string playlistName, IReadOnlyList<Guid> workIds, Guid? profileId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(workIds);
        var wanted = workIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var existing = (await api.GetCollectionItemsAsync(playlistId, PlaylistReadLimit, profileId, ct))
            .Select(item => item.WorkId)
            .ToHashSet();

        var added = 0;
        var already = 0;
        var failed = 0;
        foreach (var workId in wanted)
        {
            if (existing.Contains(workId))
            {
                already++;
            }
            else if (await api.AddCollectionItemAsync(playlistId, workId, profileId, ct))
            {
                added++;
            }
            else
            {
                failed++;
            }
        }

        return new PlaylistAddResult(playlistName, added, already, failed, Created: false);
    }

    /// <summary>Makes a new hand-made playlist already holding the songs (the Engine call the rail's New playlist uses).</summary>
    public static async Task<PlaylistAddResult> CreateAsync(
        IEngineApiClient api, string name, IReadOnlyList<Guid> workIds, Guid? profileId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(workIds);
        var wanted = workIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var id = await api.CreateCollectionWithItemsAsync(
            name,
            null,
            PlaylistIcon,
            "Playlist",
            new CollectionRuleDefinitionViewModel(),
            "title",
            "asc",
            "private",
            wanted,
            profileId,
            ct);
        return id is null
            ? new PlaylistAddResult(name, 0, 0, wanted.Count, Created: false, Creating: true)
            : new PlaylistAddResult(name, wanted.Count, 0, 0, Created: true, Creating: true);
    }

    private static int TrackOf(string? trackNumber, int? ordinal)
    {
        if (!string.IsNullOrWhiteSpace(trackNumber))
        {
            var digits = new string([.. trackNumber.Trim().TakeWhile(char.IsAsciiDigit)]);
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }

        return ordinal ?? int.MaxValue;
    }
}
