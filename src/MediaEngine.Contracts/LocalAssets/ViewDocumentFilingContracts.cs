using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.LocalAssets;

/// <summary>What an auto-filing rule looks at. Stored and sent as these exact lowercase words.</summary>
public static class ViewDocumentRuleConditionKinds
{
    /// <summary>The words read from the document contain this word or phrase (ignoring letter case).</summary>
    public const string TextContains = "text_contains";

    public const string FilenameContains = "filename_contains";

    /// <summary>The folder path of the file contains this text.</summary>
    public const string FolderContains = "folder_contains";

    /// <summary>The document came from this View source; the value is the source's id.</summary>
    public const string Source = "source";

    /// <summary>The value is one of <see cref="ViewDocumentFileKinds"/>.</summary>
    public const string FileKind = "file_kind";

    public static readonly IReadOnlyList<string> All = [TextContains, FilenameContains, FolderContains, Source, FileKind];
}

/// <summary>What an auto-filing rule does to a matching document.</summary>
public static class ViewDocumentRuleActionKinds
{
    /// <summary><c>ref_id</c> is a tag the person can see. Tags are only ever added.</summary>
    public const string AddTag = "add_tag";

    public const string SetCorrespondent = "set_correspondent";
    public const string SetDocumentType = "set_document_type";

    /// <summary><c>ref_id</c> is a custom field; <c>text_value</c> holds the value as the editor would type it.</summary>
    public const string SetField = "set_field";

    public static readonly IReadOnlyList<string> All = [AddTag, SetCorrespondent, SetDocumentType, SetField];
}

public static class ViewDocumentRuleMatchModes
{
    /// <summary>Every condition must hold.</summary>
    public const string All = "all";

    /// <summary>At least one condition must hold.</summary>
    public const string Any = "any";
}

/// <summary>The kinds of file a rule can match on.</summary>
public static class ViewDocumentFileKinds
{
    public const string Pdf = "pdf";
    public const string Image = "image";
    public const string Office = "office";

    public static readonly IReadOnlyList<string> All = [Pdf, Image, Office];
}

/// <summary>Whose documents a rule covers: the person's own space, or (household administrators only) the Shared Library.</summary>
public static class ViewDocumentRuleScopes
{
    public const string Mine = "mine";
    public const string Shared = "shared";
}

/// <param name="DisplayValue">For a source condition, the source's name; otherwise the same as the value.</param>
public sealed record ViewDocumentRuleConditionDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("display_value")] string? DisplayValue = null);

/// <param name="DisplayName">The tag, correspondent, type or field name, so the list reads without a second lookup.</param>
/// <param name="Missing">True when the thing the action points at no longer exists; the action then does nothing.</param>
public sealed record ViewDocumentRuleActionDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("ref_id")] Guid? RefId = null,
    [property: JsonPropertyName("text_value")] string? TextValue = null,
    [property: JsonPropertyName("display_name")] string? DisplayName = null,
    [property: JsonPropertyName("missing")] bool Missing = false);

public sealed record ViewDocumentRuleDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("match_mode")] string MatchMode,
    [property: JsonPropertyName("sort_order")] int SortOrder,
    [property: JsonPropertyName("files_automatically")] bool FilesAutomatically,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("conditions")] IReadOnlyList<ViewDocumentRuleConditionDto> Conditions,
    [property: JsonPropertyName("actions")] IReadOnlyList<ViewDocumentRuleActionDto> Actions);

public sealed record ViewDocumentRuleSourceDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>The person's rules in running order, plus what the editor needs to build one.</summary>
public sealed record ViewDocumentRulesDto(
    [property: JsonPropertyName("rules")] IReadOnlyList<ViewDocumentRuleDto> Rules,
    [property: JsonPropertyName("sources")] IReadOnlyList<ViewDocumentRuleSourceDto> Sources,
    [property: JsonPropertyName("can_manage_shared")] bool CanManageShared);

/// <summary>Creates a rule, replaces one, or (for the tester) describes one that is not saved yet.</summary>
public sealed record SaveViewDocumentRuleRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("match_mode")] string MatchMode = ViewDocumentRuleMatchModes.All,
    [property: JsonPropertyName("files_automatically")] bool FilesAutomatically = false,
    [property: JsonPropertyName("scope")] string Scope = ViewDocumentRuleScopes.Mine,
    [property: JsonPropertyName("conditions")] IReadOnlyList<ViewDocumentRuleConditionDto>? Conditions = null,
    [property: JsonPropertyName("actions")] IReadOnlyList<ViewDocumentRuleActionDto>? Actions = null);

/// <summary>The new running order of a person's rules in one scope: every rule id, first to last.</summary>
public sealed record ReorderViewDocumentRulesRequest(
    [property: JsonPropertyName("rule_ids")] IReadOnlyList<Guid> RuleIds);

public sealed record ViewDocumentRuleTestMatchDto(
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("title")] string Title);

/// <summary>Up to ten documents the rule matches. Only documents the person can open are ever listed.</summary>
public sealed record ViewDocumentRuleTestDto(
    [property: JsonPropertyName("matches")] IReadOnlyList<ViewDocumentRuleTestMatchDto> Matches,
    [property: JsonPropertyName("documents_checked")] int DocumentsChecked,
    [property: JsonPropertyName("has_more")] bool HasMore);

/// <summary>How many documents were queued to have the rules applied to them again.</summary>
public sealed record ViewDocumentRulesApplyResultDto(
    [property: JsonPropertyName("queued")] int Queued);

/// <summary>
/// What was worked out about a document waiting in the Inbox. Shown, never applied on its own. Names are words, not
/// ids: accepting one reuses the name if it exists and adds it to the list if not.
/// </summary>
/// <param name="DateFromFile">True when no date was found in the words and the file's own time is shown instead.</param>
public sealed record ViewDocumentSuggestionsDto(
    [property: JsonPropertyName("document_date")] DateOnly? DocumentDate = null,
    [property: JsonPropertyName("date_from_file")] bool DateFromFile = false,
    [property: JsonPropertyName("correspondent")] string? Correspondent = null,
    [property: JsonPropertyName("document_type")] string? DocumentType = null,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("amount")] double? Amount = null,
    [property: JsonPropertyName("currency")] string? Currency = null);

/// <summary>
/// Files one Inbox document. <c>accept_suggestions</c> fills every blank field from what was suggested (fields
/// already set, by a rule or a person, are kept). A value sent here is the person's own choice and wins over the
/// suggestion; sent tags are added to the tags it has. With nothing sent and <c>accept_suggestions</c> false the
/// document is simply marked filed. <c>suggestions</c> is what the person was shown and is accepted instead of
/// whatever is stored now (suggestions can be worked out again while a card is open), so one tap files exactly what
/// was on screen.
/// </summary>
public sealed record FileViewDocumentFromInboxRequest(
    [property: JsonPropertyName("accept_suggestions")] bool AcceptSuggestions = false,
    [property: JsonPropertyName("document_date")] DateOnly? DocumentDate = null,
    [property: JsonPropertyName("correspondent")] string? Correspondent = null,
    [property: JsonPropertyName("document_type")] string? DocumentType = null,
    [property: JsonPropertyName("tags")] IReadOnlyCollection<string>? Tags = null,
    [property: JsonPropertyName("suggestions")] ViewDocumentSuggestionsDto? Suggestions = null);

/// <summary>How many documents are waiting to be filed in the scope the person is looking at.</summary>
public sealed record ViewDocumentInboxSummaryDto(
    [property: JsonPropertyName("count")] int Count);
