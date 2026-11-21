using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Constants;
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
/// request was "New playlist" (so a failure is worded as "could not be created"); <c>Created</c> says that playlist was made. <c>Reason</c> is the
/// Engine's own explanation when a request was refused, so the sentence can say why.
/// </summary>
public sealed record PlaylistAddResult(string PlaylistName, int Added, int AlreadyThere, int Failed, bool Created, bool Creating = false, string? Reason = null);

/// <summary>
/// Putting songs into a hand-made playlist, the one add every entry point goes through (Listen playlists spec WP2, D5, D6): the song menu,
/// the selection bars, the player bar menu, the "Add to playlist" action on a page, and drag and drop. The Engine skips songs a playlist
/// already holds and says how many went in and how many were already there (no size limit), so this only sends the songs and counts what
/// did not go through. A new playlist is created already holding the songs. The Engine checks who may edit the playlist on every call.
/// </summary>
public static class PlaylistMembership
{
    /// <summary>
    /// How many songs one add request carries. The Engine has no cap; a long list goes in several requests so that if one fails the earlier
    /// ones are kept and the sentence can say how many went in and how many did not.
    /// </summary>
    private const int AddBatchSize = 250;

    /// <summary>
    /// The "Already in" tick is worked out only for playlists this long or shorter (one read each, a few at a time), so the picker opens
    /// quickly; a longer playlist shows no tick and the sentence after the add says "already there" instead.
    /// </summary>
    public const int AlreadyInProbeLimit = 250;

    /// <summary>How many playlists are read at once when working out the "Already in" tick.</summary>
    private const int ProbeParallelism = 4;

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

    /// <summary>
    /// Adds the songs to an existing playlist through the Engine's batch add: it returns how many went in and how many were already
    /// there. A request the Engine did not answer counts its songs as failed, and the songs of the requests before it are kept.
    /// </summary>
    public static async Task<PlaylistAddResult> AddAsync(
        IEngineApiClient api, Guid playlistId, string playlistName, IReadOnlyList<Guid> workIds, Guid? profileId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(workIds);
        var wanted = workIds.Where(id => id != Guid.Empty).Distinct().ToList();

        var added = 0;
        var already = 0;
        var failed = 0;
        string? reason = null;
        foreach (var batch in wanted.Chunk(AddBatchSize))
        {
            var answer = await api.AddCollectionItemsAsync(playlistId, batch, profileId, ct);
            if (answer is null)
            {
                failed += batch.Length;
                reason ??= api.LastError;
                continue;
            }

            added += answer.added;
            already += answer.already_there;
        }

        return new PlaylistAddResult(playlistName, added, already, failed, Created: false, Reason: failed > 0 ? reason : null);
    }

    /// <summary>
    /// The playlists (of those given) already known to hold every one of the songs, for the picker's "Already in" tick. Only a short playlist
    /// is read (<see cref="AlreadyInProbeLimit"/>); a playlist that cannot be read or is too long is simply not ticked.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> FindHoldingAllAsync(
        IEngineApiClient api, IEnumerable<(Guid Id, int ItemCount)> playlists, IReadOnlyList<Guid> workIds, Guid? profileId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(workIds);
        var wanted = workIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var holding = new HashSet<Guid>();
        if (wanted.Count == 0)
        {
            return holding;
        }

        using var gate = new SemaphoreSlim(ProbeParallelism);
        var probes = playlists
            .Where(playlist => playlist.ItemCount >= wanted.Count && playlist.ItemCount <= AlreadyInProbeLimit)
            .Select(async playlist =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var items = await api.GetCollectionItemsAsync(playlist.Id, playlist.ItemCount, profileId, ct);
                    var held = items.Select(item => item.WorkId).ToHashSet();
                    return wanted.All(held.Contains) ? playlist.Id : (Guid?)null;
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToList();
        foreach (var id in await Task.WhenAll(probes))
        {
            if (id is { } holds)
            {
                holding.Add(holds);
            }
        }

        return holding;
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
            CollectionTypeNames.Playlist,
            new CollectionRuleDefinitionViewModel(),
            "title",
            "asc",
            "private",
            wanted,
            profileId,
            ct);
        return id is null
            ? new PlaylistAddResult(name, 0, 0, wanted.Count, Created: false, Creating: true, Reason: api.LastError)
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
