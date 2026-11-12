using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>The record an item is matched to, found by the provider id the Engine stored (never a text search), with the reasons it fits and a Strong, Possible or Weak verdict. Null when the Engine could not be reached or the item is gone.</summary>
    Task<EditorCurrentMatchDto?> GetEditorCurrentMatchAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>What the Match tab's search opens on: the providers for the item's kind (the Engine works the kind out) with their states, the boxes pre-filled from the file, and the local evidence. A read that makes no search.</summary>
    Task<EditorMatchSetupDto?> GetEditorMatchSetupAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>Searches one provider for the item and judges each result against the file. Writes nothing. Null when the Engine could not be reached.</summary>
    Task<EditorMatchSearchResultDto?> SearchEditorMatchAsync(Guid entityId, EditorMatchSearchRequestDto request, CancellationToken ct = default);

    /// <summary>Where the item would go if this record were applied: the new parent, whether its folder is Managed, and the file's paths before and after. A read that moves nothing. Null when the Engine could not be reached.</summary>
    Task<EditorMatchPlacementDto?> GetEditorMatchPlacementAsync(Guid entityId, ItemCanonicalRetailCandidateDto candidate, CancellationToken ct = default);

    /// <summary>Judges a record picked in the TV or music flow (an episode, a track) against the item's file: Strong, Possible or Weak with its reasons. A read. Null when the Engine could not be reached.</summary>
    Task<EditorMatchResultDto?> GetEditorMatchVerdictAsync(Guid entityId, ItemCanonicalRetailCandidateDto candidate, CancellationToken ct = default);

    /// <summary>The releases of an album that fit the artist, album or song words, with the current one marked and the one that fits the file suggested. Null when the Engine could not be reached.</summary>
    Task<EditorMusicReleasesDto?> GetEditorMusicReleasesAsync(Guid entityId, string? artist, string? album, string? song, CancellationToken ct = default);

    /// <summary>One release's discs and tracks, the track that fits the file marked. Null when the Engine could not be reached or the release cannot be read.</summary>
    Task<EditorMusicReleaseTracksDto?> GetEditorMusicReleaseTracksAsync(Guid entityId, string releaseId, CancellationToken ct = default);

    /// <summary>Lines several ticked episodes or songs up with one show and season, or one release, by their own numbers (D36). A read. Null when the Engine could not be reached.</summary>
    Task<EditorGroupMatchDto?> LineUpEditorGroupMatchAsync(Guid entityId, EditorGroupMatchRequestDto request, CancellationToken ct = default);

    /// <summary>The Wikidata item the item itself is linked to, found by its stored QID (never a search). The Engine's answer carries no QID when the item has no link. Null when the Engine could not be reached.</summary>
    Task<EditorWikidataIdentityDto?> GetEditorCurrentWikidataAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>Searches Wikidata for the item, or resolves a pasted link or QID: text-only results with type, relation (itself or parent) and alignment evidence. Writes nothing. Null when the Engine could not be reached.</summary>
    Task<EditorWikidataSearchResultDto?> SearchEditorWikidataAsync(Guid entityId, EditorWikidataSearchRequestDto request, CancellationToken ct = default);

    /// <summary>Whether the provider match and the Wikidata item disagree (identifiers, year, title). A read; neither side is changed. Null when the Engine could not be reached.</summary>
    Task<EditorWikidataConflictDto?> CompareEditorWikidataAsync(Guid entityId, EditorWikidataConflictRequestDto request, CancellationToken ct = default);
}
