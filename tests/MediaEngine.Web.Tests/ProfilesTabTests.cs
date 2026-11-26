using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>Settings &gt; Profiles: the administrators' people list. Household administrators see their own household; server administrators see every one.</summary>
public sealed class ProfilesTabTests : AsyncBunitContext
{
    private static readonly Guid HouseholdId = Guid.NewGuid();
    private static readonly Guid OtherHouseholdId = Guid.NewGuid();
    private static readonly Guid OwnId = Guid.NewGuid();
    private static readonly Guid KidId = Guid.NewGuid();
    private static readonly Guid SamId = Guid.NewGuid();
    private static readonly Guid AlexId = Guid.NewGuid();

    private readonly List<ManagedProfileResponse> _managed = [];
    private readonly List<HouseholdResponse> _households = [];
    private readonly List<RequestRecord> _requests = [];
    private DashboardAuthorityResponse? _authority;
    private string? _addPersonDetail;
    private HouseholdKidSettingsResponse _kidSettings = new(HouseholdId, "PG", "PG", "PG", true, ["G", "PG", "PG-13", "R"]);
    private string? _kidSettingsRefusal;
    private string? _renameRefusal;
    private string? _ceilingRefusal;
    private static readonly Guid MoviesId = Guid.NewGuid();
    private static readonly Guid MusicId = Guid.NewGuid();
    private static readonly Guid BooksId = Guid.NewGuid();
    private static readonly List<AccessLibraryOptionDto> Libraries =
    [
        new(MoviesId, "Movies", null, "watch"),
        new(MusicId, "Music", null, "listen"),
        new(BooksId, "Books", null, "read"),
    ];
    private ConfirmedActionRunnerTests.SpyConfirmer _confirmer = new(confirmed: false);

    public ProfilesTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton<IHttpClientFactory>(new ClientFactory(new ManagedHandler(this)));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<IItsYouConfirmer>(_ => _confirmer);
        Services.AddScoped<ConfirmedActionRunner>();
    }

    [Fact]
    public void Member_IsToldThePageIsNotAvailable_AndIsNotMoved_AndNoListOfPeopleIsRequested()
    {
        Setup(serverAdministrator: false, householdAdministrator: false);
        var before = Services.GetRequiredService<NavigationManager>().Uri;

        var cut = Render<ProfilesTab>();

        Assert.Contains("Not available", cut.Find(".app-page-state").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".profiles-tab__list"));
        Assert.Empty(cut.FindAll("[data-household-wide]"));
        Assert.Equal(before, Services.GetRequiredService<NavigationManager>().Uri);
        Assert.DoesNotContain(_requests, request => request.Path.StartsWith("/access/", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePage_TellsItsHostTheHouseholdsName_ForTheTitle()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Shy's household", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false, householdAdmin: true));
        var names = new List<string?>();

        var cut = Render<ProfilesTab>(parameters => parameters.Add(component => component.HouseholdNameChanged,
            EventCallback.Factory.Create<string?>(this, name => names.Add(name))));

        cut.WaitForAssertion(() => Assert.Equal("Shy's household", names.LastOrDefault()));
    }

    [Fact]
    public void People_AreListedInTheHouseholdsSavedOrder_WithADragHandleOnEachRow()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 3, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([
            Person(OwnId, "Test User", false, householdAdmin: true) with { SortPosition = 2 },
            Person(KidId, "Robin", true) with { SortPosition = 0 },
            Person(SamId, "Sam", false) with { SortPosition = 1 }]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));
        Assert.Equal(["Robin", "Sam", "Test User"], cut.FindAll(".profiles-tab__row-name").Select(name => name.TextContent.Trim()).Where(name => name != "Add profile"));
        Assert.Equal(3, cut.FindAll(".profiles-tab__drag").Count);
    }

    [Fact]
    public void DroppingAPersonOnAnother_SavesTheNewOrderForTheWholeHousehold()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 3, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([
            Person(OwnId, "Test User", false, householdAdmin: true) with { SortPosition = 0 },
            Person(KidId, "Robin", true) with { SortPosition = 1 },
            Person(SamId, "Sam", false) with { SortPosition = 2 }]);
        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));

        cut.Find($"[data-testid='reorder-{SamId}']").DragStart();
        cut.Find($"[data-profile-id='{OwnId}']").Drop();

        cut.WaitForAssertion(() =>
        {
            var request = Assert.Single(_requests, request => request.Path.EndsWith("/people/order", StringComparison.Ordinal));
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Contains(SamId.ToString("D"), request.Body, StringComparison.Ordinal);
            Assert.True(request.Body.IndexOf(SamId.ToString("D"), StringComparison.Ordinal) < request.Body.IndexOf(OwnId.ToString("D"), StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ArrowDownOnTheHandle_MovesThePersonOneStepDownAndSavesIt()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([
            Person(OwnId, "Test User", false, householdAdmin: true) with { SortPosition = 0 },
            Person(SamId, "Sam", false) with { SortPosition = 1 }]);
        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".profiles-tab__line").Count));

        cut.Find($"[data-testid='reorder-{OwnId}']").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" });

        cut.WaitForAssertion(() =>
        {
            var request = Assert.Single(_requests, request => request.Path.EndsWith("/people/order", StringComparison.Ordinal));
            Assert.True(request.Body.IndexOf(SamId.ToString("D"), StringComparison.Ordinal) < request.Body.IndexOf(OwnId.ToString("D"), StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ADragHandle_IsNotOfferedOnAPhone_OrForAHouseholdOfOne()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false, householdAdmin: true));

        var alone = Render<ProfilesTab>();
        alone.WaitForAssertion(() => Assert.Single(alone.FindAll(".profiles-tab__line")));
        Assert.Empty(alone.FindAll(".profiles-tab__drag"));
    }

    [Fact]
    public void HouseholdAdministrator_SeesOnePlainRowPerPerson_WithOneLineOfWords_AndNoBadgesOrEditButtons()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 3, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true), Person(KidId, "Robin", true), Person(SamId, "Sam", false)]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));
        Assert.Equal("Administrator · no sign-in", cut.Find($"[data-profile-line='{OwnId}']").TextContent);
        Assert.Equal("Kids", cut.Find($"[data-profile-line='{KidId}']").TextContent);
        Assert.Equal("Member · no sign-in · PIN", cut.Find($"[data-profile-line='{SamId}']").TextContent);
        Assert.Equal("list", cut.Find(".profiles-tab__list").GetAttribute("role"));
        Assert.Single(cut.FindAll(".profiles-tab__row--add"));

        // Your own row is the only one marked, and the words "Household administrator" never appear in the list.
        var rows = cut.FindAll(".profiles-tab__line");
        Assert.Contains("You", rows[0].TextContent);
        Assert.DoesNotContain("You", rows[1].TextContent);
        Assert.DoesNotContain("Household administrator", cut.Markup, StringComparison.Ordinal);
        Assert.All(cut.FindAll(".app-chip"), chip => Assert.Equal("You", chip.TextContent.Trim()));

        // No Edit button, no second control on a row: the whole row is the link.
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Edit");
        Assert.All(rows, row => Assert.Single(row.QuerySelectorAll("a")));

        // Only a server administrator adds households, and nobody switches profile from this page.
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Add household", StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("button, a"), control => control.TextContent.Contains("Switch", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(cut.FindAll("[data-profiles-households-link]"));
    }

    [Fact]
    public void EachRow_OpensThatPersonsPage_AndYourOwnRowOpensMyProfile()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".profiles-tab__line").Count));

        Assert.Equal($"/settings/household/{SamId:D}", cut.Find($"[data-profile-id='{SamId}'] a").GetAttribute("href"));
        Assert.Equal("/settings/overview", cut.Find($"[data-profile-id='{OwnId}'] a").GetAttribute("href"));
    }

    [Fact]
    public void ServerAdministrator_SeesEveryHousehold_OwnFirstWithoutAHeading_OthersUnderTheirName_WithALinkToUsersAndHouseholds()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.AddRange([
            new HouseholdResponse(OtherHouseholdId, "Cabin", 1, 1, DateTimeOffset.UtcNow),
            new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow),
        ]);
        _managed.AddRange([Person(AlexId, "Alex", false, OtherHouseholdId), Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));
        Assert.Equal(["For everyone in this household", "Cabin", "For everyone in Cabin"],
            cut.FindAll(".profiles-tab__group .app-settings-group__heading").Select(title => title.TextContent));
        var cabin = cut.Find($"[data-household-group='{OtherHouseholdId}']");
        Assert.Contains("Alex", cabin.TextContent);
        Assert.DoesNotContain("Sam", cabin.TextContent);
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Add household", StringComparison.Ordinal));
        Assert.Equal("/settings/access/households", cut.Find("[data-profiles-households-link] a").GetAttribute("href"));
        Assert.Equal(2, cut.FindAll(".profiles-tab__row--add").Count);
        Assert.DoesNotContain(cut.FindAll("button, a"), control => control.TextContent.Contains("Switch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnAPhone_ThePeopleCanBeAdded_ThereAreNoDragHandles_AndReorderIsInThePagesMenu()
    {
        UsePhone();
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false) with { SortPosition = 0 }, Person(SamId, "Sam", false) with { SortPosition = 1 }]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".profiles-tab__line").Count));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Add household");
        Assert.Empty(cut.FindAll(".profiles-tab__drag"));
        Assert.Empty(cut.FindAll("[data-testid^='move-up-']"));

        // Reorder is in the page's "..." menu; it swaps the rows to move-up and move-down buttons, and Done puts them away again.
        cut.Find("[data-household-actions] button[aria-label='More actions']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Reorder").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid^='move-up-']").Count));
        cut.Find($"[data-testid='move-down-{OwnId}']").Click();

        cut.WaitForAssertion(() =>
        {
            var request = Assert.Single(_requests, request => request.Path.EndsWith("/people/order", StringComparison.Ordinal));
            Assert.True(request.Body.IndexOf(SamId.ToString("D"), StringComparison.Ordinal) < request.Body.IndexOf(OwnId.ToString("D"), StringComparison.Ordinal));
        });

        // The add row is the same action as on a larger screen: it opens the profile editor sheet.
        cut.Find(".profiles-tab__row--add").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='dialog']")));
    }

    [Fact]
    public void AddHousehold_ExistsOnlyOnTheHouseholdsTab_ProfilesLinksThere()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false));

        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profiles-tab__line")));

        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Add household", StringComparison.Ordinal));
        var link = cut.Find("[data-profiles-households-link] a");
        Assert.Equal("/settings/access/households", link.GetAttribute("href"));
    }

    [Fact]
    public void AddHousehold_ButtonIsOnTheHouseholdsTabAndNotOnTheProfilesTab()
    {
        var root = FindRepoRoot();
        var profiles = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Settings", "ProfilesTab.razor"));
        var households = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Settings", "HouseholdsTab.razor"));

        Assert.DoesNotContain("Common_AddHousehold", profiles, StringComparison.Ordinal);
        Assert.Contains("Common_AddHousehold", households, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    [Fact]
    public void AddRow_DisappearsOnceTheHouseholdHasEightPeople()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _managed.Add(Person(OwnId, "Test User", false));
        for (var i = 1; i < 8; i++)
        {
            _managed.Add(Person(Guid.NewGuid(), $"Person {i}", false));
        }

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(8, cut.FindAll(".profiles-tab__line").Count));
        Assert.Empty(cut.FindAll(".profiles-tab__row--add"));
    }

    [Fact]
    public void AddRow_OpensTheSheet_AndAddsThePersonToTheHouseholdAdministratorsOwnHousehold()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false));

        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".profiles-tab__row--add")));
        cut.Find(".profiles-tab__row--add").Click();
        cut.WaitForAssertion(() => Assert.Equal("Add profile", cut.Find("#profile-editor-title").TextContent));
        cut.Find("input.profile-editor__name").Input("Sam");
        cut.Find(".profile-editor__save").Click();

        cut.WaitForAssertion(() => Assert.Contains(_requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/households/{HouseholdId:D}/people"));
    }

    [Fact]
    public void AddRow_ForAServerAdministrator_AddsToTheHouseholdWhoseRowWasChosen()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.AddRange([
            new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow),
            new HouseholdResponse(OtherHouseholdId, "Cabin", 1, 1, DateTimeOffset.UtcNow),
        ]);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(AlexId, "Alex", false, OtherHouseholdId)]);

        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".profiles-tab__row--add").Count));
        cut.Find("button[aria-label='Add profile to Cabin']").Click();
        cut.WaitForAssertion(() => Assert.Equal("Add profile", cut.Find("#profile-editor-title").TextContent));
        cut.Find("input.profile-editor__name").Input("Casey");
        cut.Find(".profile-editor__save").Click();

        cut.WaitForAssertion(() => Assert.Contains(_requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/households/{OtherHouseholdId:D}/people"));
        Assert.DoesNotContain(_requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/households/{HouseholdId:D}/people");
    }

    [Fact]
    public void HouseholdAdministrator_HasNoUsersPanel_AndNoDeleteUserAction()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 3, 2, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, email: "me@home.test"), Person(KidId, "Robin", true), Person(SamId, "Sam", false, email: "sam@home.test")]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));
        Assert.DoesNotContain("Users in your household", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("button"), button => (button.GetAttribute("aria-label") ?? string.Empty).StartsWith("Delete user", StringComparison.Ordinal));
        Assert.DoesNotContain(_requests, request => request.Path == "/access/accounts");
    }

    [Fact]
    public void EachPersonsLine_SaysTheirSignInEmailOrThatTheyHaveNone_ReadOnly()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 3, 2, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, email: "me@home.test"), Person(KidId, "Robin", true), Person(SamId, "Sam", false, email: "sam@home.test")]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));
        Assert.Equal("Administrator · me@home.test", cut.Find($"[data-profile-line='{OwnId}']").TextContent);
        Assert.Equal("Kids", cut.Find($"[data-profile-line='{KidId}']").TextContent);
        Assert.Equal("Member · sam@home.test · PIN", cut.Find($"[data-profile-line='{SamId}']").TextContent);

        // Read-only: no control on the row changes a sign-in.
        var line = cut.Find($"[data-profile-id='{SamId}']");
        Assert.All(line.QuerySelectorAll("button"), button => Assert.Contains("profiles-tab__drag", button.ClassName, StringComparison.Ordinal));
    }

    [Fact]
    public void HouseholdAndServerAdministrators_BothReadAdministrator_InEveryHouseholdsList()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.AddRange([
            new HouseholdResponse(OtherHouseholdId, "Cabin", 1, 1, DateTimeOffset.UtcNow),
            new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow),
        ]);
        _managed.AddRange([
            Person(OwnId, "Test User", false, householdAdmin: true, serverAdmin: true, email: "me@home.test"),
            Person(SamId, "Sam", false),
            Person(AlexId, "Alex", false, OtherHouseholdId, householdAdmin: true, email: "alex@cabin.test")]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));
        Assert.StartsWith("Administrator", cut.Find($"[data-profile-line='{OwnId}']").TextContent, StringComparison.Ordinal);
        Assert.StartsWith("Administrator", cut.Find($"[data-profile-line='{AlexId}']").TextContent, StringComparison.Ordinal);
        Assert.StartsWith("Member", cut.Find($"[data-profile-line='{SamId}']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-role]"));
    }

    // ---- The household's name is the page's title; only a server administrator, the one the Engine lets rename it, gets the pencil

    [Fact]
    public void ServerAdministrator_RenamesTheirHousehold_FromThePencil_AndTheTitleFollows()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false));
        var names = new List<string?>();
        var cut = Render<ProfilesTab>(parameters => parameters.Add(component => component.HouseholdNameChanged,
            EventCallback.Factory.Create<string?>(this, name => names.Add(name))));
        cut.WaitForAssertion(() => Assert.Equal("Home", names.LastOrDefault()));

        cut.Find("[data-household-actions] button[aria-label='Rename Home']").Click();
        cut.Find("[data-household-rename] input").Input("Lake house");
        cut.FindAll("[data-household-rename] button").Single(button => button.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Equal("Lake house", names.LastOrDefault()));
        var put = Assert.Single(_requests, request => request.Method == HttpMethod.Put && request.Path == $"/access/households/{HouseholdId:D}");
        Assert.Equal("Lake house", System.Text.Json.JsonSerializer.Deserialize<RenameHouseholdRequest>(put.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Name);
        Assert.Empty(cut.FindAll("[data-household-rename]"));
    }

    [Fact]
    public void HouseholdAdministrator_SeesTheNameOnly_NoPencil()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false, householdAdmin: true));

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profiles-tab__line")));
        Assert.Empty(cut.FindAll("[data-testid='rename-household']"));
        Assert.DoesNotContain(cut.FindAll("button"), button => (button.GetAttribute("aria-label") ?? string.Empty).StartsWith("Rename", StringComparison.Ordinal));
    }

    [Fact]
    public void Rename_RefusedByTheEngine_ShowsItsSentenceBesideTheField_AndKeepsTheName()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false));
        _renameRefusal = "That name is already used.";
        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profiles-tab__line")));

        cut.Find("[data-household-actions] button[aria-label='Rename Home']").Click();
        cut.Find("[data-household-rename] input").Input("Cabin");
        cut.FindAll("[data-household-rename] button").Single(button => button.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Equal("That name is already used.", cut.Find("[data-household-rename-error]").TextContent));
        Assert.NotEmpty(cut.FindAll("[data-household-rename] input"));
    }

    [Fact]
    public void InsideTheSettingsFrame_TheActionsGoToTheFramesHeader_NotIntoThePage()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false));
        var header = new SettingsHeaderActions();

        var cut = Render<ProfilesTab>(parameters => parameters.AddCascadingValue(header));

        // The rename pencil sits right beside the household's name; nothing is left for the right-hand slot with one person on a desktop.
        cut.WaitForAssertion(() => Assert.NotNull(header.TitleAccessory));
        Assert.Null(header.Content);
        Assert.Empty(cut.FindAll("[data-household-actions]"));
    }

    [Fact]
    public void ThePage_TellsItsHostHowManyPeopleAreInTheHousehold_ForTheLineUnderTheTitle()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 3, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true), Person(KidId, "Robin", true), Person(SamId, "Sam", false)]);
        var counts = new List<int>();

        Render<ProfilesTab>(parameters => parameters.Add(component => component.PeopleCountChanged,
            EventCallback.Factory.Create<int>(this, count => counts.Add(count))));

        Assert.Equal(3, counts.LastOrDefault());
    }

    [Fact]
    public void WhatKidsCanSeeAndLibraries_AreFullRowsWithAOneLineSummary_NotSmallExpandButtons()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false));

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() =>
        {
            var kids = cut.Find($"[data-kid-settings='{HouseholdId}'] [data-nav-row='kid-settings']");
            Assert.Contains("What Kids can see", kids.TextContent, StringComparison.Ordinal);
            Assert.Equal("Up to PG for Watch, Read and Listen", kids.QuerySelector(".settings-nav-row__status")!.TextContent);
            Assert.Equal("false", kids.GetAttribute("aria-expanded"));
            var libraries = cut.Find("[data-household-ceiling] [data-nav-row='household-ceiling']");
            Assert.Equal("All 3 libraries", libraries.QuerySelector(".settings-nav-row__status")!.TextContent);
        });
    }

    [Fact]
    public void KidSettingsSummary_NamesEachAreaWhenTheyDiffer_AndSaysWhenUnratedItemsAreHidden()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _kidSettings = new HouseholdKidSettingsResponse(HouseholdId, "G", "PG", "PG-13", false, ["G", "PG", "PG-13", "R"]);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false, householdAdmin: true));

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal("Watch G · Read PG · Listen PG-13 · unrated items hidden",
            cut.Find($"[data-kid-settings='{HouseholdId}'] .settings-nav-row__status").TextContent));
    }

    [Fact]
    public void AddProfile_IsARowLikeTheOthers_WithTheSpaceOfTheDragHandleSoItsCircleLinesUp()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true), Person(SamId, "Sam", false)]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() =>
        {
            var add = cut.Find(".profiles-tab__item--add");
            Assert.NotNull(add.QuerySelector(".profiles-tab__drag-spacer"));
            Assert.Contains("Add profile", add.TextContent, StringComparison.Ordinal);
        });
    }

    // ---- Libraries this household can use sits with What Kids can see, for the person the Engine lets change it (a server administrator)

    [Fact]
    public void ServerAdministrator_SeesLibrariesThisHouseholdCanUse_BesideWhatKidsCanSee()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false));

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"[data-household-wide='{HouseholdId}'] [data-household-ceiling='{HouseholdId}']")));
        var wide = cut.Find($"[data-household-wide='{HouseholdId}']");
        Assert.NotNull(wide.QuerySelector("[data-kid-settings]"));
        Assert.Contains("Libraries this household can use", wide.TextContent, StringComparison.Ordinal);
        Assert.Contains("For everyone in this household", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Ask who", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HouseholdAdministrator_SeesWhatKidsCanSee_ButNotTheLibrariesRow_AndNoLibraryListIsRequested()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false, householdAdmin: true));

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"[data-household-wide='{HouseholdId}'] [data-kid-settings]")));
        Assert.Empty(cut.FindAll("[data-household-ceiling]"));
        Assert.DoesNotContain(_requests, request => request.Path == "/access/libraries");
    }

    private IRenderedComponent<ProfilesTab> OpenCeiling()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false));
        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-household-ceiling] [data-nav-row='household-ceiling']")));
        cut.Find("[data-household-ceiling] [data-nav-row='household-ceiling']").Click();
        return cut;
    }

    private static AngleSharp.Dom.IElement Tick(IRenderedComponent<ProfilesTab> cut, Guid libraryId) =>
        cut.Find($"[data-household-ceiling] [data-library-id='{libraryId}'] input");

    [Fact]
    public void Ceiling_ListsLibrariesUnderTheirAreas_AllTickedByDefault_AndNeverTheVault()
    {
        var cut = OpenCeiling();

        var ceiling = cut.Find("[data-household-ceiling]");
        Assert.Equal(["watch", "listen", "read"], ceiling.QuerySelectorAll("[data-ceiling-area]").Select(area => area.GetAttribute("data-ceiling-area")!).ToArray());
        Assert.All(ceiling.QuerySelectorAll("[data-library-id] input"), tick => Assert.NotNull(tick.GetAttribute("checked")));
        Assert.DoesNotContain("Vault", string.Concat(ceiling.QuerySelectorAll("[data-ceiling-area]").Select(area => area.TextContent)), StringComparison.Ordinal);
    }

    [Fact]
    public void Ceiling_UntickingALibrary_AndSaving_SavesThatHouseholdsHiddenList()
    {
        var cut = OpenCeiling();

        Tick(cut, MoviesId).Change(false);
        Assert.DoesNotContain(_requests, request => request.Method == HttpMethod.Put && request.Path.EndsWith("/libraries", StringComparison.Ordinal));
        cut.FindAll(".app-save-bar button").Single(button => button.TextContent.Trim() == "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Contains("Saved · 1 hidden", cut.Find("[data-household-ceiling-saved]").TextContent));
        var put = Assert.Single(_requests, request => request.Method == HttpMethod.Put && request.Path.EndsWith("/libraries", StringComparison.Ordinal));
        Assert.Equal($"/access/households/{HouseholdId:D}/libraries", put.Path);
        Assert.Equal([MoviesId], System.Text.Json.JsonSerializer.Deserialize<ReplaceHouseholdLibrariesRequest>(put.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.HiddenLibraryIds);
        Assert.Empty(cut.FindAll(".app-save-bar"));
    }

    [Fact]
    public void Ceiling_RefusedByTheEngine_ShowsItsSentence_AndKeepsTheTicksForAnotherTry()
    {
        _ceilingRefusal = "That library can't be hidden for a household.";
        var cut = OpenCeiling();

        Tick(cut, BooksId).Change(false);
        cut.FindAll(".app-save-bar button").Single(button => button.TextContent.Trim() == "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Equal("That library can't be hidden for a household.", cut.Find("[data-household-ceiling-error]").TextContent));
        Assert.NotEmpty(cut.FindAll(".app-save-bar"));
        Assert.Null(Tick(cut, BooksId).GetAttribute("checked"));
    }

    // ---- Loading, error and empty

    [Fact]
    public void WhenTheEngineCannotBeReached_ThePageShowsTheUnreachableState_WithTryAgain_ThatReadsThePeopleAgain()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Contains("Can't reach the Engine", cut.Find(".app-page-state").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".profiles-tab__list"));

        _managed.Add(Person(OwnId, "Test User", false, householdAdmin: true));
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Try again").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".profiles-tab__line")));
        Assert.Empty(cut.FindAll(".app-page-state"));
    }

    [Fact]
    public void OnlyYou_ShowsYourOwnRowAndAddProfile()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false, householdAdmin: true));

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".profiles-tab__line")));
        Assert.Contains("You", cut.Find($"[data-profile-id='{OwnId}']").TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".profiles-tab__row--add"));
    }

    [Fact]
    public void AddProfile_AtEight_IsRefusedWithTheEnginesSentence()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false, householdAdmin: true, email: "me@home.test"));
        _addPersonDetail = "A household holds up to 8 people.";

        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".profiles-tab__row--add")));
        cut.Find(".profiles-tab__row--add").Click();
        cut.WaitForAssertion(() => Assert.Equal("Add profile", cut.Find("#profile-editor-title").TextContent));
        cut.Find("input.profile-editor__name").Input("Sam");
        cut.Find(".profile-editor__save").Click();

        cut.WaitForAssertion(() => Assert.Contains("A household holds up to 8 people.", cut.Find(".who-page__error").TextContent));
    }

    // ---- What Kids can see (library access D9, D10): a level each for Watch, Read and Listen, and one Show unrated items switch

    private IRenderedComponent<ProfilesTab> OpenKidSettings(Guid householdId)
    {
        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"[data-kid-settings='{householdId}']")));
        cut.Find($"[data-kid-settings='{householdId}'] [data-nav-row='kid-settings']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"[data-kid-settings='{householdId}'] [data-kid-level]")));
        return cut;
    }

    private static AngleSharp.Dom.IElement Level(IRenderedComponent<ProfilesTab> cut, string area) =>
        cut.Find($"[data-kid-settings] [data-kid-level='{area}'] select");

    private static AngleSharp.Dom.IElement ShowUnrated(IRenderedComponent<ProfilesTab> cut) =>
        cut.Find("[data-kid-settings] [data-kid-unrated] input[role='switch']");

    private IEnumerable<RequestRecord> KidSettingsSaves() =>
        _requests.Where(request => request.Method == HttpMethod.Put && request.Path.EndsWith("/kid-settings", StringComparison.Ordinal));

    [Fact]
    public void KidSettings_ShowALevelForWatchReadAndListen_AndOneShowUnratedSwitch_WithTheHouseholdsValues()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true), Person(SamId, "Sam", false)]);
        _kidSettings = _kidSettings with { ReadLevel = "R", ShowUnrated = false };

        var cut = OpenKidSettings(HouseholdId);

        Assert.Equal(["watch", "read", "listen"], cut.FindAll("[data-kid-level]").Select(row => row.GetAttribute("data-kid-level")!).ToArray());
        Assert.Equal("PG", Level(cut, "watch").GetAttribute("value"));
        Assert.Equal("R", Level(cut, "read").GetAttribute("value"));
        Assert.Equal("PG", Level(cut, "listen").GetAttribute("value"));
        // Each choice says what it shows.
        Assert.Equal(["G: only items for all ages", "PG: some parental guidance", "PG-13: teens and up", "R: almost everything"],
            Level(cut, "watch").QuerySelectorAll("option").Select(option => option.TextContent.Trim()).ToArray());
        Assert.False(ShowUnrated(cut).HasAttribute("checked"));
        Assert.Equal("Show unrated items", ShowUnrated(cut).GetAttribute("aria-label"));
        // Nothing to save until something changes.
        Assert.Empty(cut.FindAll(".app-save-bar"));
        Assert.Empty(KidSettingsSaves());
    }

    [Fact]
    public void KidSettings_ChangingLevelsAndTheSwitch_SavesAllTogetherOnSaveChanges()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true), Person(SamId, "Sam", false)]);
        var cut = OpenKidSettings(HouseholdId);

        Level(cut, "watch").Change("PG-13");
        Level(cut, "listen").Change("G");
        ShowUnrated(cut).Change(false);
        Assert.Empty(KidSettingsSaves());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".app-save-bar")));
        cut.FindAll(".app-save-bar button").Single(button => button.TextContent.Trim() == "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Single(KidSettingsSaves()));
        var put = Assert.Single(KidSettingsSaves());
        Assert.Equal($"/access/households/{HouseholdId:D}/kid-settings", put.Path);
        using var body = System.Text.Json.JsonDocument.Parse(put.Body);
        Assert.Equal("PG-13", body.RootElement.GetProperty("watch_level").GetString());
        Assert.Equal("PG", body.RootElement.GetProperty("read_level").GetString());
        Assert.Equal("G", body.RootElement.GetProperty("listen_level").GetString());
        Assert.False(body.RootElement.GetProperty("show_unrated").GetBoolean());
        cut.WaitForAssertion(() => Assert.False(string.IsNullOrWhiteSpace(cut.Find("[data-kid-settings-saved]").TextContent)));
        Assert.Empty(cut.FindAll(".app-save-bar"));
    }

    [Fact]
    public void KidSettings_Discard_PutsTheChoicesBack_AndSendsNothing()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true), Person(SamId, "Sam", false)]);
        var cut = OpenKidSettings(HouseholdId);

        Level(cut, "read").Change("R");
        ShowUnrated(cut).Change(false);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".app-save-bar")));
        cut.FindAll(".app-save-bar button").Single(button => button.TextContent.Trim() == "Discard").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".app-save-bar")));
        Assert.Equal("PG", Level(cut, "read").GetAttribute("value"));
        Assert.True(ShowUnrated(cut).HasAttribute("checked"));
        Assert.Empty(KidSettingsSaves());
    }

    [Fact]
    public void KidSettings_WhenTheEngineRefuses_ShowItsSentence_AndKeepTheChanges()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true), Person(SamId, "Sam", false)]);
        _kidSettingsRefusal = "Choose a Watch level: G, PG, PG-13 or R.";
        var cut = OpenKidSettings(HouseholdId);

        Level(cut, "watch").Change("R");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".app-save-bar")));
        cut.FindAll(".app-save-bar button").Single(button => button.TextContent.Trim() == "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Contains("Choose a Watch level", cut.Find("[data-kid-settings-error]").TextContent));
        Assert.NotEmpty(cut.FindAll(".app-save-bar"));
    }

    [Fact]
    public void KidSettings_SitUnderEachHousehold_AndAreEditableOnAPhone()
    {
        UsePhone();
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.AddRange([
            new HouseholdResponse(OtherHouseholdId, "Cabin", 1, 1, DateTimeOffset.UtcNow),
            new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow),
        ]);
        _managed.AddRange([Person(AlexId, "Alex", false, OtherHouseholdId), Person(OwnId, "Test User", false)]);
        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-kid-settings]").Count));
        cut.Find($"[data-kid-settings='{HouseholdId}'] [data-nav-row='kid-settings']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"[data-kid-settings='{HouseholdId}'] select")));

        Assert.All(cut.FindAll($"[data-kid-settings='{HouseholdId}'] select, [data-kid-settings='{HouseholdId}'] input"), control => Assert.False(control.HasAttribute("disabled")));
    }

    // Profile and household D7: the row is one link with no Edit button and no badges, so the stylesheet has no cell for either.
    [Fact]
    public void TheStylesheet_HasNoEditButtonOrBadgeCell_BecauseTheWholeRowIsTheLink()
    {
        var css = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "ProfilesTab.razor.css"));

        Assert.DoesNotContain("profiles-tab__edit", css, StringComparison.Ordinal);
        Assert.DoesNotContain("profiles-tab__badges", css, StringComparison.Ordinal);
        Assert.Contains(".profiles-tab__open { flex: 1; min-width: 0; display: flex;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHouseholdPage_HasNoAskWhosWatchingSwitch_AndNoMemberRedirect()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "ProfilesTab.razor"));

        Assert.DoesNotContain("Nav.NavigateTo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AskWho", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("who's watching when", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PickerManageButton_ShowsPencilsInPlace_AndEachPencilGoesToThatPersonsPage_ExceptOnTelevision()
    {
        var picker = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "ProfilePickerPage.razor"));
        var menu = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Navigation", "TopNavAccountMenu.razor"));

        Assert.Contains("OnClick=\"StartManagingAsync\"", picker, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(ProfilePageHref(profile));", picker, StringComparison.Ordinal);
        Assert.Contains("/settings/household/{profile.Id:D}", picker, StringComparison.Ordinal);
        Assert.Contains("_managing = true;", picker, StringComparison.Ordinal);
        Assert.Contains("\"television\"", picker, StringComparison.Ordinal);
        Assert.Contains("ManageProfilesHref = \"/settings/household\"", menu, StringComparison.Ordinal);
    }

    private void UsePhone()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true)).GetAwaiter().GetResult();
        Services.AddScoped(_ => device);
    }

    private void Setup(bool serverAdministrator, bool householdAdministrator)
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
                Task.FromResult(new List<ProfileViewModel>
                {
                    new(OwnId, "Test User", "#C9922E", "Profile", DateTimeOffset.UtcNow),
                    new(KidId, "Robin", "#2E8BC9", "RestrictedProfile", DateTimeOffset.UtcNow),
                    new(SamId, "Sam", "#2EC98B", "Profile", DateTimeOffset.UtcNow, HasPin: true),
                }));
        });
        Services.AddSingleton(api);

        var grants = new[] { OwnId, KidId, SamId }.Select(id => new AccountProfileGrantDto(id, id, "Test User", null,
            true, true, true, new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow)).ToList();
        string[] actions = serverAdministrator ? ["access.manage"] : householdAdministrator ? ["household.manage"] : [];
        string[] navigation = serverAdministrator ? ["settings.administration"] : [];
        var authority = new DashboardAuthorityResponse(OwnId, OwnId, true, true, 1, 1, serverAdministrator, serverAdministrator, null, 1,
            grants, navigation, actions);
        var session = new DashboardSessionAccessor();
        _authority = authority;
        session.Set("test-session-token", OwnId, OwnId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
    }

    private static ManagedProfileResponse Person(Guid id, string name, bool restricted, Guid? household = null,
        bool householdAdmin = false, bool serverAdmin = false, string? email = null) =>
        new(id, name, "#C9922E", null, DateTimeOffset.UtcNow.AddMinutes(id == OwnId ? -3 : 0), household ?? HouseholdId, restricted,
            IsServerAdmin: serverAdmin, IsHouseholdAdmin: householdAdmin,
            User: email is null ? null : new ManagedProfileUserDto(Guid.NewGuid(), email));

    private static string GetRepoFile(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine([root, .. parts]);
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Body);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class ManagedHandler(ProfilesTabTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add(new RequestRecord(request.Method, path, body));

            if (request.Method == HttpMethod.Get && path == "/access/profiles")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._managed) };
            }

            if (request.Method == HttpMethod.Get && path == "/access/households")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._households) };
            }

            if (request.Method == HttpMethod.Get && path == "/access/libraries")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Libraries) };
            }

            if (request.Method == HttpMethod.Put && path.EndsWith("/libraries", StringComparison.Ordinal))
            {
                if (owner._ceilingRefusal is { } ceilingRefusal)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { title = "Bad request.", status = 400, detail = ceilingRefusal }) };
                }

                var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
                var householdId = Guid.Parse(path["/access/households/".Length..^"/libraries".Length]);
                var hidden = System.Text.Json.JsonSerializer.Deserialize<ReplaceHouseholdLibrariesRequest>(body, options)!.HiddenLibraryIds;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new HouseholdLibraryAccessResponse(householdId, hidden)) };
            }

            if (request.Method == HttpMethod.Put && path.StartsWith("/access/households/", StringComparison.Ordinal) && path.Count(character => character == '/') == 3)
            {
                if (owner._renameRefusal is { } renameRefusal)
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { title = "Conflict.", status = 409, detail = renameRefusal }) };
                }

                var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
                var id = Guid.Parse(path["/access/households/".Length..]);
                var name = System.Text.Json.JsonSerializer.Deserialize<RenameHouseholdRequest>(body, options)!.Name;
                var index = owner._households.FindIndex(household => household.Id == id);
                owner._households[index] = owner._households[index] with { Name = name };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._households[index]) };
            }

            if (path.EndsWith("/kid-settings", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._kidSettings) };
                }

                return owner._kidSettingsRefusal is { } kidRefusal
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { detail = kidRefusal }) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._kidSettings) };
            }

            if (request.Method == HttpMethod.Post && path == "/auth/session/validate" && owner._authority is { } authority)
            {
                // The same person is still signed in with the same access when the page refreshes its authority.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SessionValidationResponse
                    {
                        SessionId = Guid.NewGuid(),
                        AccountId = authority.AccountId,
                        ActiveProfileId = authority.ActiveProfileId,
                        Authority = authority,
                    }),
                };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/people", StringComparison.Ordinal) && owner._addPersonDetail is { } refusal)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { detail = refusal }) };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/people", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new ManagedProfileResponse(Guid.NewGuid(), "New", "#7C4DFF", null, DateTimeOffset.UtcNow)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
        }
    }
}
