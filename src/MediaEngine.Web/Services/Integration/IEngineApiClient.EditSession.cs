using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>
    /// POST /library/edit-session - checks everything staged in the universal editor (<see cref="EditSessionRequest.PreviewOnly"/>), or
    /// checks and saves it: all data changes together or none, file moves last. The answer lists one result per change with the plain
    /// reason for any that did not go through. The outcome tells 200, 400, 403 and unreachable apart.
    /// </summary>
    Task<LibraryBatchEditOutcome<EditSessionResult>> RunEditSessionAsync(
        EditSessionRequest request, CancellationToken ct = default);

    /// <summary>
    /// GET /library/editor-subjects/{id} - what the universal editor opens on for a person, Series or curated collection (spec D17):
    /// the Details fields with their current and canonical values and the picture slots it can have. Null when it cannot be opened.
    /// </summary>
    Task<EditorSubjectDto?> GetEditorSubjectAsync(Guid id, CancellationToken ct = default);

    /// <summary>GET /library/editor-subjects/universes/{qid} - the same for a Universe, found by its Wikidata ID (spec D17a). Null when it cannot be opened.</summary>
    Task<EditorSubjectDto?> GetUniverseEditorSubjectAsync(string qid, CancellationToken ct = default);

    /// <summary>
    /// GET /library/editor-quick-facts/{id} - the totals behind the editor's Quick Facts (children owned of total, files, size, runtime, best
    /// quality, read-only library folder) for a title at any level, a person, a Series, a curated collection or a Universe. Null when the read
    /// fails; the panel then shows only what the item's own fields say.
    /// </summary>
    Task<ItemQuickFactsDto?> GetEditorQuickFactsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// GET /library/editor-subjects/universes/{qid}/things - the rail of the Universe editor: the kinds of thing a Universe holds with their
    /// counts and, when <paramref name="kindKey"/> is given, that kind's things searched and paged. Null when the read fails.
    /// </summary>
    Task<EditorUniverseThingsDto?> GetEditorUniverseThingsAsync(
        string qid, string? kindKey, string? search, int offset, int limit, CancellationToken ct = default);

    /// <summary>DELETE /library/editor-subjects/things/{id} - deletes a character, place or faction someone added by hand to a Universe, at once. False when it did not go through.</summary>
    Task<bool> DeleteEditorUniverseThingAsync(Guid id, CancellationToken ct = default);

    /// <summary>GET /library/editor-subjects/{id}/history - what was changed in the editor on a Universe or one of its things, newest first.</summary>
    Task<(List<LibraryItemHistoryDto> Items, string? Error)> GetEditorSubjectHistoryAsync(Guid id, CancellationToken ct = default);
}
