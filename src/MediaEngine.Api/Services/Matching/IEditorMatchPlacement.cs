using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;

namespace MediaEngine.Api.Services.Matching;

/// <summary>Where applying a match would put an item and its file; a seam so the group Match can be tested without a library on disk.</summary>
public interface IEditorMatchPlacement
{
    /// <summary>Null when no such item is in the Library.</summary>
    Task<EditorMatchPlacementDto?> PlanAsync(
        Guid entityId, ItemCanonicalRetailCandidateDto candidate, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct = default);
}
