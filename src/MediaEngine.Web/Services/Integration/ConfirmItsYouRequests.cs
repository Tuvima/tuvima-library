using System.Net;
using System.Text.Json;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// The Engine asks the person to prove it is them before a sensitive account change (403 <c>confirm_its_you</c>).
/// This reads that refusal without ever replaying the response body to the screen.
/// </summary>
public static class ConfirmItsYouRequests
{
    /// <summary>The stable <c>code</c> in the Engine's 403 problem body.</summary>
    public const string Code = "confirm_its_you";

    public static async Task<bool> IsConfirmItsYouRefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden || response.Content is null)
        {
            return false;
        }

        try
        {
            // Buffered so the caller can still read the body.
            await response.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() == Code;
        }
        catch (JsonException)
        {
            // A 403 that is not a problem body (a proxy page, say) is simply not this refusal.
            return false;
        }
    }
}
