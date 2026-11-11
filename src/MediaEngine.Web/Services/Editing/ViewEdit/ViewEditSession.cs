using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The tools (tabs) of the photos, videos, documents and galleries editor.</summary>
public enum ViewEditTool
{
    Details,
    Artwork,
    History,
}

/// <summary>Where a photo was opened from: its gallery, and the gallery's photos loaded so far for the header's item dropdown (<c>HasMore</c> when the gallery holds more).</summary>
public sealed record ViewEditGalleryRail(Guid GalleryId, string GalleryName, IReadOnlyList<LocalAssetDto> Members, int ItemCount, bool HasMore = false);

/// <summary>
/// One open View editor (spec photos-documents-editor): the chosen items (or one gallery), what is staged on each, the tool showing, and the
/// one Save. It is also the pending list Review changes reads (<see cref="IReviewSource"/>), so the editor saves through the same
/// Review page as the library editor. Nothing is sent to the Engine until Save; Delete is the one exception and goes through the host.
/// </summary>
public sealed class ViewEditSession : IReviewSource
{
    private readonly IEngineApiClient _api;
    private readonly List<ViewEditStage> _stages = [];
    private List<EditSessionChangeResult> _results = [];

    public ViewEditSession(IEngineApiClient api, IReadOnlyList<LocalAssetDto> items, ViewEditGalleryRail? rail = null)
    {
        _api = api;
        ArgumentNullException.ThrowIfNull(items);
        Rail = rail;
        Active = StageFor(items);
        Tool = ViewEditTool.Details;
    }

    /// <summary>A session on one gallery (or a new one).</summary>
    public ViewEditSession(IEngineApiClient api, ViewGalleryEditStage gallery)
    {
        _api = api;
        Gallery = gallery;
        Active = new ViewEditStage([]);
        Tool = ViewEditTool.Details;
        gallery.Changed += OnStageChanged;
    }

    /// <summary>The gallery being edited, or null when photos, videos or documents are.</summary>
    public ViewGalleryEditStage? Gallery { get; }

    public bool IsGallery => Gallery is not null;

    public ViewEditGalleryRail? Rail { get; private set; }

    /// <summary>The rail's gallery lost photos (they were deleted); the dropdown lists what is left.</summary>
    public void SetRail(ViewEditGalleryRail? rail)
    {
        Rail = rail;
        Raise();
    }

    /// <summary>The stage of what the window shows now (one item, or the whole ticked group).</summary>
    public ViewEditStage Active { get; private set; }

    public ViewEditTool Tool { get; private set; }

    public bool Saving { get; private set; }

    public bool Switching { get; private set; }

    /// <summary>The ids of everything this session touched, so a list refreshes just those rows when the window closes.</summary>
    public IReadOnlyCollection<Guid> TouchedIds => _stages.SelectMany(stage => stage.Items.Select(item => item.Id))
        .Concat(Gallery?.Id is { } gallery ? [gallery] : []).Distinct().ToList();

    public bool ChangedWhileOpen { get; private set; }

    public Guid? CreatedGalleryId { get; private set; }

    /// <summary>The first words of the outcome of the last Save, for the page's slim ribbon.</summary>
    public string? LastSaved { get; private set; }

    public event Action? Changed;

    public int ChangeCount => _stages.Sum(stage => stage.ChangeCount) + (Gallery?.ChangeCount ?? 0);

    public bool HasPendingChanges => ChangeCount > 0;

    public IReadOnlyList<ViewEditTool> Tools => IsGallery
        ? Gallery is { IsDocumentView: true }
            ? [ViewEditTool.Details, ViewEditTool.History]
            : [ViewEditTool.Details, ViewEditTool.Artwork, ViewEditTool.History]
        : [ViewEditTool.Details, ViewEditTool.History];

    public ViewEditStage StageFor(IReadOnlyList<LocalAssetDto> items)
    {
        // Stepping back to a photo already visited brings back what was staged on it.
        var key = items.Select(item => item.Id).ToList();
        var found = _stages.FirstOrDefault(stage => stage.Items.Select(item => item.Id).SequenceEqual(key));
        if (found is not null)
        {
            return found;
        }

        var created = new ViewEditStage(items);
        created.Changed += OnStageChanged;
        _stages.Add(created);
        return created;
    }

    public void SelectTool(ViewEditTool tool)
    {
        if (Tools.Contains(tool) && tool != Tool)
        {
            Tool = tool;
            Raise();
        }
    }

    /// <summary>Moves the window to another item of the gallery it was opened from; what was staged on the other photos is kept.</summary>
    public void SwitchTo(IReadOnlyList<LocalAssetDto> items)
    {
        Active = StageFor(items);
        if (!Tools.Contains(Tool))
        {
            Tool = ViewEditTool.Details;
        }

        Raise();
    }

    /// <summary>Replaces what an item says now (after it was read again, or a quick action changed it) without losing what is staged on it.</summary>
    public void Refresh(LocalAssetDto fresh)
    {
        var index = _stages.FindIndex(stage => stage.Items.Count == 1 && stage.Items[0].Id == fresh.Id);
        if (index < 0)
        {
            return;
        }

        var old = _stages[index];
        if (old.HasChanges)
        {
            return;
        }

        var replacement = new ViewEditStage([fresh]);
        replacement.Changed += OnStageChanged;
        _stages[index] = replacement;
        if (ReferenceEquals(Active, old))
        {
            Active = replacement;
        }

        Raise();
    }

    /// <summary>Drops every staged change on items that were deleted, without a question.</summary>
    public void Forget(IReadOnlyCollection<Guid> ids)
    {
        foreach (var stage in _stages.Where(stage => stage.Items.Any(item => ids.Contains(item.Id))).ToList())
        {
            stage.Clear();
        }

        ChangedWhileOpen = true;
        Raise();
    }

    // ---- IReviewSource ----

    public IReadOnlyList<EditReviewRow> ReviewRows() =>
        [.. _stages.SelectMany(stage => stage.Rows()), .. Gallery?.Rows() ?? []];

    public bool RemoveReviewRow(EditReviewRow row)
    {
        if (row.ChangeId.StartsWith("gallery:", StringComparison.Ordinal) && Gallery is { } gallery)
        {
            switch (row.ChangeId["gallery:".Length..])
            {
                case "name": gallery.SetName(gallery.OriginalName); break;
                case "description": gallery.SetDescription(gallery.Original?.Description ?? gallery.VaultOriginal?.Description); break;
                case "rule": gallery.SetRule(gallery.Original?.SmartRuleJson ?? gallery.VaultOriginal?.SmartRuleJson); break;
                case "soundtrack": gallery.SetSoundtrack(gallery.Original?.SoundtrackPlaylistId?.ToString("D")); break;
                case "cover": gallery.RevertCover(); break;
                default: return false;
            }

            return true;
        }

        foreach (var stage in _stages)
        {
            if (!stage.Rows().Any(candidate => candidate.RowId == row.RowId))
            {
                continue;
            }

            switch (row.ChangeId["view:".Length..])
            {
                case "description": stage.SetDescription(stage.Items[0].Description); break;
                case "tags": stage.RevertTags(); break;
                case "people": stage.RevertPeople(); break;
                case "date": stage.RevertDate(); break;
                case "place": stage.RevertPlace(); break;
                case "galleries":
                    foreach (var id in stage.GalleriesAdd.Concat(stage.GalleriesRemove).ToList())
                    {
                        stage.CancelGallery(id);
                    }

                    stage.CreateGallery(null);
                    break;
                default:
                    var key = row.ChangeId["view:".Length..];
                    if (key.StartsWith("doc:", StringComparison.Ordinal))
                    {
                        stage.RevertDocumentField(key["doc:".Length..]);
                    }
                    else if (key.StartsWith("field:", StringComparison.Ordinal) && Guid.TryParseExact(key["field:".Length..], "N", out var fieldId))
                    {
                        stage.RevertCustomField(fieldId);
                    }

                    break;
            }

            return true;
        }

        return false;
    }

    public Task JumpToAsync(EditReviewRow row)
    {
        SelectTool(ViewEditTool.Details);
        return Task.CompletedTask;
    }

    /// <summary>"Unsaved changes" is asked by the host; this is set by it. Unset, a session with changes keeps the person in the window.</summary>
    public Func<Task<bool>>? ConfirmDiscard { get; set; }

    /// <summary>Raised when the window should close (after a save, a discard or a delete that left nothing).</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Asks "Unsaved changes" when something is staged and, once the person agrees, drops it. It does not close the window: the host uses it when the
    /// window moves to another item or gallery, so the page that opened the window keeps waiting for the one real close.
    /// </summary>
    public async Task<bool> ConfirmLeaveAsync()
    {
        if (Saving)
        {
            return false;
        }

        if (HasPendingChanges)
        {
            if (ConfirmDiscard is null || !await ConfirmDiscard())
            {
                return false;
            }
        }

        foreach (var stage in _stages)
        {
            stage.Clear();
        }

        Gallery?.Clear();
        return true;
    }

    public async Task<bool> LeaveAsync()
    {
        if (!await ConfirmLeaveAsync())
        {
            return false;
        }

        CloseRequested?.Invoke();
        return true;
    }

    public bool DataSaved { get; private set; }

    public string? Message { get; private set; }

    public IReadOnlyList<EditSessionChangeResult> Results => _results;

    /// <summary>A View edit never writes into the files, so Review shows no "files will be updated" line (D7).</summary>
    public IReadOnlyList<string> FileWriteMediaTypes => [];

    public bool ShowsFilesStatus => false;

    public bool? WriteIntoFiles { get; set; }

    public async Task<EditSessionSaveOutcome> PreviewAsync(CancellationToken ct = default)
    {
        if (ChangeCount == 0)
        {
            return EditSessionSaveOutcome.Unsaved("There are no changes to save.");
        }

        if (Gallery is { } gallery && gallery.HasChanges && gallery.Problem() is { } problem)
        {
            _results = [.. Gallery!.Rows().Select(row => Result(row, EditSessionChangeStatuses.Invalid, problem))];
            return new EditSessionSaveOutcome(false, [], Results: _results);
        }

        var set = ToSet(previewOnly: true);
        if (set.Edits.Count == 0)
        {
            _results = [.. ReviewRows().Select(row => Result(row, EditSessionChangeStatuses.Ok))];
            return EditSessionSaveOutcome.Success with { Results = _results };
        }

        var answer = await _api.EditViewItemsAsync(set, ct);
        if (!answer.Success)
        {
            return EditSessionSaveOutcome.Unsaved(answer.Error ?? "The Engine could not be reached. Nothing was saved.");
        }

        var result = answer.Value!;
        _results = [.. ReviewRows().Select(row => result.Valid
            ? Result(row, EditSessionChangeStatuses.Ok)
            : Result(row, EditSessionChangeStatuses.Invalid, ReasonFor(result, row)))];
        return result.Valid
            ? EditSessionSaveOutcome.Success with { Results = _results }
            : new EditSessionSaveOutcome(false, [], result.Message, Results: _results);
    }

    public async Task<bool> SaveAsync()
    {
        if (Saving || ChangeCount == 0)
        {
            return false;
        }

        Saving = true;
        Message = null;
        DataSaved = false;
        Raise();
        try
        {
            var preview = await PreviewAsync();
            if (!preview.Saved)
            {
                Message = preview.Message ?? "Nothing was saved.";
                return false;
            }

            var count = ChangeCount;
            var touched = TouchedIds.ToList();
            var set = ToSet(previewOnly: false);
            if (set.Edits.Count > 0)
            {
                var answer = await _api.EditViewItemsAsync(set);
                if (!answer.Success || !answer.Value!.Saved)
                {
                    Message = answer.Value?.Message ?? answer.Error ?? "Nothing was saved. Your changes are still here.";
                    return false;
                }

                CreatedGalleryId = answer.Value.CreatedGalleryId;
            }

            if (Gallery is { HasChanges: true } gallery && !await SaveGalleryAsync(gallery))
            {
                return false;
            }

            DataSaved = true;
            ChangedWhileOpen = true;
            LastSaved = count == 1 ? "Saved 1 change to Library." : $"Saved {count} changes to Library.";
            foreach (var stage in _stages)
            {
                stage.Clear();
            }

            _ = touched;
            CloseRequested?.Invoke();
            return true;
        }
        finally
        {
            Saving = false;
            Raise();
        }
    }

    private async Task<bool> SaveGalleryAsync(ViewGalleryEditStage gallery)
    {
        if (gallery.IsVault)
        {
            var vault = gallery.VaultOriginal is { } existing
                ? await _api.UpdateVaultImageGalleryAsync(existing.Id, gallery.ToVaultRequest())
                : await _api.CreateVaultImageGalleryAsync(gallery.ToVaultRequest());
            if (!vault.Succeeded)
            {
                Message = vault.Message ?? "The gallery could not be saved. Your changes are still here.";
                return false;
            }

            return true;
        }

        var saved = gallery.Original is { } original
            ? await _api.UpdateViewGalleryAsync(original.Id, gallery.ToViewRequest())
            : await _api.CreateViewGalleryAsync(gallery.ToViewRequest());
        if (saved is null)
        {
            Message = "The gallery could not be saved. Your changes are still here.";
            return false;
        }

        CreatedGalleryId = saved.Id;
        if (gallery.IsNew && gallery.StartingMembers.Count > 0)
        {
            if (await _api.AddViewGalleryItemsAsync(saved.Id, [.. gallery.StartingMembers.Select(member => member.Id)]) is null)
            {
                Message = "The gallery was saved, but its photos could not be added. Your changes are still here.";
                return false;
            }
        }

        return true;
    }

    private ViewItemsEditSetRequest ToSet(bool previewOnly) =>
        new([.. _stages.Where(stage => stage.HasChanges && stage.Items.Count > 0).Select(stage => stage.ToRequest())], previewOnly);

    private static string ReasonFor(ViewItemsEditResultDto result, EditReviewRow row) =>
        result.Refusals.FirstOrDefault(refusal => refusal.ItemId == row.EntityId)?.Reason
        ?? result.Refusals.FirstOrDefault()?.Reason
        ?? result.Message
        ?? "This change could not be saved.";

    private static EditSessionChangeResult Result(EditReviewRow row, string status, string? reason = null) =>
        new() { Id = row.ChangeId, Kind = row.Kind, EntityId = row.EntityId, Status = status, Reason = reason };

    private void OnStageChanged() => Raise();

    private void Raise() => Changed?.Invoke();
}
