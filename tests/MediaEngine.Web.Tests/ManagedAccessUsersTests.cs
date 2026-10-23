using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Users and Sign-in for the server administrator: one row per sign-in, and only sign-in actions (the person's access is on their profile).</summary>
public sealed class ManagedAccessUsersTests : AsyncBunitContext
{
    private readonly UsersHandler _handler = new();

    public ManagedAccessUsersTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IHttpClientFactory>(new ClientFactory(_handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped(_ => AdministratorSession());
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
    }

    // ----- the list -----

    [Fact]
    public void EachRow_ShowsTheEmailTheProfileItOpensAndTheHousehold()
    {
        var cut = RenderUsers();

        var row = cut.Find(".access-users__row");
        Assert.Equal("owner@example.test", row.QuerySelector(".access-users__identity strong")!.TextContent.Trim());
        Assert.Equal("Owner", row.QuerySelector(".access-users__profile")!.TextContent.Trim());
        Assert.Equal("Household 1", row.QuerySelector(".access-users__household")!.TextContent.Trim());
        Assert.Equal(["Sign-in", "Opens profile", "Household", "Actions"], cut.FindAll("[role='columnheader']").Select(heading => heading.TextContent.Trim()));
    }

    [Fact]
    public void ABadgeAppears_OnlyWhenTheSignInHasNoPasswordOrHasTwoStepOn()
    {
        var plain = RenderUsers();
        Assert.Empty(plain.FindAll(".access-users__badges"));
        Assert.DoesNotContain("No password", plain.Markup);
        Assert.DoesNotContain("Two-step on", plain.Markup);

        _handler.HasPassword = false;
        _handler.HasTwoStep = true;
        var badged = RenderUsers();
        Assert.Contains("No password", badged.Find(".access-users__badges").TextContent);
        Assert.Contains("Two-step on", badged.Find(".access-users__badges").TextContent);
    }

    [Fact]
    public void TheRowMenu_OffersSignInActionsOnly_AndNoAccessEditing()
    {
        var cut = RenderUsers();

        cut.Find("button[aria-label='Actions for owner@example.test']").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            ["Change email", "Set temporary password", "Export", "Delete sign-in"],
            cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim())));
        Assert.DoesNotContain("Edit permissions", cut.Markup);
        Assert.DoesNotContain("Profile access", cut.Markup);
        Assert.DoesNotContain("Default profile", cut.Markup);
        Assert.DoesNotContain("can use View", cut.Markup);
        Assert.DoesNotContain("Administrator", cut.Markup);
    }

    [Fact]
    public void TurnOffTwoStep_IsOfferedOnlyForSignInsThatHaveIt()
    {
        var without = RenderUsers();
        without.Find("button[aria-label='Actions for owner@example.test']").Click();
        without.WaitForAssertion(() => Assert.NotEmpty(without.FindAll("[role='menuitem']")));
        Assert.DoesNotContain(without.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Turn off two-step codes");

        _handler.HasTwoStep = true;
        var with = RenderUsers();
        with.Find("button[aria-label='Actions for owner@example.test']").Click();
        with.WaitForAssertion(() => Assert.Contains(with.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Turn off two-step codes"));
    }

    // ----- add a sign-in -----

    [Fact]
    public void AddASignIn_ForAProfileWithoutOne_SendsThatProfileAndShowsTheInvitationOnce()
    {
        var cut = RenderUsers();
        OpenInvite(cut);

        cut.Find("select[aria-label='Profile without a sign-in']").Change(_handler.KidProfileId.ToString());
        TypeInto(cut, "Email", "kid@example.test");
        Click(cut, "Add sign-in");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("It won't be shown again", cut.Markup);
            Assert.Contains(UsersHandler.InvitationCode, cut.Markup);
            Assert.Contains($"https://tuvima.example/auth/invite?code={UsersHandler.InvitationCode}", cut.Markup);
            Assert.Contains("<svg", cut.Find(".access-invitation").InnerHtml);
        });
        var body = Created();
        Assert.Equal("kid@example.test", body.Email);
        Assert.Equal("invitation", body.Method);
        Assert.Equal(_handler.KidProfileId, body.ProfileId);
        Assert.Null(body.NewProfile);

        cut.Find("button[aria-label='Close user drawer']").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain(UsersHandler.InvitationCode, cut.Markup));
    }

    [Fact]
    public void AddASignIn_ForANewPerson_ChoosesAnExistingHouseholdAndKidsIsOffByDefault()
    {
        var cut = RenderUsers();
        OpenInvite(cut);
        Click(cut, "A new person");

        Assert.False(KidsTick(cut).HasAttribute("checked"));
        TypeInto(cut, "Name", "Pat");
        cut.Find("select[aria-label='Household']").Change(_handler.HouseholdId.ToString());
        TypeInto(cut, "Email", "pat@example.test");
        Click(cut, "Add sign-in");

        cut.WaitForAssertion(() => Assert.Contains(UsersHandler.InvitationCode, cut.Markup));
        var body = Created();
        Assert.Null(body.ProfileId);
        Assert.Equal("Pat", body.NewProfile!.Name);
        Assert.Equal(_handler.HouseholdId, body.NewProfile.HouseholdId);
        Assert.Null(body.NewProfile.NewHouseholdName);
        Assert.False(body.NewProfile.IsKids);
    }

    [Fact]
    public void AddASignIn_ForANewPersonInANewHouseholdWithKidsTicked_SendsBothChoices()
    {
        var cut = RenderUsers();
        OpenInvite(cut);
        Click(cut, "A new person");

        TypeInto(cut, "Name", "Sam");
        cut.Find("select[aria-label='Household']").Change("new-household");
        TypeInto(cut, "New household name", "Household 2");
        KidsTick(cut).Change(true);
        TypeInto(cut, "Email", "sam@example.test");
        Click(cut, "Add sign-in");

        cut.WaitForAssertion(() => Assert.Contains(UsersHandler.InvitationCode, cut.Markup));
        var profile = Created().NewProfile!;
        Assert.Equal("Sam", profile.Name);
        Assert.Null(profile.HouseholdId);
        Assert.Equal("Household 2", profile.NewHouseholdName);
        Assert.True(profile.IsKids);
    }

    [Fact]
    public void AddASignIn_WithATemporaryPassword_ShowsThePasswordOnceAndSendsIt()
    {
        var cut = RenderUsers();
        OpenInvite(cut);
        cut.Find("select[aria-label='Profile without a sign-in']").Change(_handler.KidProfileId.ToString());
        TypeInto(cut, "Email", "kid@example.test");
        Click(cut, "Set a temporary password");
        Click(cut, "Generate");

        Click(cut, "Add sign-in");

        cut.WaitForAssertion(() => Assert.Contains("Temporary password set.", cut.Markup));
        var body = Created();
        Assert.Equal("temporary_password", body.Method);
        Assert.Equal(16, body.TemporaryPassword!.Length);
        Assert.Equal(body.TemporaryPassword, cut.Find(".access-invitation code").TextContent);

        cut.Find("button[aria-label='Close user drawer']").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain(body.TemporaryPassword, cut.Markup));
    }

    [Fact]
    public void AddASignIn_WhenEveryProfileAlreadyHasOne_OffersOnlyANewPerson()
    {
        _handler.KidHasSignIn = true;
        var cut = RenderUsers();

        OpenInvite(cut);

        Assert.Empty(cut.FindAll("select[aria-label='Profile without a sign-in']"));
        Assert.NotEmpty(cut.FindAll("select[aria-label='Household']"));
    }

    [Fact]
    public void AddASignIn_ForANewHousehold_OffersTheSetPasswordButtonForTheEnginesCode_WhateverTheSentenceSays()
    {
        _handler.CreateStatus = HttpStatusCode.Conflict;
        _handler.CreateDetail = "Give yourself a password before adding another household.";
        _handler.CreateCode = "secure_administrators_first";
        var cut = RenderUsers();
        OpenInvite(cut);
        Click(cut, "A new person");
        TypeInto(cut, "Name", "Sam");
        cut.Find("select[aria-label='Household']").Change("new-household");
        TypeInto(cut, "New household name", "Household 2");
        TypeInto(cut, "Email", "sam@example.test");

        Click(cut, "Add sign-in");

        cut.WaitForAssertion(() => Assert.Equal(_handler.CreateDetail, cut.Find("[data-user-invite-error]").TextContent.Trim()));
        var setPassword = cut.Find("[data-user-invite-set-password] button");
        Assert.Equal("Set an email and password", setPassword.TextContent.Trim());

        setPassword.Click();

        Assert.EndsWith("/settings/security", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void AddASignIn_WhenTheEngineRefuses_ShowsItsSentenceAsItIsAndKeepsTheForm()
    {
        _handler.CreateStatus = HttpStatusCode.Conflict;
        _handler.CreateDetail = "Set an email and password for your own sign-in first. A Library with several households must not be open to anyone who reaches it.";
        var cut = RenderUsers();
        OpenInvite(cut);
        Click(cut, "A new person");
        TypeInto(cut, "Name", "Sam");
        cut.Find("select[aria-label='Household']").Change("new-household");
        TypeInto(cut, "New household name", "Household 2");
        TypeInto(cut, "Email", "sam@example.test");

        Click(cut, "Add sign-in");

        cut.WaitForAssertion(() => Assert.Equal(_handler.CreateDetail, cut.Find("[data-user-invite-error]").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-user-invite-set-password]"));
        Assert.NotEmpty(cut.FindAll("[data-user-invite]"));
        Assert.DoesNotContain(UsersHandler.InvitationCode, cut.Markup);
    }

    // ----- change email -----

    [Fact]
    public void ChangeEmail_SavesTheNewEmail_AndSaysTheProfileIsUnchanged()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Change email");

        TypeInto(cut, "Email", "owner@new.example");
        Click(cut, "Save email");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Their profile and everything in it are unchanged.", cut.Find("[data-testid='access-notice']").TextContent);
            Assert.Contains("owner@new.example", cut.Find(".access-users__row").TextContent);
        });
        var request = Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Equal($"/access/users/{_handler.UserId:D}/email", request.Path);
        Assert.Equal("owner@new.example", JsonSerializer.Deserialize<ChangeAccountEmailRequest>(request.Body, JsonOptions)!.Email);
    }

    [Fact]
    public void ChangeEmail_WhenTheEmailIsInUse_ShowsTheEnginesSentenceAndKeepsTheOldEmail()
    {
        _handler.EmailStatus = HttpStatusCode.Conflict;
        _handler.EmailDetail = "That email is already used by another sign-in.";
        var cut = RenderUsers();
        OpenAction(cut, "Change email");
        TypeInto(cut, "Email", "taken@example.test");

        Click(cut, "Save email");

        cut.WaitForAssertion(() => Assert.Equal("That email is already used by another sign-in.", cut.Find("[data-testid='access-error']").TextContent.Trim()));
        Assert.DoesNotContain("are unchanged", cut.Markup);
        Assert.Contains("owner@example.test", cut.Find(".access-users__row").TextContent);
    }

    [Fact]
    public void ChangeEmail_CannotBeSavedUntilTheEmailIsDifferent()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Change email");

        Assert.True(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save email").HasAttribute("disabled"));
    }

    // ----- temporary password -----

    [Fact]
    public void TemporaryPassword_ForAUser_ShowsThePasswordOnce()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Set temporary password");

        Click(cut, "Generate");
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Set temporary password").Click();

        cut.WaitForAssertion(() => Assert.Contains("Temporary password set.", cut.Markup));
        var request = Assert.Single(_handler.Requests, request => request.Path.EndsWith("/temporary-password", StringComparison.Ordinal));
        Assert.Equal($"/access/accounts/{_handler.UserId:D}/temporary-password", request.Path);
        var payload = JsonSerializer.Deserialize<SetTemporaryPasswordRequest>(request.Body, JsonOptions)!;
        Assert.Equal(payload.TemporaryPassword, cut.Find(".access-invitation code").TextContent);

        cut.Find("button[aria-label='Close user drawer']").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain(payload.TemporaryPassword, cut.Markup));
    }

    [Fact]
    public void TemporaryPassword_WhenThePasswordIsNotAllowed_SaysHowToChooseOneAndShowsNothing()
    {
        _handler.PasswordStatus = HttpStatusCode.BadRequest;
        _handler.PasswordDetail = "That password is on the list of common passwords.";
        var cut = RenderUsers();
        OpenAction(cut, "Set temporary password");

        Click(cut, "Generate");
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Set temporary password").Click();

        cut.WaitForAssertion(() => Assert.Contains("That password isn't allowed. Use at least 12 characters", cut.Find("[data-testid='access-error']").TextContent));
        Assert.Empty(cut.FindAll(".access-invitation"));
    }

    // ----- delete sign-in -----

    [Fact]
    public void DeleteSignIn_NormallySaysTheProfileAndEverythingInItStay()
    {
        var cut = RenderUsers();

        OpenAction(cut, "Delete sign-in");

        Assert.Equal(
            "Delete the sign-in owner@example.test? Owner's profile and everything in it stay in Household 1. You can attach a new sign-in to it later.",
            cut.Find("[data-user-deletion] [role='alert'], [data-user-deletion] .app-alert").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-user-deletion-photos]"));
        Assert.Contains(_handler.Requests, request => request.Method == HttpMethod.Get && request.Path == $"/access/users/{_handler.UserId:D}/deletion-preview");
    }

    [Fact]
    public void DeleteSignIn_Normally_SendsOnlyTheSignInDelete_AndKeepsTheProfileInTheNotice()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Delete sign-in");

        Click(cut, "Delete sign-in", last: true);

        cut.WaitForAssertion(() => Assert.Equal("The sign-in owner@example.test was deleted. Owner's profile was kept.", cut.Find("[data-testid='access-notice']").TextContent.Trim()));
        Assert.Equal("?profiles=", Assert.Single(_handler.DeleteQueries));
        Assert.Empty(cut.FindAll(".access-users__row"));
    }

    [Fact]
    public void DeleteSignIn_ForTheLastHouseholdAdministrator_ListsTheHouseholdsProfilesBeforeRemovingIt()
    {
        _handler.RemovesHousehold("Household 2", "Pat", "Sam", "Leo");
        var cut = RenderUsers();

        OpenAction(cut, "Delete sign-in");

        Assert.Equal(
            "This is the last sign-in that can look after Household 2. Deleting it removes Household 2 and its profiles: Pat, Sam, Leo, with their progress, bookmarks, lists, photos and videos.",
            cut.Find("[data-user-deletion] [role='alert'], [data-user-deletion] .app-alert").TextContent.Trim());
    }

    [Fact]
    public void DeleteSignIn_ForAHouseholdWithPhotos_SaysPhotosAndVideosGo_AndOffersNoPhotoChoice()
    {
        _handler.RemovesHousehold("Household 2", "Pat", "Sam");
        _handler.PreviewHasPhotos = true;
        var cut = RenderUsers();

        OpenAction(cut, "Delete sign-in");

        Assert.Contains("with their progress, bookmarks, lists, photos and videos.", cut.Find("[data-user-deletion] .app-alert").TextContent);
        Assert.Empty(cut.FindAll("[data-user-deletion-photos]"));
        Assert.Empty(cut.FindAll("input[type='radio']"));
    }

    [Fact]
    public void DeleteSignIn_ForAHouseholdWhoseSharedLibraryHoldsPhotos_NamesTheSharedPhotosAndVideosToo()
    {
        _handler.RemovesHousehold("Household 2", "Pat", "Sam");
        _handler.PreviewHasPhotos = true;
        _handler.PreviewHasSharedPhotos = true;
        var cut = RenderUsers();

        OpenAction(cut, "Delete sign-in");

        Assert.Equal(
            "This is the last sign-in that can look after Household 2. Deleting it removes Household 2 and its profiles: Pat, Sam, with their progress, bookmarks, lists, photos and videos, and the household's shared photos and videos.",
            cut.Find("[data-user-deletion] .app-alert").TextContent.Trim());
    }

    [Fact]
    public void DeleteSignIn_ForAHouseholdWithNoSharedPhotos_DoesNotMentionSharedPhotos()
    {
        _handler.RemovesHousehold("Household 2", "Pat", "Sam");
        _handler.PreviewHasPhotos = true;
        var cut = RenderUsers();

        OpenAction(cut, "Delete sign-in");

        Assert.DoesNotContain("shared photos", cut.Find("[data-user-deletion] .app-alert").TextContent);
    }

    [Fact]
    public void DeleteSignIn_ForAHouseholdWithPhotos_SendsOnlyTheShownProfiles_NoPhotoChoice()
    {
        _handler.RemovesHousehold("Household 2", "Pat", "Sam");
        _handler.PreviewHasPhotos = true;
        var cut = RenderUsers();
        OpenAction(cut, "Delete sign-in");

        Click(cut, "Delete sign-in", last: true);

        cut.WaitForAssertion(() => Assert.Contains("Household 2 and its profiles were removed", cut.Find("[data-testid='access-notice']").TextContent));
        var shown = string.Join(',', _handler.PreviewIds.Select(id => id.ToString("D")));
        Assert.Equal($"?profiles={shown}", Assert.Single(_handler.DeleteQueries));
    }

    [Fact]
    public void DeleteSignIn_WhenThePreviewRefuses_ShowsTheReasonAndOffersNoDeleteButton()
    {
        _handler.PreviewRefusal = "Shy is the Library's only server administrator. Make someone else a server administrator first.";
        var cut = RenderUsers();

        OpenAction(cut, "Delete sign-in");

        Assert.Equal(_handler.PreviewRefusal, cut.Find("[data-user-deletion-refused]").TextContent.Trim());
        Assert.DoesNotContain(cut.FindAll(".access-drawer__body button"), button => button.TextContent.Trim() == "Delete sign-in");
        Assert.Contains(cut.FindAll(".access-drawer__body button"), button => button.TextContent.Trim() == "Keep sign-in");
    }

    [Fact]
    public void DeleteSignIn_WhenThePreviewCallFails_ShowsTheEnginesSentenceAndOffersNoDeleteButton()
    {
        _handler.PreviewStatus = HttpStatusCode.Conflict;
        _handler.PreviewDetail = "mia@example.com can only open this profile. Give mia@example.com another profile first.";
        var cut = RenderUsers();

        OpenAction(cut, "Delete sign-in");

        Assert.Equal("mia@example.com can only open this profile. Give mia@example.com another profile first.", cut.Find("[data-testid='access-error']").TextContent.Trim());
        Assert.DoesNotContain(cut.FindAll(".access-drawer__body button"), button => button.TextContent.Trim() == "Delete sign-in");
    }

    [Fact]
    public void DeleteSignIn_WhenTheEngineRefusesTheDelete_ShowsItsSentenceAndKeepsTheSignIn()
    {
        _handler.DeleteStatus = HttpStatusCode.Conflict;
        _handler.DeleteDetail = "Household 2 changed since you looked. Open Delete sign-in again.";
        var cut = RenderUsers();
        OpenAction(cut, "Delete sign-in");

        Click(cut, "Delete sign-in", last: true);

        cut.WaitForAssertion(() => Assert.Equal("Household 2 changed since you looked. Open Delete sign-in again.", cut.Find("[data-user-deletion-error]").TextContent.Trim()));
        Assert.DoesNotContain("was deleted", cut.Markup);
        Assert.Single(cut.FindAll(".access-users__row"));
    }

    // ----- who sees it, and where -----

    [Fact]
    public async Task OnAPhone_SignInsReadAsCardsWithoutManageControls()
    {
        await UsePhoneAsync();

        var cut = RenderUsers();

        Assert.NotEmpty(cut.FindAll(".app-table-shell--cards"));
        Assert.Equal("owner@example.test", cut.Find("td[data-card-role='title']").TextContent.Trim());
        Assert.Equal("Owner · Household 1", cut.Find("td[data-card-role='subtitle']").TextContent.Trim());
        Assert.Empty(cut.FindAll("td[data-card-role='value']"));
        Assert.Contains("Manage on a computer or tablet", cut.Find("p[role='note']").TextContent);
        Assert.Empty(cut.FindAll("button[aria-label^='Actions for']"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("sign-in", StringComparison.OrdinalIgnoreCase) || button.TextContent.Contains("Import"));
    }

    [Fact]
    public void OnADesktop_SignInsKeepTheRowActionsAndNoPhoneNote()
    {
        var cut = RenderUsers();

        Assert.Empty(cut.FindAll(".app-table-shell--cards"));
        Assert.NotEmpty(cut.FindAll("button[aria-label='Actions for owner@example.test']"));
        Assert.DoesNotContain("Manage on a computer or tablet", cut.Markup);
    }

    [Fact]
    public void AnyoneWhoIsNotAServerAdministrator_SeesNothingToManageAndTheEngineIsNotAsked()
    {
        Services.AddScoped(_ => Session(["settings.administration"]));

        var cut = Render<ManagedAccessUsers>();

        cut.WaitForAssertion(() => Assert.Contains("Administrator access is required to manage users.", cut.Markup));
        Assert.Empty(cut.FindAll("button"));
        Assert.Empty(cut.FindAll(".access-users__row"));
        Assert.DoesNotContain("owner@example.test", cut.Markup);
        Assert.Empty(_handler.Requests);
    }

    // ----- helpers -----

    private CreateUserRequest Created()
    {
        var request = Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/access/users");
        return JsonSerializer.Deserialize<CreateUserRequest>(request.Body, JsonOptions)!;
    }

    private async Task UsePhoneAsync()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        Services.AddScoped(_ => device);
    }

    private IRenderedComponent<ManagedAccessUsers> RenderUsers(string expectedEmail = "owner@example.test")
    {
        var cut = Render<ManagedAccessUsers>();
        cut.WaitForAssertion(() => Assert.Contains(expectedEmail, cut.Markup));
        return cut;
    }

    private static void OpenAction(IRenderedComponent<ManagedAccessUsers> cut, string action)
    {
        cut.Find("button[aria-label='Actions for owner@example.test']").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == action));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == action).Click();
        cut.WaitForAssertion(() => Assert.Contains("access-drawer__body", cut.Markup));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".access-drawer__body button")));
    }

    private static void OpenInvite(IRenderedComponent<ManagedAccessUsers> cut)
    {
        cut.Find("[data-testid='access-add-sign-in']").Click();
        cut.WaitForElement("[data-user-invite]");
    }

    private static void Click(IRenderedComponent<ManagedAccessUsers> cut, string label, bool last = false)
    {
        var buttons = cut.FindAll(".access-drawer__body button").Where(button => button.TextContent.Trim() == label).ToList();
        (last ? buttons.Last() : buttons.Single()).Click();
    }

    private static void TypeInto(IRenderedComponent<ManagedAccessUsers> cut, string label, string value)
    {
        var field = cut.FindAll(".access-drawer__body label").Single(candidate => candidate.TextContent.Trim() == label);
        cut.Find("#" + field.GetAttribute("for")).Input(value);
    }

    private static IElement KidsTick(IRenderedComponent<ManagedAccessUsers> cut) =>
        cut.FindAll(".access-drawer__body label").Single(candidate => candidate.TextContent.Trim() == "Kids profile").QuerySelector("input")!;

    private static DashboardSessionAccessor AdministratorSession() => Session(["settings.administration"], manage: true);

    private static DashboardSessionAccessor Session(string[] scopes, bool manage = false)
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var session = new DashboardSessionAccessor();
        session.Set("session", accountId, profileId, Guid.NewGuid(), new DashboardAuthorityResponse(
            accountId, profileId, true, true, 1, 1, manage, manage, null, 1, [],
            scopes, manage ? ["access.manage"] : []));
        return session;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class UsersHandler : HttpMessageHandler
    {
        public const string InvitationCode = "KQ7M4-XH2TA";
        private const string OwnerProfileName = "Owner";
        private const string HouseholdName = "Household 1";

        private string[] _householdProfiles = [];
        private string? _removedHousehold;
        private bool _deleted;

        public Guid UserId { get; } = Guid.NewGuid();
        public Guid OwnerProfileId { get; } = Guid.NewGuid();
        public Guid KidProfileId { get; } = Guid.NewGuid();
        public Guid HouseholdId { get; } = Guid.NewGuid();
        public string Email { get; set; } = "owner@example.test";
        public bool HasPassword { get; set; } = true;
        public bool HasTwoStep { get; set; }
        public bool KidHasSignIn { get; set; }
        public List<RequestRecord> Requests { get; } = [];

        public HttpStatusCode CreateStatus { get; set; } = HttpStatusCode.Created;
        public string? CreateDetail { get; set; }
        public string? CreateCode { get; set; }
        public HttpStatusCode EmailStatus { get; set; } = HttpStatusCode.OK;
        public string? EmailDetail { get; set; }
        public HttpStatusCode PasswordStatus { get; set; } = HttpStatusCode.OK;
        public string? PasswordDetail { get; set; }

        public HttpStatusCode PreviewStatus { get; set; } = HttpStatusCode.OK;
        public string? PreviewDetail { get; set; }
        public string? PreviewRefusal { get; set; }
        public bool PreviewHasPhotos { get; set; }
        public bool PreviewHasSharedPhotos { get; set; }

        /// <summary>The ids of the profiles the last preview listed, in order.</summary>
        public List<Guid> PreviewIds { get; } = [];
        public HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.NoContent;
        public string? DeleteDetail { get; set; }

        /// <summary>The query string of each delete request ("?profiles=").</summary>
        public List<string> DeleteQueries { get; } = [];

        /// <summary>The preview will say the household goes too, with these profiles.</summary>
        public void RemovesHousehold(string household, params string[] profiles)
        {
            _removedHousehold = household;
            _householdProfiles = profiles;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, path, body));

            if (request.Method == HttpMethod.Get && path == "/access/users")
            {
                return Json(_deleted ? Array.Empty<UserSummaryResponse>() : [User()]);
            }

            if (request.Method == HttpMethod.Get && path == "/access/profiles")
            {
                return Json(new[]
                {
                    new ManagedProfileResponse(OwnerProfileId, OwnerProfileName, "#7C4DFF", null, DateTimeOffset.UtcNow, HouseholdId,
                        User: new ManagedProfileUserDto(UserId, Email)),
                    new ManagedProfileResponse(KidProfileId, "Kid", "#7C4DFF", null, DateTimeOffset.UtcNow, HouseholdId, IsRestricted: true,
                        User: KidHasSignIn ? new ManagedProfileUserDto(Guid.NewGuid(), "kid@example.test") : null),
                });
            }

            if (request.Method == HttpMethod.Get && path == "/access/households")
            {
                return Json(new[] { new HouseholdResponse(HouseholdId, HouseholdName, 2, 1, DateTimeOffset.UtcNow) });
            }

            if (request.Method == HttpMethod.Post && path == "/access/users")
            {
                if (CreateStatus != HttpStatusCode.Created)
                {
                    return new HttpResponseMessage(CreateStatus) { Content = JsonContent.Create(new { detail = CreateDetail, code = CreateCode }) };
                }

                var created = JsonSerializer.Deserialize<CreateUserRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var summary = new UserSummaryResponse(Guid.NewGuid(), created.Email, created.ProfileId ?? Guid.NewGuid(), "Kid", HouseholdId, HouseholdName, true, false, true, false);
                var invitation = created.Method == "invitation"
                    ? new AccountInvitationResponse(summary.Id, InvitationCode, DateTimeOffset.UtcNow.AddDays(1), "https://tuvima.example")
                    : null;
                return Json(new CreateUserResponse(summary, invitation, invitation is null ? DateTimeOffset.UtcNow.AddDays(7) : null), HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Put && path == $"/access/users/{UserId:D}/email")
            {
                if (EmailStatus != HttpStatusCode.OK)
                {
                    return Problem(EmailStatus, EmailDetail);
                }

                Email = JsonSerializer.Deserialize<ChangeAccountEmailRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Email;
                return Json(User());
            }

            if (request.Method == HttpMethod.Post && path == $"/access/accounts/{UserId:D}/temporary-password")
            {
                return PasswordStatus != HttpStatusCode.OK
                    ? Problem(PasswordStatus, PasswordDetail)
                    : Json(new UserTemporaryPasswordResponse(User(), DateTimeOffset.UtcNow.AddDays(7)));
            }

            if (request.Method == HttpMethod.Get && path == $"/access/users/{UserId:D}/deletion-preview")
            {
                return PreviewStatus == HttpStatusCode.OK ? Json(NewPreview()) : Problem(PreviewStatus, PreviewDetail);
            }

            if (request.Method == HttpMethod.Delete && path == $"/access/users/{UserId:D}")
            {
                DeleteQueries.Add(request.RequestUri.Query);
                if (DeleteStatus != HttpStatusCode.NoContent)
                {
                    return Problem(DeleteStatus, DeleteDetail);
                }

                _deleted = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private UserSummaryResponse User() =>
            new(UserId, Email, OwnerProfileId, OwnerProfileName, HouseholdId, HouseholdName, HasPassword, HasTwoStep, true, false);

        private UserDeletionPreviewResponse NewPreview()
        {
            PreviewIds.Clear();
            PreviewIds.AddRange(_householdProfiles.Select(_ => Guid.NewGuid()));
            return new UserDeletionPreviewResponse(UserId, Email, OwnerProfileName, _removedHousehold is not null, _removedHousehold,
                _householdProfiles.Select((name, index) => new AccountDeletionProfileDto(PreviewIds[index], name)).ToList(),
                PreviewRefusal, PreviewHasPhotos, PreviewHasSharedPhotos);
        }

        private static HttpResponseMessage Problem(HttpStatusCode status, string? detail) =>
            new(status) { Content = JsonContent.Create(new { detail }) };

        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(value) };
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Body);
}
