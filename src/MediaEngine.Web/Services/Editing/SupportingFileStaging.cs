using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// How the supporting-file editor turns what the person typed into the staged change (universal editor v5 WP7). Pure: the Engine's read of
/// the file and the current stage go in, the new stage comes out. A value typed back to what the file already is takes its field out of the
/// stage, so Save changes only ever says what really differs; a field the form never touched is never in it. The words Review and the footer
/// show for each field (the before and after) are made here, once, so the form and Review can never describe a change differently.
/// </summary>
public static class SupportingFileStaging
{
    public const string FromFileName = "From the file name";
    public const string NoGroup = "No group";
    public const string NoAlongside = "None";
    public const string FolderLocation = "From its folder location";
    public const string SetByYou = "Set by you";

    /// <summary>A stage with nothing in it, for this file.</summary>
    public static SupportingFileStage Start(EditorSupportingFileDto file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new SupportingFileStage(file.TitleId, Guid.Parse(file.File.Id), file.File.Title);
    }

    /// <summary>The stage to carry on from: what is staged for the file, else a fresh one.</summary>
    public static SupportingFileStage From(EditorSupportingFileDto file, SupportingFileStage? staged) => staged ?? Start(file);

    // -- Title and description ------------------------------------------------------------------------------------------------------

    public static SupportingFileStage SetTitle(EditorSupportingFileDto file, SupportingFileStage stage, string? text)
    {
        var typed = Clean(text);
        var saved = file.File.Title;
        if (typed.Length == 0)
        {
            // Blank goes back to the name read from the file; there is nothing to change when the file never had a title of its own.
            return string.IsNullOrWhiteSpace(file.File.OwnTitle)
                ? stage.Without(SupportingFileFields.Title)
                : Replace(stage with { Title = null, ClearTitle = true }, SupportingFileFields.Title, "Title", saved, FromFileName);
        }

        return string.Equals(typed, saved, StringComparison.Ordinal)
            ? stage.Without(SupportingFileFields.Title)
            : Replace(stage with { Title = typed, ClearTitle = false }, SupportingFileFields.Title, "Title", saved, typed);
    }

    public static SupportingFileStage SetDescription(EditorSupportingFileDto file, SupportingFileStage stage, string? text)
    {
        var typed = Clean(text);
        var saved = Clean(file.File.Description);
        if (string.Equals(typed, saved, StringComparison.Ordinal))
        {
            return stage.Without(SupportingFileFields.Description);
        }

        return typed.Length == 0
            ? Replace(stage with { Description = null, ClearDescription = true }, SupportingFileFields.Description, "Description", saved, EditReviewRows.NotSet)
            : Replace(stage with { Description = typed, ClearDescription = false }, SupportingFileFields.Description, "Description", saved.Length == 0 ? EditReviewRows.NotSet : saved, typed);
    }

    // -- Group and role -------------------------------------------------------------------------------------------------------------

    public static SupportingFileStage SetGroup(EditorSupportingFileDto file, SupportingFileStage stage, string? text)
    {
        var typed = Clean(text);
        var saved = file.File.Group ?? string.Empty;
        if (typed.Length == 0)
        {
            // "No group": a group a person chose and a group read from a sub-folder can both be taken off. Group never moves a file (D30).
            return saved.Length == 0
                ? stage.Without(SupportingFileFields.Group)
                : Replace(stage with { GroupName = null, NoGroup = true, CreatesGroup = false }, SupportingFileFields.Group, "Group", saved, NoGroup);
        }

        if (string.Equals(typed, saved, StringComparison.OrdinalIgnoreCase))
        {
            return stage.Without(SupportingFileFields.Group);
        }

        var existing = file.Groups.FirstOrDefault(name => string.Equals(name, typed, StringComparison.OrdinalIgnoreCase));
        return Replace(
            stage with { GroupName = existing ?? typed, NoGroup = false, CreatesGroup = existing is null },
            SupportingFileFields.Group, "Group", saved.Length == 0 ? NoGroup : saved, existing ?? typed);
    }

    public static SupportingFileStage SetRole(EditorSupportingFileDto file, SupportingFileStage stage, string? roleId)
    {
        var typed = Clean(roleId);
        if (typed.Length == 0 || string.Equals(typed, file.File.Role, StringComparison.Ordinal))
        {
            return stage.Without(SupportingFileFields.Role);
        }

        return Replace(stage with { Role = typed }, SupportingFileFields.Role, "Role", RoleLabel(file, file.File.Role), RoleLabel(file, typed));
    }

    // -- Belongs to and Shown alongside ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Belongs to. Picking the place read from the folder when the person had set another one is the same as "Use folder location". Any other
    /// place is the person's own; picking the place it already is changes nothing.
    /// </summary>
    public static SupportingFileStage SetScope(EditorSupportingFileDto file, SupportingFileStage stage, string? key) =>
        FollowScope(file, ChooseScope(file, stage, key));

    private static SupportingFileStage ChooseScope(EditorSupportingFileDto file, SupportingFileStage stage, string? key)
    {
        var typed = Clean(key);
        var setByYou = file.File.ScopeSource == "you";
        if (typed.Length == 0 || string.Equals(typed, file.File.ScopeKey, StringComparison.Ordinal))
        {
            return stage.Without(SupportingFileFields.Scope);
        }

        return string.Equals(typed, file.File.InferredScopeKey, StringComparison.Ordinal) && setByYou
            ? UseFolderLocation(file, stage)
            : Replace(
                stage with { ScopeKey = typed, UseFolderLocation = false, MoveFrom = null, MoveTo = null },
                SupportingFileFields.Scope, "Belongs to", file.ScopeLabel, PlaceLabel(file, typed));
    }

    // A file that belongs to an episode, lesson or track is not also shown alongside one (D14), so a value left from the place it had is
    // cleared with the move; moving back to a place that shows the field takes that clearing out again.
    private static SupportingFileStage FollowScope(EditorSupportingFileDto file, SupportingFileStage stage)
    {
        if (!ShowsAlongside(ScopeOf(file, stage)))
        {
            return AlongsideOf(file, stage).Length == 0
                ? stage
                : SetAlongside(file, stage, null) with { ClearedByScope = true };
        }

        return stage.ClearedByScope ? stage.Without(SupportingFileFields.Alongside) : stage;
    }

    /// <summary>"Use folder location": the file belongs to the place its folder says again.</summary>
    public static SupportingFileStage UseFolderLocation(EditorSupportingFileDto file, SupportingFileStage stage) =>
        FollowScope(file, ReturnToFolder(file, stage));

    private static SupportingFileStage ReturnToFolder(EditorSupportingFileDto file, SupportingFileStage stage)
    {
        if (file.File.ScopeSource != "you")
        {
            return stage.Without(SupportingFileFields.Scope);
        }

        return Replace(
            stage with { ScopeKey = null, UseFolderLocation = true, MoveFrom = null, MoveTo = null },
            SupportingFileFields.Scope, "Belongs to", file.ScopeLabel, file.FolderScopeLabel);
    }

    /// <summary>The Engine's check says the staged Belongs to moves the file: the footer and Review say from where to where.</summary>
    public static SupportingFileStage WithMove(SupportingFileStage stage, string? from, string? to) =>
        stage.Row(SupportingFileFields.Scope) is null ? stage : stage with { MoveFrom = from, MoveTo = to };

    /// <summary>Shown alongside: an episode, lesson or track, or nothing. Picking the guessed one when it was set to another is the same as going back to the guess.</summary>
    public static SupportingFileStage SetAlongside(EditorSupportingFileDto file, SupportingFileStage stage, string? key)
    {
        stage = stage with { ClearedByScope = false };
        var typed = Clean(key);
        var saved = file.File.AlongsideKey;
        var guessed = file.File.InferredAlongsideKey;
        var before = saved is null ? NoAlongside : PlaceLabel(file, saved);
        if (string.Equals(typed, saved ?? string.Empty, StringComparison.Ordinal))
        {
            return stage.Without(SupportingFileFields.Alongside);
        }

        if (typed.Length == 0)
        {
            // "Not shown alongside anything": a guess is overridden; a choice the person made is taken back to the (empty) guess.
            return guessed is null
                ? Replace(stage with { AlongsideKey = null, NoAlongside = false, UseGuessedAlongside = true }, SupportingFileFields.Alongside, "Shown alongside", before, NoAlongside)
                : Replace(stage with { AlongsideKey = null, NoAlongside = true, UseGuessedAlongside = false }, SupportingFileFields.Alongside, "Shown alongside", before, NoAlongside);
        }

        return string.Equals(typed, guessed, StringComparison.Ordinal)
            ? Replace(stage with { AlongsideKey = null, NoAlongside = false, UseGuessedAlongside = true }, SupportingFileFields.Alongside, "Shown alongside", before, PlaceLabel(file, typed))
            : Replace(stage with { AlongsideKey = typed, NoAlongside = false, UseGuessedAlongside = false }, SupportingFileFields.Alongside, "Shown alongside", before, PlaceLabel(file, typed));
    }

    // -- Tags -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Tags. The typed list is compared with the file's own, so the stage holds only the tags to add and to take off.</summary>
    public static SupportingFileStage SetTags(EditorSupportingFileDto file, SupportingFileStage stage, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var typed = tags.Select(Clean).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var saved = file.File.Tags;
        var add = typed.Where(tag => !saved.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
        var remove = saved.Where(tag => !typed.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
        if (add.Count == 0 && remove.Count == 0)
        {
            return stage.Without(SupportingFileFields.Tags);
        }

        return Replace(
            stage with { AddTags = add, RemoveTags = remove },
            SupportingFileFields.Tags, "Tags", saved.Count == 0 ? EditReviewRows.NotSet : string.Join(", ", saved), typed.Count == 0 ? "No tags" : string.Join(", ", typed));
    }

    // -- What the form shows --------------------------------------------------------------------------------------------------------

    public static string TitleOf(EditorSupportingFileDto file, SupportingFileStage? stage) =>
        stage is { ClearTitle: true } ? string.Empty : stage?.Title ?? file.File.Title;

    public static string DescriptionOf(EditorSupportingFileDto file, SupportingFileStage? stage) =>
        stage is { ClearDescription: true } ? string.Empty : stage?.Description ?? file.File.Description ?? string.Empty;

    public static string GroupOf(EditorSupportingFileDto file, SupportingFileStage? stage) =>
        stage is { NoGroup: true } ? string.Empty : stage?.GroupName ?? file.File.Group ?? string.Empty;

    public static string RoleOf(EditorSupportingFileDto file, SupportingFileStage? stage) => stage?.Role ?? file.File.Role;

    /// <summary>The place the file belongs to as the form shows it: the staged one, the folder's after "Use folder location", else the saved one.</summary>
    public static string ScopeOf(EditorSupportingFileDto file, SupportingFileStage? stage) =>
        stage is { UseFolderLocation: true } ? file.File.InferredScopeKey : stage?.ScopeKey ?? file.File.ScopeKey;

    /// <summary>True when the file's place is the person's own now (a staged choice, or one saved earlier and not being reverted).</summary>
    public static bool ScopeIsSetByYou(EditorSupportingFileDto file, SupportingFileStage? stage) =>
        stage is { UseFolderLocation: true } ? false : stage?.ScopeKey is not null || file.File.ScopeSource == "you";

    /// <summary>The key of the episode, lesson or track shown alongside as the form shows it; empty for none.</summary>
    public static string AlongsideOf(EditorSupportingFileDto file, SupportingFileStage? stage)
    {
        if (stage is { NoAlongside: true })
        {
            return string.Empty;
        }

        if (stage is { UseGuessedAlongside: true })
        {
            return file.File.InferredAlongsideKey ?? string.Empty;
        }

        return stage?.AlongsideKey ?? file.File.AlongsideKey ?? string.Empty;
    }

    /// <summary>True when the episode or lesson shown alongside is the Engine's guess from the file name (so the hint can say so).</summary>
    public static bool AlongsideIsGuessed(EditorSupportingFileDto file, SupportingFileStage? stage) =>
        AlongsideOf(file, stage) is { Length: > 0 } key && string.Equals(key, file.File.InferredAlongsideKey, StringComparison.Ordinal);

    public static IReadOnlyList<string> TagsOf(EditorSupportingFileDto file, SupportingFileStage? stage)
    {
        if (stage is null || (stage.AddTags.Count == 0 && stage.RemoveTags.Count == 0))
        {
            return file.File.Tags;
        }

        return
        [
            .. file.File.Tags.Where(tag => !stage.RemoveTags.Contains(tag, StringComparer.OrdinalIgnoreCase)),
            .. stage.AddTags,
        ];
    }

    /// <summary>
    /// The places Shown alongside offers: the episodes, lessons and tracks under the place the file belongs to, or all of them when it belongs to
    /// the title itself. The title's own place and seasons or sections are not offered: a file is shown alongside one item.
    /// </summary>
    public static IReadOnlyList<EditorSupportingFilePlaceDto> AlongsideChoices(EditorSupportingFileDto file, string scopeKey)
    {
        ArgumentNullException.ThrowIfNull(file);
        var items = file.Places.Where(place => place.Level is "episode" or "file" or "track").ToList();
        if (string.IsNullOrEmpty(scopeKey) || scopeKey == "root")
        {
            return items;
        }

        return [.. items.Where(place => string.Equals(place.ParentKey, scopeKey, StringComparison.Ordinal))];
    }

    /// <summary>
    /// What the title calls the items Shown alongside lists: "episode" for a show, "lesson" for a course (a folder of videos), "track" for an
    /// album. It follows the title's media type, the same rule the header uses for "with lesson 02", and the places the Engine offers.
    /// </summary>
    public static string AlongsideNoun(EditorSupportingFileDto file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var mediaType = file.Header?.Root.MediaType;
        return VideoFolderWording.IsVideoFolderMediaType(mediaType) || file.Places.Any(place => place.Level == "file") ? "lesson"
            : string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase) || file.Places.Any(place => place.Level == "track") ? "track"
            : "episode";
    }

    /// <summary>Shown alongside is hidden when the file already belongs to an episode, lesson or track (D14): it would only repeat it.</summary>
    public static bool ShowsAlongside(string scopeKey) =>
        !(scopeKey.StartsWith("episode:", StringComparison.Ordinal) || scopeKey.StartsWith("file:", StringComparison.Ordinal) || scopeKey.StartsWith("track:", StringComparison.Ordinal));

    // -- Words ----------------------------------------------------------------------------------------------------------------------

    public static string PlaceLabel(EditorSupportingFileDto file, string key) =>
        file.Places.FirstOrDefault(place => string.Equals(place.Key, key, StringComparison.Ordinal))?.Label ?? key;

    public static string RoleLabel(EditorSupportingFileDto file, string? roleId) =>
        file.Roles.FirstOrDefault(role => string.Equals(role.Id, roleId, StringComparison.Ordinal))?.Label ?? file.File.RoleLabel;

    private static SupportingFileStage Replace(SupportingFileStage stage, string key, string label, string before, string after)
    {
        var rows = stage.Rows.Where(row => row.Key != key).ToList();
        rows.Add(new SupportingFileStagedRow(key, label, before, after));
        return stage with { Rows = [.. rows.OrderBy(row => Order(row.Key))] };
    }

    // The order the form shows the fields in, which is the order Review lists them.
    private static int Order(string key) => key switch
    {
        SupportingFileFields.Title => 0,
        SupportingFileFields.Description => 1,
        SupportingFileFields.Group => 2,
        SupportingFileFields.Role => 3,
        SupportingFileFields.Scope => 4,
        SupportingFileFields.Alongside => 5,
        _ => 6,
    };

    private static string Clean(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
}
