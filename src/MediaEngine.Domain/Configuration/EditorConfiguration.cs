using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// The editor's numbers and per-kind rules, loaded from <c>config/editor.json</c> (universal editor v5 D34): how many supporting files the
/// item dropdown lists inline, how many cards a details-page group shows, how many rows a page of the browser has, which tabs each kind of
/// item gets, which facts its facts line and Quick facts show, and which fields its Match search offers. The shipped file is also built
/// into the Engine, so a config folder without it still works, and a key a person's file leaves out is filled from the shipped one. No number
/// of the D34 table is written in code: a count left unset (0) is read as missing. The Dashboard holds none of these numbers; it reads them
/// from the Engine.
/// </summary>
public sealed class EditorConfiguration
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1";

    /// <summary>Up to this many supporting files at a scope appear inline in the item dropdown; above it the dropdown offers "All supporting files".</summary>
    [JsonPropertyName("inline_supporting_files")]
    public int InlineSupportingFiles { get; set; }

    /// <summary>The most cards one group shows on a details page before "See all".</summary>
    [JsonPropertyName("cards_per_group")]
    public int CardsPerGroup { get; set; }

    /// <summary>Rows a page of the supporting-files browser holds.</summary>
    [JsonPropertyName("rows_per_page")]
    public int RowsPerPage { get; set; }

    /// <summary>Rows the item dropdown reads at a time before it pages in more.</summary>
    [JsonPropertyName("item_list_page_size")]
    public int ItemListPageSize { get; set; }

    /// <summary>Entries one page of a folder in the folder tree holds.</summary>
    [JsonPropertyName("folder_tree_page_size")]
    public int FolderTreePageSize { get; set; }

    [JsonPropertyName("suggest_details")]
    public EditorSuggestDetailsConfiguration SuggestDetails { get; set; } = new();

    /// <summary>The rules of each kind of item, keyed by an <see cref="EditorKinds"/> id.</summary>
    [JsonPropertyName("kinds")]
    public Dictionary<string, EditorKindConfiguration> Kinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rules of this kind, or the generic <see cref="EditorKinds.Item"/> rules when the file does not name the kind.</summary>
    public EditorKindConfiguration KindFor(string kind) =>
        Kinds.TryGetValue(kind, out var found) ? found
        : Kinds.TryGetValue(EditorKinds.Item, out var generic) ? generic
        : new EditorKindConfiguration { Tabs = ["details", "history"] };
}

/// <summary>How much of a document the "Suggest details from the document" read looks at.</summary>
public sealed class EditorSuggestDetailsConfiguration
{
    /// <summary>How many non-empty lines of the document's first page are read when the file has no description of its own.</summary>
    [JsonPropertyName("first_lines")]
    public int FirstLines { get; set; }

    /// <summary>The longest description the read proposes, in characters.</summary>
    [JsonPropertyName("description_max_characters")]
    public int DescriptionMaxCharacters { get; set; }
}

/// <summary>The rules of one kind of item.</summary>
public sealed class EditorKindConfiguration
{
    /// <summary>The tabs the editor shows, in order: <c>details</c>, <c>artwork</c>, <c>match</c>, <c>preview</c>, <c>pictures</c>, <c>history</c>.</summary>
    [JsonPropertyName("tabs")]
    public List<string> Tabs { get; set; } = [];

    /// <summary>The facts on the header's one line under the title, in order (ids such as <c>year</c>, <c>runtime</c>, <c>match_state</c>).</summary>
    [JsonPropertyName("facts_line")]
    public List<string> FactsLine { get; set; } = [];

    /// <summary>The facts the Quick facts card shows, in order (ids such as <c>match_state</c>, <c>file_quality</c>, <c>container</c>).</summary>
    [JsonPropertyName("quick_facts")]
    public List<string> QuickFacts { get; set; } = [];

    /// <summary>The fields the Match tab's search offers for this kind, in order (ids such as <c>title</c>, <c>year</c>, <c>show</c>, <c>artist</c>).</summary>
    [JsonPropertyName("match_fields")]
    public List<string> MatchFields { get; set; } = [];
}

/// <summary>The ids of the kinds of item <see cref="EditorConfiguration.Kinds"/> is keyed by.</summary>
public static class EditorKinds
{
    public const string Item = "item";
    public const string TvShow = "tv_show";
    public const string TvSeason = "tv_season";
    public const string TvEpisode = "tv_episode";
    public const string Movie = "movie";
    public const string Album = "album";
    public const string Song = "song";
    public const string Book = "book";
    public const string Audiobook = "audiobook";
    public const string Comic = "comic";
    public const string Series = "series";
    public const string Course = "course";
    public const string CourseSection = "course_section";
    public const string Video = "video";
    public const string Person = "person";
    public const string SupportingFile = "supporting_file";
    public const string Universe = "universe";
    public const string ViewItem = "view_item";
    public const string ViewGallery = "view_gallery";
    public const string Multiple = "multiple";

    public static readonly IReadOnlyList<string> All =
    [
        Item, TvShow, TvSeason, TvEpisode, Movie, Album, Song, Book, Audiobook, Comic, Series, Course, CourseSection, Video, Person,
        SupportingFile, Universe, ViewItem, ViewGallery, Multiple,
    ];
}
