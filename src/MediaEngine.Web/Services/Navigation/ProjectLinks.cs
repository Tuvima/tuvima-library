namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// Public addresses of the project. They must match website/scripts/content-contract.mjs;
/// <c>npm run check</c> in website/ fails when they drift.
/// </summary>
public static class ProjectLinks
{
    /// <summary>The published documentation site, opened by the Help menu item.</summary>
    public const string DocsSite = "https://tuvima.github.io/tuvima-library/";
}
