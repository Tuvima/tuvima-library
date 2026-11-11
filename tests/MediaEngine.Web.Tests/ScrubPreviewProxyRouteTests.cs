using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// <c>/engine-scrub/{assetId}/{file}</c> carries a video's scrub-preview index and sprite sheets from the Engine to the browser. Its
/// route only accepts the two file shapes the Engine makes, so nothing else is ever forwarded.
/// </summary>
public sealed class ScrubPreviewProxyRouteTests
{
    private static readonly Guid Asset = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData("index.json", true)]
    [InlineData("sheet-000.jpg", true)]
    [InlineData("sheet-017.jpg", true)]
    [InlineData("sheet-0.jpg", false)]
    [InlineData("sheet-0000.jpg", false)]
    [InlineData("sheet-000.png", false)]
    [InlineData("xindex.json", false)]
    [InlineData("index.json.bak", false)]
    [InlineData("..%2Fsecret.db", false)]
    [InlineData("../secret.db", false)]
    [InlineData("subtitles", false)]
    public void TheProxyRouteAcceptsOnlyTheIndexAndTheNumberedSheets(string file, bool accepted)
    {
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Program.cs"));
        var start = program.IndexOf("app.MapGet(\"/engine-scrub/", StringComparison.Ordinal);
        Assert.True(start >= 0, "the scrub proxy route is mapped");
        start += "app.MapGet(\"".Length;
        var template = program[start..program.IndexOf("\", async", start, StringComparison.Ordinal)].Replace("\\\\", "\\", StringComparison.Ordinal);

        var parsed = TemplateParser.Parse(template);
        var constraint = Assert.Single(parsed.GetParameter("file")!.InlineConstraints!).Constraint;
        var resolver = new ServiceCollection().AddRouting().BuildServiceProvider().GetRequiredService<IInlineConstraintResolver>();
        var route = resolver.ResolveConstraint(constraint)!;
        var values = new RouteValueDictionary { ["assetId"] = Asset.ToString("D"), ["file"] = file };

        Assert.Equal(accepted, route.Match(httpContext: null, route: null, "file", values, RouteDirection.IncomingRequest));
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
