using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Taste;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Where Taste sits in the Settings menu, under Personal &gt; Overview.</summary>
public sealed class TasteNavTests
{
    [Fact]
    public void Taste_IsTheChildOfOverview_AtTheOverviewTasteAddress_ForEveryKindOfPerson()
    {
        var personal = SettingsNav.MenuGroups.Single(group => group.Key == "personal");
        var overview = personal.Nodes.Single(node => node.Section == SettingsSection.Overview && node.Subsection is null);

        var taste = overview.Children.Single(child => child.Label == "Taste");
        Assert.Equal(SettingsSection.Overview, taste.Section);
        Assert.Equal(SettingsNav.TasteSubsection, taste.Subsection);
        Assert.Equal("/settings/overview/taste", taste.Route);
        Assert.Equal(SettingsNav.TasteRoute, SettingsNav.RouteFor(SettingsSection.Overview, SettingsNav.TasteSubsection));
        Assert.Equal("/settings/overview", overview.Route);
        Assert.NotNull(SettingsNav.ResolveSubsection(SettingsSection.Overview, SettingsNav.TasteSubsection));

        foreach (var (server, household) in new[] { (false, false), (false, true), (true, false), (true, true) })
        {
            var visible = SettingsNav.Flatten(SettingsNav.FilteredMenuGroups(server, household).Single(group => group.Key == "personal").Nodes);
            Assert.Contains(visible, node => node.Label == "Taste" && node.Route == SettingsNav.TasteRoute);
            Assert.True(SettingsNav.ResolveRoute("overview", server, household).RequestedSectionAllowed);
        }
    }

    [Fact]
    public void Taste_SitsUnderOverviewInTheBreadcrumb()
    {
        var location = SettingsNav.FindMenuLocation(SettingsSection.Overview, SettingsNav.TasteSubsection);

        Assert.NotNull(location);
        Assert.Equal(["Overview", "Taste"], location.Trail.Select(node => node.Label));
    }
}

/// <summary>
/// Personal &gt; Overview &gt; Taste (D34): your own taste with removable chips and an Add control, other people's taste read-only,
/// and a Scope filter for administrators only. The Engine decides what each person may see; the page only offers the choices.
/// </summary>
public sealed class OverviewTastePageTests : AsyncBunitContext
{
    private readonly TasteHandler _handler = new();
    private readonly DashboardSessionAccessor _session = new();
    private readonly List<string?> _reads = [];
    private readonly List<(string Group, string Value)> _added = [];
    private readonly List<(string Group, string Value)> _removed = [];
    private Func<string?, TasteViewDto?> _answer = _ => null;
    private bool _writesSucceed = true;

    private ProfileViewModel _activeProfile = null!;

    public OverviewTastePageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IHttpClientFactory>(new TasteClientFactory(_handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, true, true)).GetAwaiter().GetResult();
        Services.AddScoped(_ => device);
        Services.AddSingleton(_session);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel> { _activeProfile }));
            stub.SetHandler(nameof(IEngineApiClient.GetPersonalTasteAsync), args =>
            {
                _reads.Add((string?)args![0]);
                return Task.FromResult(_answer((string?)args[0]));
            });
            stub.SetHandler(nameof(IEngineApiClient.AddPersonalTasteItemAsync), args =>
            {
                _added.Add(((string)args![0]!, (string)args[1]!));
                return Task.FromResult(_writesSucceed);
            });
            stub.SetHandler(nameof(IEngineApiClient.RemovePersonalTasteItemAsync), args =>
            {
                _removed.Add(((string)args![0]!, (string)args[1]!));
                return Task.FromResult(_writesSucceed);
            });
        }));
    }

    // ----- Your own taste -----

    [Fact]
    public void YourOwnTaste_ShowsGroupsAsChipsWithARemoveXAndAnAddControl()
    {
        SignIn(Who.Member);
        _answer = _ => Own();

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-person]");
        Assert.Equal(["Genres", "Moods", "Eras"], cut.FindAll(".taste-group__title").Select(title => title.TextContent.Trim()));
        Assert.Equal("true", cut.Find("[data-taste-person]").GetAttribute("data-taste-editable"));
        Assert.Equal("You love sci-fi and drama.", cut.Find("[data-taste-sentence]").TextContent);
        var chip = cut.Find("[data-taste-item='sci-fi']");
        Assert.NotNull(chip.QuerySelector(".app-chip__remove"));
        Assert.Equal(3, cut.FindAll("[data-taste-add]").Count);
        Assert.Equal(3, cut.FindAll("[data-taste-add-button]").Count);
        Assert.Equal(5, cut.FindAll(".app-chip__remove").Count);
    }

    [Fact]
    public void AValueYouTyped_IsMarkedAddedByYou_AndALearnedOneIsNot()
    {
        SignIn(Who.Member);
        _answer = _ => Own();

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-person]");
        var added = cut.Find("[data-taste-item='western']");
        Assert.Equal("added", added.GetAttribute("data-taste-source"));
        Assert.Contains("Added by you", added.TextContent, StringComparison.Ordinal);
        var learned = cut.Find("[data-taste-item='sci-fi']");
        Assert.Equal("learned", learned.GetAttribute("data-taste-source"));
        Assert.DoesNotContain("Added by you", learned.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingAChip_AsksTheEngineToHideThatValue_ThenShowsTheNewTaste()
    {
        SignIn(Who.Member);
        _answer = _ => Own();
        var cut = Render<OverviewTastePage>();
        cut.WaitForElement("[data-taste-person]");
        _answer = _ => Own(withoutSciFi: true);

        await cut.InvokeAsync(() => cut.Find("[data-taste-item='sci-fi'] .app-chip__remove").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal([(TasteValues.Genres, "sci-fi")], _removed);
            Assert.Empty(cut.FindAll("[data-taste-item='sci-fi']"));
        });
    }

    [Fact]
    public async Task TypingAValueAndPressingAdd_AddsItToThatGroup_AndClearsTheBox()
    {
        SignIn(Who.Member);
        _answer = _ => Own();
        var cut = Render<OverviewTastePage>();
        cut.WaitForElement("[data-taste-person]");

        cut.Find("[data-taste-add='moods'] input").Input("gritty");
        await cut.InvokeAsync(() => cut.Find("[data-taste-add-button='moods']").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal([(TasteValues.Moods, "gritty")], _added);
            Assert.Equal(string.Empty, cut.Find("[data-taste-add='moods'] input").GetAttribute("value") ?? string.Empty);
        });
    }

    [Fact]
    public void TheAddButton_IsOffUntilSomethingIsTyped()
    {
        SignIn(Who.Member);
        _answer = _ => Own();

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-person]");
        Assert.True(cut.Find("[data-taste-add-button='genres']").HasAttribute("disabled"));
        cut.Find("[data-taste-add='genres'] input").Input("noir");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-taste-add-button='genres']").HasAttribute("disabled")));
    }

    [Fact]
    public async Task ASuggestionChip_AddsThatKnownValue_AndTheLearnedOnesAreNotSuggestedAgain()
    {
        SignIn(Who.Member);
        _answer = _ => Own();
        var cut = Render<OverviewTastePage>();
        cut.WaitForElement("[data-taste-suggestions='genres']");

        var suggestions = cut.FindAll("[data-taste-suggestions='genres'] .app-chip").Select(chip => chip.TextContent.Trim()).ToList();
        Assert.Contains("thriller", suggestions);
        Assert.DoesNotContain("sci-fi", suggestions);
        await cut.InvokeAsync(() => cut.FindAll("[data-taste-suggestions='genres'] .app-chip").First(chip => chip.TextContent.Contains("thriller", StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() => Assert.Equal([(TasteValues.Genres, "thriller")], _added));
    }

    [Fact]
    public void TypingNarrowsTheSuggestions()
    {
        SignIn(Who.Member);
        _answer = _ => Own();
        var cut = Render<OverviewTastePage>();
        cut.WaitForElement("[data-taste-suggestions='genres']");

        cut.Find("[data-taste-add='genres'] input").Input("thri");

        cut.WaitForAssertion(() =>
            Assert.Equal(["thriller"], cut.FindAll("[data-taste-suggestions='genres'] .app-chip").Select(chip => chip.TextContent.Trim())));
    }

    [Fact]
    public async Task WhenTheEngineRefusesAChange_ThePersonIsToldAndNothingIsHidden()
    {
        SignIn(Who.Member);
        _answer = _ => Own();
        var cut = Render<OverviewTastePage>();
        cut.WaitForElement("[data-taste-person]");
        _writesSucceed = false;

        cut.Find("[data-taste-add='eras'] input").Input("1980");
        await cut.InvokeAsync(() => cut.Find("[data-taste-add-button='eras']").Click());

        cut.WaitForAssertion(() => Assert.Contains("An era is a decade", cut.Find("[data-taste-message]").TextContent, StringComparison.Ordinal));
        Assert.NotNull(cut.Find("[data-taste-item='sci-fi']"));
    }

    [Fact]
    public void AnEmptyGroup_SaysSo_AndInvitesYouToAdd()
    {
        SignIn(Who.Member);
        _answer = _ => new TasteViewDto { People = [new TastePersonDto { ProfileId = Guid.NewGuid().ToString("D"), IsYou = true }] };

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-person]");
        Assert.Equal(3, cut.FindAll("[data-taste-empty]").Count);
        Assert.Contains("Add a genre", cut.Find("[data-taste-group='genres'] [data-taste-empty]").TextContent, StringComparison.Ordinal);
    }

    // ----- Who gets a Scope filter -----

    [Fact]
    public void AMember_HasNoScopeFilter_AndNeverAsksForOtherPeople()
    {
        SignIn(Who.Member);
        _answer = _ => Own();

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-person]");
        Assert.Empty(cut.FindAll("[data-taste-scope]"));
        Assert.Equal([null], _reads);
        Assert.DoesNotContain(_handler.Paths, path => path.StartsWith("/access/", StringComparison.Ordinal));
    }

    [Fact]
    public void AHouseholdAdministrator_CanPickMe_TheirHousehold_OrAPersonInIt_ButNotEveryone()
    {
        SignIn(Who.HouseholdAdministrator);
        _answer = _ => Own();

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-scope-select]");
        Assert.Equal(["Me", "Household: Home", "Anna", "Ben"], ScopeLabels(cut));
        Assert.DoesNotContain(ScopeValues(cut), value => value == "everyone");
        Assert.DoesNotContain(ScopeValues(cut), value => value == $"profile:{_handler.OwnerProfile}");
    }

    [Fact]
    public void AServerAdministrator_CanPickMe_Everyone_AnyHousehold_OrAnyPerson()
    {
        SignIn(Who.ServerAdministrator);
        _answer = _ => Own();

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-scope-select]");
        Assert.Equal(["Me", "Everyone", "Household: Cabin", "Household: Home", "Anna", "Ben", "Mary"], ScopeLabels(cut));
        Assert.Contains($"household:{_handler.HomeHousehold}", ScopeValues(cut));
        Assert.Contains($"profile:{_handler.MaryProfile}", ScopeValues(cut));
    }

    [Fact]
    public void AHouseholdAdministratorWhoseChoicesAreRefused_SeesOnlyThemselves()
    {
        _handler.ChoicesStatus = HttpStatusCode.Forbidden;
        SignIn(Who.HouseholdAdministrator);
        _answer = _ => Own();

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-person]");
        Assert.Empty(cut.FindAll("[data-taste-scope]"));
    }

    [Fact]
    public void AServerAdministratorWhoseChoicesAreRefused_CanStillAskForEveryone_AndTheEngineDecides()
    {
        _handler.ChoicesStatus = HttpStatusCode.Forbidden;
        SignIn(Who.ServerAdministrator);
        _answer = _ => Own();

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-scope-select]");
        Assert.Equal(["Me", "Everyone"], ScopeLabels(cut));
    }

    // ----- Other people's taste is read-only -----

    [Fact]
    public async Task APersonInTheHousehold_IsShownReadOnly_WithNoXAndNoAddControl()
    {
        SignIn(Who.HouseholdAdministrator);
        var anna = Guid.NewGuid();
        _answer = scope => scope is null ? Own() : Others(anna, "Anna", scope);
        var cut = Render<OverviewTastePage>();
        cut.WaitForElement("[data-taste-scope-select]");

        await cut.InvokeAsync(() => cut.Find("[data-taste-scope-select]").Change($"profile:{_handler.AnnaProfile}"));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal($"profile:{_handler.AnnaProfile}", _reads.Last());
            var person = cut.Find("[data-taste-person]");
            Assert.Equal("false", person.GetAttribute("data-taste-editable"));
            Assert.Contains("Anna", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Empty(cut.FindAll(".app-chip__remove"));
        Assert.Empty(cut.FindAll("[data-taste-add]"));
        Assert.Empty(cut.FindAll("[data-taste-add-button]"));
        Assert.Empty(cut.FindAll("[data-taste-suggestions]"));
        Assert.NotNull(cut.Find("[data-taste-item='drama']"));
        Assert.Contains("Added by them", cut.Find("[data-taste-item='western']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Everyone_ShowsEachPersonByName_AndOnlyYourOwnPanelCanBeChanged()
    {
        SignIn(Who.ServerAdministrator);
        var anna = Guid.NewGuid();
        _answer = scope => scope is null
            ? Own()
            : new TasteViewDto
            {
                Scope = "everyone",
                IncludesOthers = true,
                People =
                [
                    new TastePersonDto { ProfileId = _handler.OwnerProfile.ToString("D"), Person = "Owner", IsYou = true, Genres = [new("drama", 0.5, "learned")] },
                    new TastePersonDto { ProfileId = anna.ToString("D"), Person = "Anna", Genres = [new("horror", 0.5, "learned")] },
                ],
            };
        var cut = Render<OverviewTastePage>();
        cut.WaitForElement("[data-taste-scope-select]");

        await cut.InvokeAsync(() => cut.Find("[data-taste-scope-select]").Change("everyone"));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-taste-person]").Count));
        var mine = cut.Find($"[data-taste-person='{_handler.OwnerProfile:D}']");
        var theirs = cut.Find($"[data-taste-person='{anna:D}']");
        Assert.Equal("true", mine.GetAttribute("data-taste-editable"));
        Assert.Equal("false", theirs.GetAttribute("data-taste-editable"));
        Assert.NotEmpty(mine.QuerySelectorAll(".app-chip__remove"));
        Assert.Empty(theirs.QuerySelectorAll(".app-chip__remove"));
        Assert.Empty(theirs.QuerySelectorAll("[data-taste-add]"));
        Assert.Contains("Anna", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheEngineRefusesAScope_ThePageSaysSo_AndMeStillWorks()
    {
        SignIn(Who.HouseholdAdministrator);
        _answer = scope => scope is null ? Own() : null;
        var cut = Render<OverviewTastePage>();
        cut.WaitForElement("[data-taste-scope-select]");

        await cut.InvokeAsync(() => cut.Find("[data-taste-scope-select]").Change($"profile:{_handler.BenProfile}"));

        cut.WaitForAssertion(() => Assert.Contains("isn't available", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-taste-person]"));
        await cut.InvokeAsync(() => cut.Find("[data-taste-scope-select]").Change("me"));
        cut.WaitForElement("[data-taste-person]");
    }

    [Fact]
    public void WhenTheEngineIsUnreachable_ThePageOffersARetry()
    {
        SignIn(Who.Member);
        _answer = _ => null;

        var cut = Render<OverviewTastePage>();

        cut.WaitForAssertion(() => Assert.Contains("Taste couldn't be loaded", cut.Markup, StringComparison.Ordinal));
    }

    // ----- The Overview row and the Settings shell lead here -----

    [Fact]
    public void TheOverviewRowLink_AndTheSettingsShell_PointAtThisPage()
    {
        var root = RepositoryRoot();
        var overview = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Settings", "Overview", "OverviewPage.razor"));
        var settings = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Pages", "Settings.razor"));

        Assert.Contains("SettingsNav.TasteRoute", overview, StringComparison.Ordinal);
        Assert.Contains("SettingsNav.TasteSubsection", settings, StringComparison.Ordinal);
        Assert.Contains("<OverviewTastePage />", settings, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private static string[] ScopeLabels(IRenderedComponent<OverviewTastePage> cut) =>
        cut.Find("[data-taste-scope-select]").QuerySelectorAll("option").Select(option => option.TextContent.Trim()).ToArray();

    private static string[] ScopeValues(IRenderedComponent<OverviewTastePage> cut) =>
        cut.Find("[data-taste-scope-select]").QuerySelectorAll("option").Select(option => option.GetAttribute("value") ?? string.Empty).ToArray();

    // ----- The redesigned page -----

    [Fact]
    public void TheSentenceNamesTheTopThreeGenres_AndTheDonutShowsEachWithItsShare()
    {
        SignIn(Who.Member);
        _answer = _ => Own(extraGenres: true);

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-person]");
        Assert.Equal("You love sci-fi, drama and mystery.", cut.Find("[data-taste-sentence]").TextContent);
        var slices = cut.FindAll("[data-taste-slice]").Select(slice => slice.GetAttribute("data-taste-slice")!).ToArray();
        Assert.Equal(["Sci-fi", "Drama", "Mystery", "Western"], slices);
        Assert.Equal("40%", cut.Find("[data-taste-slice='Sci-fi'] .taste-donut__percent").TextContent);
        Assert.Contains("Your genres: Sci-fi 40%", cut.Find(".taste-donut__ring").GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFormatMixShowsEachKindOfMediaWithItsShare()
    {
        SignIn(Who.Member);
        _answer = _ => Own();

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-formats]");
        Assert.Equal("60%", cut.Find("[data-taste-format='Movies'] .taste-formats__percent").TextContent);
        Assert.Equal("40%", cut.Find("[data-taste-format='Books'] .taste-formats__percent").TextContent);
        Assert.NotNull(cut.Find(".taste-formats__segment--movies"));
        Assert.NotNull(cut.Find(".taste-formats__segment--books"));
    }

    [Fact]
    public void HowYourTasteShifts_DrawsAColumnPerMonth_AndSaysHowManyTitlesItIsBasedOn()
    {
        SignIn(Who.Member);
        _answer = _ => Own(shifts: true);

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-shifts]");
        Assert.Equal(["2026-09", "2026-10"], cut.FindAll("[data-taste-shift-month]").Select(column => column.GetAttribute("data-taste-shift-month")!).ToArray());
        Assert.Equal(["Drama", "Sci-fi", "Other"], cut.FindAll("[data-taste-shift-key]").Select(key => key.GetAttribute("data-taste-shift-key")!).ToArray());
        Assert.StartsWith("Based on 12 finished titles", cut.Find("[data-taste-basis]").TextContent.Trim(), StringComparison.Ordinal);
        Assert.Contains("How your taste shifts", cut.Find("[data-taste-shifts-card]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void BeforeTheLibraryHasWorkedATasteOut_TheSentenceAndTheMonthlyChartWait()
    {
        SignIn(Who.Member);
        _answer = _ =>
        {
            var view = Own(shifts: true);
            view.People[0].LastUpdatedAt = null;
            return view;
        };

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-person]");
        Assert.NotEmpty(cut.FindAll("[data-taste-waiting]"));
        Assert.Empty(cut.FindAll("[data-taste-sentence]"));
        Assert.Empty(cut.FindAll("[data-taste-shifts]"));
    }

    [Fact]
    public void AKidsProfile_SeesItsOwnTasteWithNoRemoveCrossesAndNoAddControls()
    {
        SignIn(Who.Kids);
        _answer = _ => Own();

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-person]");
        Assert.Equal("false", cut.Find("[data-taste-person]").GetAttribute("data-taste-editable"));
        Assert.Empty(cut.FindAll(".app-chip__remove"));
        Assert.Empty(cut.FindAll("[data-taste-add]"));
        Assert.Empty(cut.FindAll("[data-taste-add-button]"));
        Assert.NotEmpty(cut.FindAll("[data-taste-item='sci-fi']"));
    }

    [Fact]
    public void ThereAreNoAiControls_StarRatings_OrDiscoveryDial_OnThePage()
    {
        SignIn(Who.Member);
        _answer = _ => Own();

        var cut = Render<OverviewTastePage>();

        cut.WaitForElement("[data-taste-person]");
        foreach (var word in new[] { "More like this", "Less like this", "mood lately", "Discovery", "streak", "Rating" })
        {
            Assert.DoesNotContain(word, cut.Markup, StringComparison.OrdinalIgnoreCase);
        }
    }

    private TasteViewDto Own(bool withoutSciFi = false, bool extraGenres = false, bool shifts = false) => new()
    {
        Scope = "me",
        People =
        [
            new TastePersonDto
            {
                ProfileId = _handler.OwnerProfile.ToString("D"),
                IsYou = true,
                Summary = "You like stories.",
                Genres = withoutSciFi
                    ? [new("drama", 0.4, TasteValues.SourceLearned)]
                    : extraGenres
                        ? [new("sci-fi", 0.4, TasteValues.SourceLearned), new("drama", 0.3, TasteValues.SourceLearned), new("mystery", 0.2, TasteValues.SourceLearned), new("western", 0.1, TasteValues.SourceLearned)]
                        : [new("sci-fi", 0.6, TasteValues.SourceLearned), new("drama", 0.4, TasteValues.SourceLearned)],
                Moods = [new("cozy", 0.7, TasteValues.SourceLearned), new("western", null, TasteValues.SourceAdded)],
                Eras = [new("1990s", 1, TasteValues.SourceLearned)],
                MediaMix = new Dictionary<string, double> { ["Movies"] = 0.6, ["Books"] = 0.4 },
                LastUpdatedAt = "2026-10-01T00:00:00.0000000Z",
                FinishedTitleCount = shifts ? 12 : 0,
                Shifts = shifts
                    ?
                    [
                        new TasteMonthDto { Month = "2026-09", Finished = 3, Genres = new() { ["sci-fi"] = 2, ["drama"] = 1 } },
                        new TasteMonthDto { Month = "2026-10", Finished = 5, Genres = new() { ["sci-fi"] = 1, ["drama"] = 3 } },
                    ]
                    : [],
            },
        ],
        Suggestions = new TasteSuggestionsDto
        {
            Genres = ["sci-fi", "drama", "thriller", "comedy"],
            Moods = ["cozy", "gritty"],
            Eras = ["2020s", "1990s"],
        },
    };

    private static TasteViewDto Others(Guid person, string name, string scope) => new()
    {
        Scope = scope,
        IncludesOthers = true,
        People =
        [
            new TastePersonDto
            {
                ProfileId = person.ToString("D"),
                Person = name,
                Genres = [new("drama", 0.5, TasteValues.SourceLearned), new("western", null, TasteValues.SourceAdded)],
            },
        ],
        Suggestions = new TasteSuggestionsDto { Genres = ["thriller"] },
    };

    private void SignIn(Who who)
    {
        var authority = who switch
        {
            Who.ServerAdministrator => new DashboardAuthorityResponse(
                _handler.OwnerAccount, _handler.OwnerProfile, true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], ["access.manage"]),
            Who.HouseholdAdministrator => new DashboardAuthorityResponse(
                _handler.OwnerAccount, _handler.OwnerProfile, true, true, 1, 1, false, false, null, 1, [], [], ["household.manage"]),
            Who.Kids => new DashboardAuthorityResponse(
                _handler.OwnerAccount, _handler.OwnerProfile, true, true, 1, 1, false, false, null, 1,
                [new AccountProfileGrantDto(_handler.OwnerAccount, _handler.OwnerProfile, "Kid", null, true, true, false,
                    new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow, IsRestricted: true)],
                [], []),
            _ => new DashboardAuthorityResponse(_handler.OwnerAccount, _handler.OwnerProfile, true, true, 1, 1, false, false, null, 1, [], [], []),
        };
        _handler.Who = who;
        _activeProfile = new ProfileViewModel(_handler.OwnerProfile, "Test User", "#C9922E", who == Who.Kids ? "RestrictedProfile" : "Profile",
            new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero));
        _session.Set("session", _handler.OwnerAccount, _handler.OwnerProfile, Guid.NewGuid(), authority);
    }

    private enum Who
    {
        Member,
        HouseholdAdministrator,
        ServerAdministrator,
        Kids,
    }

    private sealed class TasteClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://engine.example.test") };
    }

    /// <summary>Plays the Engine's household and people lists: a server administrator gets everyone, a household administrator their own household.</summary>
    private sealed class TasteHandler : HttpMessageHandler
    {
        public Guid HomeHousehold { get; } = Guid.NewGuid();
        public Guid CabinHousehold { get; } = Guid.NewGuid();
        public Guid OwnerAccount { get; } = Guid.NewGuid();
        public Guid OwnerProfile { get; } = Guid.NewGuid();
        public Guid AnnaProfile { get; } = Guid.NewGuid();
        public Guid BenProfile { get; } = Guid.NewGuid();
        public Guid MaryProfile { get; } = Guid.NewGuid();
        public Who Who { get; set; }
        public HttpStatusCode ChoicesStatus { get; set; } = HttpStatusCode.OK;
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (ChoicesStatus != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(ChoicesStatus));
            }

            var server = Who == Who.ServerAdministrator;
            var owner = new ManagedProfileResponse(OwnerProfile, "Owner", "#fff", null, DateTimeOffset.UtcNow, HomeHousehold);
            var anna = new ManagedProfileResponse(AnnaProfile, "Anna", "#fff", null, DateTimeOffset.UtcNow, HomeHousehold);
            var ben = new ManagedProfileResponse(BenProfile, "Ben", "#fff", null, DateTimeOffset.UtcNow, HomeHousehold);
            var mary = new ManagedProfileResponse(MaryProfile, "Mary", "#fff", null, DateTimeOffset.UtcNow, CabinHousehold);
            var response = path switch
            {
                "/access/profiles" => Json(server ? new[] { owner, anna, ben, mary } : [owner, anna, ben]),
                "/access/households" => Json(server
                    ? new[] { new HouseholdResponse(HomeHousehold, "Home", 3, 3, DateTimeOffset.UtcNow), new HouseholdResponse(CabinHousehold, "Cabin", 1, 1, DateTimeOffset.UtcNow) }
                    : [new HouseholdResponse(HomeHousehold, "Home", 3, 3, DateTimeOffset.UtcNow)]),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
