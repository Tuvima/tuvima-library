using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackSleepTimerControlTests : AsyncBunitContext
{
    public PlaybackSleepTimerControlTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
    }

    [Fact]
    public async Task TimerMenuKeepsTheChosenPresetAndSendsOnlyExplicitSelections()
    {
        Render<AppPopoverHost>();
        AudiobookSleepTimerSelectionDto? selected = null;
        bool? openRequest = null;
        var cut = Render<PlaybackSleepTimerControl>(parameters => parameters
            .Add(component => component.State, new AudiobookSleepTimerStateDto
            {
                Mode = AudiobookSleepTimerModes.Timer,
                ChosenMinutes = 15,
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(11).AddSeconds(20),
            })
            .Add(component => component.Availability, new AudiobookSleepTimerAvailabilityDto
            {
                CanEndCurrent = false,
                CanEndNext = false,
                CurrentUnavailableReason = "This chapter has no verified end.",
                NextUnavailableReason = "A timed next chapter is not available.",
            })
            .Add(component => component.OptionsMinutes, new[] { 90 })
            .Add(component => component.Open, false)
            .Add(component => component.OpenChanged, EventCallback.Factory.Create<bool>(this, value => openRequest = value))
            .Add(component => component.SelectionChanged, EventCallback.Factory.Create<AudiobookSleepTimerSelectionDto>(this, value => selected = value)));

        var select = cut.FindComponent<AppSelect>().Instance;
        Assert.Equal("minutes:15", select.Value);
        var options = select.Options!;
        Assert.Equal("Off", options[0].Label);
        var firstTimedOption = Assert.Single(options, option => option.Value == "minutes:15");
        Assert.Equal("15 min", firstTimedOption.Label);
        Assert.True(firstTimedOption.SeparatorBefore);
        Assert.Contains(select.Options!, option => option.Value == "minutes:90" && option.Label == "90 min");
        var endCurrent = Assert.Single(options, option => option.Value == "end-current");
        Assert.Equal("End of chapter", endCurrent.Label);
        Assert.Equal(AppMaterialIcons.Outlined.MenuBook, endCurrent.Icon);
        Assert.True(endCurrent.Disabled);
        Assert.True(endCurrent.SeparatorBefore);
        Assert.Equal("This chapter has no verified end.", endCurrent.Title);
        Assert.Equal("End of chapter unavailable: This chapter has no verified end.", endCurrent.AccessibleLabel);
        var endNext = Assert.Single(options, option => option.Value == "end-next");
        Assert.Equal("End of next chapter", endNext.Label);
        Assert.Equal(AppMaterialIcons.Outlined.SkipNext, endNext.Icon);
        Assert.True(endNext.Disabled);
        Assert.Equal("A timed next chapter is not available.", endNext.Title);
        Assert.Equal("End of next chapter unavailable: A timed next chapter is not available.", endNext.AccessibleLabel);
        Assert.Contains("15 minutes selected, 12 minutes remaining", select.AriaLabel);

        await cut.InvokeAsync(() => select.OpenChanged.InvokeAsync(true));
        Assert.True(openRequest);
        Assert.Null(selected);

        await cut.InvokeAsync(() => select.ValueChanged.InvokeAsync("minutes:45"));
        Assert.Equal(AudiobookSleepTimerModes.Timer, selected?.Mode);
        Assert.Equal(45, selected?.Minutes);
        Assert.False(openRequest);
    }

    [Fact]
    public async Task RejectedOwnerChoiceRebindsTheVisibleSelectionToAuthoritativeOffState()
    {
        Render<AppPopoverHost>();
        var cut = Render<PlaybackSleepTimerControl>(parameters => parameters
            .Add(component => component.State, new AudiobookSleepTimerStateDto
            {
                Mode = AudiobookSleepTimerModes.Off,
                TimerGeneration = 8,
            })
            .Add(component => component.Availability, new AudiobookSleepTimerAvailabilityDto
            {
                CanEndCurrent = true,
                CurrentChapterTitle = "Chapter 1",
            })
            .Add(component => component.OpenChanged, EventCallback.Factory.Create<bool>(this, _ => { }))
            .Add(component => component.SelectionChanged, EventCallback.Factory.Create<AudiobookSleepTimerSelectionDto>(this, _ => { })));

        var selectComponent = cut.FindComponent<AppSelect>();
        var before = selectComponent.Instance;
        Assert.Equal(AudiobookSleepTimerModes.Off, before.Value);

        await cut.InvokeAsync(() => before.ValueChanged.InvokeAsync(AudiobookSleepTimerModes.EndCurrent));

        var rebound = cut.FindComponent<AppSelect>().Instance;
        Assert.Same(before, rebound);
        Assert.Equal(AudiobookSleepTimerModes.Off, rebound.Value);
        Assert.Contains("Sleep timer: Off.", rebound.AriaLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturedChapterTimerUsesTheOwnersVerifiedTargetAsItsSelectedChoice()
    {
        Render<AppPopoverHost>();
        var cut = Render<PlaybackSleepTimerControl>(parameters => parameters
            .Add(component => component.State, new AudiobookSleepTimerStateDto
            {
                Mode = AudiobookSleepTimerModes.EndNext,
                TargetChapterTitle = "Chapter 8",
                TimerGeneration = 4,
            })
            .Add(component => component.Availability, new AudiobookSleepTimerAvailabilityDto
            {
                CanEndCurrent = true,
                CanEndNext = true,
                CurrentChapterTitle = "Chapter 7",
                NextChapterTitle = "Chapter 8",
            }));

        var select = cut.FindComponent<AppSelect>().Instance;
        Assert.Equal(AudiobookSleepTimerModes.EndNext, select.Value);
        Assert.Contains(select.Options!, option => option.Value == AudiobookSleepTimerModes.EndCurrent
            && !option.Disabled && option.Label == "End of chapter");
        Assert.Contains(select.Options!, option => option.Value == AudiobookSleepTimerModes.EndNext
            && !option.Disabled && option.Label == "End of next chapter");
        Assert.Equal("Sleep timer: End of next chapter: Chapter 8. Choose when playback stops.", select.AriaLabel);
    }
}
