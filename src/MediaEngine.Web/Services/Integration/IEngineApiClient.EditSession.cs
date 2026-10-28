using MediaEngine.Contracts.Editing;

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
}
