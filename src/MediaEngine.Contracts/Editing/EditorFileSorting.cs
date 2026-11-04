using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>What kind of title a file sits in, which decides the places the editor offers for an Extra (universal editor v4 D20).</summary>
public static class EditorFileSortingTitleKinds
{
    /// <summary>A TV show: the whole show, a season, or one episode.</summary>
    public const string Show = "show";

    /// <summary>A film: the film.</summary>
    public const string Film = "film";

    /// <summary>An album: the album, or one track.</summary>
    public const string Album = "album";

    /// <summary>Anything else (a book, a comic, a course): the whole title.</summary>
    public const string Other = "other";
}

/// <summary>
/// What "What is this file?" needs to start from for one item (universal editor v4, D20): the file's name, whether it can be an Extra, the
/// guess its name gives for the kind and place, and the other unsorted files in the same folder so they can be sorted in one pass.
/// A read; nothing changes.
/// </summary>
public sealed class EditorFileSortingDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    /// <summary>False when the item is not a single file (a show, an album, an audiobook with several files), so the question does not apply.</summary>
    [JsonPropertyName("can_sort")]
    public bool CanSort { get; set; }

    /// <summary>The file's name without its folder, as it sits on disk; empty when the item has no single file.</summary>
    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>One of <see cref="EditorFileSortingTitleKinds"/>: which places an Extra of this file's own title can have.</summary>
    [JsonPropertyName("title_kind")]
    public string TitleKind { get; set; } = EditorFileSortingTitleKinds.Other;

    /// <summary>The name of the title the file's folder holds (the show, film or album), when it is known; null when the file is in no title's folder.</summary>
    [JsonPropertyName("title_name")]
    public string? TitleName { get; set; }

    /// <summary>True when the file is a document, pictures or a clip, which are the files that can be an Extra.</summary>
    [JsonPropertyName("can_be_extra")]
    public bool CanBeExtra { get; set; }

    /// <summary>True when the file sits in a title's folder, so "An Extra" can keep it with that title (otherwise it can only go to another title or be ignored).</summary>
    [JsonPropertyName("in_title_folder")]
    public bool InTitleFolder { get; set; }

    /// <summary>The kinds this file can be, in the order the picker shows them (<c>trailer</c>, <c>document</c> and so on).</summary>
    [JsonPropertyName("extra_kinds")]
    public List<string> ExtraKinds { get; set; } = [];

    /// <summary>The guess from the file name. Only pre-fills the picker; null when the file cannot be an Extra.</summary>
    [JsonPropertyName("guess")]
    public EditorFileGuessDto? Guess { get; set; }

    /// <summary>The other files in the same folder that the Engine could not place, newest first, so they can be sorted together.</summary>
    [JsonPropertyName("folder_files")]
    public List<EditorFolderFileDto> FolderFiles { get; set; } = [];
}

/// <summary>A guess at an Extra's kind and place, read from a file name. Every part is only a pre-fill.</summary>
public sealed class EditorFileGuessDto
{
    [JsonPropertyName("extra_kind")]
    public string ExtraKind { get; set; } = string.Empty;

    /// <summary>The season the name points at; 0 is Specials; null when it names none.</summary>
    [JsonPropertyName("season")]
    public int? Season { get; set; }

    [JsonPropertyName("episode")]
    public int? Episode { get; set; }

    [JsonPropertyName("track")]
    public int? Track { get; set; }
}

/// <summary>One more unsorted file in the folder, with its own guess.</summary>
public sealed class EditorFolderFileDto
{
    /// <summary>The item the file is, so a sorting choice can be staged for it.</summary>
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("can_be_extra")]
    public bool CanBeExtra { get; set; }

    [JsonPropertyName("guess")]
    public EditorFileGuessDto? Guess { get; set; }
}
