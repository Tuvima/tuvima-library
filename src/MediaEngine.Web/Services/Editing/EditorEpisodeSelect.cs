using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What "Select episodes" in the Season (or Section) dropdown asks for: the season or section, the item the editor is open on (the Engine reads
/// the list through it), and the words and editor media type the ticked rows carry into "Editing N episodes".
/// </summary>
/// <param name="EntityId">The item the editor is open on.</param>
/// <param name="LevelId">The season or section whose episodes or videos are listed.</param>
/// <param name="LevelTitle">The season's or section's name ("Season 2"), shown over the list.</param>
/// <param name="Videos">True for a Section of videos; false for a Season of episodes.</param>
public sealed record EpisodeSelectRequest(Guid EntityId, Guid LevelId, string LevelTitle, bool Videos)
{
    /// <summary>The editor media type of the ticked rows: <c>TV</c> for episodes, <c>Videos</c> for a folder of videos.</summary>
    public string MediaType => Videos ? "Videos" : "TV";
}

/// <summary>
/// The rules of the "Select episodes" entry (spec details-page-bottom D9, decision A): the Season (or Section) dropdown of the editor's header
/// offers it for the season or section the editor is in, when that one holds episodes or videos. Pure, so the header, the window and the tests
/// say the same thing. Who may edit (a device that may edit items, a profile the Engine lets edit) is asked of the editor itself
/// (<see cref="EditorHeaderHooks.CanSelectEpisodes"/>), never decided here.
/// </summary>
public static class EditorEpisodeSelectRules
{
    /// <summary>The request the entry makes for this header, or null when the header has no season or section with episodes or videos in it to choose from.</summary>
    public static EpisodeSelectRequest? Offer(EditorHeaderDto? header)
    {
        if (header?.SecondLevel is not { CurrentId: { } levelId } second)
        {
            return null;
        }

        var videos = string.Equals(second.Label, "Section", StringComparison.OrdinalIgnoreCase);
        if (!videos && !string.Equals(second.Label, "Season", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var entry = second.Entries.FirstOrDefault(candidate => candidate.Id == levelId);
        return entry is null || entry.ItemCount <= 0 || header.CurrentId == Guid.Empty
            ? null
            : new EpisodeSelectRequest(header.CurrentId, levelId, entry.Title, videos);
    }

    /// <summary>The word for a ticked list's rows: "episodes" or "videos" (singular for exactly one).</summary>
    public static string Noun(EpisodeSelectRequest request, int count = 2)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (request.Videos, count == 1) switch
        {
            (true, true) => "video",
            (true, false) => "videos",
            (false, true) => "episode",
            _ => "episodes",
        };
    }

    /// <summary>The ticked row for one listed episode or video: what the bulk panel needs to name it and read its fields.</summary>
    public static BulkEditRow RowFor(EditorNavItemDto item, EpisodeSelectRequest request)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(request);
        return new BulkEditRow(
            item.Id,
            item.Title,
            request.Videos ? DetailEntityType.Work : DetailEntityType.TvEpisode,
            request.LevelId,
            request.MediaType,
            item.StillUrl);
    }
}

/// <summary>
/// The ticked list of one season's episodes (or one section's videos) that "Select episodes" opens inside the editor window (details-page-bottom
/// D9). It reads the whole list from the Engine's header list, holds which rows are ticked, and nothing else: the edit itself is the editor's
/// ordinary "Editing N episodes" panel, so nothing is saved here and everything staged waits for Save changes and Review changes.
/// </summary>
public sealed class EditorEpisodeSelect(IEngineApiClient api, ILogger logger)
{
    private readonly List<EditorNavItemDto> _items = [];
    private readonly HashSet<Guid> _ticked = [];
    private CancellationTokenSource? _read;

    public event Action? Changed;

    /// <summary>True while the window shows the ticked list.</summary>
    public bool IsOpen { get; private set; }

    public EpisodeSelectRequest? Request { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>True when the list could not be read; the list offers Try again.</summary>
    public bool Failed { get; private set; }

    /// <summary>What a tick that was refused said (the most the Engine takes in one go), or null.</summary>
    public string? Notice { get; private set; }

    public IReadOnlyList<EditorNavItemDto> Items => _items;

    public int TickedCount => _ticked.Count;

    public bool IsTicked(Guid id) => _ticked.Contains(id);

    public bool AllTicked => _items.Count > 0 && _items.All(item => _ticked.Contains(item.Id));

    /// <summary>The ticked rows in the order the list shows them.</summary>
    public IReadOnlyList<BulkEditRow> TickedRows() =>
        Request is null ? [] : [.. _items.Where(item => _ticked.Contains(item.Id)).Select(item => EditorEpisodeSelectRules.RowFor(item, Request))];

    /// <summary>Opens the list for the season or section and reads every page of it. <paramref name="alreadyTicked"/> are rows ticked before (coming back from the edit panel).</summary>
    public async Task OpenAsync(EpisodeSelectRequest request, IEnumerable<Guid>? alreadyTicked = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        _read?.Cancel();
        var read = new CancellationTokenSource();
        _read = read;
        Request = request;
        IsOpen = true;
        Failed = false;
        Notice = null;
        _items.Clear();
        _ticked.Clear();
        foreach (var id in alreadyTicked ?? [])
        {
            _ticked.Add(id);
        }

        await ReadAsync(request, read);
    }

    /// <summary>Reads the list again after a failed read, keeping the ticks.</summary>
    public async Task RetryAsync()
    {
        if (!IsOpen || Request is not { } request)
        {
            return;
        }

        _read?.Cancel();
        var read = new CancellationTokenSource();
        _read = read;
        Failed = false;
        _items.Clear();
        await ReadAsync(request, read);
    }

    private async Task ReadAsync(EpisodeSelectRequest request, CancellationTokenSource read)
    {
        Loading = true;
        Changed?.Invoke();
        try
        {
            var offset = 0;
            while (!read.IsCancellationRequested)
            {
                var page = await api.GetEditorNavLevelAsync(request.EntityId, request.LevelId, null, offset, null, read.Token);
                if (read.IsCancellationRequested)
                {
                    return;
                }

                if (page is null)
                {
                    Failed = true;
                    return;
                }

                _items.AddRange(page.Items.Where(item => _items.All(known => known.Id != item.Id)));
                offset += page.Items.Count;
                if (!page.HasMore || page.Items.Count == 0)
                {
                    break;
                }
            }

            // A row ticked before that is no longer in the season or section (deleted, moved) is dropped from the ticks.
            _ticked.IntersectWith(_items.Select(item => item.Id));
        }
        catch (OperationCanceledException)
        {
            // Closed or read again while the page was coming; nothing to show for this read.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The list of {Level} could not be read for the editor's Select episodes", request.LevelId);
            Failed = true;
        }
        finally
        {
            if (ReferenceEquals(_read, read))
            {
                Loading = false;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Ticks the row, or unticks it when it is ticked. False when it could not be ticked (the Engine's most in one go).</summary>
    public bool Toggle(Guid id)
    {
        if (!_ticked.Remove(id))
        {
            if (_ticked.Count >= LibraryBatchEditRequest.MaxEntityIds)
            {
                Notice = BulkEditSelection.FullNotice;
                Changed?.Invoke();
                return false;
            }

            _ticked.Add(id);
        }

        Notice = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Ticks every row (up to the Engine's most in one go), or none when they are all ticked already.</summary>
    public void ToggleAll()
    {
        Notice = null;
        if (AllTicked)
        {
            _ticked.Clear();
        }
        else
        {
            foreach (var item in _items.Take(LibraryBatchEditRequest.MaxEntityIds))
            {
                _ticked.Add(item.Id);
            }

            if (_items.Count > LibraryBatchEditRequest.MaxEntityIds)
            {
                Notice = BulkEditSelection.FullNotice;
            }
        }

        Changed?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        _read?.Cancel();
        _read = null;
        IsOpen = false;
        Loading = false;
        Failed = false;
        Notice = null;
        Request = null;
        _items.Clear();
        _ticked.Clear();
        Changed?.Invoke();
    }
}
