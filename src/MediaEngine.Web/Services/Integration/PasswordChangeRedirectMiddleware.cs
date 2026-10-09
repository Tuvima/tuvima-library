namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// While someone is signed in with a temporary password, every page they open leads to the full-page
/// "Choose a new password" screen. The Engine enforces the same rule for everything else, so this only decides what
/// the person sees.
/// </summary>
public sealed class PasswordChangeRedirectMiddleware(RequestDelegate next)
{
    public const string ChangePasswordPath = "/auth/change-password";

    public Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(DashboardPrincipalFactory.PasswordChangeRequiredClaim, "true")
            && !context.Request.Path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase)
            && IsPageRequest(context.Request))
        {
            context.Response.Redirect(ChangePasswordPath);
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static bool IsPageRequest(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
}
