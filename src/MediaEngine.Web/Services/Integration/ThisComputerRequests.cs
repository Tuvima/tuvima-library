using System.Net;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// The two small rules the Dashboard follows for an account that works only on this computer: say where the visitor is
/// on every Engine call (the Engine refuses the session otherwise), and notice when the Engine answers
/// <c>secure_account_first</c> so the screen can point to Secure account.
/// </summary>
public static class ThisComputerRequests
{
    /// <summary>The stable <c>code</c> in the Engine's 409 problem body.</summary>
    public const string SecureAccountFirstCode = "secure_account_first";

    /// <summary>The <c>authentication_method</c> of a sign-in made without a password on this computer.</summary>
    public const string AuthenticationMethod = "ThisComputer";

    /// <summary>
    /// Adds the ingress header when the visitor is on this computer. Other places need no header: a this-computer-only
    /// session is refused without one (fail closed) and every other session behaves as before.
    /// </summary>
    public static void AddIngress(HttpRequestMessage request, string? ingress)
    {
        if (ingress == ClientIngressValues.ThisComputer
            && !request.Headers.Contains(ClientIngressValues.ValidateHeader))
        {
            request.Headers.TryAddWithoutValidation(ClientIngressValues.ValidateHeader, ClientIngressValues.ThisComputer);
        }
    }

    public static async Task<bool> IsSecureAccountRefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.Conflict || response.Content is null)
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
                && code.GetString() == SecureAccountFirstCode;
        }
        catch (JsonException)
        {
            // A 409 that is not a problem body (a proxy page, say) is simply not this refusal.
            return false;
        }
    }
}
