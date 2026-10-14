using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Storage.Contracts;

public sealed record DocumentRuleCondition(string Kind, string Value);

/// <param name="RefId">The tag, correspondent, type or field the action points at.</param>
/// <param name="TextValue">For a custom field, its value as the editor would type it.</param>
public sealed record DocumentRuleAction(string Kind, Guid? RefId, string? TextValue);

/// <param name="PersonalSpaceId">Null for a rule that covers the household's Shared Library.</param>
public sealed record DocumentRule(
    Guid Id,
    Guid OwnerProfileId,
    Guid? PersonalSpaceId,
    string Name,
    bool Enabled,
    string MatchMode,
    int SortOrder,
    bool FilesAutomatically,
    IReadOnlyList<DocumentRuleCondition> Conditions,
    IReadOnlyList<DocumentRuleAction> Actions);

public sealed record DocumentRuleSource(Guid Id, string Name);

/// <summary>Everything a rule can look at on one document.</summary>
/// <param name="Text">The words read from the document (limited to a sensible length), or null when none were read.</param>
/// <param name="FolderPath">The folder the primary file lives in.</param>
/// <param name="SourceIds">The View sources the primary file was found through.</param>
/// <param name="FileTime">When the file was created or captured; used when no date is found in the words.</param>
public sealed record DocumentRuleSubject(
    Guid ItemId,
    Guid LibraryId,
    Guid? PersonalSpaceId,
    Guid? OwnerProfileId,
    bool IsShared,
    string Title,
    string FileName,
    string FolderPath,
    string? Text,
    IReadOnlyCollection<Guid> SourceIds,
    string FilingState,
    DateTimeOffset FileTime);

/// <param name="Text">The field value as written in the rule; it is checked against the field's kind when applied.</param>
public sealed record DocumentRuleFieldValue(Guid FieldId, string? Text);

/// <summary>What all matching rules together want done to one document.</summary>
public sealed record DocumentRulePlan(
    IReadOnlyList<Guid> TagIds,
    Guid? CorrespondentId,
    Guid? DocumentTypeId,
    IReadOnlyList<DocumentRuleFieldValue> Fields,
    bool FileNow)
{
    public static DocumentRulePlan Empty { get; } = new([], null, null, [], false);
}

public sealed record DocumentRuleApplyOutcome(bool Changed, bool Filed);

/// <summary>An earlier filed document that is alike, with the names it was filed under.</summary>
public sealed record SimilarFiledDocument(Guid ItemId, string? Correspondent, string? DocumentType, IReadOnlyList<string> Tags);

public sealed record DocumentAmountField(Guid FieldId, string Kind);

/// <param name="AcceptSuggestions">Fill blank fields from the suggestions.</param>
/// <param name="Shown">The suggestions the person was shown; used instead of the stored ones when sent.</param>
public sealed record FileInboxWrite(
    bool AcceptSuggestions,
    DateOnly? DocumentDate,
    string? Correspondent,
    string? DocumentType,
    IReadOnlyCollection<string>? Tags,
    ViewDocumentSuggestionsDto? Shown = null);

/// <summary>
/// What the suggester learns from documents already filed in one library. Only that library's documents are ever
/// consulted, so a suggestion can only repeat a name that person could already see on their own documents.
/// </summary>
public interface IDocumentSuggestionCorpus
{
    /// <summary>Filed documents in the library that carry a correspondent, type or tag.</summary>
    Task<int> CountOrganisedDocumentsAsync(Guid libraryId, CancellationToken ct = default);

    /// <summary>For each word, how many of those documents contain it. Words found nowhere are left out.</summary>
    Task<IReadOnlyDictionary<string, int>> GetWordDocumentCountsAsync(
        Guid libraryId, IReadOnlyCollection<string> words, CancellationToken ct = default);

    /// <summary>The best matches for any of the words, most alike first.</summary>
    Task<IReadOnlyList<SimilarFiledDocument>> FindSimilarDocumentsAsync(
        Guid libraryId, Guid excludeItemId, IReadOnlyCollection<string> words, int take, CancellationToken ct = default);

    /// <summary>
    /// The custom field named Amount when it exists, can hold an amount, and is visible to the owner of this library
    /// (they made it, or it is in use on a document there they can see).
    /// </summary>
    Task<DocumentAmountField?> FindAmountFieldAsync(Guid libraryId, Guid? ownerProfileId, CancellationToken ct = default);
}

/// <summary>
/// Applying auto-filing rules, storing Inbox suggestions and filing from the Inbox. Rules are read for the document's
/// own library only: the owner's Personal Space, or the Shared Library for rules a household administrator made.
/// </summary>
public interface IDocumentFilingRepository : IDocumentSuggestionCorpus
{
    /// <summary>The document as rules see it, or null when it is not an active document.</summary>
    Task<DocumentRuleSubject?> GetSubjectAsync(Guid itemId, CancellationToken ct = default);

    /// <summary>The enabled rules that cover this document, in running order.</summary>
    Task<IReadOnlyList<DocumentRule>> GetApplicableRulesAsync(DocumentRuleSubject subject, CancellationToken ct = default);

    /// <summary>
    /// Writes what the rules decided. Tags are only added; a correspondent, type or field is set only when it is
    /// empty or was last set by a rule, never over a value a person chose.
    /// </summary>
    Task<DocumentRuleApplyOutcome> ApplyPlanAsync(Guid itemId, DocumentRulePlan plan, CancellationToken ct = default);

    /// <summary>Stores (or clears, with null) the suggestions of a document that is in the Inbox.</summary>
    Task SaveSuggestionsAsync(Guid itemId, ViewDocumentSuggestionsDto? suggestions, CancellationToken ct = default);

    /// <summary>Documents waiting to be filed in the viewer's libraries, counted as the Documents list counts them.</summary>
    int CountInbox(ViewCatalogueViewer viewer, CancellationToken ct = default);

    /// <summary>
    /// Files one Inbox document, optionally taking its suggestions. Returns false when it is not a document the viewer
    /// can reach. A document that is already filed is left as it is.
    /// </summary>
    Task<bool> FileFromInboxAsync(ViewCatalogueViewer viewer, Guid itemId, FileInboxWrite write, CancellationToken ct = default);

    /// <summary>How many active documents one library holds.</summary>
    Task<int> CountDocumentsAsync(Guid libraryId, CancellationToken ct = default);

    /// <summary>Active documents of one library in id order, for queueing the rules to be applied again.</summary>
    Task<IReadOnlyList<Guid>> GetDocumentIdsAsync(Guid libraryId, Guid? afterItemId, int take, CancellationToken ct = default);

    /// <summary>
    /// A page of the viewer's documents in one library, newest first, with their text, for trying a rule out. Only
    /// documents the viewer can reach are returned: the library must be one of theirs and others' hidden items are left out.
    /// </summary>
    Task<IReadOnlyList<DocumentRuleSubject>> GetSubjectsPageAsync(
        ViewCatalogueViewer viewer, Guid libraryId, int skip, int take, CancellationToken ct = default);
}

/// <summary>Reading and changing a person's own auto-filing rules.</summary>
public interface IDocumentRuleRepository
{
    /// <summary>Every rule the person made, in running order within each scope.</summary>
    Task<IReadOnlyList<DocumentRule>> ListOwnedAsync(Guid ownerProfileId, CancellationToken ct = default);

    Task<DocumentRule?> FindOwnedAsync(Guid ownerProfileId, Guid ruleId, CancellationToken ct = default);

    /// <summary>Adds the rule at the end of its scope's running order.</summary>
    Task<DocumentRule> CreateAsync(DocumentRule rule, CancellationToken ct = default);

    /// <summary>Replaces name, switches, conditions and actions. The scope and place in the order are kept.</summary>
    Task<DocumentRule?> ReplaceAsync(DocumentRule rule, CancellationToken ct = default);

    Task<bool> DeleteOwnedAsync(Guid ownerProfileId, Guid ruleId, CancellationToken ct = default);

    /// <summary>Gives the rules the running order they are listed in. Returns false unless the list names every rule the person has, once each.</summary>
    Task<bool> ReorderOwnedAsync(Guid ownerProfileId, IReadOnlyList<Guid> ruleIds, CancellationToken ct = default);

    /// <summary>The enabled View sources of one library, for the "came from" condition.</summary>
    Task<IReadOnlyList<DocumentRuleSource>> ListSourcesAsync(Guid libraryId, CancellationToken ct = default);
}
