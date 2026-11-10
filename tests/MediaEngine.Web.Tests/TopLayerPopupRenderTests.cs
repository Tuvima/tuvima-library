using Bunit;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.Navigation;
using MediaEngine.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Pop-ups that used to draw their own absolutely positioned panel (issue 704c, Look pass v2) now sit inside the shared top-layer
/// popover, and the popover's outside-click or Escape report closes them.
/// </summary>
public sealed class TopLayerPopupRenderTests : AsyncBunitContext
{
    [Fact]
    public async Task AccountMenu_DrawsItsMenuInsideTheSharedPopover_AndClosesWhenThePopoverIsDismissed()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<TopNavAccountMenu>(parameters => parameters.Add(p => p.DisplayName, "Maya"));
        Assert.Empty(cut.FindAll(".top-nav-account-menu__panel"));

        cut.Find(".top-nav-account-menu__trigger").Click();
        var popover = cut.FindComponent<AppPopover>();
        Assert.Contains("top-nav-account-menu__surface", popover.Find("[popover]").ClassName, StringComparison.Ordinal);
        Assert.NotNull(popover.Find(".top-nav-account-menu__panel[role='menu']"));
        Assert.Empty(cut.FindAll(".app-dismissible-surface__dismiss"));

        await cut.InvokeAsync(() => popover.Instance.Dismiss());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".top-nav-account-menu__panel")));
    }

    [Fact]
    public async Task TagEditor_DrawsItsSuggestionsInsideTheSharedPopover_AndHidesThemWhenDismissed()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<MediaEditorTagEditor>(parameters => parameters
            .Add(p => p.Value, "noir")
            .Add(p => p.Suggestions, ["heist", "western"]));
        Assert.Empty(cut.FindAll("[role='listbox']"));

        cut.Find(".sme-tag-editor__add").Click();
        var popover = cut.FindComponent<AppPopover>();
        Assert.Equal(2, popover.FindAll("[role='listbox'] .sme-tag-editor__suggestion").Count);

        await cut.InvokeAsync(() => popover.Instance.Dismiss());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role='listbox']")));
    }

    [Theory]
    [InlineData("Components/Details/EditorHeaderNav.razor")]
    [InlineData("Components/Details/EditorUniverseRail.razor")]
    [InlineData("Components/Details/ViewEditHeaderNav.razor")]
    [InlineData("Components/Collections/CollectionRuleValuePicker.razor")]
    public void RailsAndRulePicker_DrawTheirListsThroughTheSharedPopover_WithoutAScrim(string relative)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MediaEngine.Web", relative.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("<AppPopover", source, StringComparison.Ordinal);
        Assert.DoesNotContain("__scrim", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MediaEngine.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        return root ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
