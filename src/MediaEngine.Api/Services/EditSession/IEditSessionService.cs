using MediaEngine.Contracts.Editing;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Checks and applies a whole edit session (universal editor spec D13). Every change is checked first; the data changes
/// are then saved as one unit (all of them or none); file moves run last, one at a time.
/// </summary>
public interface IEditSessionService
{
    /// <summary>
    /// Runs the session. <paramref name="refusals"/> names the items the caller may not edit, with the reason shown to the
    /// person; a change on such an item fails the check. With <see cref="EditSessionRequest.PreviewOnly"/> nothing is written.
    /// </summary>
    Task<EditSessionResult> RunAsync(
        EditSessionRequest request,
        IReadOnlyDictionary<Guid, string> refusals,
        CancellationToken ct = default);
}
