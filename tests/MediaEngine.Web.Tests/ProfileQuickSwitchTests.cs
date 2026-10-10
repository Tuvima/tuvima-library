using Bunit;
using MediaEngine.Web.Components.Navigation;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>The top-bar quick switch: who is listed, what a PIN profile asks for, and that it adds no page requests.</summary>
public sealed class ProfileQuickSwitchTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static ProfileViewModel Person(string name, bool pin = false, bool kids = false) => new(
        Guid.NewGuid(), name, "#3B82F6", kids ? "RestrictedProfile" : "StandardUser", DateTimeOffset.UtcNow, HasPin: pin);

    private static void AddServices(BunitContext ctx)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = EngineApiClientStub.Create(_ => { });
        ctx.Services.AddSingleton<ActiveProfileSessionService>(provider => new ActiveProfileSessionService(
            provider.GetRequiredService<IJSRuntime>(), api));
        ctx.Services.AddSingleton<UIOrchestratorService>(provider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            provider.GetRequiredService<ActiveProfileSessionService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
    }

    private static IRenderedComponent<TopNavProfileSwitcher> Render(BunitContext ctx, Guid? active, params ProfileViewModel[] profiles)
    {
        AddServices(ctx);
        return ctx.Render<TopNavProfileSwitcher>(parameters => parameters
            .Add(p => p.ActiveProfileId, active)
            .Add(p => p.Profiles, profiles));
    }

    [Fact]
    public void AloneInTheHousehold_NothingIsListed()
    {
        using var ctx = new BunitContext();
        var only = Person("Maya");

        var cut = Render(ctx, only.Id, only);

        Assert.Empty(cut.FindAll(".profile-switcher"));
    }

    [Fact]
    public void WithOthers_ListsEveryoneButTheActiveProfile_WithKidsAndLockMarks()
    {
        using var ctx = new BunitContext();
        var me = Person("Maya");
        var dad = Person("Dad", pin: true);
        var kid = Person("Sam", kids: true);

        var cut = Render(ctx, me.Id, me, dad, kid);

        var items = cut.FindAll(".profile-switcher__item");
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, item => item.TextContent.Contains("Maya", StringComparison.Ordinal));
        Assert.Contains("Switch to Dad, needs a PIN", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Kids", cut.Find(".profile-switcher__tag").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingAProfileWithAPin_ShowsTheNumberPad_AndBackReturnsToTheList()
    {
        using var ctx = new BunitContext();
        var me = Person("Maya");
        var dad = Person("Dad", pin: true);

        var cut = Render(ctx, me.Id, me, dad);
        cut.Find(".profile-switcher__item").Click();

        Assert.NotEmpty(cut.FindAll(".pin-pad"));
        Assert.Empty(cut.FindAll(".profile-switcher__item"));

        cut.Find(".profile-switcher__back").Click();

        Assert.Empty(cut.FindAll(".pin-pad"));
        Assert.Single(cut.FindAll(".profile-switcher__item"));
    }

    [Fact]
    public void AccountMenu_OffersTheSwitcherOnlyWithSeveralProfiles_AndKeepsTheFullPickerLink()
    {
        var menu = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Navigation", "TopNavAccountMenu.razor"));
        var switcher = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Navigation", "TopNavProfileSwitcher.razor"));
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Navigation", "TopNavProfileSwitcher.razor.css"));

        Assert.Contains("@if (Profiles.Count > 1)", menu, StringComparison.Ordinal);
        Assert.Contains("<TopNavProfileSwitcher", menu, StringComparison.Ordinal);
        Assert.Contains("Who's watching?", menu, StringComparison.Ordinal);
        Assert.Contains("ProfilePickerRoute.Path", menu, StringComparison.Ordinal);

        // It reuses the list the page shell already loaded: no Engine reads of its own, and the same PIN pad and switch rules as the picker.
        Assert.DoesNotContain("GetProfilesAsync", switcher, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshProfilesAsync", switcher, StringComparison.Ordinal);
        Assert.Contains("<PinPad", switcher, StringComparison.Ordinal);
        Assert.Contains("Orchestrator.SetActiveProfileAsync(profile.Id, pin)", switcher, StringComparison.Ordinal);
        Assert.Contains("ProfileSwitchStatus.TooManyAttempts", switcher, StringComparison.Ordinal);

        Assert.DoesNotContain("::deep", css, StringComparison.Ordinal);
        Assert.DoesNotContain("!important", css, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", switcher, StringComparison.Ordinal);
    }
}
