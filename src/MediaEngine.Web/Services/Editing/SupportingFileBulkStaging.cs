using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// How a bulk action on ticked supporting files (universal editor v5 WP8, screens E and F) becomes staged changes. Pure, like
/// <see cref="SupportingFileStaging"/>, but it works from the browser's rows instead of one file's full editor read: the row says what the file is
/// now, the staged change says what it will be. Each file keeps its own <see cref="SupportingFileStage"/>, so Review lists every file before saving
/// and one file can be taken out again. A value that equals what the file already is takes its field out of the stage.
/// </summary>
public static class SupportingFileBulkStaging
{
    /// <summary>Review's words for a Belongs to that goes back to the folder.</summary>
    public const string FolderLocationWords = SupportingFileStaging.FolderLocation;

    /// <summary>The stage to carry on from: what is staged for the file, else a fresh one.</summary>
    public static SupportingFileStage StageOf(Guid titleId, SupportingFileRowDto row, SupportingFileStage? staged)
    {
        ArgumentNullException.ThrowIfNull(row);
        return staged ?? new SupportingFileStage(titleId, Guid.Parse(row.Id), row.Title);
    }

    /// <summary>Group: a name picks (or creates) the group; blank puts the file in no group. Group never moves a file (D30).</summary>
    public static SupportingFileStage SetGroup(
        Guid titleId, SupportingFileRowDto row, SupportingFileStage? staged, string? name, IReadOnlyCollection<string> knownGroups)
    {
        var stage = StageOf(titleId, row, staged);
        var typed = Clean(name);
        var saved = row.Group ?? string.Empty;
        if (typed.Length == 0)
        {
            return saved.Length == 0
                ? stage.Without(SupportingFileFields.Group)
                : SupportingFileStaging.Replace(
                    stage with { GroupName = null, NoGroup = true, CreatesGroup = false },
                    SupportingFileFields.Group, "Group", saved, SupportingFileStaging.NoGroup);
        }

        if (string.Equals(typed, saved, StringComparison.OrdinalIgnoreCase))
        {
            return stage.Without(SupportingFileFields.Group);
        }

        var existing = knownGroups.FirstOrDefault(known => string.Equals(known, typed, StringComparison.OrdinalIgnoreCase));
        return SupportingFileStaging.Replace(
            stage with { GroupName = existing ?? typed, NoGroup = false, CreatesGroup = existing is null },
            SupportingFileFields.Group, "Group", saved.Length == 0 ? SupportingFileStaging.NoGroup : saved, existing ?? typed);
    }

    /// <summary>Role: one of the roles of the files' kind.</summary>
    public static SupportingFileStage SetRole(
        Guid titleId, SupportingFileRowDto row, SupportingFileStage? staged, string roleId, string roleLabel)
    {
        var stage = StageOf(titleId, row, staged);
        var typed = Clean(roleId);
        return typed.Length == 0 || string.Equals(typed, row.Role, StringComparison.Ordinal)
            ? stage.Without(SupportingFileFields.Role)
            : SupportingFileStaging.Replace(stage with { Role = typed }, SupportingFileFields.Role, "Role", row.RoleLabel, roleLabel);
    }

    /// <summary>
    /// Belongs to: a place of the title. The file moves when the change is saved, in a Managed folder; the Engine's check says whether it does
    /// (<see cref="SupportingFileStaging.WithMove"/>). Choosing the place the file is already in changes nothing.
    /// </summary>
    public static SupportingFileStage SetScope(
        Guid titleId, SupportingFileRowDto row, SupportingFileStage? staged, string scopeKey, string scopeLabel)
    {
        var stage = StageOf(titleId, row, staged);
        var typed = Clean(scopeKey);
        if (typed.Length == 0 || string.Equals(typed, row.WhereKey, StringComparison.Ordinal))
        {
            return stage.Without(SupportingFileFields.Scope);
        }

        return SupportingFileStaging.Replace(
            stage with { ScopeKey = typed, UseFolderLocation = false, MoveFrom = null, MoveTo = null },
            SupportingFileFields.Scope, "Belongs to", row.WhereLabel, scopeLabel);
    }

    /// <summary>"Use folder location": every file goes back to the place its folder says.</summary>
    public static SupportingFileStage UseFolderLocation(Guid titleId, SupportingFileRowDto row, SupportingFileStage? staged) =>
        SupportingFileStaging.Replace(
            StageOf(titleId, row, staged) with { ScopeKey = null, UseFolderLocation = true, MoveFrom = null, MoveTo = null },
            SupportingFileFields.Scope, "Belongs to", row.WhereLabel, FolderLocationWords);

    /// <summary>Add tags: tags the file lacks are added; the ones it has are left as they are.</summary>
    public static SupportingFileStage AddTags(Guid titleId, SupportingFileRowDto row, SupportingFileStage? staged, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var stage = StageOf(titleId, row, staged);
        var wanted = tags.Select(Clean).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var add = stage.AddTags
            .Concat(wanted.Where(tag => !row.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (add.Count == 0 && stage.RemoveTags.Count == 0)
        {
            return stage.Without(SupportingFileFields.Tags);
        }

        var after = row.Tags.Where(tag => !stage.RemoveTags.Contains(tag, StringComparer.OrdinalIgnoreCase)).Concat(add).ToList();
        return SupportingFileStaging.Replace(
            stage with { AddTags = add },
            SupportingFileFields.Tags, "Tags",
            row.Tags.Count == 0 ? EditReviewRows.NotSet : string.Join(", ", row.Tags),
            string.Join(", ", after));
    }

    /// <summary>The tags to add, as the several-files form shows them: replaces what was staged before, so taking a tag out of the box takes it off.</summary>
    public static SupportingFileStage SetAddedTags(Guid titleId, SupportingFileRowDto row, SupportingFileStage? staged, IEnumerable<string> tags) =>
        AddTags(titleId, row, StageOf(titleId, row, staged).Without(SupportingFileFields.Tags), tags);

    /// <summary>
    /// Shown alongside: an episode, lesson or track of the title (a place key), or none. Shown alongside never moves a file and never changes
    /// where it belongs. The list does not say what a file is shown alongside now, so Review says "Not set" for the earlier value.
    /// </summary>
    public static SupportingFileStage SetAlongside(Guid titleId, SupportingFileRowDto row, SupportingFileStage? staged, string? key, string label)
    {
        var stage = StageOf(titleId, row, staged) with { ClearedByScope = false };
        var typed = Clean(key);
        return typed.Length == 0
            ? stage.Without(SupportingFileFields.Alongside)
            : SupportingFileStaging.Replace(
                stage with { AlongsideKey = typed, NoAlongside = false, UseGuessedAlongside = false },
                SupportingFileFields.Alongside, "Shown alongside", EditReviewRows.NotSet, label);
    }

    /// <summary>
    /// Puts the plan of a Belongs to check onto the stages: a file the Engine says will move gets the from and to paths (so the amber footer and
    /// Review show them), and one it does not move has them cleared.
    /// </summary>
    public static SupportingFileStage WithPlannedMove(SupportingFileStage stage, SupportingFilePlannedMoveDto? move) =>
        SupportingFileStaging.WithMove(stage, move?.From, move?.To);

    // -- What the several-files form shows -----------------------------------------------------------------------------------------

    /// <summary>
    /// A value several files share, as the form shows it: the value when they all read the same, else null (the form says "Mixed · leave
    /// unchanged"). <paramref name="valueOf"/> reads one file's value as it will be (staged or saved).
    /// </summary>
    public static string? Shared(IReadOnlyList<SupportingFileRowDto> rows, Func<SupportingFileRowDto, string> valueOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(valueOf);
        if (rows.Count == 0)
        {
            return null;
        }

        var first = valueOf(rows[0]);
        return rows.All(row => string.Equals(valueOf(row), first, StringComparison.Ordinal)) ? first : null;
    }

    public static string GroupOf(SupportingFileRowDto row, SupportingFileStage? stage) =>
        stage is { NoGroup: true } ? string.Empty : stage?.GroupName ?? row.Group ?? string.Empty;

    public static string RoleOf(SupportingFileRowDto row, SupportingFileStage? stage) => stage?.Role ?? row.Role;

    /// <summary>The key of the episode, lesson or track the file is shown alongside, as staged; empty when nothing is staged (the list does not know the saved value).</summary>
    public static string AlongsideOf(SupportingFileStage? stage) => stage?.AlongsideKey ?? string.Empty;

    /// <summary>The value the Belongs to dropdown carries for "Use folder location".</summary>
    public const string FolderScopeValue = "use-folder-location";

    public static string ScopeOf(SupportingFileRowDto row, SupportingFileStage? stage) =>
        stage is { UseFolderLocation: true } ? FolderScopeValue : stage?.ScopeKey ?? row.WhereKey;

    private static string Clean(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
}
