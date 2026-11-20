using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// View spec WP13: the viewer's root element is drawn by AppViewer, so Blazor's scoped-style id is never on it. The root's own layout
/// (the grid that gives the photo stage its height) therefore has to live in the global sheet; a scoped rule on the bare root silently
/// does nothing and the photo opens small.
/// </summary>
public sealed class MediaViewerRootStyleGuardrailTests
{
    [Fact]
    public void ViewerRootGridLivesInTheGlobalSheet_NotInTheScopedSheet()
    {
        var web = WebRoot();
        var scoped = File.ReadAllText(Path.Combine(web, "Components", "Shared", "MediaViewerShell.razor.css"));
        var global = File.ReadAllText(Path.Combine(web, "wwwroot", "app.css"));

        Assert.DoesNotMatch(new Regex(@"(^|\})\s*\.media-viewer(--video)?\s*\{", RegexOptions.Multiline), scoped);
        Assert.Matches(new Regex(@"\.media-viewer\s*\{[^}]*grid-template-rows:\s*auto minmax\(0, 1fr\)"), global);
    }

    private static string WebRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir ?? throw new DirectoryNotFoundException("MediaEngine.slnx not found above the test output."), "src", "MediaEngine.Web");
    }
}
