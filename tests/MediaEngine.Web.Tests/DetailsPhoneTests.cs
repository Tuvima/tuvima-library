using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Cinematic;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class DetailsPhoneTests : AsyncBunitContext
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public DetailsPhoneTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
    }

    private Task UseAsync(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(
            formFactor == "phone" ? "mobile" : "web",
            formFactor,
            formFactor != "desktop",
            FinePointer: formFactor == "desktop",
            Hover: formFactor == "desktop"));

    private static DetailAction Action(string key, string label, string icon, bool primary = false) => new()
    {
        Key = key,
        Label = label,
        Icon = icon,
        IsPrimary = primary,
    };

    private static readonly DetailAction[] OverflowActions =
    [
        Action("status-complete", "Mark as watched", "check"),
        Action("copy-link", "Copy library link", "link"),
    ];

    [Fact]
    public async Task PhoneHeroKeepsOnePrimaryActionAndNamesEachRoundIcon()
    {
        await UseAsync("phone");

        var hero = Render<DetailHeroContent>(parameters => parameters
            .Add(content => content.Title, "The Quiet Crossing")
            .Add(content => content.MetadataContent, (RenderFragment)(builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "tl-detail-metadata-stack");
                builder.OpenElement(2, "div");
                builder.AddAttribute(3, "class", "tl-detail-watch-metadata-row tl-detail-watch-metadata-row--facts");
                builder.OpenElement(4, "span");
                builder.AddAttribute(5, "class", "tl-detail-watch-metadata-item");
                builder.AddContent(6, "2024");
                builder.CloseElement();
                builder.OpenElement(7, "span");
                builder.AddAttribute(8, "class", "tl-detail-watch-metadata-item");
                builder.AddContent(9, "45m");
                builder.CloseElement();
                builder.CloseElement();
                builder.CloseElement();
            }))
            .Add(content => content.ActionContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<HeroActionRow>(0);
                builder.AddComponentParameter(1, nameof(HeroActionRow.PrimaryActions), (IReadOnlyList<DetailAction>)[Action("watch", "Watch", "play_arrow", primary: true)]);
                builder.AddComponentParameter(2, nameof(HeroActionRow.SecondaryActions), (IReadOnlyList<DetailAction>)[Action("my-list", "Add to My List", "add")]);
                builder.AddComponentParameter(3, nameof(HeroActionRow.OverflowActions), (IReadOnlyList<DetailAction>)OverflowActions);
                builder.CloseComponent();
            })));

        // Facts are separate chip items (the phone CSS lets them wrap), identity comes first and actions follow the facts.
        Assert.Equal(2, hero.FindAll(".tl-detail-watch-metadata-item").Count);
        var order = hero.FindAll(".tl-detail-hero__identity, .tl-detail-metadata-stack, .tl-detail-actions").Select(element => element.ClassName ?? string.Empty).ToList();
        Assert.Contains("tl-detail-hero__identity", order[0]);
        Assert.Contains("tl-detail-metadata-stack", order[1]);
        Assert.Contains("tl-detail-actions", order[2]);

        // One primary button; every round icon carries the short name the phone layout prints under it.
        Assert.Single(hero.FindAll(".tl-detail-primary-actions .tl-detail-action--primary"));
        var circles = hero.FindAll(".tl-detail-secondary-actions .hero-icon-circle");
        Assert.Contains(circles, circle => circle.GetAttribute("data-phone-label") == "My List");
        Assert.Contains(circles, circle => circle.GetAttribute("aria-label") == "More actions");
    }

    [Fact]
    public async Task MoreMenuOpensASheetOnPhoneListingTheSameActions()
    {
        await UseAsync("phone");
        DetailAction? chosen = null;
        var menu = Render<OverflowActionMenu>(parameters => parameters
            .Add(component => component.Actions, OverflowActions)
            .Add(component => component.ButtonClass, "hero-icon-circle")
            .Add(component => component.UsePlaybackGlyph, true)
            .Add(component => component.OnActionSelected, EventCallback.Factory.Create<DetailAction>(this, action => chosen = action)));

        var trigger = menu.Find("button.hero-icon-circle");
        Assert.Equal("dialog", trigger.GetAttribute("aria-haspopup"));
        Assert.Empty(menu.FindAll("dialog"));

        trigger.Click();

        Assert.Contains("app-native-dialog--sheet", menu.Find("dialog").ClassName);
        var items = menu.FindAll("[role='menuitem']");
        Assert.Equal(OverflowActions.Select(action => action.Label), items.Select(item => item.TextContent.Trim()));

        await items[1].ClickAsync(new());

        Assert.Equal("copy-link", chosen?.Key);
        menu.WaitForAssertion(() => Assert.Empty(menu.FindAll("dialog")));
    }

    [Fact]
    public async Task PhoneMoreSheetHasNoEditItemAndShowsOneDisabledNote()
    {
        await UseAsync("phone");
        DetailAction[] actions = [.. OverflowActions, Action("edit", "Edit details", "edit")];
        var menu = Render<OverflowActionMenu>(parameters => parameters
            .Add(component => component.Actions, actions)
            .Add(component => component.ButtonClass, "hero-icon-circle"));

        menu.Find("button.hero-icon-circle").Click();

        var labels = menu.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim()).ToList();
        Assert.DoesNotContain("Edit details", labels);
        Assert.Equal(1, labels.Count(label => label == "Edit on a computer"));
        var note = menu.FindAll("[role='menuitem']").Single(item => item.TextContent.Contains("Edit on a computer"));
        Assert.True(note.HasAttribute("disabled") || note.GetAttribute("aria-disabled") == "true");
    }

    [Fact]
    public async Task PhoneMoreMenuThatHeldOnlyEditDisappears()
    {
        await UseAsync("phone");
        var menu = Render<OverflowActionMenu>(parameters => parameters
            .Add(component => component.Actions, [Action("edit", "Edit details", "edit")]));

        Assert.Empty(menu.FindAll("button"));
    }

    [Fact]
    public async Task DesktopWithAMouseKeepsTheEditItemInTheMoreMenu()
    {
        await UseAsync("desktop");
        var menu = Render<OverflowActionMenu>(parameters => parameters
            .Add(component => component.Actions, [Action("edit", "Edit details", "edit")]));

        menu.Find("button[aria-label='More actions']").Click();

        Assert.Contains(menu.FindAll("[role='menuitem']"), item => item.TextContent.Contains("Edit details"));
    }

    [Fact]
    public async Task TabletShowsNothingAboutEditingInTheMoreMenu()
    {
        await UseAsync("tablet");
        var onlyEdit = Render<OverflowActionMenu>(parameters => parameters
            .Add(component => component.Actions, [Action("edit", "Edit details", "edit")]));
        Assert.Empty(onlyEdit.FindAll("button"));

        var mixed = Render<OverflowActionMenu>(parameters => parameters
            .Add(component => component.Actions, [.. OverflowActions, Action("edit", "Edit details", "edit")]));
        mixed.Find("button[aria-label='More actions']").Click();

        var labels = mixed.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim()).ToList();
        Assert.Equal(OverflowActions.Select(action => action.Label), labels);
        Assert.DoesNotContain("Edit on a computer", mixed.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreMenuKeepsThePopoverOnDesktop()
    {
        await UseAsync("desktop");
        var menu = Render<OverflowActionMenu>(parameters => parameters
            .Add(component => component.Actions, OverflowActions)
            .Add(component => component.ButtonClass, "hero-icon-circle"));

        var trigger = menu.Find("button.hero-icon-circle");
        Assert.Equal("menu", trigger.GetAttribute("aria-haspopup"));

        trigger.Click();

        Assert.Empty(menu.FindAll("dialog"));
        Assert.Equal(OverflowActions.Length, menu.FindAll("[role='menuitem']").Count);
    }

    [Fact]
    public async Task SectionTabsAttachTheEdgeHintAndBringTheActiveTabIntoView()
    {
        var module = JSInterop.SetupModule("./js/scroll-edge-hint.js");
        module.Mode = JSRuntimeMode.Loose;
        await UseAsync("phone");
        IReadOnlyList<SurfaceTabItem> items =
        [
            new("overview", "Overview"),
            new("cast", "Cast"),
            new("credits", "Credits"),
            new("details", "Details"),
        ];

        var bar = Render<SurfaceTabBar>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.ActiveKey, "overview"));

        Assert.Single(module.Invocations, invocation => invocation.Identifier == "attach");
        Assert.Equal(1, module.Invocations.Count(invocation => invocation.Identifier == "showActive"));

        bar.Render(parameters => parameters.Add(component => component.ActiveKey, "details"));

        Assert.Equal(2, module.Invocations.Count(invocation => invocation.Identifier == "showActive"));
        Assert.Single(module.Invocations, invocation => invocation.Identifier == "attach");
        Assert.Equal("[aria-selected=\"true\"]", module.Invocations.Last(invocation => invocation.Identifier == "showActive").Arguments[1]);
    }

    [Fact]
    public void CreditsGridKeepsItsDesktopColumnsAndHasAPhoneOverride()
    {
        var detailPage = Read("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");
        var appCss = Read("src/MediaEngine.Web/wwwroot/app.css");

        // The desktop rule has a 24rem minimum column, which cannot fit a 360px screen.
        Assert.Contains("::deep .tl-media-overview-credits-list--video {", detailPage);
        Assert.Contains("minmax(24rem, 1fr)", detailPage);

        var phoneRule = RuleFor(appCss, "html[data-form-factor=\"phone\"] .tl-detail-page .tl-media-overview-credits-list--video");
        Assert.Contains("grid-template-columns: minmax(0, 1fr)", phoneRule);
    }

    [Fact]
    public void PhoneDetailRulesAreScopedToThePhoneFormFactor()
    {
        var appCss = Read("src/MediaEngine.Web/wwwroot/app.css");
        var marker = appCss.IndexOf("/* Details page, phone pass", StringComparison.Ordinal);
        Assert.True(marker > 0, "The phone details block is missing from app.css.");
        var block = Regex.Replace(appCss[marker..], @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        // No media-query or unscoped rule may slip into the phone block: tablet, desktop and non-detail pages keep their own layout.
        var selectors = block
            .Split('{')
            .Select(part => part.Split('}').Last().Trim())
            .Where(part => part.Length > 0);
        foreach (var selector in selectors)
        {
            foreach (var single in selector.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0))
            {
                var scoped = single.StartsWith("html[data-form-factor=\"phone\"] .tl-detail-page", StringComparison.Ordinal)
                    || single.StartsWith(".tl-detail-more-sheet", StringComparison.Ordinal);
                Assert.True(scoped, $"Unscoped selector in the phone details block: {single}");
            }
        }

        // The hero shrinks to its content through a variable the phone block sets and the shared hero surface reads.
        Assert.Contains("--cinematic-hero-phone-min-height", appCss);
        Assert.Contains("var(--cinematic-hero-phone-min-height, 100svh)", Read("src/MediaEngine.Web/Components/Cinematic/CinematicHeroSurface.razor.css"));
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static string RuleFor(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Rule not found: {selector}");
        var open = css.IndexOf('{', start);
        var close = css.IndexOf('}', open);
        return css[(open + 1)..close];
    }
}
