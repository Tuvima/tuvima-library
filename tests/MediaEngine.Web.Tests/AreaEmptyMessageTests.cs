using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.MediaHub;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Tests;

/// <summary>Honest waiting messages WP2 and WP3: the words an empty area page shows for each true reason, and who sees what.</summary>
public sealed class AreaEmptyMessageTests : LiveLibraryTestContext
{
    private AreaReadinessDto? _readiness;
    private int _reads;

    private AreaEmptyMessagePresenter Presenter => new(Services.GetRequiredService<IStringLocalizer<SharedStrings>>());

    private static AreaReadinessDto Admin(
        string area, string reason, int found = 0, int inProgress = 0, int waiting = 0, int review = 0, int ready = 0, int scanning = 0,
        int libraries = 1, string[]? unreachable = null, params AreaReadinessMediaTypeDto[] types) => new(
        area, AreaReadinessAudiences.Administrator, reason, true, libraries, unreachable ?? [],
        found, inProgress, waiting, review, ready, scanning, types);

    private void UseReadiness() => AreaReadiness = () =>
    {
        _reads++;
        return Task.FromResult(_readiness);
    };

    [Fact]
    public void WaitingForASource_SaysHowManyFilesWereFound_AndOffersConnectAndSeeTheFiles()
    {
        var message = Presenter.Build(Admin("watch", AreaReadinessReasons.WaitingForSource, found: 19, waiting: 19));

        Assert.Equal("19 videos found, none identified yet", message.Title);
        Assert.Contains("metadata source", message.Message, StringComparison.Ordinal);
        Assert.Equal(["Connect a metadata source", "See the 19 files"], message.Actions.Select(action => action.Label));
        Assert.Equal("/settings/plugins", message.Actions[0].Href);
        Assert.DoesNotContain("Add a library", message.Actions.Select(action => action.Label));
        Assert.DoesNotContain("waiting for media", message.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddALibrary_IsOfferedOnlyWhenNoLibraryOfThatKindExists()
    {
        foreach (var reason in new[]
                 {
                     AreaReadinessReasons.SetupUnfinished, AreaReadinessReasons.FolderUnreachable, AreaReadinessReasons.StillLooking,
                     AreaReadinessReasons.WaitingForSource, AreaReadinessReasons.WaitingInReview, AreaReadinessReasons.NothingFound,
                     AreaReadinessReasons.Ready,
                 })
        {
            var message = Presenter.Build(Admin("watch", reason, found: 3, waiting: 3, review: 3, inProgress: 3, unreachable: ["Movies"]));
            Assert.DoesNotContain("Add a library", message.Actions.Select(action => action.Label));
        }

        var none = Presenter.Build(Admin("watch", AreaReadinessReasons.NoLibrary, libraries: 0));
        Assert.Equal("Nothing to watch yet", none.Title);
        Assert.Equal("Add a library", Assert.Single(none.Actions).Label);
    }

    [Fact]
    public void UnfinishedSetup_SaysSo_WithContinueSetup()
    {
        var message = Presenter.Build(Admin("home", AreaReadinessReasons.SetupUnfinished));

        Assert.Equal("Finish setup to start scanning", message.Title);
        Assert.Equal("Continue setup", Assert.Single(message.Actions).Label);
        Assert.Equal("/setup", message.Actions[0].Href);
    }

    [Fact]
    public void FolderTheEngineCannotReach_IsNamed()
    {
        var message = Presenter.Build(Admin("watch", AreaReadinessReasons.FolderUnreachable, unreachable: ["Movies"]));

        Assert.Equal("Library can't reach Movies", message.Title);
        Assert.Equal("Open Libraries", Assert.Single(message.Actions).Label);
    }

    [Fact]
    public void WhileLooking_ShowsTheCount_AndLinksToProgress()
    {
        var message = Presenter.Build(Admin("watch", AreaReadinessReasons.StillLooking, found: 54, inProgress: 54));

        Assert.Equal("Looking through 54 files", message.Title);
        Assert.Equal("/settings/general/library-activity", Assert.Single(message.Actions).Href);
    }

    [Fact]
    public void FilesInNeedsReview_SayLibraryIsNotSure()
    {
        var message = Presenter.Build(Admin("read", AreaReadinessReasons.WaitingInReview, found: 4, review: 4));

        Assert.Equal("4 books and comics found", message.Title);
        Assert.Equal("Library isn't sure what they are.", message.Message);
        Assert.Equal("Open Needs Review", Assert.Single(message.Actions).Label);
    }

    [Fact]
    public void ReadyPersonalVideos_ArePointedAtOnTheOtherTab()
    {
        var videos = new AreaReadinessMediaTypeDto("Videos", 4, 0, 0, 0, 4);
        var message = Presenter.Build(Admin("watch", AreaReadinessReasons.WaitingForSource, found: 23, waiting: 19, ready: 4, types: videos));

        Assert.Equal("Your 4 personal videos need no source and are in Videos.", message.ExtraLine);
        Assert.Equal("/watch/videos", message.ExtraHref);
    }

    [Fact]
    public void AMember_SeesOneSentence_NoCounts_NoButtons()
    {
        var message = Presenter.Build(AreaReadinessReadServiceShape("watch", AreaReadinessAudiences.Member));

        Assert.Equal("Nothing here yet", message.Title);
        Assert.Equal("An administrator is still setting this up.", message.Message);
        Assert.Empty(message.Actions);
        Assert.Null(message.ExtraLine);
    }

    [Fact]
    public void AKidsProfile_SeesTheTitleOnly()
    {
        var message = Presenter.Build(AreaReadinessReadServiceShape("watch", AreaReadinessAudiences.Kids));

        Assert.Equal("Nothing here yet", message.Title);
        Assert.Null(message.Message);
        Assert.Empty(message.Actions);
    }

    [Fact]
    public void OnATelevision_OnlyTheSentenceShows()
    {
        var message = Presenter.Build(Admin("watch", AreaReadinessReasons.WaitingForSource, found: 3, waiting: 3), television: true);

        Assert.Empty(message.Actions);
        Assert.Contains("3 videos found", message.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Component_ShowsTheTrueReason_WithTheTwoButtons_AndNeverAddALibrary()
    {
        _readiness = Admin("watch", AreaReadinessReasons.WaitingForSource, found: 19, waiting: 19);
        UseReadiness();

        var cut = Render<AreaEmptyState>(parameters => parameters.Add(component => component.Area, "watch"));

        cut.WaitForAssertion(() => Assert.Contains("19 videos found, none identified yet", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(2, cut.FindAll(".empty-shelf-state__actions a").Count);
        Assert.DoesNotContain("Add a library", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Component_WhenTheEngineCannotBeReached_ShowsTheErrorState_NotAddALibrary()
    {
        _readiness = null;
        UseReadiness();

        var cut = Render<AreaEmptyState>(parameters => parameters.Add(component => component.Area, "watch"));

        cut.WaitForAssertion(() => Assert.Contains("Can't reach Library", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Try again", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Add a library", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Component_ChangesByItself_WhenTheCauseClears()
    {
        _readiness = Admin("watch", AreaReadinessReasons.WaitingForSource, found: 2, waiting: 2);
        UseReadiness();
        var cut = Render<AreaEmptyState>(parameters => parameters.Add(component => component.Area, "watch"));
        cut.WaitForAssertion(() => Assert.Contains("none identified yet", cut.Markup, StringComparison.Ordinal));

        _readiness = Admin("watch", AreaReadinessReasons.WaitingInReview, found: 2, review: 2);
        cut.InvokeAsync(() => State.PushMediaAdded(new MediaEngine.Contracts.Realtime.MediaAddedEvent(Guid.NewGuid(), null, "Movies", "A film")));

        cut.WaitForAssertion(() => Assert.Contains("Library isn't sure what they are.", cut.Markup, StringComparison.Ordinal));
        Assert.True(_reads >= 2);
    }

    [Fact]
    public void PartlyReadyLine_ShowsTheCount_ToAdministratorsOnly_AndIsGoneWhenNothingWaits()
    {
        _readiness = Admin("watch", AreaReadinessReasons.WaitingForSource, found: 30, waiting: 23, ready: 7);
        UseReadiness();
        var cut = Render<AreaPartlyReadyLine>(parameters => parameters.Add(component => component.Area, "watch"));
        cut.WaitForAssertion(() => Assert.Equal("23 more files are waiting for a metadata source.", cut.Find("[data-area-partly-ready]").TextContent.Trim()));
        Assert.Equal("/settings/review", cut.Find("[data-area-partly-ready] a").GetAttribute("href"));

        _readiness = AreaReadinessShapeNone();
        var member = Render<AreaPartlyReadyLine>(parameters => parameters.Add(component => component.Area, "watch"));
        member.WaitForState(() => _reads >= 2);
        Assert.Empty(member.FindAll("[data-area-partly-ready]"));
    }

    [Fact]
    public void SetupNotice_ShowsWhileSetupIsUnfinished_AndGoesWithoutAReload()
    {
        _readiness = Admin("home", AreaReadinessReasons.SetupUnfinished) with { SetupFinished = false };
        UseReadiness();
        var cut = Render<SetupUnfinishedNotice>();
        cut.WaitForAssertion(() => Assert.Contains("Finish setup to start scanning", cut.Find("[data-setup-unfinished-notice]").TextContent, StringComparison.Ordinal));
        Assert.Equal("/setup", cut.Find("[data-setup-unfinished-notice] a").GetAttribute("href"));

        _readiness = Admin("home", AreaReadinessReasons.NothingFound);
        cut.InvokeAsync(() => State.PushMediaAdded(new MediaEngine.Contracts.Realtime.MediaAddedEvent(Guid.NewGuid(), null, "Movies", "A film")));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-setup-unfinished-notice]")));
    }

    private static AreaReadinessDto AreaReadinessReadServiceShape(string area, string audience) =>
        new(area, audience, AreaReadinessReasons.Hidden, null, 0, [], 0, 0, 0, 0, 0, 0, []);

    private static AreaReadinessDto AreaReadinessShapeNone() => AreaReadinessReadServiceShape("watch", AreaReadinessAudiences.Member);
}
