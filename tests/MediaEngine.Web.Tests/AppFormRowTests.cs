using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

/// <summary>The shared form row (Editor v6 WP1, D12): a label line, a control line and a message line that line a row of controls up by construction.</summary>
public sealed class AppFormRowTests : AsyncBunitContext
{
    private static RenderFragment Children => b => b.AddMarkupContent(0, "<div class=\"app-field\"><label class=\"app-field__label\">Title</label><input /></div><button type=\"button\">Search</button>");

    [Fact]
    public void AFormRowRendersItsChildrenAndItsMessage()
    {
        var cut = Render<AppFormRow>(p => p
            .Add(x => x.ChildContent, Children)
            .Add(x => x.Message, (RenderFragment)(b => b.AddContent(0, "10 results"))));

        var root = cut.Find("div.app-form-row");
        Assert.NotNull(root.QuerySelector("input"));
        Assert.NotNull(root.QuerySelector("button"));
        var message = cut.Find(".app-form-row__message");
        Assert.Equal("10 results", message.TextContent.Trim());
        Assert.Equal("status", message.GetAttribute("role"));
        Assert.True(message.HasAttribute("data-control-text"));
    }

    [Fact]
    public void AFormRowWithoutAMessageRendersNoMessageLine()
    {
        var cut = Render<AppFormRow>(p => p.Add(x => x.ChildContent, Children));

        Assert.Empty(cut.FindAll(".app-form-row__message"));
    }

    [Fact]
    public void ReserveMessageRendersTheMessageLineEvenWhenThereIsNoMessage()
    {
        var cut = Render<AppFormRow>(p => p
            .Add(x => x.ChildContent, Children)
            .Add(x => x.ReserveMessage, true));

        var message = cut.Find(".app-form-row__message");
        Assert.Equal(string.Empty, message.TextContent.Trim());
        Assert.Contains("app-form-row__message--reserved", message.ClassList);
    }

    [Fact]
    public void TheColumnsValueReachesTheRowStyle()
    {
        var cut = Render<AppFormRow>(p => p
            .Add(x => x.ChildContent, Children)
            .Add(x => x.Columns, "220px 1.3fr 1fr 220px auto"));

        Assert.Contains("--tl-form-row-columns: 220px 1.3fr 1fr 220px auto", cut.Find(".app-form-row").GetAttribute("style"));
    }

    [Fact]
    public void WithoutColumnsTheRowSharesTheWidthEvenly()
    {
        var cut = Render<AppFormRow>(p => p.Add(x => x.ChildContent, Children));

        Assert.Contains("--tl-form-row-columns: repeat(auto-fit, minmax(0, 1fr))", cut.Find(".app-form-row").GetAttribute("style"));
    }

    [Fact]
    public void StackAddsTheStackModifierClass()
    {
        var plain = Render<AppFormRow>(p => p.Add(x => x.ChildContent, Children));
        var stacked = Render<AppFormRow>(p => p.Add(x => x.ChildContent, Children).Add(x => x.Stack, true));

        Assert.DoesNotContain("app-form-row--stack", plain.Find(".app-form-row").ClassList);
        Assert.Contains("app-form-row--stack", stacked.Find(".app-form-row").ClassList);
    }

    [Fact]
    public void TheRowRootIsMarkedAsAnAlignmentRowAndKeepsCallerClassAndAttributes()
    {
        var cut = Render<AppFormRow>(p => p
            .Add(x => x.ChildContent, Children)
            .Add(x => x.Class, "my-row")
            .AddUnmatched("data-testid", "preview-alignment-formrow"));

        var root = cut.Find(".app-form-row");
        Assert.True(root.HasAttribute("data-alignment-row"));
        Assert.True(root.HasAttribute("data-form-row"));
        Assert.Contains("my-row", root.ClassList);
        Assert.Equal("preview-alignment-formrow", root.GetAttribute("data-testid"));
    }

    [Fact]
    public void TheFormRowStyleSheetUsesASubgridAndNeverNudgesWithMarginTop()
    {
        // The row's own rules are in its scoped sheet; the rules that place each child field on the row's three lines are global (the
        // children are other components' roots, and the style-ownership ratchet allows no new ::deep rules).
        var web = WebRoot();
        var scoped = StripComments(File.ReadAllText(Path.Combine(web, "Components", "Shared", "AppFormRow.razor.css")));
        var childRules = string.Concat(
            Regex.Matches(StripComments(File.ReadAllText(Path.Combine(web, "wwwroot", "app.css"))), @"(?<selector>[^{}]*\.app-form-row[^{}]*)\{(?<body>[^{}]*)\}")
                .Select(match => match.Value));

        Assert.Contains("grid-template-rows: subgrid", childRules, StringComparison.Ordinal);
        foreach (var css in new[] { scoped, childRules })
        {
            Assert.DoesNotContain("margin-top", css, StringComparison.Ordinal);
            Assert.DoesNotContain("vertical-align", css, StringComparison.Ordinal);
            Assert.DoesNotContain("text-align", css, StringComparison.Ordinal);
        }
    }

    private static string StripComments(string css) => Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

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
