using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Unified player v2 D16 and acceptance A4: the Sleep timer panel (presets, End of chapter, Custom duration, the live countdown, "+5 minutes" and
/// "Cancel timer") against the real components. A test clock stands in for the time provider, so the countdown is worked out from the deadline the
/// session holds and the one-second tick is fired by hand.
/// </summary>
public sealed class SleepTimerPanelTests : AsyncBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 22, 0, 0, TimeSpan.Zero);

    private sealed class TestClock : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State, TimeSpan Period)> _timers = [];
        public DateTimeOffset Now { get; set; } = SleepTimerPanelTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var entry = (callback, state, period);
            _timers.Add(entry);
            if (period == TimeSpan.FromSeconds(1))
            {
                LastTick = () => callback(state);
            }

            return new ManualTimer(() => _timers.Remove(entry));
        }

        /// <summary>Moves the clock on and fires the one-second ticks of the open countdowns.</summary>
        public void Advance(TimeSpan by)
        {
            Now += by;
            foreach (var timer in _timers.Where(timer => timer.Period == TimeSpan.FromSeconds(1)).ToList())
            {
                timer.Callback(timer.State);
            }
        }

        /// <summary>The newest one-second tick, kept after its timer is disposed so a test can fire a tick that was already on its way.</summary>
        public Action? LastTick { get; private set; }

        public int RunningTicks => _timers.Count(timer => timer.Period == TimeSpan.FromSeconds(1));
    }

    private sealed class ManualTimer(Action onDispose) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() => onDispose();
        public ValueTask DisposeAsync() { onDispose(); return ValueTask.CompletedTask; }
    }

    private readonly TestClock _clock = new();
    private readonly List<AudiobookSleepTimerSelectionDto> _selections = [];

    public SleepTimerPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Render<AppPopoverHost>();
    }

    private static AudiobookSleepTimerStateDto Running(TimeSpan remaining, int chosenMinutes = 15) => new()
    {
        Mode = AudiobookSleepTimerModes.Timer,
        ChosenMinutes = chosenMinutes,
        DeadlineUtc = Now + remaining,
        TimerGeneration = 3,
    };

    private static IReadOnlyList<PlaybackChapterDto> Chapters(int count) => Enumerable.Range(0, count)
        .Select(index => new PlaybackChapterDto { Index = index, Title = $"Chapter {index + 1}", StartSeconds = index * 600d, EndSeconds = (index + 1) * 600d })
        .ToList();

    private async Task<IRenderedComponent<SleepTimerPanel>> OpenAsync(
        AudiobookSleepTimerStateDto? state = null,
        IReadOnlyList<PlaybackChapterDto>? chapters = null,
        AudiobookSleepTimerAvailabilityDto? availability = null,
        bool offersEndOfChapter = true)
    {
        var cut = Render<SleepTimerPanel>(parameters => parameters
            .Add(component => component.State, state ?? new AudiobookSleepTimerStateDto())
            .Add(component => component.Chapters, chapters ?? [])
            .Add(component => component.Availability, availability ?? new AudiobookSleepTimerAvailabilityDto { CanEndCurrent = true })
            .Add(component => component.OffersEndOfChapter, offersEndOfChapter)
            .Add(component => component.SelectionChanged, EventCallback.Factory.Create<AudiobookSleepTimerSelectionDto>(this, selection => _selections.Add(selection))));
        var panel = cut.FindComponent<PlaybackPanel>();
        await panel.InvokeAsync(() => panel.Instance.OpenAsync(true));
        cut.WaitForElement("[data-sleep-timer-view]");
        return cut;
    }

    private static string[] SelectedPresets(IRenderedComponent<SleepTimerPanel> cut) =>
        cut.FindAll("[data-sleep-preset][aria-checked='true']").Select(preset => preset.GetAttribute("data-sleep-preset")!).ToArray();

    // ---- Header, presets and the two rows (mockup screen 6) ----

    [Fact]
    public async Task Panel_HasTheHeaderSleepTimerWithAMoonBeforeTheTitleAndNoFooterWhileNoTimerRuns()
    {
        var cut = await OpenAsync();

        Assert.Equal("Sleep timer", cut.Find(".playback-panel__title").TextContent);
        Assert.NotEmpty(cut.FindAll(".playback-panel__heading .stp__moon"));
        Assert.Empty(cut.FindAll("[data-sleep-footer]"));
        Assert.Empty(cut.FindAll("[data-sleep-countdown]"));
        Assert.Equal("Choose when playback should stop.", cut.Find("[data-sleep-idle]").TextContent);
    }

    [Fact]
    public async Task Panel_OffersTheFourPresetsInOrderAndNoneIsFilledWhileNoTimerRuns()
    {
        var cut = await OpenAsync();

        Assert.Equal(["15 min", "30 min", "45 min", "60 min"], cut.FindAll("[data-sleep-preset]").Select(preset => preset.TextContent.Trim()));
        Assert.Empty(SelectedPresets(cut));
        Assert.Equal("radiogroup", cut.Find(".stv__presets").GetAttribute("role"));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    public async Task Preset_StartsATimerOfThatLengthAndKeepsThePanelOpen(int minutes)
    {
        var cut = await OpenAsync();

        await cut.Find($"[data-sleep-preset='{minutes}']").ClickAsync();

        var selection = Assert.Single(_selections);
        Assert.Equal(AudiobookSleepTimerModes.Timer, selection.Mode);
        Assert.Equal(minutes, selection.Minutes);
        Assert.NotNull(cut.Find("[data-sleep-timer-view]"));
    }

    [Fact]
    public async Task Preset_RestartsARunningTimerWhenChosenAgain()
    {
        var cut = await OpenAsync(Running(TimeSpan.FromMinutes(4), chosenMinutes: 15));

        await cut.Find("[data-sleep-preset='15']").ClickAsync();

        Assert.Equal(15, Assert.Single(_selections).Minutes);
    }

    [Fact]
    public async Task Preset_TheRunningPresetIsFilledAndACustomLengthFillsNone()
    {
        var cut = await OpenAsync(Running(TimeSpan.FromMinutes(14), chosenMinutes: 15));
        Assert.Equal(["15"], SelectedPresets(cut));

        cut.Render(parameters => parameters.Add(component => component.State, Running(TimeSpan.FromMinutes(80), chosenMinutes: 90)));
        Assert.Empty(SelectedPresets(cut));
    }

    // ---- End of chapter ----

    [Fact]
    public async Task EndOfChapter_IsOfferedOnlyForABookWithChapters()
    {
        var withChapters = await OpenAsync(chapters: Chapters(8));
        Assert.Equal("End of chapter", withChapters.Find("[data-sleep-row='end-of-chapter'] .stv__label").TextContent);

        var withoutChapters = await OpenAsync(chapters: []);
        Assert.Empty(withoutChapters.FindAll("[data-sleep-row='end-of-chapter']"));
        Assert.NotEmpty(withoutChapters.FindAll("[data-sleep-row='custom']"));
    }

    [Fact]
    public async Task EndOfChapter_IsHiddenWhenTheProfileTurnedChapterStopsOff()
    {
        var cut = await OpenAsync(chapters: Chapters(8), offersEndOfChapter: false);

        Assert.Empty(cut.FindAll("[data-sleep-row='end-of-chapter']"));
    }

    [Fact]
    public async Task EndOfChapter_SetsAnEndOfChapterTimer()
    {
        var cut = await OpenAsync(chapters: Chapters(8));

        await cut.Find("[data-sleep-row='end-of-chapter']").ClickAsync();

        Assert.Equal(AudiobookSleepTimerModes.EndCurrent, Assert.Single(_selections).Mode);
    }

    [Fact]
    public async Task EndOfChapter_IsDisabledWithTheReasonWhenNoVerifiedBoundaryExists()
    {
        var cut = await OpenAsync(chapters: Chapters(8), availability: new AudiobookSleepTimerAvailabilityDto { CanEndCurrent = false, CurrentUnavailableReason = "The chapter timing could not be verified" });

        var row = cut.Find("[data-sleep-row='end-of-chapter']");
        Assert.True(row.HasAttribute("disabled"));
        Assert.Equal("End of chapter, unavailable: The chapter timing could not be verified", row.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task EndOfChapterTimer_SaysWhichChapterItStopsAtInsteadOfShowingACountdown()
    {
        var chapters = Chapters(24);
        var state = new AudiobookSleepTimerStateDto { Mode = AudiobookSleepTimerModes.EndCurrent, TargetChapterIndex = 7, TargetChapterTitle = "Chapter 8", TimerGeneration = 4 };
        var cut = await OpenAsync(state, chapters);

        Assert.Equal("Stopping at the end of chapter 8", cut.Find("[data-sleep-stop]").TextContent);
        Assert.Empty(cut.FindAll("[data-sleep-countdown]"));
        Assert.Contains("Playback will pause automatically.", cut.Find("[data-sleep-status]").TextContent);
        Assert.Empty(SelectedPresets(cut));
    }

    [Fact]
    public async Task EndOfChapterTimer_FooterHasCancelButNothingToAddFiveMinutesTo()
    {
        var state = new AudiobookSleepTimerStateDto { Mode = AudiobookSleepTimerModes.EndCurrent, TargetChapterIndex = 7, TimerGeneration = 4 };
        var cut = await OpenAsync(state, Chapters(24));

        Assert.Empty(cut.FindAll("[data-sleep-extend]"));
        Assert.Equal("Cancel timer", cut.Find("[data-sleep-cancel]").TextContent.Trim());
    }

    // ---- Custom duration ----

    private static async Task<IRenderedComponent<SleepTimerCustomView>> OpenCustomAsync(IRenderedComponent<SleepTimerPanel> cut)
    {
        await cut.Find("[data-sleep-row='custom']").ClickAsync();
        return cut.FindComponent<SleepTimerCustomView>();
    }

    [Fact]
    public async Task CustomDuration_SlidesInAsANestedViewWithABackArrowAndAMinutesField()
    {
        var cut = await OpenAsync();

        await OpenCustomAsync(cut);

        Assert.Equal("Custom duration", cut.Find(".playback-panel__title").TextContent);
        Assert.NotEmpty(cut.FindAll(".playback-panel__back"));
        Assert.Equal("Minutes", cut.Find(".stc__label").TextContent);
        var field = cut.Find("[data-sleep-custom-input]");
        Assert.Equal("1", field.GetAttribute("min"));
        Assert.Equal("720", field.GetAttribute("max"));
        Assert.Equal("Set timer", cut.Find("[data-sleep-custom-set]").TextContent.Trim());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("721")]
    [InlineData("-5")]
    [InlineData("12.5")]
    [InlineData("1e2")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    public async Task CustomDuration_RefusesAnythingButWholeMinutesFromOneTo720AndSaysWhy(string text)
    {
        var cut = await OpenAsync();
        await OpenCustomAsync(cut);

        await cut.Find("[data-sleep-custom-input]").InputAsync(new ChangeEventArgs { Value = text });
        await cut.Find("[data-sleep-custom-set]").ClickAsync();

        Assert.Empty(_selections);
        Assert.Equal("That isn't a whole number from 1 to 720.", cut.Find("[data-sleep-custom-error]").TextContent);
        Assert.Equal("alert", cut.Find("[data-sleep-custom-error]").GetAttribute("role"));
        Assert.Equal("true", cut.Find("[data-sleep-custom-input]").GetAttribute("aria-invalid"));
        Assert.Equal("Custom duration", cut.Find(".playback-panel__title").TextContent);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("90", 90)]
    [InlineData(" 45 ", 45)]
    [InlineData("720", 720)]
    public async Task CustomDuration_SetButtonStartsTheTimerAndReturnsToTheCountdownView(string text, int minutes)
    {
        var cut = await OpenAsync();
        await OpenCustomAsync(cut);

        await cut.Find("[data-sleep-custom-input]").InputAsync(new ChangeEventArgs { Value = text });
        await cut.Find("[data-sleep-custom-set]").ClickAsync();

        var selection = Assert.Single(_selections);
        Assert.Equal(AudiobookSleepTimerModes.Timer, selection.Mode);
        Assert.Equal(minutes, selection.Minutes);
        Assert.Equal("Sleep timer", cut.Find(".playback-panel__title").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-sleep-timer-view]"));
    }

    [Fact]
    public async Task CustomDuration_EnterInTheFieldDoesWhatTheSetButtonDoes()
    {
        var cut = await OpenAsync();
        await OpenCustomAsync(cut);

        await cut.Find("[data-sleep-custom-input]").InputAsync(new ChangeEventArgs { Value = "75" });
        await cut.Find("[data-sleep-custom-input]").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(75, Assert.Single(_selections).Minutes);
    }

    [Fact]
    public async Task CustomDuration_EditingTheFieldClearsTheError()
    {
        var cut = await OpenAsync();
        await OpenCustomAsync(cut);
        await cut.Find("[data-sleep-custom-input]").InputAsync(new ChangeEventArgs { Value = "0" });
        await cut.Find("[data-sleep-custom-set]").ClickAsync();
        Assert.NotEmpty(cut.FindAll("[data-sleep-custom-error]"));

        await cut.Find("[data-sleep-custom-input]").InputAsync(new ChangeEventArgs { Value = "5" });

        Assert.Empty(cut.FindAll("[data-sleep-custom-error]"));
        Assert.Null(cut.Find("[data-sleep-custom-input]").GetAttribute("aria-invalid"));
    }

    // ---- The countdown (A4) ----

    [Theory]
    [InlineData(872d, "14:32")]
    [InlineData(3872d, "1:04:32")]
    [InlineData(3600d, "1:00:00")]
    [InlineData(59d, "0:59")]
    [InlineData(0d, "0:00")]
    [InlineData(0.2d, "0:01")]
    [InlineData(59.5d, "1:00")]
    [InlineData(-4d, "0:00")]
    public void Countdown_IsMinutesAndSecondsAndHoursFromAnHour(double seconds, string expected)
    {
        Assert.Equal(expected, PlaybackSleepTimerChoices.FormatCountdown(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task Countdown_ShowsStoppingInTheTimeLeftAndTheAutomaticPauseLine()
    {
        var cut = await OpenAsync(Running(TimeSpan.FromSeconds(872)));

        Assert.Equal("Stopping in", cut.Find(".stv__caption").TextContent);
        Assert.Equal("14:32", cut.Find("[data-sleep-countdown]").TextContent);
        Assert.Equal("timer", cut.Find("[data-sleep-countdown]").GetAttribute("role"));
        Assert.Contains("Playback will pause automatically.", cut.Find("[data-sleep-status]").TextContent);
    }

    [Fact]
    public async Task Countdown_TicksEverySecondFromTheDeadline()
    {
        var cut = await OpenAsync(Running(TimeSpan.FromSeconds(872)));

        _clock.Advance(TimeSpan.FromSeconds(1));
        cut.WaitForAssertion(() => Assert.Equal("14:31", cut.Find("[data-sleep-countdown]").TextContent));
        _clock.Advance(TimeSpan.FromSeconds(31));
        cut.WaitForAssertion(() => Assert.Equal("14:00", cut.Find("[data-sleep-countdown]").TextContent));
    }

    [Fact]
    public async Task Countdown_OnlyTicksWhileThePanelIsOpen()
    {
        var cut = await OpenAsync(Running(TimeSpan.FromMinutes(10)));
        Assert.Equal(1, _clock.RunningTicks);

        var panel = cut.FindComponent<PlaybackPanel>();
        await panel.InvokeAsync(() => panel.Instance.CloseAsync());

        cut.WaitForAssertion(() => Assert.Equal(0, _clock.RunningTicks));
    }

    [Fact]
    public async Task Countdown_ATickThatFiresJustAfterThePanelClosedDoesNothing()
    {
        var cut = await OpenAsync(Running(TimeSpan.FromMinutes(10)));
        var panel = cut.FindComponent<PlaybackPanel>();
        await panel.InvokeAsync(() => panel.Instance.CloseAsync());
        cut.WaitForAssertion(() => Assert.Equal(0, _clock.RunningTicks));

        var exception = Record.Exception(() => _clock.LastTick!());
        await Task.Delay(50);

        Assert.Null(exception);
        Assert.Empty(cut.FindAll("[data-sleep-countdown]"));
    }

    [Fact]
    public async Task Countdown_IsTheSameValueInASecondPanelBuiltFromTheSameSessionState()
    {
        // Opening Now playing builds the bar's panel again from the one timer the session holds: the value does not restart, it is the deadline's.
        var state = Running(TimeSpan.FromSeconds(872));
        var first = await OpenAsync(state);
        Assert.Equal("14:32", first.Find("[data-sleep-countdown]").TextContent);

        _clock.Advance(TimeSpan.FromSeconds(40));
        var firstPanel = first.FindComponent<PlaybackPanel>();
        await firstPanel.InvokeAsync(() => firstPanel.Instance.CloseAsync());
        var second = await OpenAsync(state);

        Assert.Equal("13:52", second.Find("[data-sleep-countdown]").TextContent);
        Assert.Empty(_selections);
    }

    [Fact]
    public async Task Countdown_RecoversAfterAReloadFromTheDeadlineAlone()
    {
        // A reload restores the state from the session snapshot; only the deadline is needed to show the same time left as before.
        var deadline = Now + TimeSpan.FromSeconds(872);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var cut = await OpenAsync(new AudiobookSleepTimerStateDto { Mode = AudiobookSleepTimerModes.Timer, ChosenMinutes = 15, DeadlineUtc = deadline, TimerGeneration = 9 });

        Assert.Equal("9:32", cut.Find("[data-sleep-countdown]").TextContent);
    }

    [Fact]
    public void Countdown_NeverGoesBelowZeroOnceTheDeadlinePassed()
    {
        var state = Running(TimeSpan.FromSeconds(-30));

        Assert.Equal(TimeSpan.Zero, PlaybackSleepTimerChoices.Remaining(state, Now));
        Assert.Null(PlaybackSleepTimerChoices.Remaining(new AudiobookSleepTimerStateDto(), Now));
        Assert.Null(PlaybackSleepTimerChoices.Remaining(new AudiobookSleepTimerStateDto { Mode = AudiobookSleepTimerModes.EndCurrent }, Now));
    }

    // ---- The footer: +5 minutes and Cancel timer ----

    [Fact]
    public async Task Footer_AppearsOnlyWhileATimerRunsAndHoldsPlusFiveMinutesAndCancelTimer()
    {
        var cut = await OpenAsync();
        Assert.Empty(cut.FindAll("[data-sleep-footer]"));

        cut.Render(parameters => parameters.Add(component => component.State, Running(TimeSpan.FromMinutes(14))));

        Assert.Equal("+5 minutes", cut.Find("[data-sleep-extend]").TextContent.Trim());
        Assert.Equal("Cancel timer", cut.Find("[data-sleep-cancel]").TextContent.Trim());
    }

    [Theory]
    [InlineData(600d, 15)]
    [InlineData(872d, 20)]
    [InlineData(870d, 20)]
    [InlineData(1d, 6)]
    [InlineData(0d, 5)]
    public async Task PlusFiveMinutes_SetsANewTimerAtWhatIsLeftPlusFiveThroughTheSameSetAction(double remainingSeconds, int expectedMinutes)
    {
        var cut = await OpenAsync(Running(TimeSpan.FromSeconds(remainingSeconds)));

        await cut.Find("[data-sleep-extend]").ClickAsync();

        var selection = Assert.Single(_selections);
        Assert.Equal(AudiobookSleepTimerModes.Timer, selection.Mode);
        Assert.Equal(expectedMinutes, selection.Minutes);
        Assert.True(expectedMinutes * 60d >= remainingSeconds + 300d, "The new length must cover what was left plus five minutes.");
        Assert.True(expectedMinutes * 60d < remainingSeconds + 300d + 60d, "The new length rounds up by less than a minute.");
    }

    [Fact]
    public void PlusFiveMinutes_NeverGoesPastTwelveHours()
    {
        var selection = PlaybackSleepTimerChoices.Extension(Running(TimeSpan.FromMinutes(718)), Now);

        Assert.Equal(720, selection!.Minutes);
    }

    [Fact]
    public void PlusFiveMinutes_HasNothingToAddToForAnEndOfChapterOrAnIdleTimer()
    {
        Assert.Null(PlaybackSleepTimerChoices.Extension(new AudiobookSleepTimerStateDto { Mode = AudiobookSleepTimerModes.EndCurrent }, Now));
        Assert.Null(PlaybackSleepTimerChoices.Extension(new AudiobookSleepTimerStateDto(), Now));
    }

    [Fact]
    public async Task CancelTimer_TurnsTheTimerOffAndClosesThePanel()
    {
        var cut = await OpenAsync(Running(TimeSpan.FromMinutes(14)));

        await cut.Find("[data-sleep-cancel]").ClickAsync();

        Assert.Equal(AudiobookSleepTimerModes.Off, Assert.Single(_selections).Mode);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-sleep-timer-view]")));
    }

    // ---- The moon on the bar ----

    [Fact]
    public void Moon_IsSelectedWhileATimerRunsAndPlainOtherwise()
    {
        var idle = Render<SleepTimerPanel>(parameters => parameters.Add(component => component.State, new AudiobookSleepTimerStateDto()));
        Assert.DoesNotContain("is-on", idle.Find("button.playback-popover-trigger").ClassList);
        Assert.Equal("Sleep timer", idle.Find("button.playback-popover-trigger").GetAttribute("aria-label"));

        var running = Render<SleepTimerPanel>(parameters => parameters.Add(component => component.State, Running(TimeSpan.FromMinutes(14))));
        Assert.Contains("is-on", running.Find("button.playback-popover-trigger").ClassList);
        Assert.Equal("Sleep timer, on", running.Find("button.playback-popover-trigger").GetAttribute("aria-label"));
    }

    [Fact]
    public void BarTools_AudiobooksAlwaysHaveTheSleepTimerAndMusicNeverDoes()
    {
        var book = PlaybackControlCatalog.BuildBarTools(PlaybackBarMedia.Audiobook, new PlaybackBarState(HasChapters: true));
        Assert.Contains(book, tool => tool.Key == PlaybackControlKey.SleepTimer && !tool.IsActive);

        var idleMusic = PlaybackControlCatalog.BuildBarTools(PlaybackBarMedia.Music, new PlaybackBarState(HasQueue: true));
        Assert.DoesNotContain(idleMusic, tool => tool.Key == PlaybackControlKey.SleepTimer);

        // The Engine only starts a sleep timer for audiobooks, so a music bar offers no moon even when a stale timer state says one is on.
        var staleMusic = PlaybackControlCatalog.BuildBarTools(PlaybackBarMedia.Music, new PlaybackBarState(HasQueue: true, IsSleepTimerActive: true));
        Assert.DoesNotContain(staleMusic, tool => tool.Key == PlaybackControlKey.SleepTimer);

        var runningBook = PlaybackControlCatalog.BuildBarTools(PlaybackBarMedia.Audiobook, new PlaybackBarState(HasChapters: true, IsSleepTimerActive: true));
        Assert.True(Assert.Single(runningBook, tool => tool.Key == PlaybackControlKey.SleepTimer).IsActive);
    }

    // ---- Wording helpers ----

    [Fact]
    public void ChapterNumber_CountsTheTargetChapterInBookOrderAndIsUnknownForAMissingChapter()
    {
        var chapters = Chapters(24);
        var state = new AudiobookSleepTimerStateDto { Mode = AudiobookSleepTimerModes.EndNext, TargetChapterIndex = 8 };

        Assert.Equal(9, PlaybackSleepTimerChoices.TargetChapterNumber(state, chapters));
        Assert.Null(PlaybackSleepTimerChoices.TargetChapterNumber(state with { TargetChapterIndex = 99 }, chapters));
        Assert.Null(PlaybackSleepTimerChoices.TargetChapterNumber(state, []));
        Assert.Null(PlaybackSleepTimerChoices.TargetChapterNumber(new AudiobookSleepTimerStateDto(), chapters));
    }

    [Fact]
    public async Task EndOfChapterTimer_WithAnUnknownChapterSaysTheChapterWithoutANumber()
    {
        var state = new AudiobookSleepTimerStateDto { Mode = AudiobookSleepTimerModes.EndCurrent, TargetChapterIndex = 99, TimerGeneration = 4 };
        var cut = await OpenAsync(state, Chapters(3));

        Assert.Equal("Stopping at the end of the chapter", cut.Find("[data-sleep-stop]").TextContent);
    }
}
