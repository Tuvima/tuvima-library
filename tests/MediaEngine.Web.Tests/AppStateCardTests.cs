using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Settings;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

/// <summary>The one state card every plugin, integration, tester and the Local AI engine is shown with.</summary>
public sealed class AppStateCardTests : AsyncBunitContext
{
    public AppStateCardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Card_ShowsNameKindHealthAllowedToAndWhetherDataLeaves()
    {
        var cut = Render<AppStateCard>(parameters => parameters
            .Add(p => p.Name, "Intro skipper")
            .Add(p => p.Kind, StateCardKind.Plugin)
            .Add(p => p.Health, HealthWord.NeedsAttention)
            .Add(p => p.AllowedTo, ["Reads your media files", "Uses Local AI"])
            .Add(p => p.SendsDataOut, true)
            .Add(p => p.SendsDataTo, "the internet"));

        Assert.Equal("Intro skipper", cut.Find(".app-state-card__name").TextContent);
        Assert.Equal("Plugin", cut.Find(".app-state-card__kind").TextContent);
        Assert.Equal("Needs attention", cut.Find(".app-state-card__health").TextContent.Trim());
        Assert.Equal(["Reads your media files", "Uses Local AI"], cut.FindAll(".app-state-card__list li").Select(item => item.TextContent));
        Assert.Equal("Yes, to the internet", cut.Find("[data-testid=sends-data]").TextContent.Trim());
        Assert.Equal("needsattention", cut.Find("[data-state-card]").GetAttribute("data-health"));
    }

    [Theory]
    [InlineData(StateCardKind.Plugin, "Plugin")]
    [InlineData(StateCardKind.Integration, "Integration")]
    [InlineData(StateCardKind.Tester, "Tester")]
    [InlineData(StateCardKind.LocalAiEngine, "Local AI engine")]
    public void Card_ShowsTheKindLabel(StateCardKind kind, string label)
    {
        var cut = Render<AppStateCard>(parameters => parameters.Add(p => p.Name, "Thing").Add(p => p.Kind, kind));

        Assert.Equal(label, cut.Find(".app-state-card__kind").TextContent);
    }

    [Theory]
    [InlineData(HealthWord.Working, "Working")]
    [InlineData(HealthWord.Off, "Off")]
    [InlineData(HealthWord.NeedsAttention, "Needs attention")]
    [InlineData(HealthWord.Broken, "Broken")]
    public void Card_WritesTheHealthWordAsText(HealthWord word, string text)
    {
        var cut = Render<AppStateCard>(parameters => parameters.Add(p => p.Name, "Thing").Add(p => p.Health, word));

        Assert.Equal(text, cut.Find(".app-state-card__health").TextContent.Trim());
    }

    [Fact]
    public void Card_WithNoLastActivity_SaysNoActivityYet()
    {
        var cut = Render<AppStateCard>(parameters => parameters.Add(p => p.Name, "Thing"));

        Assert.Equal("No activity yet", cut.Find("[data-testid=last-activity]").TextContent.Trim());
    }

    [Fact]
    public void Card_WithLastActivity_ShowsRelativeTimeWithTheAbsoluteTimeAsTooltip()
    {
        var when = DateTimeOffset.Now.AddHours(-3);

        var cut = Render<AppStateCard>(parameters => parameters.Add(p => p.Name, "Thing").Add(p => p.LastActivity, when));

        var activity = cut.Find("[data-testid=last-activity]");
        Assert.Equal("3 hours ago", activity.TextContent.Trim());
        Assert.False(string.IsNullOrWhiteSpace(activity.GetAttribute("title")));
    }

    [Fact]
    public void Card_HidesTheLastProblemWhenThereIsNone_AndShowsItWhenThereIs()
    {
        var quiet = Render<AppStateCard>(parameters => parameters.Add(p => p.Name, "Thing"));
        Assert.Empty(quiet.FindAll("[data-testid=last-problem]"));

        var problem = Render<AppStateCard>(parameters => parameters.Add(p => p.Name, "Thing").Add(p => p.LastProblem, "The manifest is missing."));
        Assert.Equal("The manifest is missing.", problem.Find("[data-testid=last-problem]").TextContent);
    }

    [Fact]
    public void Card_WhenLoading_ShowsQuietPlaceholdersInsteadOfHealthAndProblem()
    {
        var cut = Render<AppStateCard>(parameters => parameters
            .Add(p => p.Name, "Thing")
            .Add(p => p.Loading, true)
            .Add(p => p.LastProblem, "Should not show yet"));

        Assert.NotEmpty(cut.FindAll(".app-skeleton"));
        Assert.Empty(cut.FindAll(".app-state-card__health"));
        Assert.Empty(cut.FindAll("[data-testid=last-problem]"));
        Assert.Equal("Thing", cut.Find(".app-state-card__name").TextContent);
    }

    [Fact]
    public void Card_Switch_IsLabelledWithTheItemName_AndRaisesIsOnChanged()
    {
        bool? changedTo = null;
        var cut = Render<AppStateCard>(parameters => parameters
            .Add(p => p.Name, "Intro skipper")
            .Add(p => p.IsOn, false)
            .Add(p => p.IsOnChanged, EventCallback.Factory.Create<bool>(this, value => changedTo = value)));

        var toggle = cut.Find("input[role=switch]");
        Assert.Equal("Intro skipper", toggle.GetAttribute("aria-label"));

        toggle.Change(true);

        Assert.True(changedTo);
    }

    [Fact]
    public void Card_WithNoIsOn_HasNoSwitch()
    {
        var cut = Render<AppStateCard>(parameters => parameters.Add(p => p.Name, "Active model").Add(p => p.Kind, StateCardKind.LocalAiEngine));

        Assert.Empty(cut.FindAll("input[role=switch]"));
    }

    [Fact]
    public void Card_WithASwitchDisabledReason_DisablesTheSwitchAndExplainsWhy()
    {
        var changed = 0;
        var cut = Render<AppStateCard>(parameters => parameters
            .Add(p => p.Name, "Thing")
            .Add(p => p.IsOn, false)
            .Add(p => p.SwitchDisabledReason, "Needs a newer Library.")
            .Add(p => p.IsOnChanged, EventCallback.Factory.Create<bool>(this, _ => changed++)));

        Assert.True(cut.Find("input[role=switch]").HasAttribute("disabled"));
        Assert.Equal("Needs a newer Library.", cut.Find(".app-state-card__reason").TextContent);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Card_ForDataThatStaysHere_SaysNoAndWhereItStays()
    {
        var cut = Render<AppStateCard>(parameters => parameters
            .Add(p => p.Name, "Vibe tags")
            .Add(p => p.SendsDataOut, false)
            .Add(p => p.SendsDataTo, StateCardHealth.RunsOnThisMachine));

        Assert.Equal("No. Runs on this machine.", cut.Find("[data-testid=sends-data]").TextContent.Trim());
    }

    [Fact]
    public void Card_RendersTheBadgesAndTheOneActionButtonItWasGiven()
    {
        var cut = Render<AppStateCard>(parameters => parameters
            .Add(p => p.Name, "Thing")
            .Add(p => p.Badges, (RenderFragment)(builder => builder.AddContent(0, "Official")))
            .Add(p => p.Action, (RenderFragment)(builder => builder.AddContent(0, "Retry"))));

        Assert.Contains("Official", cut.Find(".app-state-card__badges").TextContent, StringComparison.Ordinal);
        Assert.Equal("Retry", cut.Find(".app-state-card__action").TextContent);
    }
}
