using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackPresentationPrimitiveTests : AsyncBunitContext
{
    public PlaybackPresentationPrimitiveTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Render<AppPopoverHost>();
    }

    [Theory]
    [InlineData(false, "false")]
    [InlineData(true, "true")]
    public void IconUtilitiesKeepStateNamesAndBothToggleStatesWithoutVisibleValues(bool active, string expected)
    {
        var control = new PlaybackControlDefinition(PlaybackControlKey.Shuffle, "Shuffle", active ? "Shuffle on" : "Shuffle off",
            string.Empty, PlaybackControlPlacement.Utility, "shuffle", ValueText: "visible value", BadgeText: "badge", IsActive: active);
        var cut = Render<PlaybackIconButton>(parameters => parameters.Add(component => component.Control, control).Add(component => component.Surface, "phone"));
        var button = cut.Find("button");
        Assert.Equal(control.AriaLabel, button.GetAttribute("aria-label"));
        Assert.Equal(expected, button.GetAttribute("aria-pressed"));
        Assert.Equal(string.Empty, button.TextContent.Trim());
        Assert.NotNull(button.QuerySelector("svg"));
        Assert.DoesNotContain("visible value", cut.Markup);
        Assert.DoesNotContain("badge", cut.Markup);
    }

    [Fact]
    public async Task ModalSheetGivesNestedSelectItsOwnPortalTokenAndClosesOnlyTheMenu()
    {
        var selected = "one";
        var closedSheets = 0;
        var cut = Render<PlaybackToolSheet>(parameters => parameters.Add(component => component.Modal, true)
            .Add(component => component.Title, "Lyrics").Add(component => component.OnClose, () => closedSheets++)
            .AddChildContent<AppSelect>(select => select.Add(component => component.Appearance, "playback-flat")
                .Add(component => component.Value, selected).Add(component => component.AriaLabel, "Lyrics version")
                .Add(component => component.Options, new[] { new AppSelectOption("one", "Preferred"), new AppSelectOption("two", "Alternate") })));
        var nested = cut.FindComponent<AppSelect>();
        Assert.Equal(cut.Find("section[role='dialog']").Id, nested.Instance.PlaybackPopoverOwner);
        Assert.NotEmpty(nested.Find(".app-field").GetAttribute("data-playback-parent-panel")!);
        await nested.Find(".tl-select-trigger").ClickAsync();
        Assert.Null(Services.GetRequiredService<PlaybackTransientToolCoordinator>().OpenToolId);
        await cut.InvokeAsync(() => nested.Instance.ClosePlaybackMenuAsync());
        Assert.Equal("one", nested.Instance.Value);
        Assert.Equal(0, closedSheets);
        await cut.Find("button[aria-label='Close Lyrics']").ClickAsync();
        Assert.Equal(1, closedSheets);
    }

    [Fact]
    public void OrdinaryUtilityIsNotPretendedToBeAToggle()
    {
        var cut = Render<PlaybackIconButton>(parameters => parameters.Add(component => component.Control,
            new PlaybackControlDefinition(PlaybackControlKey.Bookmarks, "Bookmark", "Add bookmark", string.Empty,
                PlaybackControlPlacement.ToolStrip, "bookmark", IsActive: true)));
        Assert.Null(cut.Find("button").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task ToolsAreMutuallyExclusiveAndItemChangeClosesOnlyTheTemporaryPanel()
    {
        var first = Render<PlaybackPopover>(parameters => parameters.Add(component => component.Title, "Queue")
            .Add(component => component.ItemKey, "song-one").AddChildContent("Queue contents"));
        var second = Render<PlaybackPopover>(parameters => parameters.Add(component => component.Title, "Lyrics").AddChildContent("Lyrics contents"));
        await first.InvokeAsync(() => first.Instance.OpenAsync(false));
        Assert.Contains("Queue contents", first.Markup);
        await second.InvokeAsync(() => second.Instance.OpenAsync(true));
        first.WaitForAssertion(() => Assert.DoesNotContain("Queue contents", first.Markup));
        Assert.Contains("Lyrics contents", second.Markup);
        await first.InvokeAsync(() => first.Instance.OpenAsync(true));
        first.Render(parameters => parameters.Add(component => component.ItemKey, "song-two"));
        first.WaitForAssertion(() => Assert.DoesNotContain("Queue contents", first.Markup));
        Assert.Null(Services.GetRequiredService<PlaybackTransientToolCoordinator>().OpenToolId);
    }

    [Fact]
    public async Task PhonePopoverUsesTheModalSharedSheetAndToolDismissalClosesOnlyItsContent()
    {
        var cut = Render<PlaybackPopover>(parameters => parameters.Add(component => component.Title, "Queue").AddChildContent("Upcoming songs"));
        await cut.InvokeAsync(() => cut.Instance.SetViewport(true));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));
        Assert.Equal("true", cut.Find("section[role='dialog']").GetAttribute("aria-modal"));
        await cut.Find("button[aria-label='Close Queue']").ClickAsync();
        Assert.DoesNotContain("Upcoming songs", cut.Markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserClosePreservesItsFocusIntentWhileReleasingThePortal(bool restoreFocus)
    {
        var module = JSInterop.SetupModule("./js/playback-popover.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = Render<PlaybackPopover>(parameters => parameters.Add(component => component.Title, "Queue")
            .AddChildContent("Upcoming songs"));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-playback-popover-panel]")));
        var release = module.SetupVoid("update", call => call.Arguments.Count == 5 && Equals(call.Arguments[2], false));
        var closing = cut.InvokeAsync(() => cut.Instance.CloseFromBrowserAsync(restoreFocus));
        cut.WaitForAssertion(() => Assert.Single(release.Invocations));
        var invocation = release.Invocations.Single();
        Assert.Equal(restoreFocus, invocation.Arguments[4]);
        Assert.Single(cut.FindAll("[data-playback-popover-panel]")); // Blazor must wait for portal release.
        release.SetVoidResult();
        await closing;
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-playback-popover-panel]")));
        Assert.Null(Services.GetRequiredService<PlaybackTransientToolCoordinator>().OpenToolId);
    }

    [Fact]
    public async Task PhoneSheetRemainsRenderedUntilJavascriptRestoresItsPortalBeforeClose()
    {
        var module = JSInterop.SetupModule("./js/playback-popover.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = Render<PlaybackPopover>(parameters => parameters.Add(component => component.Title, "Chapters")
            .AddChildContent("Actual chapters"));
        await cut.InvokeAsync(() => cut.Instance.SetViewport(true));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("section[aria-modal='true']")));
        var release = module.SetupVoid("update", call => call.Arguments.Count >= 3 && Equals(call.Arguments[2], false));
        var closing = cut.Find("button[aria-label='Close Chapters']").ClickAsync();
        cut.WaitForAssertion(() => Assert.NotEmpty(release.Invocations));
        Assert.NotEmpty(cut.FindAll("section[aria-modal='true']"));
        release.SetVoidResult();
        await closing;
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("section[aria-modal='true']")));
    }

    [Fact]
    public void VideoPrimaryAndSelectedRowsUseExplicitAppearanceWithoutChangingDockDefaults()
    {
        var video = Render<PlaybackPrimaryButton>(parameters => parameters.Add(component => component.Appearance, "video"));
        Assert.Contains("playback-primary-button-shell--appearance-video", video.Markup);
        var dock = Render<PlaybackPrimaryButton>(parameters => parameters.Add(component => component.Appearance, "dock"));
        Assert.DoesNotContain("appearance-video", dock.Markup);
        var selected = Render<PlaybackSheetRow>(parameters => parameters.Add(component => component.Appearance, "outlined")
            .Add(component => component.IsActive, true).AddChildContent("English"));
        Assert.Contains("playback-sheet-row-shell--outlined", selected.Markup);
        Assert.Equal("true", selected.Find("button").GetAttribute("aria-current"));
    }

    [Fact]
    public void CoordinatorIgnoresPositionTicksButClosesOnNewPlaybackGeneration()
    {
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new() { Queue = [new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music" }], CurrentIndex = 0 });
        using var tools = new PlaybackTransientToolCoordinator(playback);
        tools.Open("queue", true);
        playback.UpdateTransportState(currentTimeSeconds: 42, isPlaying: true);
        Assert.Equal("queue", tools.OpenToolId);
        playback.ReservePlaybackRequest();
        playback.UpdateTransportState(currentTimeSeconds: 43, isPlaying: false);
        Assert.Null(tools.OpenToolId);
    }

    [Fact]
    public void SpeedAndSleepTriggersAreIconsWhileTheirAccessibleNamesRetainCurrentState()
    {
        var speed = Render<PlaybackSpeedControl>(parameters => parameters.Add(component => component.Value, 1.75));
        var select = speed.FindComponent<PlaybackPopover>().Instance;
        Assert.NotNull(select.TriggerContent);
        Assert.Contains("1.75x", select.Title);
        var sleep = Render<PlaybackSleepTimerControl>(parameters => parameters.Add(component => component.State,
            new AudiobookSleepTimerStateDto { Mode = AudiobookSleepTimerModes.EndNext, TargetChapterTitle = "Part two" }));
        Assert.Contains("End of next chapter: Part two", sleep.FindComponent<AppSelect>().Instance.AriaLabel);
        Assert.DoesNotContain("playback-sleep-timer__trigger-status", sleep.Markup);
    }

    [Fact]
    public async Task ControlledSpeedMenuCanReopenAfterAnotherToolForcesItClosed()
    {
        var cut = Render<PlaybackSpeedControl>(parameters => parameters.Add(component => component.Open, true));
        var tools = Services.GetRequiredService<PlaybackTransientToolCoordinator>();
        Assert.True(cut.FindComponent<PlaybackPopover>().Find("button").GetAttribute("aria-expanded") == "true");
        await cut.InvokeAsync(() => tools.Open("queue", true));
        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<PlaybackPopover>().Find("button").GetAttribute("aria-expanded") == "true"));
        cut.Render(parameters => parameters.Add(component => component.Open, false));
        cut.Render(parameters => parameters.Add(component => component.Open, true));
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<PlaybackPopover>().Find("button").GetAttribute("aria-expanded") == "true"));
        Assert.NotEqual("queue", tools.OpenToolId);
    }
}
