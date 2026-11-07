using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Details;

/// <summary>
/// The words and limits the supporting-files list uses (universal editor v5 D15, D26, screen E). The page size is the one number here
/// that is a setting in <c>config/editor.json</c> (rows per page); a caller passes it as <c>pageSize</c> and these are only what the
/// Engine uses when it is left out.
/// </summary>
public static class SupportingFileListDefaults
{
    /// <summary>Rows per page when the request names none. The Dashboard sends the configured value (<c>config/editor.json</c>).</summary>
    public const int PageSize = 25;

    /// <summary>The most rows one page may hold, whatever the request asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary><c>where</c> value for no filter: the whole title.</summary>
    public const string WhereAll = "all";

    /// <summary><c>group</c> value for "All groups".</summary>
    public const string GroupAll = "all";

    /// <summary><c>group</c> value for files that are in no group.</summary>
    public const string GroupUngrouped = "ungrouped";

    /// <summary>Sort by the title shown (the default).</summary>
    public const string SortTitle = "title";

    /// <summary>Sort by kind of file, then title.</summary>
    public const string SortType = "type";

    /// <summary>Sort by where the file belongs (the title itself, then seasons and episodes in order), then title.</summary>
    public const string SortWhere = "where";

    /// <summary>The type filter values: what a file is, as a person would say it.</summary>
    public const string TypeDocument = "document";

    public const string TypePicture = "picture";

    /// <summary>A video clip.</summary>
    public const string TypeClip = "clip";

    public const string TypeAudio = "audio";

    /// <summary>The most files one bulk call (change or delete) takes; the browser stops ticking at this number.</summary>
    public const int MaxFilesPerCall = 2000;
}

/// <summary>One row of the supporting-files list: what the browser's table shows and what a bulk action needs to name.</summary>
public sealed class SupportingFileRowDto
{
    /// <summary>The file's id. For a folder of pictures it is the first picture's; bulk actions on it apply to the whole folder.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>One of <see cref="DetailExtraCardKinds"/>.</summary>
    public string Kind { get; init; } = DetailExtraCardKinds.Document;

    /// <summary>One of the <c>SupportingFileListDefaults.Type*</c> values.</summary>
    public string Type { get; init; } = SupportingFileListDefaults.TypeDocument;

    /// <summary>The short badge on the row: "PDF", "TXT", "EPUB", "IMG", "VIDEO", "AUDIO".</summary>
    public string Badge { get; init; } = string.Empty;

    /// <summary>The type column: "PDF", "Text", "EPUB", "Image", "Video", "Audio".</summary>
    public string TypeLabel { get; init; } = string.Empty;

    /// <summary>The title shown: the one a person gave, else the cleaned file name.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>True when a person gave the title; false while the cleaned file name stands in ("no title yet").</summary>
    public bool HasOwnTitle { get; init; }

    /// <summary>The file name with its extension.</summary>
    public string FileName { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>What the file belongs to, as a scope key (<c>root</c>, <c>season:2</c>, <c>episode:2:3</c>, <c>folder:Section 1</c>, ...).</summary>
    public string WhereKey { get; init; } = "root";

    /// <summary>The scope as shown: "Season 2", "Section 1", "Season 2, episode 3"; "Title level" for the title itself.</summary>
    public string WhereLabel { get; init; } = string.Empty;

    /// <summary>Where the scope comes from: <c>folder</c> (the file's folder says it) or <c>you</c> (a person chose it).</summary>
    public string ScopeSource { get; init; } = "folder";

    /// <summary>The episode, lesson or track the file is shown alongside, as a scope key; null when none.</summary>
    public string? AlongsideKey { get; init; }

    /// <summary>The words for <see cref="AlongsideKey"/> ("Season 2, episode 3"); null when none.</summary>
    public string? AlongsideLabel { get; init; }

    /// <summary>The group's id; null when ungrouped.</summary>
    public string? GroupId { get; init; }

    /// <summary>The group's name; null when ungrouped.</summary>
    public string? Group { get; init; }

    /// <summary>What the file is: a role id (see <see cref="DetailExtraCardViewModel.Role"/>).</summary>
    public string Role { get; init; } = string.Empty;

    public string RoleLabel { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public long SizeBytes { get; init; }

    public int? PageCount { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>How many files the row holds (more than one only for a folder of pictures).</summary>
    public int ItemCount { get; init; } = 1;

    /// <summary>True when the file is no longer on disk and the record is kept for a while as "File missing".</summary>
    public bool IsMissing { get; init; }
}

/// <summary>
/// One entry of the browser's Where list ("Season 2", 108 files). A season includes its episodes and a section includes its lessons;
/// the title level is the files that belong to the title itself.
/// </summary>
public sealed class SupportingFileWhereOptionDto
{
    /// <summary>The <c>where</c> value that selects it: <c>all</c>, <c>root</c>, or a scope key.</summary>
    public string Key { get; init; } = SupportingFileListDefaults.WhereAll;

    /// <summary>"Whole title", "Title level", "Season 2", "Season 2, episode 3", "Section 1".</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary><c>all</c>, <c>root</c>, <c>season</c>, <c>episode</c>, <c>folder</c>, <c>file</c> or <c>track</c>.</summary>
    public string Level { get; init; } = "all";

    /// <summary>How many files this option selects (all files of the title, not narrowed by the other filters).</summary>
    public int FileCount { get; init; }

    /// <summary>The key of the option this one sits under (an episode under its season); null for the top level.</summary>
    public string? ParentKey { get; init; }
}

/// <summary>One page of a root title's supporting files, with the facets the browser's left list needs.</summary>
public sealed class SupportingFileListDto
{
    /// <summary>This page's rows.</summary>
    public IReadOnlyList<SupportingFileRowDto> Items { get; init; } = [];

    /// <summary>How many rows match the filters (the "of 238" in "Showing 1-25 of 238").</summary>
    public int Total { get; init; }

    /// <summary>Every supporting file of the title, whatever the filters (the count beside "Whole title").</summary>
    public int TotalAll { get; init; }

    /// <summary>The page shown, from 1.</summary>
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = SupportingFileListDefaults.PageSize;

    /// <summary>How many pages the filtered list has (at least 1).</summary>
    public int PageCount { get; init; } = 1;

    /// <summary>The title's groups in their order, each with how many files it holds.</summary>
    public IReadOnlyList<SupportingFileGroupDto> Groups { get; init; } = [];

    /// <summary>How many files are in no group.</summary>
    public int UngroupedCount { get; init; }

    /// <summary>The Where list: the whole title, the title level, then each season, section, episode and lesson that has files.</summary>
    public IReadOnlyList<SupportingFileWhereOptionDto> WhereOptions { get; init; } = [];
}

/// <summary>
/// The changes one bulk action makes to every file it names (universal editor v5 D16: Set group, Set role, Belongs to, Add tags). Only
/// the fields that are present change; the rest are left alone. Nothing here renames or rewrites a file.
/// </summary>
public sealed class SupportingFileBulkChangesDto
{
    /// <summary>Put the files in the group of this name; a name the title has no group for yet creates the group.</summary>
    [JsonPropertyName("groupName")]
    public string? GroupName { get; init; }

    /// <summary>Take the files out of whatever group a person chose (a group read from a folder applies again).</summary>
    [JsonPropertyName("clearGroup")]
    public bool ClearGroup { get; init; }

    /// <summary>
    /// Put the files in no group at all, even where their folder names one. A rescan keeps it. (<see cref="ClearGroup"/> goes back to the
    /// group the folder names; this says "none".)
    /// </summary>
    [JsonPropertyName("noGroup")]
    public bool NoGroup { get; init; }

    /// <summary>The role to give (a role id from the list for the files' kind in <c>config/extras.json</c>).</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>Go back to the role guessed from the names.</summary>
    [JsonPropertyName("clearRole")]
    public bool ClearRole { get; init; }

    /// <summary>The scope the files belong to now (a scope key). In a Managed folder the file moves when this is saved.</summary>
    [JsonPropertyName("scopeKey")]
    public string? ScopeKey { get; init; }

    /// <summary>"Use folder location": go back to the scope read from the file's folder.</summary>
    [JsonPropertyName("useFolderLocation")]
    public bool UseFolderLocation { get; init; }

    [JsonPropertyName("addTags")]
    public IReadOnlyList<string> AddTags { get; init; } = [];

    [JsonPropertyName("removeTags")]
    public IReadOnlyList<string> RemoveTags { get; init; } = [];

    /// <summary>
    /// The title to give the file. A title belongs to one file, so a change that names more than one file and a title is refused. A blank
    /// text counts as <see cref="ClearTitle"/>.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Go back to the title read from the file name.</summary>
    [JsonPropertyName("clearTitle")]
    public bool ClearTitle { get; init; }

    /// <summary>The description to give the file (one file only, like the title). A blank text counts as <see cref="ClearDescription"/>.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Take the description away.</summary>
    [JsonPropertyName("clearDescription")]
    public bool ClearDescription { get; init; }

    /// <summary>
    /// The episode or lesson the files are shown alongside (a scope key). Shown alongside never moves a file and never changes where it belongs.
    /// </summary>
    [JsonPropertyName("alongsideKey")]
    public string? AlongsideKey { get; init; }

    /// <summary>Say the files are shown alongside no episode or lesson, overriding a guess from the file name.</summary>
    [JsonPropertyName("noAlongside")]
    public bool NoAlongside { get; init; }

    /// <summary>Go back to the episode or lesson guessed from the file name.</summary>
    [JsonPropertyName("useGuessedAlongside")]
    public bool UseGuessedAlongside { get; init; }
}

/// <summary>
/// A bulk update of the supporting files of one title (universal editor v5 D16, D30). It is a change in the editor's staged save
/// (<c>EditSessionChangeKinds.SupportingFiles</c>, whose entity is the title), and the same body asks for the preview that lists what would
/// change and which files would move. Repeating a saved request changes nothing more.
/// </summary>
public sealed class SupportingFileBulkUpdateRequest
{
    /// <summary>The files to change, by id. An id that is not one of this title's files is named in the answer; a staged save with one is refused as a whole.</summary>
    [JsonPropertyName("ids")]
    public IReadOnlyList<string> Ids { get; init; } = [];

    [JsonPropertyName("changes")]
    public SupportingFileBulkChangesDto Changes { get; init; } = new();
}

/// <summary>What happened to one file named by a bulk action.</summary>
public static class SupportingFileBulkStatuses
{
    /// <summary>The file was changed (or, in a preview, would be).</summary>
    public const string Changed = "changed";

    /// <summary>The file already read as asked.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>The file is not there any more.</summary>
    public const string NotFound = "not_found";

    /// <summary>The file is not one of this title's.</summary>
    public const string Refused = "refused";

    /// <summary>A delete removed the file's record.</summary>
    public const string Removed = "removed";
}

/// <summary>The result for one file of a bulk action.</summary>
public sealed class SupportingFileBulkItemResultDto
{
    public string Id { get; init; } = string.Empty;

    /// <summary>One of <see cref="SupportingFileBulkStatuses"/>.</summary>
    public string Status { get; init; } = SupportingFileBulkStatuses.Changed;

    /// <summary>The plain-English reason for <c>refused</c> or <c>not_found</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// A line for the person about the file itself: "Set by you · file stays where it is" for a Belongs to change in a Read-only folder,
    /// "A file with that name is already there, so this file stays where it is." for a move that cannot happen.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>What a Belongs to change does to a file on disk.</summary>
public static class SupportingFileMoveStatuses
{
    /// <summary>A preview: the file will move when the change is saved.</summary>
    public const string Planned = "planned";

    /// <summary>The file was moved.</summary>
    public const string Moved = "moved";

    /// <summary>The file could not be moved (in use, no permission); the change itself is saved.</summary>
    public const string Failed = "failed";
}

/// <summary>One file a Belongs to change moves in a Managed folder: the before and after path the amber footer and Review show.</summary>
public sealed class SupportingFilePlannedMoveDto
{
    public string Id { get; init; } = string.Empty;

    public string From { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;

    /// <summary>One of <see cref="SupportingFileMoveStatuses"/>.</summary>
    public string Status { get; init; } = SupportingFileMoveStatuses.Planned;

    /// <summary>Why a move failed.</summary>
    public string? Reason { get; init; }
}

/// <summary>The answer to a bulk update: one result per file, and the moves a Belongs to change makes (none in a Read-only folder).</summary>
public sealed class SupportingFileBulkUpdateResultDto
{
    /// <summary>True when nothing was written (a preview).</summary>
    public bool Preview { get; init; }

    public IReadOnlyList<SupportingFileBulkItemResultDto> Results { get; init; } = [];

    /// <summary>How many files changed (or would).</summary>
    public int Changed { get; init; }

    /// <summary>The files that move in a Managed folder. Empty when no Belongs to change moves a file.</summary>
    public IReadOnlyList<SupportingFilePlannedMoveDto> Moves { get; init; } = [];
}

/// <summary>A bulk delete of supporting files (universal editor v5 D16, D29): the records go; the files follow the library's file handling.</summary>
public sealed class SupportingFileBulkDeleteRequest
{
    public IReadOnlyList<string> Ids { get; init; } = [];
}

/// <summary>What became of a deleted file on disk.</summary>
public static class SupportingFileDiskOutcomes
{
    /// <summary>The file was deleted.</summary>
    public const string Deleted = "deleted";

    /// <summary>The file went to the Recycle bin.</summary>
    public const string Recycled = "recycled";

    /// <summary>The file stays where it is (a Read-only folder, or a file the safety check keeps); it is hidden from the Library and future scans.</summary>
    public const string Kept = "kept";

    /// <summary>The file was not on disk.</summary>
    public const string Gone = "gone";
}

/// <summary>The result for one file of a bulk delete.</summary>
public sealed class SupportingFileDeleteItemResultDto
{
    public string Id { get; init; } = string.Empty;

    /// <summary><see cref="SupportingFileBulkStatuses.Removed"/>, <see cref="SupportingFileBulkStatuses.NotFound"/> or <see cref="SupportingFileBulkStatuses.Refused"/>.</summary>
    public string Status { get; init; } = SupportingFileBulkStatuses.Removed;

    /// <summary>One of <see cref="SupportingFileDiskOutcomes"/>; null when the record was not removed.</summary>
    public string? Disk { get; init; }

    public string? Reason { get; init; }
}

/// <summary>The answer to a bulk delete.</summary>
public sealed class SupportingFileBulkDeleteResultDto
{
    public IReadOnlyList<SupportingFileDeleteItemResultDto> Results { get; init; } = [];

    /// <summary>How many records were removed.</summary>
    public int Removed { get; init; }
}

/// <summary>
/// A supporting file found by search (universal editor v5 item 18): who owns it and where in the owner it sits. Carried on a
/// <c>UniversalSearchResultDto</c> in the "Supporting files" section.
/// </summary>
public sealed class SupportingFileSearchHitDto
{
    /// <summary>The title the file belongs to (the show, album, movie or book the file's page opens on).</summary>
    public Guid OwnerId { get; init; }

    public string OwnerName { get; init; } = string.Empty;

    /// <summary>"Season 2", "Section 1"; empty for the title itself.</summary>
    public string ScopeLabel { get; init; } = string.Empty;

    /// <summary>One of the <c>SupportingFileListDefaults.Type*</c> values.</summary>
    public string Type { get; init; } = SupportingFileListDefaults.TypeDocument;

    public string TypeLabel { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string RoleLabel { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;
}
