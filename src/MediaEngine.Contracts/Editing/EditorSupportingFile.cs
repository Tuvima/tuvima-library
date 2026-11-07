using System.Text.Json.Serialization;
using MediaEngine.Contracts.Details;

namespace MediaEngine.Contracts.Editing;

/// <summary>One role a supporting file of this kind of file may have (the fixed lists in <c>config/extras.json</c>).</summary>
public sealed class EditorSupportingFileRoleDto
{
    /// <summary>The stable id ("guide", "worksheet", "behind_the_scenes").</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>The words shown ("Guide", "Behind the scenes").</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// One place a supporting file may belong to or be shown alongside: the title itself, a season, an episode, a section or a lesson. The key is
/// the scope key the bulk change takes (<c>root</c>, <c>season:2</c>, <c>episode:2:3</c>, <c>folder:Section 1</c>, <c>file:Section 1/02 Intro.mkv</c>).
/// </summary>
public sealed class EditorSupportingFilePlaceDto
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = "root";

    /// <summary>The words for the Belongs to list: "The Last Signal", "Season 2", "Season 2, episode 3", "02 · Components".</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary><c>root</c>, <c>season</c>, <c>episode</c>, <c>folder</c>, <c>file</c> or <c>track</c>.</summary>
    [JsonPropertyName("level")]
    public string Level { get; init; } = "root";

    /// <summary>The key of the place this one sits under (an episode under its season); null for the title and for a place with nothing above it.</summary>
    [JsonPropertyName("parent_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentKey { get; init; }
}

/// <summary>
/// Everything the supporting-file editor needs to open one file (universal editor v5 D11, D13, D14, section 4): the file with what a person set
/// and what the Engine read from the folders, the lists the form chooses from (roles for this kind of file, the title's groups, the places the
/// file can belong to), the facts for Quick facts, and the owner's header with this file marked as the row the editor is open on. A read only:
/// nothing here changes anything; the form's changes are staged and saved as a <c>supporting_files</c> change.
/// </summary>
public sealed class EditorSupportingFileDto
{
    /// <summary>The title (a work id) this read was made for; the staged save names this id as the change's entity.</summary>
    [JsonPropertyName("title_id")]
    public Guid TitleId { get; init; }

    [JsonPropertyName("file")]
    public SupportingFileDto File { get; init; } = new();

    /// <summary>What the file is: one of the <c>SupportingFileListDefaults.Type*</c> values.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = SupportingFileListDefaults.TypeDocument;

    /// <summary>The words for the type column: "PDF", "Text", "EPUB", "Image", "Video", "Audio", "Pictures".</summary>
    [JsonPropertyName("type_label")]
    public string TypeLabel { get; init; } = string.Empty;

    /// <summary>The short badge: "PDF", "TXT", "IMG", "VIDEO", "AUDIO".</summary>
    [JsonPropertyName("badge")]
    public string Badge { get; init; } = string.Empty;

    /// <summary>The file's media type, for example <c>application/pdf</c>.</summary>
    [JsonPropertyName("mime_type")]
    public string MimeType { get; init; } = "application/octet-stream";

    /// <summary>The folder the file sits in, from the title's folder down ("Season 02 / Documents"); when the file is missing, the last folder it was known in.</summary>
    [JsonPropertyName("folder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Folder { get; init; }

    /// <summary>True when the file is in a Read-only library folder (or outside every managed folder): it is read where it is and never moved or deleted.</summary>
    [JsonPropertyName("read_only")]
    public bool ReadOnly { get; init; }

    /// <summary>The roles this kind of file may have, in the order the Role list shows them.</summary>
    [JsonPropertyName("roles")]
    public IReadOnlyList<EditorSupportingFileRoleDto> Roles { get; init; } = [];

    /// <summary>The names of this title's groups, in order, for the Group list.</summary>
    [JsonPropertyName("groups")]
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>The places the file can belong to, the title first, then seasons with their episodes, then sections with their lessons.</summary>
    [JsonPropertyName("places")]
    public IReadOnlyList<EditorSupportingFilePlaceDto> Places { get; init; } = [];

    /// <summary>The place the file belongs to now, as shown.</summary>
    [JsonPropertyName("scope_label")]
    public string ScopeLabel { get; init; } = string.Empty;

    /// <summary>The place read from the file's folder, as shown (what "Use folder location" goes back to).</summary>
    [JsonPropertyName("folder_scope_label")]
    public string FolderScopeLabel { get; init; } = string.Empty;

    /// <summary>The episode or lesson the file is shown alongside, as shown ("with lesson 02"); null when there is none.</summary>
    [JsonPropertyName("alongside_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AlongsideLabel { get; init; }

    /// <summary>The owner's header (home, Season or Section, item dropdown) with this file marked current; null when the header could not be built.</summary>
    [JsonPropertyName("header")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditorHeaderDto? Header { get; init; }

    /// <summary>The rules of a supporting file from <c>config/editor.json</c>: its tabs (Details, Preview, History), facts line and Quick facts.</summary>
    [JsonPropertyName("config")]
    public EditorHeaderConfigDto Config { get; init; } = new();
}
