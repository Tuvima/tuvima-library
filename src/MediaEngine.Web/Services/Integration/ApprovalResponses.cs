using System.Text.Json;
using MediaEngine.Contracts.Approvals;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Reads the Engine's approval-required 409 (the one that is not a problem body).</summary>
internal static class ApprovalResponses
{
    /// <summary>
    /// The approval-required body of a 409, or null when the response is not one (another kind of conflict, or no body).
    /// </summary>
    public static async Task<ApprovalRequiredResponse?> TryReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            return null;
        }

        try
        {
            // Read as text so the body stays available to the readers that follow (the problem detail of an ordinary conflict).
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!raw.Contains(ApprovalErrorCodes.ApprovalRequired, StringComparison.Ordinal))
            {
                return null;
            }

            var body = JsonSerializer.Deserialize<ApprovalRequiredResponse>(raw);
            return body is { Error: ApprovalErrorCodes.ApprovalRequired, Approvals.Count: > 0 } ? body : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// The outcome of saving the Local AI configuration. <see cref="Approval"/> is set when a feature was switched on and the
/// Engine wants the person to confirm what it can do first; nothing was saved in that case.
/// </summary>
public sealed record AiConfigSaveResult(bool Saved, ApprovalRequiredResponse? Approval = null);
