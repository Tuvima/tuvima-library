using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<LibraryBatchEditOutcome<EditSessionResult>> RunEditSessionAsync(
        EditSessionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string endpoint = "POST /library/edit-session";
        try
        {
            using var response = await _http.PostAsJsonAsync("/library/edit-session", request, ct);
            return await ReadBatchEditOutcomeAsync<EditSessionResult>(response, endpoint, ct);
        }
        catch (OperationCanceledException)
        {
            return LibraryBatchEditOutcome<EditSessionResult>.Failed(LibraryBatchEditOutcomeStatus.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return BatchEditFailure<EditSessionResult>(endpoint, ex);
        }
    }
}
