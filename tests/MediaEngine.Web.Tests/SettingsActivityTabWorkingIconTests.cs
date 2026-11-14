using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Activity tab of the Libraries page shows the small turning sync icon whenever the Library is working (the top bar's own rule),
/// selected or not, and keeps its static icon when it is idle.
/// </summary>
public sealed class SettingsActivityTabWorkingIconTests : AsyncBunitContext
{
    private readonly UniverseStateContainer _state = new();

    public SettingsActivityTabWorkingIconTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(api);
        Services.AddSingleton(_state);
        Services.AddSingleton(new PlaybackSessionController(null!, api));
        Services.AddSingleton<ActivityNotificationQueue>();
        Services.AddSingleton<ShellActivityState>();
    }

    [Fact]
    public void WorkingLibrary_ShowsTheTurningSyncIconOnTheActivityTab_EvenWhenAnotherTabIsOpen()
    {
        _state.PushIngestionQueueProgress(new IngestionQueueProgressDto { State = "working", FilesFound = 10, FilesDone = 2, Progress = 0.2, UpdatedAt = DateTimeOffset.UtcNow });

        var cut = RenderTabs();

        var activity = ActivityTab(cut);
        Assert.DoesNotContain("tl-tab-active", activity.ClassList);
        var icon = activity.QuerySelector(".app-sync-icon");
        Assert.NotNull(icon);
        Assert.Contains("app-sync-icon--spin", icon!.ClassList);
        Assert.Equal("Working", activity.QuerySelector(".sr-only")!.TextContent.Trim());
        Assert.Null(activity.GetAttribute("title"));
        Assert.Single(cut.FindAll(".app-sync-icon"));
    }

    [Fact]
    public void IdleLibrary_KeepsTheStaticActivityIconAndSaysNothingAboutWorking()
    {
        _state.PushIngestionQueueProgress(new IngestionQueueProgressDto { State = "idle", Progress = 1, UpdatedAt = DateTimeOffset.UtcNow });

        var cut = RenderTabs();

        var activity = ActivityTab(cut);
        Assert.Empty(cut.FindAll(".app-sync-icon"));
        Assert.Empty(activity.QuerySelectorAll(".sr-only"));
        Assert.NotNull(activity.QuerySelector("svg"));
    }

    [Fact]
    public void TheActivityTabStartsAndStopsTurningAsTheLibraryWorks()
    {
        var cut = RenderTabs();
        Assert.Empty(cut.FindAll(".app-sync-icon"));

        _state.PushIngestionQueueProgress(new IngestionQueueProgressDto { State = "working", FilesFound = 10, FilesDone = 2, Progress = 0.2, UpdatedAt = DateTimeOffset.UtcNow });
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".app-sync-icon")));

        _state.PushIngestionQueueProgress(new IngestionQueueProgressDto { State = "idle", Progress = 1, UpdatedAt = DateTimeOffset.UtcNow });
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".app-sync-icon")));
    }

    [Fact]
    public void TheSmallTurningIcon_StandsStillWhenThePersonPrefersReducedMotion()
    {
        var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "Shared", "AppSyncActivityIcon.razor.css"));
        var reduced = css[css.IndexOf("@media (prefers-reduced-motion: reduce)", StringComparison.Ordinal)..];

        Assert.Contains(".app-sync-icon--spin", reduced, StringComparison.Ordinal);
        Assert.Contains("animation: none", reduced, StringComparison.Ordinal);
    }

    private IRenderedComponent<SettingsPageTabs> RenderTabs()
    {
        var libraries = SettingsNav.MenuGroups.SelectMany(group => group.Nodes).Single(node => node.Label == "Libraries");
        return Render<SettingsPageTabs>(parameters => parameters
            .Add(component => component.Tabs, libraries.Tabs)
            .Add(component => component.Active, libraries.Tabs[0])
            .Add(component => component.PageLabel, libraries.Label));
    }

    private static AngleSharp.Dom.IElement ActivityTab(IRenderedComponent<SettingsPageTabs> cut) =>
        cut.FindAll("a.tl-tab").Single(tab => tab.GetAttribute("href") == "/settings/library-activity");
}
