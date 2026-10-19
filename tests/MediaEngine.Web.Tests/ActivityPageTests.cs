using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Settings &gt; Personal &gt; Overview &gt; Activity (D15, D16, D27, D31, D32, D33): the person's list, the administrators' scope, the filters
/// and the date rail, and the server administrator's delete. The Engine is a stub that answers what it would answer; the page only asks.
/// </summary>
public sealed class ActivityPageTests : AsyncBunitContext
{
    private static readonly Guid OwnProfile = Guid.NewGuid();
    private static readonly Guid AnnaProfile = Guid.NewGuid();
    private static readonly Guid BenProfile = Guid.NewGuid();
    private static readonly Guid MaryProfile = Guid.NewGuid();
    private static readonly Guid HomeHousehold = Guid.NewGuid();
    private static readonly Guid CabinHousehold = Guid.NewGuid();
    private static readonly string WorkId = Guid.NewGuid().ToString("D");

    private readonly FakeActivityEngine _engine = new();
    private readonly IdentityHandler _identity = new();
    private readonly DashboardSessionAccessor _session = new();
    private DeviceContextService? _device;

    public ActivityPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IHttpClientFactory>(new IdentityClientFactory(_identity));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddSingleton(_session);
        Services.AddScoped(_ => _device ??= Device("desktop"));
        Services.AddSingleton(_ => EngineApiClientStub.Create(_engine.Configure));
        Render<AppPopoverHost>();
    }

    private static DeviceContextService Device(string formFactor)
    {
        var phone = formFactor == "phone";
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        device.InitialiseAsync(new DeviceContextSnapshot(phone ? "mobile" : "web", formFactor, phone, !phone, !phone)).GetAwaiter().GetResult();
        return device;
    }

    private enum Role
    {
        Member,
        HouseholdAdministrator,
        ServerAdministrator,
    }

    private void SignIn(Role role)
    {
        var authority = role switch
        {
            Role.ServerAdministrator => new DashboardAuthorityResponse(
                Guid.NewGuid(), OwnProfile, true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], ["access.manage"]),
            Role.HouseholdAdministrator => new DashboardAuthorityResponse(
                Guid.NewGuid(), OwnProfile, true, true, 1, 1, false, false, null, 1, [], [], ["household.manage"]),
            _ => new DashboardAuthorityResponse(Guid.NewGuid(), OwnProfile, true, true, 1, 1, false, false, null, 1, [], [], []),
        };
        _session.Set("test-session-token", authority.AccountId, OwnProfile, Guid.NewGuid(), authority);
        _identity.Role = role;
    }

    private IRenderedComponent<ActivityPage> RenderPage(bool standalone = false, string? scope = null)
    {
        var cut = Render<ActivityPage>(parameters =>
        {
            parameters.Add(page => page.Standalone, standalone);
            parameters.Add(page => page.Scope, scope);
        });
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-activity-state='loading']")), TimeSpan.FromSeconds(10));
        return cut;
    }

    /// <summary>Opens the Filters popover (desktop), the way a person does, so the Type, date, Outcome and Device controls are drawn.</summary>
    private static void OpenFilters(IRenderedComponent<ActivityPage> cut)
    {
        cut.Find("[data-activity-filters-button]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-activity-filters]")));
    }

    // ---- items ---------------------------------------------------------------------------------------------------------------

    private static string Utc(int daysAgo, int hour = 20) =>
        DateTimeOffset.UtcNow.Date.AddDays(-daysAgo).AddHours(hour).ToString("O");

    internal static PersonalActivityItemDto Entry(
        string id,
        string kind = PersonalActivityKindValues.Watch,
        string title = "Tremors",
        string? subtitle = null,
        long seconds = 1800,
        double? progress = 31,
        bool finished = false,
        string started = "",
        string? device = "Living room TV",
        string? workId = null,
        string? person = null,
        string? artwork = null) => new()
        {
            Id = id,
            Kind = kind,
            Title = title,
            Subtitle = subtitle,
            PlayedSeconds = seconds,
            ProgressPct = progress,
            Finished = finished,
            StartedAt = string.IsNullOrEmpty(started) ? Utc(0) : started,
            DeviceName = device,
            WorkId = workId,
            ArtworkUrl = artwork,
            Person = person is null ? null : new PersonalActivityPersonDto(Guid.NewGuid().ToString("D"), person),
        };

    private static PersonalActivityItemDto Group(string id, string title, params PersonalActivityItemDto[] attempts) => new()
    {
        Id = id,
        Kind = PersonalActivityKindValues.Watch,
        Title = title,
        StartedAt = attempts[^1].StartedAt,
        PlayedSeconds = attempts.Sum(attempt => attempt.PlayedSeconds),
        Finished = attempts.Any(attempt => attempt.Finished),
        AttemptCount = attempts.Length,
        Attempts = attempts,
    };

    private static PersonalActivityItemDto Session(string id, int songs = 50, int albums = 12, IReadOnlyList<PersonalActivityItemDto>? withSongs = null) => new()
    {
        Id = id,
        Kind = PersonalActivityKindValues.MusicSession,
        Title = string.Empty,
        StartedAt = Utc(0, 21),
        EndedAt = Utc(0, 22),
        PlayedSeconds = 3900,
        SongCount = songs,
        AlbumCount = albums,
        CoverUrls = Enumerable.Range(0, 9).Select(index => $"/engine-activity-artwork/{new string((char)('a' + index), 64)}").ToList(),
        Songs = withSongs,
    };

    private static PersonalActivityPageDto Page(IEnumerable<PersonalActivityItemDto> items, string? next = null, bool others = false) =>
        new([.. items], next, others);

    private static IReadOnlyList<string> RowIds(IRenderedComponent<ActivityPage> cut) =>
        [.. cut.FindAll("[data-activity-row]:not([data-activity-attempt])").Select(row => row.GetAttribute("data-activity-row")!)];

    private static string[] OptionLabels(IRenderedComponent<ActivityPage> cut, string selector) =>
        [.. cut.Find(selector).QuerySelectorAll("option").Select(option => option.TextContent.Trim())];

    // ---- who sees what -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Member_SeesTheirOwnList_WithNoScopeControl_NoDeleteAndNoAdministratorCalls()
    {
        SignIn(Role.Member);
        _engine.OnList = _ => Page([Entry("a", title: "Tremors", workId: WorkId), Entry("b", title: "Dune", kind: PersonalActivityKindValues.Read)]);

        var cut = RenderPage();

        Assert.Equal(["a", "b"], RowIds(cut));
        Assert.Empty(cut.FindAll("[data-activity-scope]"));
        Assert.Equal(["Refresh"], ActionMenuItems(cut));
        Assert.DoesNotContain("Delete history", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("data-activity-person", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("me", _engine.Lists.Single().Scope);
        Assert.DoesNotContain(_identity.Paths, path => path is "/access/profiles" or "/access/households");
        // The title opens the item.
        Assert.Equal($"/details/work/{WorkId}?context=watch", cut.Find("[data-activity-row='a'] a").GetAttribute("href"));
        // No IP address, sign-in detail, rating or like is shown anywhere.
        Assert.DoesNotContain("Like", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Rating", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_ShowsWhatHappenedHowLongHowFarHowItEndedAndTheDevice()
    {
        SignIn(Role.Member);
        _engine.OnList = _ => Page(
        [
            Entry("w", title: "Tremors", subtitle: "S2 E4 · Fear", seconds: 52 * 60, progress: 31, device: "Living room TV"),
            Entry("f", title: "Dune", kind: PersonalActivityKindValues.Read, seconds: 80 * 60, progress: 100, finished: true, device: "Kobo"),
            Entry("m", title: "Heat", kind: PersonalActivityKindValues.MyList, seconds: 0, progress: null, device: null),
        ]);

        var cut = RenderPage();

        var watched = cut.Find("[data-activity-row='w']");
        Assert.Equal("Watched", watched.QuerySelector("[data-activity-what]")!.TextContent);
        Assert.Equal("52 m", watched.QuerySelector("[data-activity-how-long]")!.TextContent);
        Assert.Equal("31%", watched.QuerySelector("[data-activity-how-far]")!.TextContent);
        Assert.Equal("Stopped", watched.QuerySelector("[data-activity-outcome]")!.TextContent);
        Assert.Equal("Living room TV", watched.QuerySelector("[data-activity-device]")!.TextContent);
        Assert.Contains("S2 E4 · Fear", watched.TextContent, StringComparison.Ordinal);

        var read = cut.Find("[data-activity-row='f']");
        Assert.Equal("Read", read.QuerySelector("[data-activity-what]")!.TextContent);
        Assert.Equal("1 h 20 m", read.QuerySelector("[data-activity-how-long]")!.TextContent);
        Assert.Equal("Finished", read.QuerySelector("[data-activity-outcome]")!.TextContent);

        var myList = cut.Find("[data-activity-row='m']");
        Assert.Equal("Added to My list", myList.QuerySelector("[data-activity-what]")!.TextContent);
        Assert.Equal(string.Empty, myList.QuerySelector("[data-activity-how-long]")!.TextContent);
        Assert.Equal(string.Empty, myList.QuerySelector("[data-activity-outcome]")!.TextContent);
    }

    [Fact]
    public void HouseholdAdministrator_ScopeOptionsAreLimitedToTheirHousehold_AndHaveNoEveryoneOrDelete()
    {
        SignIn(Role.HouseholdAdministrator);
        _engine.OnList = _ => Page([Entry("a")]);

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal(
            ["Me", "Household: Home", "Anna", "Ben"], OptionLabels(cut, "[data-activity-scope]")));
        Assert.DoesNotContain("Everyone", cut.Find("[data-activity-scope]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Mary", cut.Find("[data-activity-scope]").TextContent, StringComparison.Ordinal);
        Assert.Equal(["Refresh"], ActionMenuItems(cut));
        Assert.DoesNotContain("Delete history", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void HouseholdAdministrator_CanExportTheirOwnActivity_ButNotTheHouseholdsOrAnotherPersons()
    {
        SignIn(Role.HouseholdAdministrator);
        _engine.OnList = _ => Page([Entry("a", person: "Anna")]);
        var cut = RenderPage();
        Assert.NotEmpty(cut.FindAll("[data-activity-export]"));

        cut.Find("[data-activity-scope]").Change($"household:{HomeHousehold:D}");

        cut.WaitForAssertion(() => Assert.Equal($"household:{HomeHousehold:D}", _engine.Lists.Last().Scope));
        // Exporting other people's activity is a server-administrator action; the Engine would refuse, so the button is not offered.
        Assert.Empty(cut.FindAll("[data-activity-export]"));
    }

    [Fact]
    public void ServerAdministrator_ScopeIncludesEveryone_HouseholdsAndPeople_AndAPersonColumnAppearsForThem()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = request => request.Scope == "me"
            ? Page([Entry("a")])
            : Page([Entry("a", person: "Anna"), Entry("b", person: "Ben", title: "Dune")], others: true);

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal(
            ["Me", "Everyone", "Household: Cabin", "Household: Home", "Anna (Home)", "Ben (Home)", "Mary (Cabin)"],
            OptionLabels(cut, "[data-activity-scope]")));
        Assert.Empty(cut.FindAll("[data-activity-person]"));
        Assert.Equal("Search titles and devices", cut.Find(".activity-toolbar__search input").GetAttribute("placeholder"));

        cut.Find("[data-activity-scope]").Change("everyone");

        cut.WaitForAssertion(() => Assert.Equal(["Anna", "Ben"], cut.FindAll("[data-activity-person]").Select(cell => cell.TextContent).ToArray()));
        Assert.Equal("everyone", _engine.Lists.Last().Scope);
        Assert.Equal("everyone", _engine.Periods.Last().Scope);
        Assert.Contains("Person", cut.Find(".activity-list__head").TextContent, StringComparison.Ordinal);
        Assert.Equal("Search titles, people and devices", cut.Find(".activity-toolbar__search input").GetAttribute("placeholder"));

        // Back to Me: the column goes again.
        cut.Find("[data-activity-scope]").Change("me");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-activity-person]")));
    }

    [Fact]
    public void ServerAdministrator_CanPickOnePerson_AndTheEngineIsAskedForThatProfile()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = _ => Page([Entry("a", person: "Mary")], others: true);
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Contains("Mary (Cabin)", cut.Find("[data-activity-scope]").TextContent, StringComparison.Ordinal));

        cut.Find("[data-activity-scope]").Change($"profile:{MaryProfile:D}");

        cut.WaitForAssertion(() => Assert.Equal($"profile:{MaryProfile:D}", _engine.Lists.Last().Scope));
        Assert.Equal("Mary", cut.Find("[data-activity-person]").TextContent);
    }

    [Fact]
    public void AScopeFromAPersonsPage_OpensThatPersonsActivity_ForAServerAdministrator()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = _ => Page([Entry("a", person: "Anna")], others: true);

        var cut = RenderPage(scope: $"profile:{AnnaProfile:D}");

        Assert.Equal($"profile:{AnnaProfile:D}", _engine.Lists.First().Scope);
        Assert.Equal($"profile:{AnnaProfile:D}", cut.Find("[data-activity-scope]").GetAttribute("value"));
    }

    [Fact]
    public void AScopeInTheAddress_IsIgnored_ForAMember_AndOutsideTheirHouseholdForAHouseholdAdministrator()
    {
        SignIn(Role.Member);
        RenderPage(scope: "everyone");
        Assert.Equal("me", _engine.Lists.Single().Scope);

        _engine.Lists.Clear();
        SignIn(Role.HouseholdAdministrator);
        var other = RenderPage(scope: $"profile:{MaryProfile:D}");
        Assert.Equal("me", _engine.Lists.Single().Scope);
        Assert.Equal("me", other.Find("[data-activity-scope]").GetAttribute("value"));
    }

    [Fact]
    public void TheStandalonePage_ShowsOnlyOwnActivity_EvenForAServerAdministrator()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = _ => Page([Entry("a")]);

        var cut = RenderPage(standalone: true, scope: "everyone");

        Assert.Empty(cut.FindAll("[data-activity-scope]"));
        Assert.Equal(["Refresh"], ActionMenuItems(cut));
        Assert.Equal("me", _engine.Lists.Single().Scope);
        Assert.NotEmpty(cut.FindAll("[data-activity-export]"));
        Assert.DoesNotContain(_identity.Paths, path => path is "/access/profiles" or "/access/households");
        Assert.Equal("true", cut.Find("[data-activity-page]").GetAttribute("data-activity-standalone"));
    }

    // ---- filters -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TypeOutcomeAndDateFilters_ChangeTheRequest_AndShowAsChipsThatCanBeRemoved()
    {
        SignIn(Role.Member);
        _engine.OnList = _ => Page([Entry("a")]);
        var cut = RenderPage();

        OpenFilters(cut);
        cut.Find("[data-activity-kind-filter='watch']").Change(true);
        cut.WaitForAssertion(() => Assert.Equal(["watch"], _engine.Lists.Last().Kinds));
        cut.Find("[data-activity-kind-filter='read']").Change(true);
        cut.WaitForAssertion(() => Assert.Equal(["watch", "read"], _engine.Lists.Last().Kinds));
        cut.Find("[data-activity-outcome-filter]").Change("finished");
        cut.WaitForAssertion(() => Assert.Equal("finished", _engine.Lists.Last().Outcome));
        cut.Find("[data-activity-from]").Input("2026-09-01");
        cut.WaitForAssertion(() => Assert.Equal(ActivityListPresentation.StartOfLocalDay("2026-09-01"), _engine.Lists.Last().From));
        cut.Find("[data-activity-to]").Input("2026-09-30");
        // The end is the start of the next day, so the last day is included.
        cut.WaitForAssertion(() => Assert.Equal(ActivityListPresentation.StartOfLocalDay("2026-10-01"), _engine.Lists.Last().To));

        var chips = cut.FindAll(".app-active-filter-summary__chip").Select(chip => chip.TextContent.Trim()).ToArray();
        Assert.Equal(["Watched", "Read", "From 2026-09-01", "Until 2026-09-30", "Finished"], chips);
        // The date rail is asked with the same filters, so its counts follow them.
        Assert.Equal(_engine.Lists.Last().Kinds, _engine.Periods.Last().Kinds);
        Assert.Equal("finished", _engine.Periods.Last().Outcome);

        cut.FindAll(".app-active-filter-summary__chip").Single(chip => chip.TextContent.Contains("Finished", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Null(_engine.Lists.Last().Outcome));
        cut.Find(".app-active-filter-summary__clear").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(_engine.Lists.Last().Kinds);
            Assert.Null(_engine.Lists.Last().From);
            Assert.Null(_engine.Lists.Last().To);
        });
        Assert.Empty(cut.FindAll(".app-active-filter-summary__chip"));
    }

    [Fact]
    public void OnDesktop_TheControlsAreOneCompactRow_AndFiltersStayInAPopoverUntilOpened()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = _ => Page([Entry("a")]);
        var cut = RenderPage();

        var toolbar = cut.Find("[data-activity-toolbar]");
        // Scope, search, Filters, Group by, the every-session switch, Export and the more menu share one row.
        Assert.NotNull(toolbar.QuerySelector("[data-activity-scope]"));
        Assert.NotNull(toolbar.QuerySelector(".activity-toolbar__search input"));
        Assert.NotNull(toolbar.QuerySelector("[data-activity-filters-button]"));
        Assert.NotNull(toolbar.QuerySelector("select[aria-label='Group the list']"));
        Assert.NotNull(toolbar.QuerySelector("input[role='switch']"));
        Assert.NotNull(toolbar.QuerySelector("[data-activity-export]"));
        Assert.NotNull(toolbar.QuerySelector("button[aria-label='Activity actions']"));
        // Type, dates, Outcome and Device are not an open form above the list.
        Assert.Empty(cut.FindAll("[data-activity-filters]"));
        Assert.Empty(cut.FindAll("[data-activity-filters-count]"));
        Assert.Equal("false", cut.Find("[data-activity-filters-button]").GetAttribute("aria-expanded"));

        OpenFilters(cut);

        Assert.Equal("true", cut.Find("[data-activity-filters-button]").GetAttribute("aria-expanded"));
        var popover = cut.Find(".activity-filters-popover");
        Assert.NotNull(popover.QuerySelector("[data-activity-kind-filter='watch']"));
        Assert.NotNull(popover.QuerySelector("[data-activity-from]"));
        Assert.NotNull(popover.QuerySelector("[data-activity-to]"));
        Assert.NotNull(popover.QuerySelector("[data-activity-outcome-filter]"));
        Assert.NotNull(popover.QuerySelector("[data-activity-device-filter]"));
    }

    [Fact]
    public void ActiveFilters_ShowAsAMarkOnTheFiltersButton_AndAsRemovableChipsUnderTheRow()
    {
        SignIn(Role.Member);
        _engine.OnList = _ => Page([Entry("a")]);
        var cut = RenderPage();
        OpenFilters(cut);

        cut.Find("[data-activity-outcome-filter]").Change("finished");
        cut.Find("[data-activity-kind-filter='watch']").Change(true);

        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("[data-activity-filters-count]").TextContent.Trim()));
        // The chips follow the row and come before the list; the search box alone does not count as a filter.
        var summary = cut.Find(".app-active-filter-summary");
        Assert.True(cut.Markup.IndexOf("data-activity-toolbar", StringComparison.Ordinal) < cut.Markup.IndexOf("app-active-filter-summary", StringComparison.Ordinal));
        Assert.True(cut.Markup.IndexOf("app-active-filter-summary", StringComparison.Ordinal) < cut.Markup.IndexOf("data-activity-list", StringComparison.Ordinal));
        Assert.Equal(2, summary.QuerySelectorAll(".app-active-filter-summary__chip").Length);

        cut.Find(".activity-toolbar__search input").Input("Tremors");
        cut.WaitForAssertion(() => Assert.Equal("Tremors", _engine.Lists.Last().Search), TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => Assert.Equal(["a"], RowIds(cut)));
        Assert.Equal("2", cut.Find("[data-activity-filters-count]").TextContent.Trim());

        cut.FindAll(".app-active-filter-summary__chip").Single(chip => chip.TextContent.Contains("Finished", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Equal("1", cut.Find("[data-activity-filters-count]").TextContent.Trim()));
    }

    [Fact]
    public void Refresh_LivesInTheMoreMenu_AndAsksForTheListAgain()
    {
        SignIn(Role.Member);
        _engine.OnList = _ => Page([Entry("a")]);
        var cut = RenderPage();
        var asked = _engine.Lists.Count;
        Assert.Empty(cut.FindAll(".app-filter-bar"));

        Assert.Equal(["Refresh"], ActionMenuItems(cut));
        cut.Find("[role='menuitem']").Click();

        cut.WaitForAssertion(() => Assert.True(_engine.Lists.Count > asked));
    }

    [Fact]
    public void Search_AsksForTheTypedTitle_AfterItSettles_AndDevicesAreFilteredToo()
    {
        SignIn(Role.Member);
        _engine.OnList = _ => Page([Entry("a")]);
        var cut = RenderPage();

        cut.Find(".activity-toolbar__search input").Input("Tremors");
        cut.WaitForAssertion(() => Assert.Equal("Tremors", _engine.Lists.Last().Search), TimeSpan.FromSeconds(5));
        Assert.Contains("Search: Tremors", cut.Find(".app-active-filter-summary").TextContent, StringComparison.Ordinal);

        OpenFilters(cut);
        cut.Find("[data-activity-device-filter]").Input("Kobo");
        cut.WaitForAssertion(() => Assert.Equal("Kobo", _engine.Lists.Last().Device), TimeSpan.FromSeconds(5));
    }

    // ---- grouping ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ByDay_GroupsRowsUnderDateHeadings_AndByMonthUnderMonthHeadings()
    {
        SignIn(Role.Member);
        _engine.OnList = _ => Page([Entry("today"), Entry("yesterday", started: Utc(1)), Entry("earlier", started: Utc(40))]);

        var cut = RenderPage();

        var headings = cut.FindAll(".activity-section__heading").Select(heading => heading.TextContent).ToArray();
        Assert.Equal("Today", headings[0]);
        Assert.Equal("Yesterday", headings[1]);
        Assert.Equal(3, headings.Length);

        cut.Find("select[aria-label='Group the list']").Change("month");
        cut.WaitForAssertion(() => Assert.Equal("This month", cut.FindAll(".activity-section__heading")[0].TextContent));
        // Dates only change the headings: the Engine is still asked for single entries.
        Assert.False(_engine.Lists.Last().GroupByTitle);
    }

    [Fact]
    public void ByTitle_AsksForGroups_AndAGroupExpandsToItsAttemptsWithTheirDatePercentAndOutcome()
    {
        SignIn(Role.Member);
        var tremors = Group("g1", "Tremors",
            Entry("t1", title: "Tremors", seconds: 30 * 60, progress: 31, started: "2026-09-06T20:00:00Z"),
            Entry("t2", title: "Tremors", seconds: 12 * 60, progress: 43, started: "2026-09-09T20:00:00Z"),
            Entry("t3", title: "Tremors", seconds: 54 * 60, progress: 100, finished: true, started: "2026-09-13T20:00:00Z", device: "Phone"));
        _engine.OnList = request => request.GroupByTitle ? Page([tremors]) : Page([Entry("t3", title: "Tremors")]);
        var cut = RenderPage();

        cut.Find("select[aria-label='Group the list']").Change("title");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-activity-group]")));

        Assert.True(_engine.Lists.Last().GroupByTitle);
        Assert.Empty(cut.FindAll(".activity-section__heading"));
        var group = cut.Find("[data-activity-group]");
        // One entry for the title, not three.
        Assert.Single(cut.FindAll("[data-activity-row]:not([data-activity-attempt])"));
        Assert.Equal("3 sessions", group.QuerySelector("[data-activity-what]")!.TextContent);
        Assert.Equal("1 h 36 m", group.QuerySelector("[data-activity-how-long]")!.TextContent);
        Assert.Empty(cut.FindAll("[data-activity-attempt]"));

        cut.Find("[data-activity-toggle='attempts']").Click();

        var attempts = cut.FindAll("[data-activity-attempts] [data-activity-attempt]");
        Assert.Equal(3, attempts.Count);
        Assert.Contains("31%", attempts[0].TextContent, StringComparison.Ordinal);
        Assert.Equal("Stopped", attempts[0].QuerySelector("[data-activity-outcome]")!.TextContent);
        Assert.Contains("43%", attempts[1].TextContent, StringComparison.Ordinal);
        Assert.Equal("Finished", attempts[2].QuerySelector("[data-activity-outcome]")!.TextContent);
        Assert.Equal("Phone", attempts[2].QuerySelector("[data-activity-device]")!.TextContent);
        Assert.Equal("true", cut.Find("[data-activity-toggle='attempts']").GetAttribute("aria-expanded"));

        cut.Find("[data-activity-toggle='attempts']").Click();
        Assert.Empty(cut.FindAll("[data-activity-attempt]"));
    }

    [Fact]
    public void ShowEverySession_TurnsTheTitleGroupsIntoAFlatList()
    {
        SignIn(Role.Member);
        _engine.OnList = request => request.GroupByTitle
            ? Page([Group("g1", "Tremors", Entry("t1"), Entry("t2"))])
            : Page([Entry("t2"), Entry("t1")]);
        var cut = RenderPage();
        // Only the title list has groups to flatten, so the switch is off the table until it is chosen.
        Assert.True(cut.Find("input[role='switch']").HasAttribute("disabled"));

        cut.Find("select[aria-label='Group the list']").Change("title");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-activity-group]")));
        Assert.False(cut.Find("input[role='switch']").HasAttribute("disabled"));

        cut.Find("input[role='switch']").Change(true);

        cut.WaitForAssertion(() => Assert.Equal(["t2", "t1"], RowIds(cut)));
        Assert.False(_engine.Lists.Last().GroupByTitle);
        Assert.Empty(cut.FindAll("[data-activity-group]"));
    }

    [Fact]
    public void AMusicSession_IsOneRowWithTheCollage_AndShowSongsAsksForTheSongsOfThatSession()
    {
        SignIn(Role.Member);
        var session = Session("S1");
        _engine.OnList = request => request.ExpandSongs
            ? Page([Session("S1", withSongs: [Entry("s1", PersonalActivityKindValues.Listen, "Song A", seconds: 200, progress: null), Entry("s2", PersonalActivityKindValues.Listen, "Song B", seconds: 180, progress: null)])])
            : Page([session]);
        var cut = RenderPage();

        var row = cut.Find("[data-activity-row='S1']");
        Assert.Equal("Played", row.QuerySelector("[data-activity-what]")!.TextContent);
        Assert.Equal(9, row.QuerySelectorAll("[data-activity-collage] img").Length);
        Assert.Contains("50 songs from 12 albums", row.TextContent, StringComparison.Ordinal);
        Assert.Equal("1 h 05 m", row.QuerySelector("[data-activity-how-long]")!.TextContent);
        Assert.Empty(cut.FindAll("[data-activity-songs]"));
        Assert.DoesNotContain(_engine.Lists, request => request.ExpandSongs);

        cut.Find("[data-activity-toggle='songs']").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-activity-song]").Count));
        Assert.Equal(["Song A", "Song B"], cut.FindAll(".activity-song__title").Select(title => title.TextContent).ToArray());
        var songsRequest = _engine.Lists.Single(request => request.ExpandSongs);
        // The songs are asked for just under the end of the session, never for the whole history.
        Assert.Equal(1, songsRequest.Limit);
        Assert.NotNull(songsRequest.To);
    }

    // ---- date rail -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDateRail_ListsYearsWithMonthsFromTheEngine_AndJumpingAsksForTheEndOfThatPeriod()
    {
        SignIn(Role.Member);
        _engine.OnList = _ => Page([Entry("a")]);
        _engine.OnPeriods = _ => new PersonalActivityPeriodsDto(
        [
            new(2026, 14, [new(10, 9), new(9, 5)]),
            new(2025, 3, [new(12, 3)]),
        ]);
        var cut = RenderPage();

        var rail = cut.Find("[data-activity-rail]");
        Assert.Equal(["2026", "2025"], rail.QuerySelectorAll(".view-timeline-scrubber__year > button span").Select(span => span.TextContent).ToArray());
        // The year in view is open to its months.
        Assert.Equal(["Oct", "Sep"], rail.QuerySelectorAll(".view-timeline-scrubber__months em").Select(month => month.TextContent).ToArray());
        Assert.Contains("14", rail.QuerySelector(".view-timeline-scrubber__year > button")!.GetAttribute("aria-label"), StringComparison.Ordinal);

        rail.QuerySelectorAll(".view-timeline-scrubber__months button").Last().Click();
        cut.WaitForAssertion(() => Assert.Equal(ActivityListPresentation.JumpBefore(2026, 9), _engine.Lists.Last().To));
        Assert.Contains("From Sep 2026 back", cut.Find(".app-active-filter-summary").TextContent, StringComparison.Ordinal);
        // Jumping repositions the list; the rail keeps its counts.
        Assert.Single(_engine.Periods);

        cut.Find("[data-activity-rail]").QuerySelectorAll(".view-timeline-scrubber__year > button").Last().Click();
        cut.WaitForAssertion(() => Assert.Equal(ActivityListPresentation.JumpBefore(2025, null), _engine.Lists.Last().To));

        cut.FindAll(".app-active-filter-summary__chip").Single(chip => chip.TextContent.Contains("back", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Null(_engine.Lists.Last().To));
    }

    [Fact]
    public void AJumpNeverWidensAnEarlierEndDateTheUserChose()
    {
        SignIn(Role.Member);
        _engine.OnList = _ => Page([Entry("a")]);
        _engine.OnPeriods = _ => new PersonalActivityPeriodsDto([new(2026, 2, [new(10, 1), new(3, 1)])]);
        var cut = RenderPage();
        OpenFilters(cut);
        cut.Find("[data-activity-to]").Input("2026-04-10");
        cut.WaitForAssertion(() => Assert.Equal(ActivityListPresentation.StartOfLocalDay("2026-04-11"), _engine.Lists.Last().To));

        cut.Find("[data-activity-rail]").QuerySelectorAll(".view-timeline-scrubber__months button").Last().Click();

        // March 2026 ends before the chosen end date, so the jump wins; October would not have.
        cut.WaitForAssertion(() => Assert.Equal(ActivityListPresentation.JumpBefore(2026, 3), _engine.Lists.Last().To));
    }

    [Fact]
    public void OnAPhone_TheRailBecomesTheJumpToPeriodPicker_AndFiltersLiveInASheet()
    {
        _device = Device("phone");
        SignIn(Role.Member);
        _engine.OnList = _ => Page([Entry("a")]);
        _engine.OnPeriods = _ => new PersonalActivityPeriodsDto([new(2026, 1, [new(10, 1)]), new(2025, 1, [new(1, 1)])]);

        var cut = RenderPage();

        Assert.Contains("activity-page--phone", cut.Find("[data-activity-page]").ClassName, StringComparison.Ordinal);
        Assert.Equal(["2026", "2025"], cut.Find("select[aria-label='Jump to period']").QuerySelectorAll("option").Select(option => option.TextContent).ToArray());
        // Filters collapse into one button; the controls only draw once it is opened.
        Assert.Empty(cut.FindAll("[data-activity-filters]"));
        var filters = cut.FindAll("button").Single(button => button.TextContent.Contains("Filters", StringComparison.Ordinal));

        filters.Click();

        Assert.NotEmpty(cut.FindAll("[data-activity-filters]"));
        // The popover is a desktop surface: the phone uses the sheet only.
        Assert.Empty(cut.FindAll("[data-activity-filters-button]"));
    }

    // ---- paging, export, states ----------------------------------------------------------------------------------------------

    [Fact]
    public void LoadMore_AppendsTheNextPage_UsingTheCursor_AndGoesWhenThereIsNone()
    {
        SignIn(Role.Member);
        _engine.OnList = request => request.Cursor is null
            ? Page([Entry("a"), Entry("b")], next: "c1")
            : Page([Entry("c")]);
        var cut = RenderPage();
        Assert.Equal(["a", "b"], RowIds(cut));

        cut.Find("[data-activity-load-more]").Click();

        cut.WaitForAssertion(() => Assert.Equal(["a", "b", "c"], RowIds(cut)));
        Assert.Equal("c1", _engine.Lists.Last().Cursor);
        Assert.Empty(cut.FindAll("[data-activity-load-more]"));
    }

    [Fact]
    public void Export_IsADownloadOfExactlyTheCurrentFilters_ThroughTheDashboard()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = _ => Page([Entry("a")]);
        var cut = RenderPage();
        cut.Find("[data-activity-scope]").Change("everyone");
        cut.WaitForAssertion(() => Assert.Equal("everyone", _engine.Lists.Last().Scope));
        OpenFilters(cut);
        cut.Find("[data-activity-kind-filter='watch']").Change(true);
        cut.Find("[data-activity-outcome-filter]").Change("not_finished");
        cut.WaitForAssertion(() => Assert.Equal("not_finished", _engine.Lists.Last().Outcome));

        var export = cut.Find("[data-activity-export]");

        Assert.Equal("a", export.TagName.ToLowerInvariant());
        Assert.NotNull(export.GetAttribute("download"));
        var href = export.GetAttribute("href")!;
        Assert.StartsWith("/engine-activity-export?", href, StringComparison.Ordinal);
        Assert.Contains("scope=everyone", href, StringComparison.Ordinal);
        Assert.Contains("kind=watch", href, StringComparison.Ordinal);
        Assert.Contains("outcome=not_finished", href, StringComparison.Ordinal);
        // Paging and grouping are never part of an export.
        Assert.DoesNotContain("cursor", href, StringComparison.Ordinal);
        Assert.DoesNotContain("limit", href, StringComparison.Ordinal);
        Assert.DoesNotContain("group", href, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyList_SaysSo_AndWithFiltersSaysNothingMatches()
    {
        SignIn(Role.Member);
        _engine.OnList = _ => Page([]);
        var cut = RenderPage();
        Assert.Contains("Nothing here yet", cut.Find("[data-activity-state='empty']").TextContent, StringComparison.Ordinal);

        OpenFilters(cut);
        cut.Find("[data-activity-outcome-filter]").Change("finished");

        cut.WaitForAssertion(() => Assert.Contains("Nothing matches these filters", cut.Find("[data-activity-state='empty']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheEngineCannotBeReached_ThePageSaysSo_AndTryAgainAsksAgain()
    {
        SignIn(Role.Member);
        _engine.OnListOutcome = _ => PersonalActivityOutcome<PersonalActivityPageDto>.Failed(PersonalActivityOutcomeStatus.Unavailable);
        var cut = RenderPage();
        Assert.NotEmpty(cut.FindAll("[data-activity-state='error']"));

        _engine.OnListOutcome = null;
        _engine.OnList = _ => Page([Entry("a")]);
        cut.FindAll("button").Single(button => button.TextContent.Contains("Try again", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(["a"], RowIds(cut)));
    }

    [Fact]
    public void AScopeThatNoLongerExists_OffersBackToMyActivity()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnListOutcome = request => request.Scope == "me"
            ? PersonalActivityOutcome<PersonalActivityPageDto>.Ok(Page([Entry("a")]))
            : PersonalActivityOutcome<PersonalActivityPageDto>.Failed(PersonalActivityOutcomeStatus.NotFound, "That person or household was not found.");
        var cut = RenderPage(scope: $"profile:{Guid.NewGuid():D}");
        Assert.NotEmpty(cut.FindAll("[data-activity-state='not-found']"));

        cut.FindAll("button").Single(button => button.TextContent.Contains("Back to my activity", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(["a"], RowIds(cut)));
    }

    // ---- administrator PIN ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenTheEngineSaysUnlockIsRequired_ThePinIsAsked_AndTheListIsAskedAgainOnceItIsEntered()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnListOutcome = request => request.Scope == "everyone" && !_identity.Unlocked
            ? PersonalActivityOutcome<PersonalActivityPageDto>.Failed(PersonalActivityOutcomeStatus.UnlockRequired, "Unlock administrator access to see other people's activity.")
            : PersonalActivityOutcome<PersonalActivityPageDto>.Ok(Page([Entry("a", person: "Anna")], others: true));
        var cut = RenderPage();

        cut.Find("[data-activity-scope]").Change("everyone");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".admin-unlock")));
        Assert.NotEmpty(cut.FindAll("[data-activity-state='locked']"));
        _identity.Unlocked = true;
        foreach (var digit in "1234")
        {
            await cut.InvokeAsync(() => cut.Find($".pin-pad__key[aria-label='{digit}']").Click());
        }

        await cut.InvokeAsync(() => cut.FindAll(".admin-unlock button").Single(button => button.TextContent.Contains("Continue", StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() => Assert.Equal(["a"], RowIds(cut)));
        Assert.Empty(cut.FindAll(".admin-unlock"));
        Assert.Equal(1, _identity.Unlocks);
        // The PIN never reaches the page.
        Assert.DoesNotMatch(@"(?<![0-9a-fA-F])1234(?![0-9a-fA-F])", cut.Markup);
    }

    // ---- deleting history ----------------------------------------------------------------------------------------------------

    /// <summary>Opens the "…" menu and returns what it offers; only a server administrator is offered Delete history.</summary>
    private static string[] ActionMenuItems(IRenderedComponent<ActivityPage> cut)
    {
        cut.Find("button[aria-label='Activity actions']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        return [.. cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim())];
    }

    private static void OpenDelete(IRenderedComponent<ActivityPage> cut)
    {
        cut.Find("button[aria-label='Activity actions']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Contains("Delete history", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-activity-delete-dialog]")));
    }

    [Fact]
    public void DeleteHistory_ShowsThePreviewCount_AndNeedsTheWordDeleteBeforeItGoesAhead_ThenRefreshes()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = _ => Page([Entry("a")]);
        _engine.PreviewCount = 12;
        _engine.DeletedCount = 12;
        var cut = RenderPage();
        OpenDelete(cut);
        var confirm = () => cut.Find("[data-activity-delete-confirm-button]");

        // Nothing is counted, and nothing can be confirmed, until there is a date.
        Assert.True(confirm().HasAttribute("disabled"));
        cut.Find("[data-activity-delete-before]").Input("2026-01-01");
        cut.WaitForAssertion(() => Assert.Contains("12 entries will be deleted", cut.Find("[data-activity-delete-preview]").TextContent, StringComparison.Ordinal));
        Assert.Equal(("me", ActivityListPresentation.StartOfLocalDay("2026-01-01")), _engine.Previews.Single());
        Assert.True(confirm().HasAttribute("disabled"));

        cut.Find("[data-activity-delete-confirm]").Input("delete");
        Assert.True(confirm().HasAttribute("disabled"));
        cut.Find("[data-activity-delete-confirm]").Input("DELETE");
        Assert.False(confirm().HasAttribute("disabled"));

        var listsBefore = _engine.Lists.Count;
        confirm().Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-activity-delete-dialog]")));
        Assert.Equal(("me", ActivityListPresentation.StartOfLocalDay("2026-01-01")), _engine.Deletes.Single());
        cut.WaitForAssertion(() => Assert.True(_engine.Lists.Count > listsBefore));
    }

    [Fact]
    public void DeleteHistory_CanTargetAnotherScope_AndRecountsForIt()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = _ => Page([Entry("a")]);
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Contains("Anna (Home)", cut.Find("[data-activity-scope]").TextContent, StringComparison.Ordinal));
        OpenDelete(cut);
        cut.Find("[data-activity-delete-before]").Input("2026-01-01");
        cut.WaitForAssertion(() => Assert.Single(_engine.Previews));

        cut.Find("[data-activity-delete-scope]").Change($"profile:{AnnaProfile:D}");

        cut.WaitForAssertion(() => Assert.Equal(2, _engine.Previews.Count));
        Assert.Equal($"profile:{AnnaProfile:D}", _engine.Previews.Last().Scope);
    }

    [Fact]
    public async Task DeleteHistory_AsksForThePinWhenTheEngineSaysUnlockIsRequired_AndCountsAgainAfterwards()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = _ => Page([Entry("a")]);
        _engine.PreviewOutcome = () => _identity.Unlocked
            ? PersonalActivityOutcome<PersonalActivityDeletePreviewDto>.Ok(new(3))
            : PersonalActivityOutcome<PersonalActivityDeletePreviewDto>.Failed(PersonalActivityOutcomeStatus.UnlockRequired, "Unlock administrator access.");
        var cut = RenderPage();
        OpenDelete(cut);

        cut.Find("[data-activity-delete-before]").Input("2026-01-01");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".admin-unlock")));
        _identity.Unlocked = true;
        foreach (var digit in "5678")
        {
            await cut.InvokeAsync(() => cut.Find($".pin-pad__key[aria-label='{digit}']").Click());
        }

        await cut.InvokeAsync(() => cut.FindAll(".admin-unlock button").Single(button => button.TextContent.Contains("Continue", StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() => Assert.Contains("3 entries will be deleted", cut.Find("[data-activity-delete-preview]").TextContent, StringComparison.Ordinal));
    }

    private static async Task EnterPinAsync(IRenderedComponent<ActivityPage> cut, string pin)
    {
        foreach (var digit in pin)
        {
            await cut.InvokeAsync(() => cut.Find($".pin-pad__key[aria-label='{digit}']").Click());
        }

        await cut.InvokeAsync(() => cut.FindAll(".admin-unlock button").Single(button => button.TextContent.Contains("Continue", StringComparison.Ordinal)).Click());
    }

    [Fact]
    public async Task DeleteHistoryCount_ThatStillSaysUnlockIsRequiredAfterAGoodPin_ShowsAnErrorInsteadOfThePinPadAgain()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = _ => Page([Entry("a")]);
        _engine.PreviewOutcome = () =>
            PersonalActivityOutcome<PersonalActivityDeletePreviewDto>.Failed(PersonalActivityOutcomeStatus.UnlockRequired);
        var cut = RenderPage();
        OpenDelete(cut);
        cut.Find("[data-activity-delete-before]").Input("2026-01-01");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".admin-unlock")));
        _identity.Unlocked = true;

        await EnterPinAsync(cut, "5678");

        cut.WaitForAssertion(() => Assert.Contains("still locked", cut.Find("[data-activity-delete-error]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".admin-unlock"));
        Assert.Equal(1, _identity.Unlocks);
        Assert.Equal(2, _engine.Previews.Count);
    }

    [Fact]
    public async Task DeleteHistory_ThatStillSaysUnlockIsRequiredAfterAGoodPin_ShowsAnErrorInsteadOfThePinPadAgain_AndDeletesNothing()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = _ => Page([Entry("a")]);
        _engine.PreviewCount = 4;
        _engine.DeleteOutcome = () =>
            PersonalActivityOutcome<PersonalActivityDeleteResultDto>.Failed(PersonalActivityOutcomeStatus.UnlockRequired);
        var cut = RenderPage();
        OpenDelete(cut);
        cut.Find("[data-activity-delete-before]").Input("2026-01-01");
        cut.WaitForAssertion(() => Assert.Contains("4 entries", cut.Find("[data-activity-delete-preview]").TextContent, StringComparison.Ordinal));
        cut.Find("[data-activity-delete-confirm]").Input("DELETE");
        cut.Find("[data-activity-delete-confirm-button]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".admin-unlock")));
        _identity.Unlocked = true;

        await EnterPinAsync(cut, "5678");

        cut.WaitForAssertion(() => Assert.Contains("still locked", cut.Find("[data-activity-delete-error]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".admin-unlock"));
        Assert.Equal(1, _identity.Unlocks);
        Assert.Equal(2, _engine.Deletes.Count);
        Assert.NotEmpty(cut.FindAll("[data-activity-delete-dialog]"));
    }

    [Fact]
    public void DeleteHistory_ThatTheEngineRefuses_StaysOpenWithTheReason_AndDeletesNothing()
    {
        SignIn(Role.ServerAdministrator);
        _engine.OnList = _ => Page([Entry("a")]);
        _engine.PreviewCount = 4;
        _engine.DeleteOutcome = () => PersonalActivityOutcome<PersonalActivityDeleteResultDto>.Failed(PersonalActivityOutcomeStatus.Forbidden, "Only a server administrator can delete activity history.");
        var cut = RenderPage();
        OpenDelete(cut);
        cut.Find("[data-activity-delete-before]").Input("2026-01-01");
        cut.WaitForAssertion(() => Assert.Contains("4 entries", cut.Find("[data-activity-delete-preview]").TextContent, StringComparison.Ordinal));
        cut.Find("[data-activity-delete-confirm]").Input("DELETE");

        cut.Find("[data-activity-delete-confirm-button]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Only a server administrator", cut.Find("[data-activity-delete-error]").TextContent, StringComparison.Ordinal));
        Assert.NotEmpty(cut.FindAll("[data-activity-delete-dialog]"));
    }

    // ---- the stand-ins for the Engine ----------------------------------------------------------------------------------------

    private sealed class FakeActivityEngine
    {
        public List<PersonalActivityRequest> Lists { get; } = [];
        public List<PersonalActivityRequest> Periods { get; } = [];
        public List<(string Scope, DateTimeOffset Before)> Previews { get; } = [];
        public List<(string Scope, DateTimeOffset Before)> Deletes { get; } = [];
        public Func<PersonalActivityRequest, PersonalActivityPageDto> OnList { get; set; } = _ => new([], null, false);
        public Func<PersonalActivityRequest, PersonalActivityOutcome<PersonalActivityPageDto>>? OnListOutcome { get; set; }
        public Func<PersonalActivityRequest, PersonalActivityPeriodsDto> OnPeriods { get; set; } = _ => new([]);
        public Func<PersonalActivityOutcome<PersonalActivityDeletePreviewDto>>? PreviewOutcome { get; set; }
        public Func<PersonalActivityOutcome<PersonalActivityDeleteResultDto>>? DeleteOutcome { get; set; }
        public int PreviewCount { get; set; }
        public int DeletedCount { get; set; }

        public void Configure(EngineApiClientStub stub)
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPersonalActivityAsync), args =>
            {
                var request = (PersonalActivityRequest)args![0]!;
                Lists.Add(request);
                return Task.FromResult(OnListOutcome?.Invoke(request) ?? PersonalActivityOutcome<PersonalActivityPageDto>.Ok(OnList(request)));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetPersonalActivityPeriodsAsync), args =>
            {
                var request = (PersonalActivityRequest)args![0]!;
                Periods.Add(request);
                return Task.FromResult(PersonalActivityOutcome<PersonalActivityPeriodsDto>.Ok(OnPeriods(request)));
            });
            stub.SetHandler(nameof(IEngineApiClient.PreviewPersonalActivityDeletionAsync), args =>
            {
                Previews.Add(((string)args![0]!, (DateTimeOffset)args[1]!));
                return Task.FromResult(PreviewOutcome?.Invoke()
                    ?? PersonalActivityOutcome<PersonalActivityDeletePreviewDto>.Ok(new(PreviewCount)));
            });
            stub.SetHandler(nameof(IEngineApiClient.DeletePersonalActivityAsync), args =>
            {
                Deletes.Add(((string)args![0]!, (DateTimeOffset)args[1]!));
                return Task.FromResult(DeleteOutcome?.Invoke()
                    ?? PersonalActivityOutcome<PersonalActivityDeleteResultDto>.Ok(new(DeletedCount)));
            });
        }
    }

    private sealed class IdentityClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://engine.example.test") };
    }

    /// <summary>Plays the Engine's identity side: who is in which household, and whether the administrator PIN is accepted.</summary>
    private sealed class IdentityHandler : HttpMessageHandler
    {
        public Role Role { get; set; }
        public bool Unlocked { get; set; }
        public int Unlocks { get; private set; }
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            switch (path)
            {
                case "/access/admin-unlock":
                    Unlocks++;
                    return Task.FromResult(Json(new GrantAdminUnlockResponse(Unlocked, null, 1)));
                case "/access/households":
                    return Task.FromResult(Json(Role == Role.ServerAdministrator
                        ? new[] { Household(HomeHousehold, "Home"), Household(CabinHousehold, "Cabin") }
                        : [Household(HomeHousehold, "Home")]));
                case "/access/profiles":
                    return Task.FromResult(Json(Role == Role.ServerAdministrator
                        ? new[] { Person(OwnProfile, "Owner", HomeHousehold), Person(AnnaProfile, "Anna", HomeHousehold), Person(BenProfile, "Ben", HomeHousehold), Person(MaryProfile, "Mary", CabinHousehold) }
                        : [Person(OwnProfile, "Owner", HomeHousehold), Person(AnnaProfile, "Anna", HomeHousehold), Person(BenProfile, "Ben", HomeHousehold)]));
                default:
                    // Anything else (such as a fresh sign-in check) is not answered, which leaves the session as it is.
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }
        }

        private static HouseholdResponse Household(Guid id, string name) => new(id, name, 3, 1, DateTimeOffset.UtcNow);

        private static ManagedProfileResponse Person(Guid id, string name, Guid household) =>
            new(id, name, "#C9922E", null, DateTimeOffset.UtcNow, household);

        private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}
