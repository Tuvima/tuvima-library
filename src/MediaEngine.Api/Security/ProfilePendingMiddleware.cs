using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Security;

/// <summary>
/// From sign-in until the person chooses a profile (users, profiles and households spec D3) the session can do only what the profile
/// picker needs: check itself, list the household's profiles and their pictures, switch to one of them (with its PIN when set), and
/// sign out. Everything else is refused with 403 <c>profile_required</c>, so a sign-in alone cannot be used to look around or change
/// anything before someone is picked. Switching clears the pending mark, which lifts this limit.
/// </summary>
public sealed class ProfilePendingMiddleware(RequestDelegate next)
{
    public const string SwitchProfileEndpoint = "SwitchAuthProfile";
    public const string ListProfilesEndpoint = "ListProfiles";
    public const string ProfileAvatarEndpoint = "GetProfileAvatar";
    public const string ProfileRequiredCode = "profile_required";

    private static readonly HashSet<string> AllowedEndpoints = new(StringComparer.Ordinal)
    {
        PasswordChangeRequiredMiddleware.ValidateSessionEndpoint,
        PasswordChangeRequiredMiddleware.RevokeSessionEndpoint,
        SwitchProfileEndpoint,
        ListProfilesEndpoint,
        ProfileAvatarEndpoint,
    };

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.User.HasClaim(TuvimaClaimTypes.ProfilePending, "true") || IsAllowed(context))
        {
            return next(context);
        }

        return Results.Problem(
                detail: "Choose who is using Library before doing anything else.",
                statusCode: StatusCodes.Status403Forbidden,
                title: "Choose a profile first.",
                extensions: new Dictionary<string, object?> { ["code"] = ProfileRequiredCode })
            .ExecuteAsync(context);
    }

    private static bool IsAllowed(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is { } name
        && AllowedEndpoints.Contains(name);
}
