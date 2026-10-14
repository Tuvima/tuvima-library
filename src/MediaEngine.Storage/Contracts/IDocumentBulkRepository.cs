namespace MediaEngine.Storage.Contracts;

/// <summary>What a bulk edit changes on one document in the data store. The trash and the Inbox have their own paths.</summary>
public enum DocumentBulkChangeKind
{
    AddTags,
    RemoveTags,
    SetCorrespondent,
    SetDocumentType,
    SetDocumentDate,
}

/// <param name="TagNames">Names to add; a name that does not exist yet is added to the global list.</param>
/// <param name="TagIds">Tags to add or take off, by id. Only tags the viewer can see are accepted.</param>
/// <param name="Name">The correspondent or document type; null clears it.</param>
/// <param name="Date">The document date; null clears it.</param>
public sealed record DocumentBulkChange(
    DocumentBulkChangeKind Kind,
    IReadOnlyCollection<string>? TagNames = null,
    IReadOnlyCollection<Guid>? TagIds = null,
    string? Name = null,
    DateOnly? Date = null);

public enum DocumentBulkOutcome
{
    /// <summary>The document was changed.</summary>
    Done,

    /// <summary>The document already looked the way the change asks.</summary>
    Unchanged,

    /// <summary>The document is not one the viewer can change, or is not a document.</summary>
    Refused,
}

public sealed record DocumentBulkItemResult(Guid ItemId, DocumentBulkOutcome Outcome, string? Reason = null);

/// <summary>Where one id stands for the viewer: reachable or not, and whether it lives in the Shared Library.</summary>
public sealed record DocumentBulkSubject(
    Guid ItemId, bool Reachable, bool IsDocument, bool IsShared, bool IsInbox = false, bool IsTrashed = false);

/// <summary>
/// Changes to one document at a time for the Documents bulk edit. Each document is its own transaction, so one that
/// cannot be changed never holds back or half-applies the others. Every call checks the document against the
/// libraries the viewer was approved for; callers still decide whether the viewer may <em>change</em> it.
/// </summary>
public interface IDocumentBulkRepository
{
    /// <summary>For each id: whether the viewer can reach it, whether it is a document, shared, in the Inbox or in the trash.</summary>
    IReadOnlyList<DocumentBulkSubject> Describe(
        ViewCatalogueViewer viewer, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    Task<DocumentBulkItemResult> ApplyAsync(
        ViewCatalogueViewer viewer, Guid itemId, DocumentBulkChange change, CancellationToken ct = default);
}
