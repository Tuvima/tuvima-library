using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// <c>/engine-bookmark-frame/{bookmarkId}</c> carries the still frame of a video bookmark from the Engine to the browser. Its route
/// only accepts a bookmark id, so nothing else is ever forwarded.
/// </summary>
public sealed class BookmarkFrameProxyRouteTests
{
    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", true)]
    [InlineData("not-an-id", false)]
    [InlineData("..%2Fsecret.db", false)]
    [InlineData("../secret.db", false)]
    [InlineData("11111111-1111-1111-1111-111111111111.jpg", false)]
    public void TheProxyRouteAcceptsOnlyABookmarkId(string value, bool accepted)
    {
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Program.cs"));
        var start = program.IndexOf("app.MapGet(\"/engine-bookmark-frame/", StringComparison.Ordinal);
        Assert.True(start >= 0, "the bookmark frame proxy route is mapped");
        start += "app.MapGet(\"".Length;
        var template = program[start..program.IndexOf("\", async", start, StringComparison.Ordinal)];

        var parsed = TemplateParser.Parse(template);
        var parameter = parsed.GetParameter("bookmarkId")!;
        Assert.Equal(["bookmarkId"], parsed.Parameters.Select(p => p.Name));
        var constraint = Assert.Single(parameter.InlineConstraints!).Constraint;
        var resolver = new ServiceCollection().AddRouting().BuildServiceProvider().GetRequiredService<IInlineConstraintResolver>();
        var route = resolver.ResolveConstraint(constraint)!;
        var values = new RouteValueDictionary { ["bookmarkId"] = value };

        Assert.Equal(accepted, route.Match(httpContext: null, route: null, "bookmarkId", values, RouteDirection.IncomingRequest));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
