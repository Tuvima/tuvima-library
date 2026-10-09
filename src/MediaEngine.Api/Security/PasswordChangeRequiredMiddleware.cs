using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Security;

/// <summary>
/// While a person is signed in with an administrator-set temporary password, the only things they can do are choose
/// a password of their own, check their session, and sign out. Everything else is refused with 403
/// <c>password_change_required</c>, so the temporary password alone cannot be used to look around or change anything.
/// </summary>
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    public const string ChangeTemporaryPasswordEndpoint = "ChangeTemporaryPassword";
    public const string ValidateSessionEndpoint = "ValidateAuthSession";
    public const string RevokeSessionEndpoint = "RevokeAuthSession";

    private static readonly HashSet<string> AllowedEndpoints = new(StringComparer.Ordinal)
    {
        ChangeTemporaryPasswordEndpoint,
        ValidateSessionEndpoint,
        RevokeSessionEndpoint,
    };

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.User.HasClaim(TuvimaClaimTypes.PasswordChangeRequired, "true") || IsAllowed(context))
        {
            return next(context);
        }

        return Results.Problem(
                detail: "Choose a new password before doing anything else.",
                statusCode: StatusCodes.Status403Forbidden,
                title: "Password change required.",
                extensions: new Dictionary<string, object?> { ["code"] = TemporaryPasswordPolicy.PasswordChangeRequiredCode })
            .ExecuteAsync(context);
    }

    private static bool IsAllowed(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is { } name
        && AllowedEndpoints.Contains(name);
}
