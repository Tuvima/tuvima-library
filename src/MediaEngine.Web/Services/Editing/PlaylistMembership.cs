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

    /// <summary>The songs the ticked rows stand for, and how many ticked albums could not be read (their songs are missing, not zero).</summary>
    public sealed record SongResolution(IReadOnlyList<Guid> SongIds, int UnreadableAlbums);

    /// <summary>How many album lookups run at once.</summary>
    private const int AlbumLookupParallelism = 4;

    /// <summary>
    /// The songs the ticked rows stand for: a ticked song is itself; a ticked album is every song it holds in disc and track order (spec
    /// D15). An album from the Listen Albums list is found by its title and artist, the way its own page finds it, because that list's id
    /// is not a collection id; an album row from anywhere else carries a collection id and is read by it. Each song appears once, in the
    /// order the rows were ticked. An album the Engine cannot read (or that holds no song in the library) is counted, not skipped silently.
    /// </summary>
    public static async Task<SongResolution> ResolveAsync(
        IEngineApiClient api, IReadOnlyList<BulkEditRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(rows);

        // The albums are read a few at a time (each read can make the Engine fetch a track list from a provider), then put back in tick order.
        using var gate = new SemaphoreSlim(AlbumLookupParallelism);
        var lookups = rows
            .Select((row, index) => (row, index))
            .Where(pair => pair.row.Kind == DetailEntityType.MusicAlbum)
            .Select(pair => ReadAlbumSongsLimitedAsync(api, pair.row, pair.index, gate, ct))
            .ToList();
        var albumSongs = (await Task.WhenAll(lookups)).ToDictionary(pair => pair.Index, pair => pair.Songs);

        var ids = new List<Guid>();
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Kind == DetailEntityType.MusicAlbum)
            {
                ids.AddRange(albumSongs[index]);
            }
            else
            {
                ids.Add(rows[index].Id);
            }
        }

        return new SongResolution(
            [.. ids.Where(id => id != Guid.Empty).Distinct()],
            albumSongs.Values.Count(songs => songs.Count == 0));
    }

    /// <summary>The song ids alone, for callers that do not tell the person about unreadable albums.</summary>
    public static async Task<IReadOnlyList<Guid>> ResolveSongIdsAsync(
        IEngineApiClient api, IReadOnlyList<BulkEditRow> rows, CancellationToken ct = default) =>
        (await ResolveAsync(api, rows, ct)).SongIds;

    private static async Task<(int Index, IReadOnlyList<Guid> Songs)> ReadAlbumSongsLimitedAsync(
        IEngineApiClient api, BulkEditRow row, int index, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return (index, await ReadAlbumSongsAsync(api, row, ct));
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<IReadOnlyList<Guid>> ReadAlbumSongsAsync(IEngineApiClient api, BulkEditRow row, CancellationToken ct)
    {
        // A row from the Albums list is read by name only; its id is not a collection id, so the collection read is never tried with it.
        var album = row.FromAlbumsList
            ? string.IsNullOrWhiteSpace(row.Title) ? null : await api.GetSystemViewGroupDetailAsync("album", row.Title, "Music", row.Creator, ct)
            : await api.GetCollectionGroupDetailAsync(row.Id, ct);
        if (album is null)
        {
            return [];
        }

        // Songs the library does not hold (an album's retail track list) have nothing to add.
        return
        [
            .. album.Works
                .Where(work => work.IsOwned && work.WorkId != Guid.Empty)
                .OrderBy(work => work.DiscNumber ?? 0)
                .ThenBy(work => TrackOf(work.TrackNumber, work.Ordinal))
                .ThenBy(work => work.Title, StringComparer.OrdinalIgnoreCase)
                .Select(work => work.WorkId),
        ];
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
