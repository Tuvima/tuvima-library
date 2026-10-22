using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Storage.Contracts;

/// <summary>The four global lists a View item can be organised with.</summary>
public enum ViewCatalogueKind
{
    Tag,
    Correspondent,
    DocumentType,
    Field,
}

/// <summary>
/// Who is asking, and which libraries the resolver approved for them. Every read and write below checks the item
/// or the name against these libraries (and against what the person created); callers never widen the list.
/// </summary>
/// <param name="IncludeDocuments">
/// False for a profile that may not use Documents: names that only appear on documents are then invisible to it.
/// </param>
/// <param name="BlockedContentKinds">
/// The View kinds (<c>photos</c>, <c>personal_videos</c>, <c>documents</c>) the profile may not use: names that only appear on
/// items of those kinds are invisible to it.
/// </param>
public sealed record ViewCatalogueViewer(
    Guid ProfileId, IReadOnlyCollection<Guid> AccessibleLibraryIds, bool IncludeDocuments = true,
    IReadOnlyCollection<string>? BlockedContentKinds = null);

/// <param name="UsageCount">Items the viewer can see that carry this entry (not the install-wide number).</param>
/// <param name="FieldKind">Set for custom fields only.</param>
public sealed record ViewCatalogueEntry(
    Guid Id,
    ViewCatalogueKind Kind,
    string Name,
    string? Colour,
    string? FieldKind,
    IReadOnlyList<string> Choices,
    Guid? CreatedByProfileId,
    int UsageCount);

public sealed record ViewCatalogueSnapshot(
    IReadOnlyList<ViewCatalogueEntry> Tags,
    IReadOnlyList<ViewCatalogueEntry> Correspondents,
    IReadOnlyList<ViewCatalogueEntry> DocumentTypes,
    IReadOnlyList<ViewCatalogueEntry> Fields);

public sealed record ViewDocumentFieldWrite(
    Guid FieldId,
    string? Text = null,
    double? Number = null,
    DateOnly? Date = null,
    bool? Flag = null);

/// <summary>
/// Everything the document editor saves in one go. Anything left empty is cleared, except <c>Fields</c>: null means
/// "leave the custom field values as they are" (the editor could not load them), an empty list clears them all.
/// </summary>
public sealed record ViewDocumentDetailsWrite(
    string? Title,
    DateOnly? DocumentDate,
    string? Correspondent,
    string? DocumentType,
    string? ArchiveNumber,
    string? Notes,
    string? FilingState,
    IReadOnlyCollection<string> TagNames,
    IReadOnlyCollection<Guid> TagIds,
    IReadOnlyCollection<ViewDocumentFieldWrite>? Fields);

/// <summary>A name that already exists, compared without regard to case.</summary>
public sealed class ViewCatalogueConflictException(string message) : Exception(message);

/// <summary>The item or the name is not one the viewer can reach (or does not exist; the two look the same).</summary>
public sealed class ViewCatalogueAccessException(string message) : Exception(message);

/// <summary>
/// Tags, correspondents, document types and custom fields: one global list per install, with visibility decided at
/// read time. A person sees a name only when it is in use on an item they can reach, or they made it themselves.
/// </summary>
public interface IViewCatalogueRepository
{
    /// <summary>
    /// The lists the viewer may see. <paramref name="mediaKinds"/> narrows the usage counts (for example to
    /// documents) but never what is visible.
    /// </summary>
    ViewCatalogueSnapshot GetCatalogue(
        ViewCatalogueViewer viewer, IReadOnlyCollection<string>? mediaKinds = null, CancellationToken ct = default);

    /// <summary>One entry, or null when it does not exist or the viewer may not see it.</summary>
    ViewCatalogueEntry? FindEntry(
        ViewCatalogueViewer viewer, ViewCatalogueKind kind, Guid id, CancellationToken ct = default);

    /// <summary>
    /// The custom field values of the given documents, for the CSV export. Only documents in the viewer's libraries
    /// are read, and only fields the viewer can see; anything else is simply absent from the answer.
    /// </summary>
    IReadOnlyDictionary<Guid, IReadOnlyList<ViewDocumentFieldValueDto>> GetDocumentFieldValues(
        ViewCatalogueViewer viewer, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>Tag names for the type-ahead: the viewer's visible tags, most used first, plus their saved tags.</summary>
    IReadOnlyList<string> GetTagSuggestions(
        ViewCatalogueViewer viewer, string? search, int limit, CancellationToken ct = default);

    Task<ViewCatalogueEntry> CreateEntryAsync(
        ViewCatalogueViewer viewer, ViewCatalogueKind kind, string name, string? colour, CancellationToken ct = default);

    Task<ViewCatalogueEntry> CreateFieldAsync(
        ViewCatalogueViewer viewer, string name, string fieldKind, IReadOnlyList<string>? choices, CancellationToken ct = default);

    /// <summary>
    /// Renames a tag, correspondent or document type, then refreshes the search words of items that carry it.
    /// A null <paramref name="colour"/> keeps the colour it has; an empty one clears it.
    /// </summary>
    Task<ViewCatalogueEntry> UpdateEntryAsync(
        ViewCatalogueViewer viewer, ViewCatalogueKind kind, Guid id, string name, string? colour, CancellationToken ct = default);

    Task<ViewCatalogueEntry> UpdateFieldAsync(
        ViewCatalogueViewer viewer, Guid id, string name, IReadOnlyList<string>? choices, CancellationToken ct = default);

    /// <summary>Removes the entry and its links. Items are never deleted.</summary>
    Task<bool> DeleteEntryAsync(
        ViewCatalogueViewer viewer, ViewCatalogueKind kind, Guid id, CancellationToken ct = default);

    /// <summary>Replaces an item's tags with the named ones and the visible ones by id.</summary>
    Task ReplaceItemTagsAsync(
        ViewCatalogueViewer viewer, Guid itemId, IReadOnlyCollection<string> tagNames,
        IReadOnlyCollection<Guid> tagIds, CancellationToken ct = default);

    Task<bool> UpdateDocumentDetailsAsync(
        ViewCatalogueViewer viewer, Guid itemId, ViewDocumentDetailsWrite write, CancellationToken ct = default);
}
