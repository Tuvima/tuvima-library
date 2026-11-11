using Bunit;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

/// <summary>The shared Settings parts (look pass v3 WP1): a group of rows on the page background and the one row layout.</summary>
public sealed class AppSettingsPartsTests : AsyncBunitContext
{
    private static RenderFragment Control(string text) => b => b.AddMarkupContent(0, $"<button type=\"button\">{text}</button>");

    private static RenderFragment Rows(params (string Label, string Button)[] rows) => b =>
    {
        foreach (var (label, button) in rows)
        {
            b.OpenComponent<AppSettingRow>(0);
            b.AddAttribute(1, nameof(AppSettingRow.Label), label);
            b.AddAttribute(2, nameof(AppSettingRow.Control), Control(button));
            b.CloseComponent();
        }
    };

    [Fact]
    public void ASettingsGroupRendersItsHeadingHelperAndRows()
    {
        var cut = Render<AppSettingsGroup>(p => p
            .Add(x => x.Heading, "Playback")
            .Add(x => x.Helper, "How this profile plays things.")
            .Add(x => x.ChildContent, Rows(("One", "A"), ("Two", "B"), ("Three", "C"))));

        var heading = cut.Find("h2.app-settings-group__heading");
        Assert.Equal("Playback", heading.TextContent.Trim());
        Assert.Equal(heading.Id, cut.Find("section").GetAttribute("aria-labelledby"));
        Assert.Contains("How this profile plays things.", cut.Find(".app-settings-group__helper").TextContent);
        var rows = cut.FindAll(".app-setting-row");
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.QuerySelector(".app-setting-row__control button")));
    }

    [Fact]
    public void ASettingRowPutsItsControlAtTheRightEndOfTheRow()
    {
        var cut = Render<AppSettingRow>(p => p
            .Add(x => x.Label, "Language")
            .Add(x => x.Helper, "Shown in menus.")
            .Add(x => x.Control, Control("Change")));

        var line = cut.Find(".app-setting-row__line");
        var children = line.Children.Select(c => c.ClassList.First()).ToArray();
        Assert.Equal(new[] { "app-setting-row__text", "app-setting-row__control" }, children);
        Assert.Contains("Language", line.Children[0].TextContent);
        Assert.Contains("Shown in menus.", line.Children[0].TextContent);
    }

    [Fact]
    public void ARowStatusSitsOnItsOwnLineUnderTheRow()
    {
        var cut = Render<AppSettingRow>(p => p
            .Add(x => x.Label, "Library")
            .Add(x => x.Control, Control("Scan"))
            .Add(x => x.Status, (RenderFragment)(b => b.AddContent(0, "Scanning 3 of 10"))));

        var status = cut.Find(".app-setting-row__status");
        Assert.Equal("Scanning 3 of 10", status.TextContent.Trim());
        Assert.Empty(cut.Find(".app-setting-row__line").QuerySelectorAll(".app-setting-row__status"));
        Assert.Contains("app-setting-row", status.ParentElement!.ClassList);
    }

    [Fact]
    public void ADangerGroupIsOutlinedRed()
    {
        var cut = Render<AppSettingsGroup>(p => p
            .Add(x => x.Heading, "Danger zone")
            .Add(x => x.Tone, AppSettingsGroupTone.Danger));

        Assert.Contains("app-settings-group--danger", cut.Find("section").ClassList);
        var css = File.ReadAllText(RepoPath("src/MediaEngine.Web/Components/Shared/AppSettingsGroup.razor.css"));
        Assert.Matches(@"\.app-settings-group--danger\s*\{[^}]*border:\s*1px solid var\(--tl-status-danger-border\)", css);
    }

    [Fact]
    public void ASettingRowLabelsItsControlWhenGivenAnId()
    {
        var cut = Render<AppSettingRow>(p => p.Add(x => x.Label, "Name").Add(x => x.For, "name-input"));

        Assert.Equal("name-input", cut.Find("label.app-setting-row__label").GetAttribute("for"));
    }

    [Fact]
    public void TheSettingsFrameContentColumnIsLeftAlignedAndCapsAt1180()
    {
        var css = File.ReadAllText(RepoPath("src/MediaEngine.Web/Components/Settings/Frame/SettingsPageFrame.razor.css"));
        var rule = System.Text.RegularExpressions.Regex.Match(css, @"(?m)^\.settings-frame\s*\{[^}]*\}").Value;
        Assert.Contains("--settings-frame-max-width: 1180px", rule);
        Assert.Contains("max-width: var(--settings-frame-max-width)", rule);
        Assert.DoesNotContain("margin: 0 auto", rule);
        Assert.DoesNotContain("margin-inline: auto", rule);
        Assert.DoesNotContain("auto", System.Text.RegularExpressions.Regex.Match(rule, @"margin[^;]*;").Value);
    }

    private static string RepoPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
