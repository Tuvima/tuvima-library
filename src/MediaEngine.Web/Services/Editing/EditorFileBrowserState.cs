using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>Which of the browser's two views is showing (universal editor v5, screen E).</summary>
public enum EditorBrowserView
{
    /// <summary>The supporting files of the title: search, filters, groups, a table with ticks.</summary>
    Files,

    /// <summary>The real folder tree of the title, with every file marked.</summary>
    Folders,
}

/// <summary>The roles and places a bulk action offers for the ticked files, read once from one of them.</summary>
public sealed record EditorFileFacets(
    IReadOnlyList<EditorSupportingFileRoleDto> Roles,
    IReadOnlyList<EditorSupportingFilePlaceDto> Places);

/// <summary>What a bulk delete did: how many records went, and the message to show when none did.</summary>
public sealed record EditorFileBrowserDeleteResult(int Removed, int Failed, string? Message);

/// <summary>
/// The state of the supporting-files browser (universal editor v5 WP8, screens E and F): what is searched and filtered, the page the Engine
/// answered with, the ticks, the folder view, and the bulk actions on the ticked rows. It lives in the <see cref="EditorHost"/> so the ticks and
/// filters survive the browser being closed to edit the ticked files ("Back to the list" finds them again). It holds no number of its own: the
/// page size, the thresholds and the roles all come from the Engine's answers (<c>config/editor.json</c>, <c>config/extras.json</c>). The dialog,
/// sidebar, toolbar, list and footer that draw it are separate parts, so the shared browser frame (WP8a) can take them over without touching this.
/// </summary>
public sealed class EditorFileBrowserState
{
    private readonly IEngineApiClient _api;
    private readonly Func<PendingEditList> _pending;
    private readonly List<SupportingFileRowDto> _ticked = [];
    private readonly Dictionary<string, EditorFileFacets> _facets = new(StringComparer.Ordinal);
    private int _version;
    private int _treeVersion;

    public EditorFileBrowserState(IEngineApiClient api, Func<PendingEditList> pending)
    {
        _api = api;
        _pending = pending;
    }

    public event Action? Changed;

    /// <summary>The title the files belong to (the show, album, movie or course).</summary>
    public Guid TitleId { get; private set; }

    public string TitleName { get; private set; } = string.Empty;

    /// <summary>The title's media type ("TV", "Videos", "Music"...), for the window's subtitle wording.</summary>
    public string? MediaType { get; private set; }

    /// <summary>True once the browser has been opened for a title.</summary>
    public bool IsOpenFor(Guid titleId) => TitleId == titleId && TitleId != Guid.Empty;

    public EditorBrowserView View { get; private set; } = EditorBrowserView.Files;

    // -- Filters --------------------------------------------------------------------------------------------------------------------

    public string Query { get; private set; } = string.Empty;

    public string Where { get; private set; } = SupportingFileListDefaults.WhereAll;

    /// <summary>The type filter, or null for every type.</summary>
    public string? TypeFilter { get; private set; }

    public string Group { get; private set; } = SupportingFileListDefaults.GroupAll;

    public string Sort { get; private set; } = SupportingFileListDefaults.SortTitle;

    public bool Descending { get; private set; }

    public int Page { get; private set; } = 1;

    public SupportingFileListDto? List { get; private set; }

    public bool Loading { get; private set; }

    public string? Error { get; private set; }

    /// <summary>True when a search or a filter narrows the list (so "Nothing matches that" offers Clear filters).</summary>
    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(Query)
        || Where != SupportingFileListDefaults.WhereAll
        || TypeFilter is not null
        || Group != SupportingFileListDefaults.GroupAll;

    public IReadOnlyList<SupportingFileRowDto> Rows => List?.Items ?? [];

    // -- Ticks ----------------------------------------------------------------------------------------------------------------------

    /// <summary>The ticked files, in the order they were ticked; ticks stay while the person pages and filters.</summary>
    public IReadOnlyList<SupportingFileRowDto> Ticked => _ticked;

    public bool IsTicked(string id) => _ticked.Any(row => row.Id == id);

    /// <summary>True when every row of the page showing is ticked (the header tick).</summary>
    public bool PageAllTicked => Rows.Count > 0 && Rows.All(row => IsTicked(row.Id));

    public void Toggle(SupportingFileRowDto row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var at = _ticked.FindIndex(ticked => ticked.Id == row.Id);
        if (at >= 0)
        {
            _ticked.RemoveAt(at);
        }
        else if (_ticked.Count < MaxTicked)
        {
            _ticked.Add(row);
        }
        else
        {
            TickCapReached = true;
        }

        if (at >= 0)
        {
            TickCapReached = false;
        }

        Changed?.Invoke();
    }

    /// <summary>The header tick: ticks every row of the page, or unticks them when they were all ticked.</summary>
    public void TickPage()
    {
        var on = !PageAllTicked;
        foreach (var row in Rows)
        {
            var at = _ticked.FindIndex(ticked => ticked.Id == row.Id);
            if (on && at < 0 && _ticked.Count < MaxTicked)
            {
                _ticked.Add(row);
            }
            else if (on && at < 0)
            {
                TickCapReached = true;
            }
            else if (!on && at >= 0)
            {
                _ticked.RemoveAt(at);
                TickCapReached = false;
            }
        }

        Changed?.Invoke();
    }

    public void ClearTicks()
    {
        if (_ticked.Count == 0)
        {
            return;
        }

        _ticked.Clear();
        TickCapReached = false;
        Changed?.Invoke();
    }

    /// <summary>The Engine takes at most this many files in one request (the supporting-files limit).</summary>
    public static int MaxTicked => SupportingFileListDefaults.MaxFilesPerCall;

    /// <summary>True when a tick was refused because the limit was reached; the dialog then says "You can edit up to N files at once".</summary>
    public bool TickCapReached { get; private set; }

    // -- Opening and reading --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the browser for a title. Opening it for the title it already holds keeps the ticks and filters; opening it for another starts
    /// clean. <paramref name="where"/> narrows the list to a level (the level the item dropdown named).
    /// </summary>
    public async Task OpenAsync(Guid titleId, string titleName, EditorBrowserView view, string? where = null, string? folder = null, string? mediaType = null, CancellationToken ct = default)
    {
        var sameTitle = TitleId == titleId;
        if (!sameTitle)
        {
            Reset();
            TitleId = titleId;
        }

        MediaType = mediaType;
        if (folder is not null && view == EditorBrowserView.Folders)
        {
            Folder = folder;
        }

        TitleName = titleName;
        View = view;
        if (!sameTitle && !string.IsNullOrWhiteSpace(where))
        {
            Where = where;
        }

        Changed?.Invoke();
        if (view == EditorBrowserView.Files)
        {
            await ReloadAsync(ct);
        }
        else
        {
            await OpenFolderAsync(Folder, 0, ct);
        }
    }

    /// <summary>Forgets the title, the ticks and the filters (the editor closed, or another title opened).</summary>
    public void Reset()
    {
        TitleId = Guid.Empty;
        TitleName = string.Empty;
        View = EditorBrowserView.Files;
        Query = string.Empty;
        Where = SupportingFileListDefaults.WhereAll;
        TypeFilter = null;
        Group = SupportingFileListDefaults.GroupAll;
        Sort = SupportingFileListDefaults.SortTitle;
        Descending = false;
        Page = 1;
        List = null;
        Loading = false;
        Error = null;
        _ticked.Clear();
        _facets.Clear();
        Tree = null;
        Folder = string.Empty;
        TreeError = null;
        _folderEntries.Clear();
        _version++;
        _treeVersion++;
        Changed?.Invoke();
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        if (TitleId == Guid.Empty)
        {
            return;
        }

        var version = ++_version;
        Loading = true;
        Error = null;
        Changed?.Invoke();
        SupportingFileListDto? answer;
        try
        {
            answer = await _api.ListSupportingFilesAsync(
                TitleId,
                new SupportingFileListRequest(Query, Where, TypeFilter, Group, Sort, Descending, Page),
                ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version != _version)
        {
            return;
        }

        Loading = false;
        if (answer is null)
        {
            Error = "EditorBrowser_LoadFailed";
        }
        else
        {
            List = answer;
            // A page past the end (files were removed) goes back to the last one.
            if (Page > answer.PageCount && answer.PageCount >= 1)
            {
                Page = answer.PageCount;
                await ReloadAsync(ct);
                return;
            }
        }

        Changed?.Invoke();
    }

    public Task SetQueryAsync(string? query)
    {
        var next = query?.Trim() ?? string.Empty;
        if (string.Equals(next, Query, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        Query = next;
        Page = 1;
        return ReloadAsync();
    }

    public Task SetWhereAsync(string? where)
    {
        Where = string.IsNullOrWhiteSpace(where) ? SupportingFileListDefaults.WhereAll : where;
        Page = 1;
        return ReloadAsync();
    }

    public Task SetTypeAsync(string? type)
    {
        TypeFilter = string.IsNullOrWhiteSpace(type) || type == SupportingFileListDefaults.WhereAll ? null : type;
        Page = 1;
        return ReloadAsync();
    }

    public Task SetGroupAsync(string? group)
    {
        Group = string.IsNullOrWhiteSpace(group) ? SupportingFileListDefaults.GroupAll : group;
        Page = 1;
        return ReloadAsync();
    }

    /// <summary>Sorts by a column; choosing the sort already in use turns it round.</summary>
    public Task SortByAsync(string sort)
    {
        if (string.Equals(Sort, sort, StringComparison.Ordinal))
        {
            Descending = !Descending;
        }
        else
        {
            Sort = sort;
            Descending = false;
        }

        Page = 1;
        return ReloadAsync();
    }

    public Task GoToPageAsync(int page)
    {
        var last = Math.Max(1, List?.PageCount ?? 1);
        Page = Math.Clamp(page, 1, last);
        return ReloadAsync();
    }

    public Task ClearFiltersAsync()
    {
        Query = string.Empty;
        Where = SupportingFileListDefaults.WhereAll;
        TypeFilter = null;
        Group = SupportingFileListDefaults.GroupAll;
        Page = 1;
        return ReloadAsync();
    }

    public Task ShowFilesAsync()
    {
        View = EditorBrowserView.Files;
        Changed?.Invoke();
        return ReloadAsync();
    }

    public Task ShowFoldersAsync()
    {
        View = EditorBrowserView.Folders;
        Changed?.Invoke();
        return OpenFolderAsync(Folder, 0);
    }

    // -- The folder tree ------------------------------------------------------------------------------------------------------------

    public EditorFolderTreeDto? Tree { get; private set; }

    /// <summary>The folder showing, relative to the title's folder; empty for the title's own folder.</summary>
    public string Folder { get; private set; } = string.Empty;

    public bool TreeLoading { get; private set; }

    private readonly List<EditorFolderEntryDto> _folderEntries = [];

    /// <summary>The entries of the folder showing: folders first, then files. "Show more" adds the folder's next page to them.</summary>
    public IReadOnlyList<EditorFolderEntryDto> FolderEntries => _folderEntries;

    /// <summary>True when the folder showing has more entries than the pages read so far hold.</summary>
    public bool FolderHasMore => Tree?.HasMore == true;

    /// <summary>Reads the folder's next page and adds it to the entries showing.</summary>
    public Task ShowMoreFolderAsync(CancellationToken ct = default) => OpenFolderAsync(Folder, _folderEntries.Count, ct);

    public string? TreeError { get; private set; }

    /// <summary>Opens one folder of the title's tree at an offset (the Engine pages a folder; it never leaves the title's folder).</summary>
    public async Task OpenFolderAsync(string? folder, int offset, CancellationToken ct = default)
    {
        if (TitleId == Guid.Empty)
        {
            return;
        }

        var version = ++_treeVersion;
        TreeLoading = true;
        TreeError = null;
        Changed?.Invoke();
        EditorFolderTreeDto? answer;
        try
        {
            answer = await _api.GetEditorFolderTreeAsync(TitleId, folder, offset, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version != _treeVersion)
        {
            return;
        }

        TreeLoading = false;
        if (answer is null)
        {
            TreeError = "EditorBrowser_FolderFailed";
        }
        else
        {
            if (offset == 0 || !string.Equals(answer.Folder, Folder, StringComparison.Ordinal))
            {
                _folderEntries.Clear();
            }

            Tree = answer;
            Folder = answer.Folder;
            _folderEntries.AddRange(answer.Entries);
        }

        Changed?.Invoke();
    }

    // -- What the bulk actions offer ------------------------------------------------------------------------------------------------

    /// <summary>The groups the title has, for the Group menu.</summary>
    public IReadOnlyList<string> GroupNames => [.. (List?.Groups ?? []).Select(group => group.Name)];

    /// <summary>The kinds of file in the ticked set ("document", "clip"...).</summary>
    public IReadOnlyList<string> TickedTypes => [.. _ticked.Select(row => row.Type).Distinct(StringComparer.Ordinal)];

    /// <summary>The roles and places for the ticked files; null while they are being read or when the ticked files are of different kinds (Role is then not offered).</summary>
    public EditorFileFacets? FacetsForTicked =>
        TickedTypes is [var only] && _facets.TryGetValue(only, out var facets) ? facets : null;

    /// <summary>The places of the title (Belongs to), whatever the kinds of file: read from any ticked file once.</summary>
    public IReadOnlyList<EditorSupportingFilePlaceDto> Places =>
        _facets.Values.FirstOrDefault()?.Places ?? [];

    /// <summary>Reads the roles and places for the ticked files' kind from one of them, once per kind.</summary>
    public async Task EnsureFacetsAsync(CancellationToken ct = default)
    {
        var changed = false;
        foreach (var type in TickedTypes)
        {
            if (_facets.ContainsKey(type))
            {
                continue;
            }

            var sample = _ticked.First(row => row.Type == type);
            var file = await _api.GetSupportingFileEditorAsync(TitleId, Guid.Parse(sample.Id), ct);
            if (file is not null)
            {
                _facets[type] = new EditorFileFacets(file.Roles, file.Places);
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    // -- Bulk actions ---------------------------------------------------------------------------------------------------------------

    private PendingEditList Pending => _pending();

    /// <summary>Group for every ticked file (a name creates the group on Save; blank means no group). Stages one change per file.</summary>
    public void StageGroup(string? name)
    {
        var known = GroupNames;
        foreach (var row in _ticked)
        {
            Pending.SetSupportingFile(SupportingFileBulkStaging.SetGroup(TitleId, row, Pending.FindSupportingFile(Guid.Parse(row.Id)), name, known));
        }

        Changed?.Invoke();
    }

    /// <summary>Takes back the group change staged for every ticked file (the field showed Mixed and was cleared).</summary>
    public void UnstageGroup()
    {
        foreach (var row in _ticked)
        {
            if (Pending.FindSupportingFile(Guid.Parse(row.Id)) is { } staged)
            {
                Pending.SetSupportingFile(staged.Without(SupportingFileFields.Group));
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Role for every ticked file; only offered when they are all of one kind, whose roles <see cref="FacetsForTicked"/> lists.</summary>
    public void StageRole(string roleId)
    {
        var label = FacetsForTicked?.Roles.FirstOrDefault(role => role.Id == roleId)?.Label ?? roleId;
        foreach (var row in _ticked)
        {
            Pending.SetSupportingFile(SupportingFileBulkStaging.SetRole(TitleId, row, Pending.FindSupportingFile(Guid.Parse(row.Id)), roleId, label));
        }

        Changed?.Invoke();
    }

    /// <summary>Add tags to every ticked file.</summary>
    public void StageTags(IEnumerable<string> tags)
    {
        var list = tags.ToList();
        foreach (var row in _ticked)
        {
            Pending.SetSupportingFile(SupportingFileBulkStaging.AddTags(TitleId, row, Pending.FindSupportingFile(Guid.Parse(row.Id)), list));
        }

        Changed?.Invoke();
    }

    /// <summary>The tags to add to every ticked file, as the several-files form holds them: replaces what was staged, so a tag taken out of the box is not added.</summary>
    public void StageAddedTags(IEnumerable<string> tags)
    {
        var list = tags.ToList();
        foreach (var row in _ticked)
        {
            Pending.SetSupportingFile(SupportingFileBulkStaging.SetAddedTags(TitleId, row, Pending.FindSupportingFile(Guid.Parse(row.Id)), list));
        }

        Changed?.Invoke();
    }

    /// <summary>Shown alongside for every ticked file: a place key of an episode, lesson or track, or blank for none.</summary>
    public void StageAlongside(string? key)
    {
        var label = string.IsNullOrWhiteSpace(key) ? string.Empty : Places.FirstOrDefault(place => place.Key == key)?.Label ?? key;
        foreach (var row in _ticked)
        {
            Pending.SetSupportingFile(SupportingFileBulkStaging.SetAlongside(TitleId, row, Pending.FindSupportingFile(Guid.Parse(row.Id)), key, label));
        }

        Changed?.Invoke();
    }

    /// <summary>The staged change of a file, if any.</summary>
    public SupportingFileStage? StagedOf(SupportingFileRowDto row) => Pending.FindSupportingFile(Guid.Parse(row.Id));

    /// <summary>True when the last Belongs to check of which files would move could not be made; the window then says so instead of staying silent.</summary>
    public bool PreviewFailed { get; private set; }

    /// <summary>
    /// Belongs to for every ticked file: a place key, or <see cref="SupportingFileBulkStaging.FolderScopeValue"/> for "Use folder location". The
    /// change is staged at once; then the Engine is asked (a read, nothing written) which of the files would move in a Managed folder, so the
    /// amber footer and Review can say from where to where. A Read-only folder never moves a file, and the Engine plans no move for it.
    /// </summary>
    public async Task StageScopeAsync(string scopeKey, CancellationToken ct = default)
    {
        var useFolder = scopeKey == SupportingFileBulkStaging.FolderScopeValue;
        var label = useFolder ? SupportingFileBulkStaging.FolderLocationWords : Places.FirstOrDefault(place => place.Key == scopeKey)?.Label ?? scopeKey;
        var rows = _ticked.ToList();
        foreach (var row in rows)
        {
            var staged = Pending.FindSupportingFile(Guid.Parse(row.Id));
            Pending.SetSupportingFile(useFolder
                ? SupportingFileBulkStaging.UseFolderLocation(TitleId, row, staged)
                : SupportingFileBulkStaging.SetScope(TitleId, row, staged, scopeKey, label));
        }

        Changed?.Invoke();
        var ids = rows
            .Where(row => Pending.FindSupportingFile(Guid.Parse(row.Id))?.Row(SupportingFileFields.Scope) is not null)
            .Select(row => row.Id)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var outcome = await _api.PreviewSupportingFilesChangeAsync(
            TitleId,
            new SupportingFileBulkUpdateRequest
            {
                Ids = ids,
                Changes = new SupportingFileBulkChangesDto { ScopeKey = useFolder ? null : scopeKey, UseFolderLocation = useFolder },
            },
            ct);
        if (!outcome.IsOk)
        {
            PreviewFailed = true;
            Changed?.Invoke();
            return;
        }

        PreviewFailed = false;

        foreach (var id in ids)
        {
            var current = Pending.FindSupportingFile(Guid.Parse(id));
            // A slower answer for an earlier choice must not change the move of the choice staged now.
            if (current?.Row(SupportingFileFields.Scope) is null || current.UseFolderLocation != useFolder || (!useFolder && current.ScopeKey != scopeKey))
            {
                continue;
            }

            var move = outcome.Value!.Moves.FirstOrDefault(m => m.Id == id && m.Status == SupportingFileMoveStatuses.Planned);
            Pending.SetSupportingFile(SupportingFileBulkStaging.WithPlannedMove(current, move));
        }

        Changed?.Invoke();
    }

    /// <summary>The ids deleted by the last <see cref="DeleteTickedAsync"/>, so the editor can drop what was staged on them.</summary>
    public Func<IReadOnlyCollection<Guid>, Task>? ItemsDeleted { get; set; }

    /// <summary>
    /// Deletes the ticked files at once (the person was asked once before this runs): the Engine removes the records and handles the files on
    /// disk by the library's file rules. Staged changes on them go; the list is read again.
    /// </summary>
    public Task<EditorFileBrowserDeleteResult> DeleteTickedAsync(CancellationToken ct = default) => DeleteAsync(_ticked.ToList(), ct);

    /// <summary>Deletes these files at once (see <see cref="DeleteTickedAsync"/>); a row's own menu deletes just that row.</summary>
    public async Task<EditorFileBrowserDeleteResult> DeleteAsync(IReadOnlyList<SupportingFileRowDto> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0)
        {
            return new EditorFileBrowserDeleteResult(0, 0, null);
        }

        var outcome = await _api.DeleteSupportingFilesAsync(
            TitleId, new SupportingFileBulkDeleteRequest { Ids = [.. rows.Select(row => row.Id)] }, ct);
        if (outcome.Value is not { } answer)
        {
            return new EditorFileBrowserDeleteResult(0, rows.Count, outcome.Message);
        }

        var gone = answer.Results
            .Where(result => result.Status is SupportingFileBulkStatuses.Removed or SupportingFileBulkStatuses.NotFound)
            .Select(result => Guid.TryParse(result.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
        _ticked.RemoveAll(row => gone.Contains(Guid.Parse(row.Id)));
        if (gone.Count > 0 && ItemsDeleted is { } deleted)
        {
            await deleted(gone);
        }

        await ReloadAsync(ct);
        return new EditorFileBrowserDeleteResult(gone.Count, rows.Count - gone.Count, null);
    }
}
