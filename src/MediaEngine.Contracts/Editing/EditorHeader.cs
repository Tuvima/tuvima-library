using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>The state mark on a row of the header's item dropdown.</summary>
public static class EditorNavItemStates
{
    /// <summary>The item carries a provider match (the tick).</summary>
    public const string Matched = "matched";

    /// <summary>The item has no provider match (the warning).</summary>
    public const string NotMatched = "not_matched";

    /// <summary>A video the viewer has watched to the end (a course has no provider match, so its tick means watched).</summary>
    public const string Watched = "watched";

    /// <summary>A video the viewer has not finished.</summary>
    public const string Unwatched = "unwatched";

    /// <summary>The row carries no mark (the item itself in a list of supporting files, or a kind that is neither matched nor watched).</summary>
    public const string None = "none";
}

/// <summary>
/// What the editor's header needs beyond the item's fields (universal editor v5, 3a): the way around the family (home, Season or Section,
/// item), the cover's colours for the gradient, the file facts the Files tab used to carry, and the numbers and per-kind rules from
/// <c>config/editor.json</c>. The Dashboard decides nothing from the kind of item: whether a home button, a second dropdown or an item
/// dropdown shows is told here.
/// </summary>
public sealed class EditorHeaderDto
{
    /// <summary>The kind of item the header describes: an id from <c>config/editor.json</c> such as <c>tv_episode</c>, <c>movie</c>, <c>course</c> or <c>album</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>The title at the top of the family: the show, course, album, Series, or the item itself when it has no family.</summary>
    [JsonPropertyName("root")]
    public EditorHeaderRootDto Root { get; init; } = new();

    /// <summary>The item the header was opened on (the highlighted row of the item dropdown).</summary>
    [JsonPropertyName("current_id")]
    public Guid CurrentId { get; init; }

    /// <summary>The plain label beside the title: the show's name on an episode, or the kind word ("Movie", "Book") when there is no family to name.</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>True when <see cref="Label"/> is a kind word rather than the name of a parent.</summary>
    [JsonPropertyName("label_is_kind")]
    public bool LabelIsKind { get; init; }

    /// <summary>True when the header has a home button. A movie, a standalone book and a person have none.</summary>
    [JsonPropertyName("show_home")]
    public bool ShowHome { get; init; }

    /// <summary>The item the home button opens (the root of the family).</summary>
    [JsonPropertyName("home_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? HomeId { get; init; }

    /// <summary>True when the editor is already on the root, so the home button is drawn highlighted and goes nowhere.</summary>
    [JsonPropertyName("home_is_current")]
    public bool HomeIsCurrent { get; init; }

    /// <summary>The Season or Section dropdown; null when the kind has none.</summary>
    [JsonPropertyName("second_level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditorHeaderSecondLevelDto? SecondLevel { get; init; }

    /// <summary>True when the item dropdown shows: the item's family has more than the item, or it has supporting files.</summary>
    [JsonPropertyName("show_item_dropdown")]
    public bool ShowItemDropdown { get; init; }

    /// <summary>The first page of the item dropdown for the item's own level, and the supporting files of that level.</summary>
    [JsonPropertyName("level")]
    public EditorHeaderLevelDto Level { get; init; } = new();

    /// <summary>The last row of the item dropdown, "Browse files and folders"; null when the title has no folder on disk to browse.</summary>
    [JsonPropertyName("folder_button")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditorHeaderFolderButtonDto? FolderButton { get; init; }

    /// <summary>The root's cover colours for the header gradient; null when the cover has none stored yet (the neutral near-black applies).</summary>
    [JsonPropertyName("colors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditorCoverColorsDto? Colors { get; init; }

    /// <summary>The file's facts for Quick facts; null for a container (a show, a season, an album) whose totals come from the Quick facts read.</summary>
    [JsonPropertyName("file_facts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditorFileFactsDto? FileFacts { get; init; }

    /// <summary>The thresholds and the rules of this kind of item from <c>config/editor.json</c>.</summary>
    [JsonPropertyName("config")]
    public EditorHeaderConfigDto Config { get; init; } = new();
}

/// <summary>The root of the family and its cover.</summary>
public sealed class EditorHeaderRootDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The Engine's media type of the family (<c>TV</c>, <c>Movies</c>, <c>Music</c>, <c>Books</c>, <c>Videos</c> ...).</summary>
    [JsonPropertyName("media_type")]
    public string MediaType { get; init; } = string.Empty;

    /// <summary>The 96 by 144 cover picture; null when the root has none.</summary>
    [JsonPropertyName("cover_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoverUrl { get; init; }
}

/// <summary>The cover's three stored colours (as <c>#rrggbb</c>); any of them may be missing.</summary>
public sealed class EditorCoverColorsDto
{
    [JsonPropertyName("primary_hex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryHex { get; init; }

    [JsonPropertyName("secondary_hex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecondaryHex { get; init; }

    [JsonPropertyName("accent_hex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccentHex { get; init; }
}

/// <summary>The Season (TV) or Section (a course) dropdown.</summary>
public sealed class EditorHeaderSecondLevelDto
{
    /// <summary>The dropdown's word: <c>Season</c> or <c>Section</c>.</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>The entry the editor is inside; null while the editor is on the root and none is chosen.</summary>
    [JsonPropertyName("current_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CurrentId { get; init; }

    [JsonPropertyName("entries")]
    public IReadOnlyList<EditorHeaderLevelEntryDto> Entries { get; init; } = [];
}

/// <summary>One season or section.</summary>
public sealed class EditorHeaderLevelEntryDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The season number (0 is Specials) or the section's place in the course; null when unknown.</summary>
    [JsonPropertyName("number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Number { get; init; }

    /// <summary>How many episodes or videos of it are in the Library.</summary>
    [JsonPropertyName("item_count")]
    public int ItemCount { get; init; }

    /// <summary>How many supporting files belong to it (its own and those of its episodes or lessons).</summary>
    [JsonPropertyName("supporting_file_count")]
    public int SupportingFileCount { get; init; }
}

/// <summary>
/// What the item dropdown lists for one level (a season, a section, or the root itself): the first page of its items and the supporting
/// files that belong to it. Longer lists are read page by page from <c>GET /api/v1/metadata/{id}/editor-nav/level</c>.
/// </summary>
public sealed class EditorHeaderLevelDto
{
    /// <summary>The season, section or root the list is for; null while a show is open with no season chosen.</summary>
    [JsonPropertyName("level_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LevelId { get; init; }

    /// <summary>The level's name as shown over its supporting files ("Season 2", "02 · Components", or the title).</summary>
    [JsonPropertyName("level_title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LevelTitle { get; init; }

    /// <summary>How many rows the list holds in all.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    /// <summary>How many rows a page holds (<c>item_list_page_size</c> in <c>config/editor.json</c>).</summary>
    [JsonPropertyName("page_size")]
    public int PageSize { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<EditorNavItemDto> Items { get; init; } = [];

    [JsonPropertyName("supporting_files")]
    public EditorHeaderSupportingFilesDto SupportingFiles { get; init; } = new();
}

/// <summary>One row of the item dropdown: an episode, a track, a lesson, a book or the item itself.</summary>
public sealed class EditorNavItemDto
{
    /// <summary>The item's id, the one the editor is opened on.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>What the row is: <c>episode</c>, <c>track</c>, <c>video</c>, <c>book</c>, <c>audiobook</c>, <c>comic</c>, <c>movie</c> or <c>item</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "item";

    /// <summary>The code shown before the title: <c>S02E03</c> for an episode, <c>04</c> for a track or a lesson. Never an S/E code for a course.</summary>
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    /// <summary>The item's number inside its season, section, album or Series.</summary>
    [JsonPropertyName("number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Number { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The still or thumbnail; null when there is none.</summary>
    [JsonPropertyName("still_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StillUrl { get; init; }

    /// <summary>One of <see cref="EditorNavItemStates"/>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = EditorNavItemStates.None;

    /// <summary>The item's length in seconds; null when not known.</summary>
    [JsonPropertyName("duration_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DurationSeconds { get; init; }

    /// <summary>True for the item the editor is open on.</summary>
    [JsonPropertyName("is_current")]
    public bool IsCurrent { get; init; }
}

/// <summary>The supporting files that belong to one level, as far as the item dropdown lists them.</summary>
public sealed class EditorHeaderSupportingFilesDto
{
    /// <summary>The scope key the files belong to (<c>root</c>, <c>season:2</c>, <c>folder:Section 1</c>); null when every file of the title counts (an album, a book).</summary>
    [JsonPropertyName("scope_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScopeKey { get; init; }

    /// <summary>How many files belong to this level (a picture folder counts once).</summary>
    [JsonPropertyName("count")]
    public int Count { get; init; }

    /// <summary>How many supporting files the whole title has.</summary>
    [JsonPropertyName("total_in_root")]
    public int TotalInRoot { get; init; }

    /// <summary>True when there are more files than fit inline, so the dropdown offers "All supporting files" and the browser.</summary>
    [JsonPropertyName("show_all")]
    public bool ShowAll { get; init; }

    /// <summary>The first files, at most <c>inline_supporting_files</c> of them.</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<EditorHeaderSupportingFileDto> Files { get; init; } = [];
}

/// <summary>One supporting file in the item dropdown.</summary>
public sealed class EditorHeaderSupportingFileDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>What opens it: <c>document</c>, <c>pictures</c> or <c>clip</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "document";

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The type badge: the file's extension in capitals ("PDF", "MP4").</summary>
    [JsonPropertyName("type_badge")]
    public string TypeBadge { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("role_label")]
    public string RoleLabel { get; init; } = string.Empty;

    [JsonPropertyName("group")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Group { get; init; }

    [JsonPropertyName("page_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PageCount { get; init; }

    [JsonPropertyName("duration_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DurationSeconds { get; init; }

    /// <summary>How many files the entry holds (more than one only for a picture folder).</summary>
    [JsonPropertyName("item_count")]
    public int ItemCount { get; init; } = 1;

    [JsonPropertyName("is_missing")]
    public bool IsMissing { get; init; }
}

/// <summary>The folder button that ends the item dropdown.</summary>
public sealed class EditorHeaderFolderButtonDto
{
    /// <summary>The title's folder name ("The Last Signal").</summary>
    [JsonPropertyName("folder_name")]
    public string FolderName { get; init; } = string.Empty;

    /// <summary>The line under "Browse files and folders": the title's folder, and the season or section folder when the level has one ("The Last Signal / Season 02").</summary>
    [JsonPropertyName("path_label")]
    public string PathLabel { get; init; } = string.Empty;
}

/// <summary>The file facts the Quick facts card shows now the Files tab is gone.</summary>
public sealed class EditorFileFactsDto
{
    /// <summary>How many media files the item has.</summary>
    [JsonPropertyName("file_count")]
    public int FileCount { get; init; }

    /// <summary>The best picture or sound quality ("4K HDR", "1080p", "FLAC"); null when unknown.</summary>
    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; init; }

    [JsonPropertyName("container")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Container { get; init; }

    [JsonPropertyName("size_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("file_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName { get; init; }

    /// <summary>The folder the file sits in; when the file is missing, the last folder it was known in.</summary>
    [JsonPropertyName("folder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Folder { get; init; }

    /// <summary>True when the file is in a Read-only library folder (or outside every library folder), so Tuvima will not change it.</summary>
    [JsonPropertyName("read_only")]
    public bool ReadOnly { get; init; }

    /// <summary>True when the file is no longer where Library found it ("File not found", with <see cref="Folder"/> as the last known folder).</summary>
    [JsonPropertyName("is_missing")]
    public bool IsMissing { get; init; }
}

/// <summary>The thresholds and the rules of the item's kind from <c>config/editor.json</c> (D34), so the Dashboard holds no numbers.</summary>
public sealed class EditorHeaderConfigDto
{
    [JsonPropertyName("inline_supporting_files")]
    public int InlineSupportingFiles { get; init; }

    [JsonPropertyName("cards_per_group")]
    public int CardsPerGroup { get; init; }

    [JsonPropertyName("rows_per_page")]
    public int RowsPerPage { get; init; }

    [JsonPropertyName("item_list_page_size")]
    public int ItemListPageSize { get; init; }

    /// <summary>The tabs of this kind, in order: <c>details</c>, <c>artwork</c>, <c>match</c>, <c>preview</c>, <c>pictures</c>, <c>history</c>.</summary>
    [JsonPropertyName("tabs")]
    public IReadOnlyList<string> Tabs { get; init; } = [];

    /// <summary>The facts on the header's one line, in order.</summary>
    [JsonPropertyName("facts_line")]
    public IReadOnlyList<string> FactsLine { get; init; } = [];

    /// <summary>The facts of the Quick facts card, in order.</summary>
    [JsonPropertyName("quick_facts")]
    public IReadOnlyList<string> QuickFacts { get; init; } = [];

    /// <summary>The fields the Match search offers, in order; empty when the kind has no provider to match.</summary>
    [JsonPropertyName("match_fields")]
    public IReadOnlyList<string> MatchFields { get; init; } = [];
}

/// <summary>The states <see cref="EditorCurrentMatchDto.State"/> takes.</summary>
public static class EditorCurrentMatchStates
{
    /// <summary>The item is matched; <see cref="EditorCurrentMatchDto.Record"/> holds the matched record.</summary>
    public const string Matched = "matched";

    /// <summary>The item has no retail match ("Not matched yet"); the tab opens on the search card.</summary>
    public const string NotMatched = "not_matched";
}

/// <summary>Whether the matched record came from the provider just now or from what the Engine stored.</summary>
public static class EditorCurrentMatchSources
{
    /// <summary>The record was fetched from the provider by its id (no text search).</summary>
    public const string Provider = "provider";

    /// <summary>The provider could not be asked (not connected, offline, or it has no lookup by id); the record is what the Engine stored when it matched.</summary>
    public const string Stored = "stored";
}

/// <summary>
/// The Match tab's first view for an item that is matched (universal editor v5 D12, 3c "Existing match"): the matched record found by its
/// stored provider id, never by a text search, so it can never say "Nothing called ..." about its own title.
/// </summary>
public sealed class EditorCurrentMatchDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    /// <summary>One of <see cref="EditorCurrentMatchStates"/>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = EditorCurrentMatchStates.NotMatched;

    /// <summary>The matched record; null when the item is not matched.</summary>
    [JsonPropertyName("record")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditorMatchedRecordDto? Record { get; init; }

    /// <summary>The item's Wikidata identity (a QID), kept separately from the provider match; null when it has none.</summary>
    [JsonPropertyName("wikidata_qid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WikidataQid { get; init; }

    [JsonPropertyName("wikidata_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WikidataUrl { get; init; }

    /// <summary>"Why this match": one row per fact compared between the file and the matched record. Empty when the item is not matched.</summary>
    [JsonPropertyName("why")]
    public IReadOnlyList<EditorMatchEvidenceRowDto> Why { get; init; } = [];

    /// <summary>The original file name, shown as muted text beside the provider dropdown; null when the item has no single file.</summary>
    [JsonPropertyName("file_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName { get; init; }

    /// <summary>A plain sentence when the provider could not be asked for the record (so the stored one is shown); null otherwise.</summary>
    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

/// <summary>The matched record as the provider (or the Engine's stored copy of it) describes it.</summary>
public sealed class EditorMatchedRecordDto
{
    /// <summary>The provider's id in the Engine ("tvdb", "tmdb", "musicbrainz" ...).</summary>
    [JsonPropertyName("provider_id")]
    public string ProviderId { get; init; } = string.Empty;

    /// <summary>The provider as shown ("TheTVDB", "TMDB").</summary>
    [JsonPropertyName("provider_name")]
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>The record's id at the provider.</summary>
    [JsonPropertyName("provider_item_id")]
    public string ProviderItemId { get; init; } = string.Empty;

    /// <summary>What the record is, in plain words ("Show", "Season", "Episode", "Film", "Album", "Book").</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Year { get; init; }

    /// <summary>The creator in the kind's own word: the director of a film, the artist of an album, the author of a book; null when not known.</summary>
    [JsonPropertyName("creator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Creator { get; init; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>The record's page on the provider's own site ("Open source"); null when no address can be built for it.</summary>
    [JsonPropertyName("source_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; init; }

    /// <summary>One of <see cref="EditorCurrentMatchSources"/>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = EditorCurrentMatchSources.Stored;
}

/// <summary>The states of one "Why this match" row.</summary>
public static class EditorMatchEvidenceStates
{
    /// <summary>The file and the matched record say the same thing.</summary>
    public const string Agrees = "agrees";

    /// <summary>They say different things.</summary>
    public const string Differs = "differs";

    /// <summary>The file does not say, so the fact is not checked and never counts against the match.</summary>
    public const string NotChecked = "not_checked";
}

/// <summary>One fact compared between the file and the matched record.</summary>
public sealed class EditorMatchEvidenceRowDto
{
    /// <summary>The fact: <c>title</c>, <c>year</c>, <c>season_number</c>, <c>episode_number</c> ...</summary>
    [JsonPropertyName("field")]
    public string Field { get; init; } = string.Empty;

    /// <summary>What the file's name (or tags) says; null when it does not.</summary>
    [JsonPropertyName("your_file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? YourFile { get; init; }

    /// <summary>What the matched record says; null when it does not.</summary>
    [JsonPropertyName("matched_record")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MatchedRecord { get; init; }

    /// <summary>The Engine's stored confidence (0 to 1) in the matched record's value, when it kept one; the verdict bands come with the Match tab.</summary>
    [JsonPropertyName("score")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Score { get; init; }

    /// <summary>One of <see cref="EditorMatchEvidenceStates"/>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = EditorMatchEvidenceStates.NotChecked;
}

/// <summary>Where a "Suggest details from the document" read found its answer.</summary>
public static class EditorSuggestDetailsSources
{
    public const string Pdf = "pdf";
    public const string Epub = "epub";
    public const string Text = "text";

    /// <summary>The file is not a document the read can open (a clip, a picture) or it gave nothing.</summary>
    public const string None = "none";
}

/// <summary>
/// "Suggest details from the document" (universal editor v5 D18): the file's own title, subject and author and the first lines of its first
/// page, read on this machine with no provider and no AI. Nothing is kept until the person saves.
/// </summary>
public sealed class EditorSuggestDetailsDto
{
    [JsonPropertyName("supporting_file_id")]
    public Guid SupportingFileId { get; init; }

    /// <summary>One of <see cref="EditorSuggestDetailsSources"/>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = EditorSuggestDetailsSources.None;

    /// <summary>The proposed title: the document's own title; null when it has none.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>The proposed description: the document's subject or description, else its first lines.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("author")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Author { get; init; }

    [JsonPropertyName("page_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PageCount { get; init; }

    /// <summary>The first non-empty lines of the document's first page.</summary>
    [JsonPropertyName("first_lines")]
    public IReadOnlyList<string> FirstLines { get; init; } = [];
}

/// <summary>How the Engine regards a file in the folder tree.</summary>
public static class EditorFolderEntryStates
{
    /// <summary>One of the title's own media files (an episode, a film, a track).</summary>
    public const string Main = "main";

    /// <summary>A supporting file of the title.</summary>
    public const string Supporting = "supporting";

    /// <summary>A file the Library never shows: an archive, an .nfo, a subtitle, artwork named for a media file, or anything Library cannot open ("Not shown in the library").</summary>
    public const string Hidden = "hidden";

    /// <summary>A file Library could show but has not placed; it can be sorted from "What is this file?".</summary>
    public const string Unsorted = "unsorted";
}

/// <summary>One page of one folder of a title's folder tree, for the folder button (universal editor v5 3a, section 5).</summary>
public sealed class EditorFolderTreeDto
{
    /// <summary>The title's folder name (the tree's root).</summary>
    [JsonPropertyName("root_name")]
    public string RootName { get; init; } = string.Empty;

    /// <summary>The folder this page lists, relative to the title's folder, with <c>/</c> between parts; empty for the title's folder itself.</summary>
    [JsonPropertyName("folder")]
    public string Folder { get; init; } = string.Empty;

    /// <summary>The folder above this one; null when this is the title's folder.</summary>
    [JsonPropertyName("parent_folder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentFolder { get; init; }

    /// <summary>True when the folder is inside a Read-only library folder.</summary>
    [JsonPropertyName("read_only")]
    public bool ReadOnly { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }

    /// <summary>Folders first, then files, each in natural name order.</summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<EditorFolderEntryDto> Entries { get; init; } = [];
}

/// <summary>One folder or file in the folder tree.</summary>
public sealed class EditorFolderEntryDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The entry's path relative to the title's folder, with <c>/</c> between parts. Open a folder by asking for this path.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("is_folder")]
    public bool IsFolder { get; init; }

    /// <summary>For a file, one of <see cref="EditorFolderEntryStates"/>; null for a folder.</summary>
    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; init; }

    [JsonPropertyName("size_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SizeBytes { get; init; }

    /// <summary>For a main media file, the item it is (open it in the editor).</summary>
    [JsonPropertyName("item_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ItemId { get; init; }

    /// <summary>For a supporting file, its id (open it in the editor).</summary>
    [JsonPropertyName("supporting_file_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SupportingFileId { get; init; }
}
