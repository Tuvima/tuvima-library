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
    public void AFormRowRendersItsChildrenInTheFieldGridAndItsMessageOutsideIt()
    {
        var cut = Render<AppFormRow>(p => p
            .Add(x => x.ChildContent, Children)
            .Add(x => x.Message, (RenderFragment)(b => b.AddContent(0, "10 results"))));

        var root = cut.Find("div.app-form-row");
        var fields = cut.Find(".app-form-row__fields");
        Assert.Contains("app-form-row", fields.ParentElement!.ClassList);
        Assert.NotNull(fields.QuerySelector("input"));
        Assert.NotNull(fields.QuerySelector("button"));
        var message = cut.Find(".app-form-row__message");
        Assert.Equal("10 results", message.TextContent.Trim());
        Assert.Equal("status", message.GetAttribute("role"));
        Assert.True(message.HasAttribute("data-control-text"));
        // The message is a sibling of the field grid, so it spans the full width whatever the columns are (it is never a grid cell).
        Assert.Contains("app-form-row", message.ParentElement!.ClassList);
        Assert.DoesNotContain("app-form-row__fields", message.ParentElement.ClassList);
        Assert.Empty(fields.QuerySelectorAll(".app-form-row__message"));
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
    public void TheColumnsValueReachesTheFieldGridStyle()
    {
        var cut = Render<AppFormRow>(p => p
            .Add(x => x.ChildContent, Children)
            .Add(x => x.Columns, "220px 1.3fr 1fr 220px auto")
            .Add(x => x.Message, (RenderFragment)(b => b.AddContent(0, "10 results"))));

        Assert.Contains("--tl-form-row-columns: 220px 1.3fr 1fr 220px auto", cut.Find(".app-form-row__fields").GetAttribute("style"));
        Assert.Null(cut.Find(".app-form-row").GetAttribute("style"));
    }

    [Fact]
    public void WithoutColumnsTheFieldGridGetsNoExplicitTemplateEvenWithAMessage()
    {
        var cut = Render<AppFormRow>(p => p
            .Add(x => x.ChildContent, Children)
            .Add(x => x.Message, (RenderFragment)(b => b.AddContent(0, "10 results"))));

        // No explicit template: the children flow into equal implicit columns (see the style sheet test), so a message line can never squeeze them.
        Assert.Null(cut.Find(".app-form-row__fields").GetAttribute("style"));
        Assert.Null(cut.Find(".app-form-row").GetAttribute("style"));
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
    public void TheRowRootIsMarkedAsAnAlignmentRowAndKeepsCallerClassStyleAndAttributes()
    {
        var cut = Render<AppFormRow>(p => p
            .Add(x => x.ChildContent, Children)
            .Add(x => x.Class, "my-row")
            .Add(x => x.Columns, "1fr 1fr")
            .AddUnmatched("style", "max-width: 40rem")
            .AddUnmatched("data-testid", "preview-alignment-formrow"));

        var root = cut.Find(".app-form-row");
        Assert.True(root.HasAttribute("data-alignment-row"));
        Assert.True(root.HasAttribute("data-form-row"));
        Assert.Contains("my-row", root.ClassList);
        Assert.Equal("preview-alignment-formrow", root.GetAttribute("data-testid"));
        Assert.Equal("max-width: 40rem", root.GetAttribute("style"));
        Assert.Contains("--tl-form-row-columns: 1fr 1fr", cut.Find(".app-form-row__fields").GetAttribute("style"));
    }

    [Fact]
    public void AnIconButtonAndACheckboxAreDirectChildrenOfTheFieldGridAndNotFields()
    {
        // Anything that is not an app-field takes the control line by the style sheet's default rule; these two have their own roots
        // (a tooltip wrapper, a label) that a class list would have missed.
        RenderFragment kids = b =>
        {
            b.OpenComponent<AppCheckbox>(0);
            b.AddAttribute(1, nameof(AppCheckbox.Label), "Owned only");
            b.CloseComponent();
            b.OpenComponent<AppIconButton>(2);
            b.AddAttribute(3, nameof(AppIconButton.Icon), AppMaterialIcons.Outlined.Search);
            b.AddAttribute(4, nameof(AppIconButton.Tooltip), "Search");
            b.CloseComponent();
        };

        var cut = Render<AppFormRow>(p => p.Add(x => x.ChildContent, kids));

        var direct = cut.Find(".app-form-row__fields").Children;
        Assert.Equal(2, direct.Length);
        Assert.All(direct, child => Assert.DoesNotContain("app-field", child.ClassList));
        Assert.Contains("app-checkbox", direct[0].ClassList);
    }

    [Fact]
    public void TheFormRowStyleSheetsFlowChildrenIntoImplicitColumnsUseASubgridAndNeverNudge()
    {
        // The row's own rules are in its scoped sheet; the rules that place each child on the row's three lines are global (the children are
        // other components' roots, and the style-ownership ratchet allows no new ::deep rules).
        var web = WebRoot();
        var scoped = StripComments(File.ReadAllText(Path.Combine(web, "Components", "Shared", "AppFormRow.razor.css")));
        var global = StripComments(File.ReadAllText(Path.Combine(web, "wwwroot", "app.css")));
        var childRules = string.Concat(
            Regex.Matches(global, @"[^{}]*\.app-form-row[^{}]*\{[^{}]*\}").Select(match => match.Value));

        // Equal implicit columns by default: no repeat(auto-fit ...) template, which a full-width message line would stop from collapsing.
        Assert.Contains("grid-auto-flow: column", scoped, StringComparison.Ordinal);
        Assert.Contains("grid-auto-columns: minmax(0, 1fr)", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("auto-fit", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("auto-fit", childRules, StringComparison.Ordinal);

        Assert.Contains("grid-template-rows: subgrid", childRules, StringComparison.Ordinal);
        // Every direct child defaults to the control line; fields opt into the three lines.
        Assert.Matches(@"\.app-form-row__fields\s*>\s*\*\s*\{[^}]*grid-row:\s*2", childRules);
        // The tooltip wrapper of an icon button is display: contents elsewhere; in the row it must have a box so the button is not auto-placed.
        Assert.Matches(@"\.app-form-row__fields\s*>\s*\.playback-tooltip-owner\s*\{[^}]*display:\s*flex", childRules);
        Assert.Matches(@"\.app-form-row__fields\s*>\s*:is\(\.app-field,\s*\[data-form-row-field\]\)\s*\{[^}]*grid-row:\s*1 / span 3", childRules);
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
