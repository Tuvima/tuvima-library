using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Tests;

/// <summary>The quiet Saved line and the glow helper (design system, Feedback after an action).</summary>
public sealed class AppSavedNoteTests : LocalizedBunitContext
{
    [Fact]
    public void SavedNote_IsALiveRegionThatSaysNothingUntilShown()
    {
        var cut = Render<AppSavedNote>();

        Assert.Equal("status", cut.Find("[data-saved-note]").GetAttribute("role"));
        Assert.Empty(cut.FindAll(".app-saved-note__line"));
    }

    [Fact]
    public void SavedNote_WhenVisibleTurnsTrue_ShowsTheSharedSavedWordWithACheck()
    {
        var cut = Render<AppSavedNote>(p => p.Add(n => n.Visible, true).Add(n => n.Delay, TimeSpan.FromSeconds(30)));

        var line = cut.Find(".app-saved-note__line");
        Assert.Contains("Saved", line.TextContent);
        Assert.NotEmpty(line.QuerySelectorAll("svg"));
    }

    [Fact]
    public void SavedNote_WithText_ShowsThatTextInsteadOfSaved()
    {
        var cut = Render<AppSavedNote>(p => p.Add(n => n.Visible, true).Add(n => n.Text, "Saved 3 collections").Add(n => n.Delay, TimeSpan.FromSeconds(30)));

        Assert.Contains("Saved 3 collections", cut.Find(".app-saved-note__line").TextContent);
    }

    [Fact]
    public void SavedNote_HidesItselfAfterTheDelay()
    {
        var cut = Render<AppSavedNote>(p => p.Add(n => n.Visible, true).Add(n => n.Delay, TimeSpan.FromMilliseconds(60)));
        Assert.Single(cut.FindAll(".app-saved-note__line"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".app-saved-note__line")));
        Assert.Equal("status", cut.Find("[data-saved-note]").GetAttribute("role"));
    }

    [Fact]
    public void SavedNote_ShowCalledAgain_KeepsTheLineUpForAFullNewDelay()
    {
        var cut = Render<AppSavedNote>(p => p.Add(n => n.Delay, TimeSpan.FromMilliseconds(400)));

        cut.InvokeAsync(() => cut.Instance.Show());
        Assert.Single(cut.FindAll(".app-saved-note__line"));
        Thread.Sleep(250);
        cut.InvokeAsync(() => cut.Instance.Show());
        Thread.Sleep(250);

        Assert.Single(cut.FindAll(".app-saved-note__line"));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".app-saved-note__line")));
    }

    [Fact]
    public void SavedNote_KeepsUnmatchedAttributesOnItsSlot()
    {
        var cut = Render<AppSavedNote>(p => p.AddUnmatched("data-probe", "yes"));

        Assert.Equal("yes", cut.Find("[data-saved-note]").GetAttribute("data-probe"));
    }

    [Fact]
    public void JustChanged_GlowsOnlyTheMarkedId_AndMovesWhenAnotherIsMarked()
    {
        var glow = new JustChanged();
        Assert.Null(glow.Class("a"));

        glow.Mark("a");
        Assert.Equal("tl-just-changed", glow.Class("a"));
        Assert.Null(glow.Class("b"));

        glow.Mark("b");
        Assert.Null(glow.Class("a"));
        Assert.Equal("tl-just-changed", glow.Class("b"));

        glow.Clear();
        Assert.Null(glow.Class("b"));
    }

    [Fact]
    public void JustChanged_MarkingTheSameIdAgain_ChangesItsVersionSoTheGlowPlaysAgain()
    {
        var glow = new JustChanged();
        glow.Mark("a");
        var first = glow.Version("a");

        glow.Mark("a");

        Assert.NotEqual(first, glow.Version("a"));
        Assert.Equal(0, glow.Version("other"));
    }
    [Fact]
    public void JustChanged_ClearsItselfAfterItsLifetime_AndSaysSo()
    {
        var clock = new ManualTime();
        var glow = new JustChanged(TimeSpan.FromSeconds(4), clock);
        var cleared = 0;
        glow.Changed += () => cleared++;

        glow.Mark("a");
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("tl-just-changed", glow.Class("a"));
        Assert.Equal(0, cleared);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(glow.Class("a"));
        Assert.Equal(0, glow.Version("a"));
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void JustChanged_MarkingAgainRestartsTheLifetime_AndAnOlderTimerCannotClearTheNewMark()
    {
        var clock = new ManualTime();
        var glow = new JustChanged(TimeSpan.FromSeconds(4), clock);
        glow.Mark("a");
        clock.Advance(TimeSpan.FromSeconds(3));

        glow.Mark("b");
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal("tl-just-changed", glow.Class("b"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(glow.Class("b"));
    }

    [Fact]
    public void JustChanged_AfterClearOrDispose_NothingFiresAndNothingGlows()
    {
        var clock = new ManualTime();
        var glow = new JustChanged(TimeSpan.FromSeconds(4), clock);
        var cleared = 0;
        glow.Changed += () => cleared++;
        glow.Mark("a");
        glow.Clear();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, cleared);

        glow.Mark("b");
        glow.Dispose();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, cleared);
        glow.Mark("c");
        Assert.Null(glow.Class("c"));
    }

    [Fact]
    public void TheGlowStyle_WithReducedMotion_IsABriefHighlightThatEndsAndNeverAPermanentOutline()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        var css = File.ReadAllText(Path.Combine(directory!.FullName, "src", "MediaEngine.Web", "wwwroot", "app.css"));

        var reduced = css[css.IndexOf("@media (prefers-reduced-motion:reduce){.tl-just-changed", StringComparison.Ordinal)..];
        reduced = reduced[..reduced.IndexOf('\n')];

        Assert.Contains("animation:tl-just-changed-quiet", reduced);
        Assert.DoesNotContain("animation:none", reduced);
        Assert.Contains("outline:2px solid transparent", reduced);
        Assert.Contains("@keyframes tl-just-changed-quiet{0%,70%{outline-color:var(--tl-accent-primary)}100%{outline-color:transparent}}", css);
    }

    // A clock the test moves by hand; a timer fires when the clock passes its due time.
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.ToList())
            {
                timer.FireIfDue(_now);
            }
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        private bool _done;

        public void FireIfDue(TimeSpan now)
        {
            if (!_done && now >= due)
            {
                _done = true;
                callback(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => _done = true;

        public ValueTask DisposeAsync()
        {
            _done = true;
            return ValueTask.CompletedTask;
        }
    }
}
