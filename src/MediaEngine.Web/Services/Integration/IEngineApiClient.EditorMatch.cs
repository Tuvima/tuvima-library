using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>The record an item is matched to, found by the provider id the Engine stored (never a text search), with the reasons it fits and a Strong, Possible or Check verdict. Null when the Engine could not be reached or the item is gone.</summary>
    Task<EditorCurrentMatchDto?> GetEditorCurrentMatchAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>What the Match tab's search opens on: the providers for the item's kind (the Engine works the kind out) with their states, the boxes pre-filled from the file, and the local evidence. A read that makes no search.</summary>
    Task<EditorMatchSetupDto?> GetEditorMatchSetupAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>Searches one provider for the item and judges each result against the file. Writes nothing. Null when the Engine could not be reached.</summary>
    Task<EditorMatchSearchResultDto?> SearchEditorMatchAsync(Guid entityId, EditorMatchSearchRequestDto request, CancellationToken ct = default);

    /// <summary>Where the item would go if this record were applied: the new parent, whether its folder is Managed, and the file's paths before and after. A read that moves nothing. Null when the Engine could not be reached.</summary>
    Task<EditorMatchPlacementDto?> GetEditorMatchPlacementAsync(Guid entityId, ItemCanonicalRetailCandidateDto candidate, CancellationToken ct = default);
}
