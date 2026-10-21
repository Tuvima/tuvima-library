using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Http;

namespace MediaEngine.Api.Security;

/// <summary>Marks a route as one that only a device session with NSFW mode on may reach (Videos spec D17, D39).</summary>
public sealed class VaultGatedRoute
{
    public static readonly VaultGatedRoute Instance = new();

    private VaultGatedRoute()
    {
    }
}

/// <summary>
/// Answers a Vault route "not found" for anyone without an unlocked NSFW session before the framework reads a query value or a body.
/// Without it a malformed value (a performer that is not an id, a body that is not JSON) is refused with a 400 while binding, which
/// tells a locked caller something different from the 404 every other Vault request gets. The route filter stays as a second check.
/// </summary>
public sealed class VaultGateMiddleware(RequestDelegate next)
{
    private const string PassedKey = "vault-gate-passed";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<VaultGatedRoute>() is null)
        {
            await next(context);
            return;
        }

        if (!await VaultBrowseEndpoints.PassesGateAsync(context))
        {
            await ApiErrors.NotFound("Not found.").ExecuteAsync(context);
            return;
        }

        context.Items[PassedKey] = true;
        await next(context);
    }

    /// <summary>True when this middleware already let the request through.</summary>
    internal static bool HasPassed(HttpContext context) => context.Items.ContainsKey(PassedKey);
}
