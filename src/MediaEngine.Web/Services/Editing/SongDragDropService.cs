using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>Which songs were dragged out of a song table, and the table (the owner) they came from.</summary>
public sealed record CarriedSongs(object Owner, IReadOnlyList<Guid> WorkIds);

/// <summary>What a drop of songs said, with the table the songs came from: that table shows it in place.</summary>
public sealed record SongDropNotice(object Owner, PlaylistFlowNotice Notice);

/// <summary>
/// Dragging songs onto a playlist (Listen music spec D10). A song table that starts a drag hands the songs it carries here; every place
/// that accepts them (a hand-made playlist in the rail, a playlist tile, the rail's New playlist) takes them from here and goes through
/// <see cref="PlaylistMembership"/>, the same add the selection bar uses, so songs already in the playlist are skipped and counted and the
/// sentence is the same. The sentence goes back to the table the songs were dragged from, which shows it in place (no toast).
/// </summary>
public sealed class SongDragDropService(
    IEngineApiClient api,
    UIOrchestratorService orchestrator,
    IStringLocalizer<SharedStrings> strings)
{
    private CarriedSongs? _carried;

    /// <summary>A drop finished; the table it came from shows the sentence.</summary>
    public event Action<SongDropNotice>? NoticeRaised;

    /// <summary>True while a table is carrying songs (between its drag start and the drop or drag end).</summary>
    public bool IsCarrying => _carried is { WorkIds.Count: > 0 };

    /// <summary>A table started dragging these songs, in the order its list shows them.</summary>
    public void Begin(object owner, IReadOnlyList<Guid> workIds)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(workIds);
        var ids = workIds.Where(id => id != Guid.Empty).Distinct().ToList();
        _carried = ids.Count == 0 ? null : new CarriedSongs(owner, ids);
    }

    /// <summary>The drag ended (dropped or not). Only the table that began it can end it.</summary>
    public void End(object owner)
    {
        if (_carried is { } carried && ReferenceEquals(carried.Owner, owner))
        {
            _carried = null;
        }
    }

    /// <summary>
    /// Takes the carried songs for a drop: they are handed over once, so a second drop adds nothing. Null when nothing is being carried.
    /// Callers that go on to ask for a name (the rail's New playlist) keep this and give it back to <see cref="CreatePlaylistAsync"/>.
    /// </summary>
    public CarriedSongs? Take()
    {
        var carried = _carried;
        _carried = null;
        return carried is { WorkIds.Count: > 0 } ? carried : null;
    }

    /// <summary>
    /// The carried songs were dropped on a hand-made playlist. Returns how it went (null when nothing was carried) and raises the sentence
    /// for the table the songs came from.
    /// </summary>
    public async Task<PlaylistAddResult?> DropOnPlaylistAsync(Guid playlistId, string playlistName, CancellationToken ct = default)
    {
        // The songs are taken before the first await: the drag-end event of the table can arrive while the Engine is still answering.
        if (Take() is not { } carried)
        {
            return null;
        }

        var profile = await orchestrator.GetActiveProfileAsync();
        var result = await PlaylistMembership.AddAsync(api, playlistId, playlistName, carried.WorkIds, profile?.Id, ct);
        Raise(carried.Owner, result);
        return result;
    }

    /// <summary>The songs taken by <see cref="Take"/> go into a new playlist with the name the person typed.</summary>
    public async Task<PlaylistAddResult> CreatePlaylistAsync(string name, CarriedSongs carried, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(carried);
        var profile = await orchestrator.GetActiveProfileAsync();
        var result = await PlaylistMembership.CreateAsync(api, name, carried.WorkIds, profile?.Id, ct);
        Raise(carried.Owner, result);
        return result;
    }

    private void Raise(object owner, PlaylistAddResult result) =>
        NoticeRaised?.Invoke(new SongDropNotice(owner, PlaylistMembershipFlow.Describe(result, strings)));
}
