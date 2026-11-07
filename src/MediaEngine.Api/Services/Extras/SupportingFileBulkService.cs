using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Extras;

namespace MediaEngine.Api.Services.Extras;

/// <summary>Why a bulk action did not run.</summary>
public enum SupportingFileBulkProblem
{
    /// <summary>The request is not something the Engine can do (nothing picked, a role that does not fit, an unreadable place).</summary>
    Invalid,

    /// <summary>The title has no folder the Engine reads, so it has no supporting files.</summary>
    TitleNotFound,
}

/// <summary>A bulk action's answer, or the plain reason it did not run.</summary>
public sealed record SupportingFileBulkOutcome<T>(T? Value, SupportingFileBulkProblem? Problem, string? Message) where T : class
{
    public static SupportingFileBulkOutcome<T> Ok(T value) => new(value, null, null);

    public static SupportingFileBulkOutcome<T> Fail(SupportingFileBulkProblem problem, string message) => new(null, problem, message);
}

/// <summary>
/// The bulk actions of the supporting-files browser (universal editor v5 D16, D30, D36): set group, set role, Belongs to, tags, and
/// Delete, on any number of files at once. The editor stages them and calls this at Save, so every action is safe to repeat: changing a
/// file to what it already reads as changes nothing, a file already gone is reported as gone, and a repeat of a saved Belongs to change
/// plans no second move. All data changes of one call are written in one transaction; file moves come last and cannot be undone, so a move
/// that fails leaves the data saved and says which file stayed. A picture folder is one item, so an action on it applies to every picture in it.
/// </summary>
public sealed class SupportingFileBulkService(
    IItemExtraRepository extras,
    ISupportingFileTitleResolver resolver,
    SupportingFileDiskService disk,
    IExtrasConfigurationSource? rules = null)
{
    public const int MaxFilesPerCall = 2000;
    public const string NothingPicked = "Pick at least one file.";
    public const string NothingToChange = "Choose what to change.";
    public const string TitleNotFound = "That title has no supporting files.";
    public const string NotThisTitle = "That file is not one of this title's supporting files.";
    public const string FileGone = "That file is no longer in the Library.";
    public const string BadPlace = "That is not a place a supporting file can belong to.";
    public const string ToManyFiles = "Too many files at once. Pick fewer and try again.";

    /// <summary>Checks the change and plans any file moves, writing nothing: what Review lists and the amber footer shows.</summary>
    public Task<SupportingFileBulkOutcome<SupportingFileBulkUpdateResultDto>> PreviewAsync(
        Guid titleId, SupportingFileBulkUpdateRequest request, CancellationToken ct = default) =>
        RunAsync(titleId, request, preview: true, undoBox: null, ct);

    /// <summary>
    /// Writes the change: all the data changes in one transaction, then the file moves. <c>Undo</c> puts every row back as it was and moves
    /// the files that moved back, for a staged save in which a later change failed; it is null when nothing was written.
    /// </summary>
    public async Task<(SupportingFileBulkOutcome<SupportingFileBulkUpdateResultDto> Outcome, Func<CancellationToken, Task>? Undo)> ApplyAsync(
        Guid titleId, SupportingFileBulkUpdateRequest request, CancellationToken ct = default)
    {
        var box = new UndoBox();
        var outcome = await RunAsync(titleId, request, preview: false, box, ct).ConfigureAwait(false);
        return (outcome, box.Undo);
    }

    private sealed class UndoBox
    {
        public Func<CancellationToken, Task>? Undo { get; set; }
    }

    private async Task<SupportingFileBulkOutcome<SupportingFileBulkUpdateResultDto>> RunAsync(
        Guid titleId, SupportingFileBulkUpdateRequest request, bool preview, UndoBox? undoBox, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var changes = request.Changes ?? new SupportingFileBulkChangesDto();

        if (request.Ids.Count == 0)
        {
            return Fail<SupportingFileBulkUpdateResultDto>(SupportingFileBulkProblem.Invalid, NothingPicked);
        }

        if (request.Ids.Count > MaxFilesPerCall)
        {
            return Fail<SupportingFileBulkUpdateResultDto>(SupportingFileBulkProblem.Invalid, ToManyFiles);
        }

        var built = BuildUpdate(changes);
        if (built.Message is not null)
        {
            return Fail<SupportingFileBulkUpdateResultDto>(SupportingFileBulkProblem.Invalid, built.Message);
        }

        var update = built.Update!;
        if (update.IsEmpty)
        {
            return Fail<SupportingFileBulkUpdateResultDto>(SupportingFileBulkProblem.Invalid, NothingToChange);
        }

        var load = await LoadAsync(titleId, request.Ids, ct).ConfigureAwait(false);
        if (load.Problem is not null)
        {
            return Fail<SupportingFileBulkUpdateResultDto>(load.Problem.Value, load.Message!);
        }

        var config = rules?.Current;
        if (update.Role.IsSet && config is not null)
        {
            var misfit = load.Targets.FirstOrDefault(target => !config.RoleFits(target.Kind, update.Role.Value));
            if (misfit is not null)
            {
                return Fail<SupportingFileBulkUpdateResultDto>(
                    SupportingFileBulkProblem.Invalid, RoleNotAvailable(config, misfit.Kind, update.Role.Value!));
            }
        }

        // Belongs to must name a real place of the title: the title itself, one of its seasons, or an episode, lesson or section.
        if (update.Scope.IsSet)
        {
            foreach (var folder in load.Targets.Select(target => target.ItemFolder).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!await disk.IsPlaceOfTitleAsync(folder, update.Scope.Value, ct).ConfigureAwait(false))
                {
                    return Fail<SupportingFileBulkUpdateResultDto>(SupportingFileBulkProblem.Invalid, BadPlace);
                }
            }
        }

        var movesScope = update.Scope.IsChanged;
        var plans = new Dictionary<Guid, SupportingFileMovePlan>();
        if (movesScope)
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in load.Targets.Where(target => !load.IsPictureFolder(target)))
            {
                var scope = update.Scope.IsSet ? update.Scope.Value : target.InferredScope;
                var sitsThere = scope == target.EffectiveScope;

                // A file follows its place when the place changes. One a person already put there (the move was not possible then, or
                // failed) is tried again, so a repeat of a saved change finishes what it could not; one that simply reads there stays.
                if (sitsThere && target.Scope is null)
                {
                    continue;
                }

                var plan = await disk.PlanMoveAsync(target, scope, claimed, ct).ConfigureAwait(false);
                if (plan.Moves || (!sitsThere && update.Scope.IsSet && plan.Note is not null))
                {
                    plans[target.Id] = plan;
                }
            }
        }

        var results = new List<SupportingFileBulkItemResultDto>(request.Ids.Count);
        foreach (var requested in load.Requested)
        {
            if (requested.Row is null)
            {
                results.Add(new SupportingFileBulkItemResultDto
                {
                    Id = requested.Id,
                    Status = requested.Refused ? SupportingFileBulkStatuses.Refused : SupportingFileBulkStatuses.NotFound,
                    Reason = requested.Refused ? NotThisTitle : FileGone,
                });
                continue;
            }

            var group = load.Members(requested.Row);
            var changed = group.Any(member => WouldChange(member, update)) || group.Any(member => plans.TryGetValue(member.Id, out var plan) && plan.Moves);
            var note = group.Select(member => plans.GetValueOrDefault(member.Id)).FirstOrDefault(plan => plan?.Note is not null)?.Note;
            results.Add(new SupportingFileBulkItemResultDto
            {
                Id = requested.Id,
                Status = changed ? SupportingFileBulkStatuses.Changed : SupportingFileBulkStatuses.Unchanged,
                Note = note,
            });
        }

        if (preview)
        {
            return SupportingFileBulkOutcome<SupportingFileBulkUpdateResultDto>.Ok(new SupportingFileBulkUpdateResultDto
            {
                Preview = true,
                Results = results,
                Changed = results.Count(result => result.Status == SupportingFileBulkStatuses.Changed),
                Moves = [.. plans.Where(pair => pair.Value.Moves).Select(pair => ToMove(pair.Key, pair.Value, SupportingFileMoveStatuses.Planned, null))],
            });
        }

        try
        {
            await extras.UpdateOwnedManyAsync([.. load.Targets.Select(target => target.Id)], update, ct).ConfigureAwait(false);
            undoBox?.Undo = _ => RestoreAsync(load.Targets, []);
        }
        catch (ExtraRoleNotAllowedException ex)
        {
            return Fail<SupportingFileBulkUpdateResultDto>(
                SupportingFileBulkProblem.Invalid, RoleNotAvailable(config, ex.FileKind, ex.Role));
        }

        var moves = new List<SupportingFilePlannedMoveDto>();
        var moved = new List<(ItemExtra Row, SupportingFileMovePlan Plan)>();
        foreach (var (id, plan) in plans.Where(pair => pair.Value.Moves))
        {
            var failure = disk.Move(plan);
            if (failure is null)
            {
                // The row follows the file straight away, and now counts as one a person decided, so the pipeline keeps claiming the
                // file at its new place and a rescan keeps the row. The place read from the folder is the place the file now sits in.
                var row = load.Targets.First(target => target.Id == id);
                var nowSitsIn = update.Scope.IsSet ? update.Scope.Value : row.InferredScope;
                await extras.RelocateFileAsync(id, plan.To!, nowSitsIn, ct).ConfigureAwait(false);
                moved.Add((row, plan));
            }

            moves.Add(ToMove(id, plan, failure is null ? SupportingFileMoveStatuses.Moved : SupportingFileMoveStatuses.Failed, failure));
        }

        if (undoBox is not null && moved.Count > 0)
        {
            undoBox.Undo = _ => RestoreAsync(load.Targets, moved);
        }

        return SupportingFileBulkOutcome<SupportingFileBulkUpdateResultDto>.Ok(new SupportingFileBulkUpdateResultDto
        {
            Preview = false,
            Results = results,
            Changed = results.Count(result => result.Status == SupportingFileBulkStatuses.Changed),
            Moves = moves,
        });
    }

    // Puts every row back as it was before the change. A file that moved goes back first; one that cannot go back keeps its new path,
    // so the row never points at a place the file is not in.
    private async Task RestoreAsync(IReadOnlyList<ItemExtra> snapshots, IReadOnlyList<(ItemExtra Row, SupportingFileMovePlan Plan)> moved)
    {
        var stayed = new Dictionary<Guid, string>();
        foreach (var (row, plan) in moved)
        {
            if (disk.Move(new SupportingFileMovePlan(plan.To, plan.From, null)) is not null)
            {
                stayed[row.Id] = plan.To!;
            }
        }

        await extras.RestoreAsync(
            [.. snapshots.Select(snapshot => stayed.TryGetValue(snapshot.Id, out var path) ? snapshot with { FilePath = path } : snapshot)],
            CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<SupportingFileBulkOutcome<SupportingFileBulkDeleteResultDto>> DeleteAsync(
        Guid titleId, SupportingFileBulkDeleteRequest request, Guid? accountId, string? deletedBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Ids.Count == 0)
        {
            return Fail<SupportingFileBulkDeleteResultDto>(SupportingFileBulkProblem.Invalid, NothingPicked);
        }

        if (request.Ids.Count > MaxFilesPerCall)
        {
            return Fail<SupportingFileBulkDeleteResultDto>(SupportingFileBulkProblem.Invalid, ToManyFiles);
        }

        var load = await LoadAsync(titleId, request.Ids, ct).ConfigureAwait(false);
        if (load.Problem is not null)
        {
            return Fail<SupportingFileBulkDeleteResultDto>(load.Problem.Value, load.Message!);
        }

        var disks = new Dictionary<Guid, SupportingFileDiskResult>();
        foreach (var target in load.Targets)
        {
            disks[target.Id] = await disk.DeleteFileAsync(target, target.DisplayTitle, accountId, deletedBy, ct).ConfigureAwait(false);
        }

        var removed = await extras.RemoveManyAsync([.. load.Targets.Select(target => target.Id)], ct).ConfigureAwait(false);

        var results = new List<SupportingFileDeleteItemResultDto>(request.Ids.Count);
        foreach (var requested in load.Requested)
        {
            if (requested.Row is null)
            {
                results.Add(new SupportingFileDeleteItemResultDto
                {
                    Id = requested.Id,
                    Status = requested.Refused ? SupportingFileBulkStatuses.Refused : SupportingFileBulkStatuses.NotFound,
                    Reason = requested.Refused ? NotThisTitle : FileGone,
                });
                continue;
            }

            var members = load.Members(requested.Row);
            var kept = members.Select(member => disks[member.Id]).FirstOrDefault(result => result.Outcome == SupportingFileDiskOutcomes.Kept);
            var first = kept ?? disks[requested.Row.Id];
            results.Add(new SupportingFileDeleteItemResultDto
            {
                Id = requested.Id,
                Status = SupportingFileBulkStatuses.Removed,
                Disk = first.Outcome,
                Reason = first.Reason,
            });
        }

        return SupportingFileBulkOutcome<SupportingFileBulkDeleteResultDto>.Ok(new SupportingFileBulkDeleteResultDto
        {
            Results = results,
            Removed = removed,
        });
    }

    // ── Loading what the call names ───────────────────────────────────────────

    private async Task<Loaded> LoadAsync(Guid titleId, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var folders = await resolver.ResolveItemFoldersAsync(titleId, ct).ConfigureAwait(false);
        if (folders.Count == 0)
        {
            return Loaded.Failed(SupportingFileBulkProblem.TitleNotFound, TitleNotFound);
        }

        var rows = await extras.ListByItemFoldersAsync(folders, ct).ConfigureAwait(false);
        var byId = rows.ToDictionary(row => row.Id);

        var requested = new List<Requested>(ids.Count);
        var seen = new HashSet<Guid>();
        foreach (var raw in ids)
        {
            if (!Guid.TryParse(raw, out var id))
            {
                requested.Add(new Requested(raw, null, Refused: false));
            }
            else if (!byId.TryGetValue(id, out var row))
            {
                // Not one of this title's files: refused if it belongs to some other title, gone if there is no such file at all.
                var elsewhere = await extras.GetAsync(id, ct).ConfigureAwait(false) is not null;
                requested.Add(new Requested(raw, null, elsewhere));
            }
            else if (seen.Add(id))
            {
                requested.Add(new Requested(raw, row, Refused: false));
            }
        }

        // A folder of pictures is one item: an action on it reaches every picture in it.
        var targets = new Dictionary<Guid, ItemExtra>();
        var folderOf = new Dictionary<Guid, IReadOnlyList<ItemExtra>>();
        foreach (var request in requested.Where(item => item.Row is not null))
        {
            var row = request.Row!;
            var members = row.Kind == ItemExtraKinds.Picture
                ? rows.Where(other => other.Kind == ItemExtraKinds.Picture
                        && string.Equals(other.ItemFolder, row.ItemFolder, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(other.GroupKey ?? string.Empty, row.GroupKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : [row];
            folderOf[row.Id] = members;
            foreach (var member in members)
            {
                targets[member.Id] = member;
            }
        }

        var inFolders = folderOf.Values.Where(members => members.Count > 1).SelectMany(members => members.Select(member => member.Id)).ToHashSet();
        return new Loaded(requested, [.. targets.Values], folderOf, inFolders, null, null);
    }

    private sealed record Requested(string Id, ItemExtra? Row, bool Refused);

    private sealed record Loaded(
        IReadOnlyList<Requested> Requested,
        IReadOnlyList<ItemExtra> Targets,
        IReadOnlyDictionary<Guid, IReadOnlyList<ItemExtra>> Folders,
        IReadOnlySet<Guid> InPictureFolders,
        SupportingFileBulkProblem? Problem,
        string? Message)
    {
        public static Loaded Failed(SupportingFileBulkProblem problem, string message) => new([], [], new Dictionary<Guid, IReadOnlyList<ItemExtra>>(), new HashSet<Guid>(), problem, message);

        public IReadOnlyList<ItemExtra> Members(ItemExtra row) => Folders.TryGetValue(row.Id, out var members) ? members : [row];

        public bool IsPictureFolder(ItemExtra target) => InPictureFolders.Contains(target.Id);
    }

    // ── The change ────────────────────────────────────────────────────────────

    private static (ExtraOwnedUpdate? Update, string? Message) BuildUpdate(SupportingFileBulkChangesDto changes)
    {
        var update = new ExtraOwnedUpdate
        {
            AddTags = Clean(changes.AddTags),
            RemoveTags = Clean(changes.RemoveTags),
        };

        if (!string.IsNullOrWhiteSpace(changes.GroupName))
        {
            update = update with { GroupName = ExtraValue<string>.Set(changes.GroupName.Trim()) };
        }
        else if (changes.ClearGroup)
        {
            update = update with { GroupName = ExtraValue<string>.Clear };
        }

        if (!string.IsNullOrWhiteSpace(changes.Role))
        {
            update = update with { Role = ExtraValue<string>.Set(changes.Role.Trim()) };
        }
        else if (changes.ClearRole)
        {
            update = update with { Role = ExtraValue<string>.Clear };
        }

        if (!string.IsNullOrWhiteSpace(changes.ScopeKey))
        {
            if (!ExtraScope.TryParse(changes.ScopeKey, out var scope) || scope.Level == ExtraScopeLevel.None)
            {
                return (null, BadPlace);
            }

            update = update with { Scope = ExtraValue<ExtraScope>.Set(scope) };
        }
        else if (changes.UseFolderLocation)
        {
            update = update with { Scope = ExtraValue<ExtraScope>.Clear };
        }

        return (update, null);
    }

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? tags) =>
        [.. (tags ?? []).Select(tag => tag?.Trim() ?? string.Empty).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>True when applying <paramref name="update"/> to the file would change what it reads as.</summary>
    private static bool WouldChange(ItemExtra file, ExtraOwnedUpdate update)
    {
        if (update.GroupName.IsSet && !string.Equals(file.GroupName, update.GroupName.Value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (update.GroupName.IsSet && file.GroupSource != ExtraGroupSources.You)
        {
            return true;
        }

        if (update.GroupName is { State: ExtraValueState.Cleared } && file.GroupSource == ExtraGroupSources.You)
        {
            return true;
        }

        if (update.Role.IsSet && !string.Equals(file.Role, update.Role.Value, StringComparison.Ordinal))
        {
            return true;
        }

        if (update.Role is { State: ExtraValueState.Cleared } && (file.Role is not null || file.ExtraKind is not null))
        {
            return true;
        }

        if (update.Scope.IsSet && !string.Equals(file.Scope?.Key, update.Scope.Value.Key, StringComparison.Ordinal))
        {
            return true;
        }

        if (update.Scope is { State: ExtraValueState.Cleared }
            && (file.Scope is not null || file.PlaceSeason is not null || file.PlaceEpisode is not null || file.PlaceTrack is not null))
        {
            return true;
        }

        var tags = file.TagList;
        return update.AddTags.Any(tag => !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            || update.RemoveTags.Any(tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    private static SupportingFilePlannedMoveDto ToMove(Guid id, SupportingFileMovePlan plan, string status, string? reason) => new()
    {
        Id = id.ToString("D"),
        From = plan.From!,
        To = plan.To!,
        Status = status,
        Reason = reason,
    };

    private static string RoleNotAvailable(ExtrasConfiguration? config, string fileKind, string role)
    {
        var label = config?.LabelFor(fileKind, role) ?? ExtraRoles.Label(role);
        return string.Create(CultureInfo.InvariantCulture, $"\"{label}\" is not available for this kind of file.");
    }

    private static SupportingFileBulkOutcome<T> Fail<T>(SupportingFileBulkProblem problem, string message) where T : class =>
        SupportingFileBulkOutcome<T>.Fail(problem, message);
}
