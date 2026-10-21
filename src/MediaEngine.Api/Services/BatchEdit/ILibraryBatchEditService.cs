using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.BatchEdit;

/// <summary>
/// Metadata edits across several selected items at once. A batch edit follows the pencil's rules: it writes
/// presentation overrides on each item's work, never provider facts and never structure (names, files, parents).
/// Each item stands alone: one that cannot take the change goes to Needs Review and the others still apply.
/// </summary>
public interface ILibraryBatchEditService
{
    /// <summary>
    /// How the selected items stand on each batch field: the shared value or Mixed, whether the field is a list, and
    /// which items cannot take a change and why. <paramref name="refusals"/> names selected items the caller may not edit.
    /// </summary>
    Task<LibraryBatchEditPreview> PreviewAsync(
        IReadOnlyCollection<Guid> entityIds,
        IReadOnlyCollection<string> fieldKeys,
        IReadOnlyDictionary<Guid, string> refusals,
        CancellationToken ct = default);

    /// <summary>
    /// Applies the validated changes to every selected item, one write per item, and reports each item's outcome.
    /// Fields that were not sent are untouched.
    /// </summary>
    Task<LibraryBatchEditResult> ApplyAsync(
        IReadOnlyCollection<Guid> entityIds,
        IReadOnlyList<BatchEditChange> changes,
        IReadOnlyDictionary<Guid, string> refusals,
        CancellationToken ct = default);
}
