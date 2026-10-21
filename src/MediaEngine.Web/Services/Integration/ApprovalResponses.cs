using System.Text.Json;
using MediaEngine.Contracts.Approvals;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Reads the Engine's approval-required 409 (the one that is not a problem body).</summary>
internal static class ApprovalResponses
{
    /// <summary>
    /// The approval-required body of a 409, or null when the response is not one (another kind of conflict, or no body).
    /// A body that looks like one but cannot be read is logged (the error type only, never the body) and counts as not one.
    /// </summary>
    public static async Task<ApprovalRequiredResponse?> TryReadAsync(HttpResponseMessage response, ILogger? logger, CancellationToken ct)
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
        catch (JsonException ex)
        {
            return Unreadable(logger, ex);
        }
        catch (NotSupportedException ex)
        {
            return Unreadable(logger, ex);
        }
        catch (IOException ex)
        {
            return Unreadable(logger, ex);
        }
    }

    private static ApprovalRequiredResponse? Unreadable(ILogger? logger, Exception ex)
    {
        logger?.LogWarning("The Engine's approval-required reply could not be read ({ErrorType})", ex.GetType().Name);
        return null;
    }
}
