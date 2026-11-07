using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Playback;
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

    /// <summary>
    /// GET /metadata/{id}/editor-nav/level - one searchable, paged list of the editor header's item dropdown: the items of a season, a section
    /// or the whole title, with the supporting files that belong to it. <paramref name="levelId"/> null means the title itself.
    /// <paramref name="supportingFileId"/> is the supporting file the editor is open on, so its row stays marked current. Null when the
    /// read fails; the dropdown then keeps the page it has.
    /// </summary>
    Task<EditorHeaderLevelDto?> GetEditorNavLevelAsync(
        Guid entityId, Guid? levelId, string? query, int offset, Guid? supportingFileId = null, CancellationToken ct = default);

    /// <summary>
    /// GET /metadata/editor-config?kind= - the editor's tabs, facts and search fields for a kind of item that does not open on a work (a
    /// person, a Series, a Universe). Null when the kind is not known or the read fails.
    /// </summary>
    Task<EditorHeaderConfigDto?> GetEditorKindConfigAsync(string kind, CancellationToken ct = default);

    /// <summary>GET /library/editor-subjects/{id}/history - what was changed in the editor on a Universe or one of its things, newest first.</summary>
    Task<(List<LibraryItemHistoryDto> Items, string? Error)> GetEditorSubjectHistoryAsync(Guid id, CancellationToken ct = default);

    // The editor's Bookmarks tab (universal editor v5 D37, WP15). Administrators only; each returns null when the Engine refuses or cannot
    // be reached, and LastError then says why. The changes themselves (add, remove, rename, move, import, start over) are staged like every
    // other editor change and saved through RunEditSessionAsync.

    /// <summary>GET /api/v1/editor-bookmarks/titles/{id} - a title's Everyone's list and every profile's bookmarks beside it.</summary>
    Task<EditorBookmarksDto?> GetEditorBookmarksAsync(Guid workId, CancellationToken ct = default);

    /// <summary>POST /api/v1/editor-bookmarks/titles/{id}/compare - a bookmark file laid beside the title's Everyone's list. Changes nothing.</summary>
    Task<BookmarkSetCompareDto?> CompareEditorBookmarkFileAsync(Guid workId, EditorBookmarkCompareRequestDto request, CancellationToken ct = default);

    /// <summary>POST /api/v1/editor-bookmarks/titles/{id}/export - the title's Everyone's bookmarks as a file.</summary>
    Task<BookmarkPackFileDto?> ExportEditorBookmarksAsync(Guid workId, CancellationToken ct = default);

    /// <summary>GET /api/v1/editor-bookmarks/families/{id} - one row per episode or track of a show, season or album, grouped by season.</summary>
    Task<EditorBookmarkOverviewDto?> GetEditorBookmarkOverviewAsync(Guid workId, CancellationToken ct = default);

    /// <summary>POST /api/v1/editor-bookmarks/families/{id}/export - the Everyone's bookmarks of a whole show as one file.</summary>
    Task<BookmarkPackFileDto?> ExportEditorShowBookmarksAsync(Guid workId, CancellationToken ct = default);

    /// <summary>POST /api/v1/editor-bookmarks/families/{id}/compare - a show file set beside the show, episode by episode. Changes nothing.</summary>
    Task<EditorBookmarkShowCompareDto?> CompareEditorShowBookmarkFileAsync(Guid workId, EditorBookmarkCompareRequestDto request, CancellationToken ct = default);
}
