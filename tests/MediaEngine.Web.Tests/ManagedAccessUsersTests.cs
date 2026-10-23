using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class ManagedAccessUsersTests : AsyncBunitContext
{
    private readonly UsersHandler _handler = new();

    public ManagedAccessUsersTests()
    {
        Services.AddNativeUiServices();
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IHttpClientFactory>(new ClientFactory(_handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped(_ => AdministratorSession());
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
    }

    [Fact]
    public void Permissions_SaveRealFeatureAndLibrarySelection_AndRendersSuccess()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Edit permissions");

        cut.Find("input[aria-label='Watch']").Change(false);
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save permissions").Click();

        cut.WaitForAssertion(() => Assert.Contains("Permissions for owner@example.test were saved.", cut.Markup));
        var request = Assert.Single(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal));
        var payload = JsonSerializer.Deserialize<ReplaceAccountAccessRequest>(request.Body, JsonOptions)!;
        Assert.Equal(["listen", "read", "view"], payload.FeatureIds.Order());
        Assert.Empty(payload.HiddenLibraryIds);
    }

    [Fact]
    public void Permissions_UntickingALibrary_SavesStraightAway_AndSaysItWasHiddenFromThePerson()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Edit permissions");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".area-library-list input")));
        cut.Find($".area-library-list [data-library-id='{_handler.Library.Id}'] input").Change(false);

        // No "Save permissions" click: the tick saved on its own, with the lanes as they were saved.
        cut.WaitForAssertion(() => Assert.Single(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal)));
        var request = Assert.Single(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal));
        var payload = JsonSerializer.Deserialize<ReplaceAccountAccessRequest>(request.Body, JsonOptions)!;
        Assert.Equal([_handler.Library.Id], payload.HiddenLibraryIds);
        Assert.Equal(["listen", "read", "view", "watch"], payload.FeatureIds.Order());
        cut.WaitForAssertion(() => Assert.Contains(Services.GetRequiredService<AppToastService>().Toasts, toast => toast.Message == "Hidden from owner@example.test"));
    }

    [Fact]
    public void Permissions_TickingItAgain_ShowsTheLibraryAgain_AndSaysSo()
    {
        _handler.HiddenIds = [_handler.Library.Id];
        var cut = RenderUsers();
        OpenAction(cut, "Edit permissions");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".area-library-list input")));
        Assert.False(cut.Find($".area-library-list [data-library-id='{_handler.Library.Id}'] input").HasAttribute("checked"));
        cut.Find($".area-library-list [data-library-id='{_handler.Library.Id}'] input").Change(true);

        cut.WaitForAssertion(() => Assert.Single(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal)));
        var request = Assert.Single(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal));
        var payload = JsonSerializer.Deserialize<ReplaceAccountAccessRequest>(request.Body, JsonOptions)!;
        Assert.Empty(payload.HiddenLibraryIds);
        cut.WaitForAssertion(() => Assert.Contains(Services.GetRequiredService<AppToastService>().Toasts, toast => toast.Message == "Shown to owner@example.test"));
    }

    [Fact]
    public void Permissions_EverythingStartsTicked_AnAreaWithOneLibraryShowsNoList_AndAnAreaSwitchedOffShowsNone()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Edit permissions");

        // Read has two libraries, Watch has one: only Read gets a list, and everything in it starts ticked.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-area-libraries='read']")));
        Assert.Empty(cut.FindAll("[data-area-libraries='watch']"));
        Assert.All(cut.FindAll("[data-area-libraries='read'] input"), input => Assert.True(input.HasAttribute("checked")));

        // Switching Read off takes its list away.
        cut.Find("input[aria-label='Read']").Change(false);
        Assert.Empty(cut.FindAll("[data-area-libraries='read']"));
    }

    [Fact]
    public void AFailedLibraryListNeverUnhidesLibraries_WhenAnAreaIsChanged()
    {
        var hiddenAlready = Guid.NewGuid();
        _handler.LibrariesFail = true;
        _handler.HiddenIds = [hiddenAlready];
        var cut = RenderUsers();
        OpenAction(cut, "Edit permissions");

        // The Libraries part says it could not load, and offers no ticks to change.
        cut.WaitForAssertion(() => Assert.Contains("libraries could not be loaded", cut.Markup));
        Assert.Contains("Retry", cut.Find(".app-error-state").TextContent);
        Assert.Empty(cut.FindAll(".area-library-list input"));
        cut.Find("input[aria-label='Watch']").Change(false);
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save permissions").Click();

        cut.WaitForAssertion(() => Assert.Contains("Permissions for owner@example.test were saved.", cut.Markup));
        var request = Assert.Single(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal));
        var payload = JsonSerializer.Deserialize<ReplaceAccountAccessRequest>(request.Body, JsonOptions)!;
        Assert.Equal(["listen", "read", "view"], payload.FeatureIds.Order());
        Assert.Equal([hiddenAlready], payload.HiddenLibraryIds);
    }

    [Fact]
    public void AnUntickedLibrary_IsAddedToWhatIsAlreadyHidden_AndAHiddenIdTheListDidNotShowIsKept()
    {
        var hiddenAlready = Guid.NewGuid();
        _handler.HiddenIds = [hiddenAlready];
        var cut = RenderUsers();
        OpenAction(cut, "Edit permissions");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".area-library-list input")));
        cut.Find($".area-library-list [data-library-id='{_handler.Library.Id}'] input").Change(false);

        cut.WaitForAssertion(() => Assert.Single(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal)));
        var request = Assert.Single(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal));
        var payload = JsonSerializer.Deserialize<ReplaceAccountAccessRequest>(request.Body, JsonOptions)!;
        Assert.Equal(new[] { hiddenAlready, _handler.Library.Id }.Order(), payload.HiddenLibraryIds.Order());
    }

    [Fact]
    public void CanUseViewSwitch_SavesOnlyTheViewLane()
    {
        var cut = RenderUsers();

        cut.Find("input[aria-label='owner@example.test can use View']").Change(false);

        cut.WaitForAssertion(() => Assert.Contains("can no longer use View", cut.Markup));
        var request = Assert.Single(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal));
        var payload = JsonSerializer.Deserialize<ReplaceAccountAccessRequest>(request.Body, JsonOptions)!;
        Assert.Equal(["listen", "read", "watch"], payload.FeatureIds.Order());
        Assert.Empty(payload.HiddenLibraryIds);
    }

    [Fact]
    public void InvitingSomeoneOutsideTheHousehold_DoesNotOfferViewBecauseItStartsOff()
    {
        var cut = RenderUsers();

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Invite someone outside your household").Click();

        cut.WaitForAssertion(() => Assert.Contains("View starts off for people outside your household", cut.Markup));
        Assert.Empty(cut.FindAll("input[aria-label='View']"));
        Assert.NotEmpty(cut.FindAll("input[aria-label='Watch']"));
    }

    [Fact]
    public void UserTable_RendersLongEmailAndEightDistinctProfileChipsWithoutRoleTier()
    {
        _handler.Email = "a.very.long.household.account.identity@example.test";
        _handler.ProfileCount = 8;

        var cut = RenderUsers(_handler.Email);

        Assert.Equal(8, cut.FindAll(".access-profile-chip").Count);
        Assert.Contains(_handler.Email, cut.Markup);
        Assert.DoesNotContain(cut.FindAll("[role='columnheader']"),
            heading => heading.TextContent.Equals("Role", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Permissions_ConflictWithAnEngineSentence_ShowsThatSentenceInsteadOfTheGenericLine()
    {
        _handler.AccessStatus = HttpStatusCode.Conflict;
        _handler.AccessDetail = "This is the last administrator who can sign in. Make someone else an administrator first.";
        var cut = RenderUsers();
        OpenAction(cut, "Edit permissions");

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save permissions").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("This is the last administrator who can sign in. Make someone else an administrator first.", cut.Markup);
            Assert.DoesNotContain("This change conflicts with the current account state.", cut.Markup);
        });
    }

    [Fact]
    public void Permissions_ConflictKeepsDrawerOpen_AndRendersUsefulError()
    {
        _handler.AccessStatus = HttpStatusCode.Conflict;
        var cut = RenderUsers();
        OpenAction(cut, "Edit permissions");

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save permissions").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("This change conflicts with the current account state.", cut.Markup);
            Assert.Contains("Save permissions", cut.Markup);
        });
    }

    [Fact]
    public async Task DelayedMutation_DisablesDuplicateSubmit_AndCannotOverwriteReopenedDrawer()
    {
        _handler.AccessGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.AccessStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderUsers();
        OpenAction(cut, "Edit permissions");

        var pending = cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save permissions")
            .ClickAsync(new());
        await _handler.AccessStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cut.WaitForAssertion(() => Assert.True(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save permissions").HasAttribute("disabled")));

        cut.Find("button[aria-label='Close user drawer']").Click();
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Invite user").Click();
        _handler.AccessGate.SetResult();
        await pending;

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Invite by email", cut.Markup);
            Assert.DoesNotContain("Permissions for owner@example.test were saved.", cut.Markup);
        });
        Assert.Single(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal));
    }

    [Fact]
    public void ProfileAccess_KeepsGrantsAndRevoking_ButNoProfileCreateRenameOrPinControls()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Profile access");

        // Grant and revoke stay with the account...
        Assert.Contains("Profile access · owner@example.test", cut.Find(".access-drawer__header").TextContent);
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Revoke profile");
        Assert.NotEmpty(cut.FindAll("input[aria-label='Default profile']"));
        Assert.NotEmpty(cut.FindAll("input[aria-label='Allow admin dashboard']"));

        // ...while creating, renaming and PIN protection moved to Profiles.
        Assert.DoesNotContain("Create a profile", cut.Markup);
        Assert.Empty(cut.FindAll(".access-grant__rename"));
        Assert.Empty(cut.FindAll("input[type='password']"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() is "Rename" or "Create profile" or "Enable protection");
        Assert.DoesNotContain(_handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/access/profiles");
    }

    [Fact]
    public void UsersPage_IsTitledUsers_HasNoHouseholdsList_AndRowMenuSaysProfileAccess()
    {
        var cut = RenderUsers();

        Assert.Contains("Accounts, roles and invitations.", cut.Markup);
        Assert.DoesNotContain("Households", cut.Markup);
        Assert.Empty(cut.FindAll("[aria-label='Households']"));

        cut.Find("button[aria-label='Actions for owner@example.test']").Click();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Profile access"));
        Assert.DoesNotContain(cut.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Manage profiles");
    }

    [Fact]
    public void AnInvitationThatReusesAnAccount_KeepsItsHiddenLibraries_WhenTheLibraryListFailed()
    {
        var hiddenAlready = Guid.NewGuid();
        _handler.LibrariesFail = true;
        _handler.HiddenIds = [hiddenAlready];
        _handler.InvitationReusesAccount = true;
        var cut = RenderUsers();
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Invite user").Click();
        cut.Find("input[type='text']").Input("owner@example.test");
        cut.Find(".access-drawer__body input[type='checkbox']").Change(true);
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Create invitation").Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal)));
        var request = Assert.Single(_handler.Requests, request => request.Path.EndsWith("/access", StringComparison.Ordinal));
        var payload = JsonSerializer.Deserialize<ReplaceAccountAccessRequest>(request.Body, JsonOptions)!;
        Assert.Equal([hiddenAlready], payload.HiddenLibraryIds);
    }

    [Fact]
    public void Invitation_ShowsCodeLinkAndQrOnlyInTheOpenResultDrawer()
    {
        var cut = RenderUsers();
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Invite user").Click();
        cut.Find("input[type='text']").Input("friend@example.test");
        cut.Find(".access-drawer__body input[type='checkbox']").Change(true);
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Create invitation").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("It won't be shown again", cut.Markup);
            Assert.Contains(UsersHandler.InvitationCode, cut.Markup);
            Assert.Contains($"https://tuvima.example/auth/invite?code={UsersHandler.InvitationCode}", cut.Markup);
            Assert.Contains("<svg", cut.Find(".access-invitation").InnerHtml);
            Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Copy code");
            Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Copy link");
        });

        cut.Find("button[aria-label='Close user drawer']").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(UsersHandler.InvitationCode, cut.Markup);
            Assert.DoesNotContain("/auth/invite?code=", cut.Markup);
        });
    }

    [Fact]
    public void TemporaryPassword_ForExistingUser_ShowsThePasswordOnceAndMarksTheUser()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Set temporary password");

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Generate").Click();
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Set temporary password").Click();

        cut.WaitForAssertion(() => Assert.Contains("Temporary password set.", cut.Markup));
        var request = Assert.Single(_handler.Requests, request => request.Path.EndsWith("/temporary-password", StringComparison.Ordinal));
        var payload = JsonSerializer.Deserialize<SetTemporaryPasswordRequest>(request.Body, JsonOptions)!;
        Assert.Equal(16, payload.TemporaryPassword.Length);
        Assert.Equal(payload.TemporaryPassword, cut.Find(".access-invitation code").TextContent);

        cut.Find("button[aria-label='Close user drawer']").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(payload.TemporaryPassword, cut.Markup);
            Assert.Contains("Temporary password", cut.Markup);
        });
    }

    [Fact]
    public void TwoStep_OffersTheResetOnlyForAccountsThatHaveIt()
    {
        var without = RenderUsers();
        without.Find("button[aria-label='Actions for owner@example.test']").Click();
        without.WaitForAssertion(() => Assert.Contains(without.FindAll("button"), button => button.TextContent.Trim() == "Set temporary password"));
        Assert.DoesNotContain(without.FindAll("button"), button => button.TextContent.Trim() == "Turn off two-step codes");
        Assert.DoesNotContain("Two-step codes", without.Markup);

        _handler.HasTwoStep = true;
        var with = RenderUsers();
        Assert.Contains("Two-step codes", with.Markup);
        with.Find("button[aria-label='Actions for owner@example.test']").Click();
        with.WaitForAssertion(() => Assert.Contains(with.FindAll("button"), button => button.TextContent.Trim() == "Turn off two-step codes"));
    }

    [Fact]
    public void DeleteAccount_UsesDeletionEndpoint_AndExplainsThatFilesAreKept()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Delete user");
        Assert.Contains("Personal media files are kept", cut.Markup);
        cut.FindAll("button").Last(button => button.TextContent.Trim() == "Delete user").Click();

        cut.WaitForAssertion(() => Assert.Contains("was deleted. Personal media files were kept.", cut.Markup));
        Assert.Contains(_handler.Requests, request => request.Method == HttpMethod.Delete && request.Path == $"/access/accounts/{_handler.AccountId:D}");
    }

    [Fact]
    public async Task OnAPhone_UsersReadAsCardsWithoutManageControlsOrEditing()
    {
        await UsePhoneAsync();

        var cut = RenderUsers();

        Assert.NotEmpty(cut.FindAll(".app-table-shell--cards"));
        Assert.Equal("owner@example.test", cut.Find("td[data-card-role='title']").TextContent.Trim());
        Assert.Equal("Administrator", cut.Find("td[data-card-role='subtitle']").TextContent.Trim());
        Assert.Contains("Enabled", cut.Find("td[data-card-role='value']").TextContent);
        Assert.Contains("Last active", cut.Find("td[data-card-role='value']").TextContent);
        Assert.Contains("Manage on a computer or tablet", cut.Find("p[role='note']").TextContent);
        Assert.Empty(cut.FindAll("button[aria-label^='Actions for']"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Invite"));
        Assert.DoesNotContain("can use View", cut.Markup);
    }

    [Fact]
    public async Task OnADesktop_UsersKeepTheRowActionsAndNoPhoneNote()
    {
        var cut = RenderUsers();

        Assert.Empty(cut.FindAll(".app-table-shell--cards"));
        Assert.NotEmpty(cut.FindAll("button[aria-label='Actions for owner@example.test']"));
        Assert.DoesNotContain("Manage on a computer or tablet", cut.Markup);
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
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button"), button => button.TextContent.Trim() == action));
        cut.FindAll("button").Single(button => button.TextContent.Trim() == action).Click();
        cut.WaitForAssertion(() => Assert.Contains("access-drawer__body", cut.Markup));
    }

    private static DashboardSessionAccessor AdministratorSession()
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var session = new DashboardSessionAccessor();
        session.Set("session", accountId, profileId, Guid.NewGuid(), new DashboardAuthorityResponse(
            accountId, profileId, true, true, 1, 1, true, true, null, 1, [],
            ["settings.administration"], ["access.manage"]));
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
        public bool TemporaryPasswordSet { get; set; }
        public bool HasTwoStep { get; set; }
        public Guid AccountId { get; } = Guid.NewGuid();
        public Guid OwnerProfileId { get; } = Guid.NewGuid();
        public AccessLibraryOptionDto Library { get; } = new(Guid.NewGuid(), "Books", "Books", "read");
        public AccessLibraryOptionDto SecondLibrary { get; } = new(Guid.NewGuid(), "Comics", "Comics", "read");
        public AccessLibraryOptionDto WatchLibrary { get; } = new(Guid.NewGuid(), "Movies", "Movies", "watch");
        public HttpStatusCode AccessStatus { get; set; } = HttpStatusCode.NoContent;

        /// <summary>When true the Engine's library list can't be read (a server error, not an empty list).</summary>
        public bool LibrariesFail { get; set; }

        /// <summary>When true the invitation reuses the existing account (one with no password yet), as the Engine does for the same email.</summary>
        public bool InvitationReusesAccount { get; set; }

        /// <summary>The libraries the Engine already hides from the account.</summary>
        public Guid[] HiddenIds { get; set; } = [];

        /// <summary>The Engine's plain sentence sent with a refusal of the access save, when a test wants one.</summary>
        public string? AccessDetail { get; set; }
        public TaskCompletionSource? AccessGate { get; set; }
        public TaskCompletionSource? AccessStarted { get; set; }
        public string Email { get; set; } = "owner@example.test";
        public int ProfileCount { get; set; } = 1;
        public List<RequestRecord> Requests { get; } = [];

        private const string ProfileName = "Owner";
        private bool _deleted;
        private readonly Guid[] _extraProfileIds = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, path, body));

            if (request.Method == HttpMethod.Get && path == "/access/accounts")
            {
                return Json(_deleted ? Array.Empty<AccountAccessResponse>() : [Account()]);
            }

            if (request.Method == HttpMethod.Get && path == "/access/profiles")
            {
                return Json(Profiles());
            }

            if (request.Method == HttpMethod.Get && path == "/access/libraries")
            {
                if (LibrariesFail)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                return Json(new[] { Library, SecondLibrary, WatchLibrary });
            }

            if (request.Method == HttpMethod.Put && path.EndsWith("/access", StringComparison.Ordinal))
            {
                AccessStarted?.TrySetResult();
                if (AccessGate is not null)
                {
                    await AccessGate.Task.WaitAsync(cancellationToken);
                }

                return new HttpResponseMessage(AccessStatus)
                {
                    Content = AccessDetail is null ? null : JsonContent.Create(new { detail = AccessDetail }),
                };
            }
            if (request.Method == HttpMethod.Post && path == "/access/invitations")
            {
                return Json(new AccountInvitationResponse(InvitationReusesAccount ? AccountId : Guid.NewGuid(), InvitationCode, DateTimeOffset.UtcNow.AddDays(1), "https://tuvima.example"));
            }

            if (request.Method == HttpMethod.Post && path == $"/access/accounts/{AccountId:D}/temporary-password")
            {
                TemporaryPasswordSet = true;
                return Json(Account());
            }

            if (request.Method == HttpMethod.Delete && path == $"/access/accounts/{AccountId:D}")
            {
                _deleted = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private IReadOnlyList<ManagedProfileResponse> Profiles()
        {
            var profiles = new List<ManagedProfileResponse> { new(OwnerProfileId, ProfileName, "#7C4DFF", null, DateTimeOffset.UtcNow) };
            profiles.AddRange(_extraProfileIds.Take(ProfileCount - 1).Select((id, index) =>
                new ManagedProfileResponse(id, $"Profile {index + 2}", "#7C4DFF", null, DateTimeOffset.UtcNow)));
            return profiles;
        }

        private AccountAccessResponse Account()
        {
            var grants = new List<AccountProfileGrantDto> { Grant(OwnerProfileId, ProfileName, true) };
            grants.AddRange(_extraProfileIds.Take(ProfileCount - 1).Select((id, index) => Grant(id, $"Profile {index + 2}", false)));

            return new(AccountId, Email, true, true, 1,
                [new("read", true), new("watch", true), new("listen", true), new("view", true)],
                HiddenIds, grants,
                DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(-2),
                MustChangePassword: TemporaryPasswordSet,
                TemporaryPasswordExpiresAt: TemporaryPasswordSet ? DateTimeOffset.UtcNow.AddDays(7) : null,
                HasTwoStep: HasTwoStep);
        }

        private AccountProfileGrantDto Grant(Guid id, string name, bool isDefault) =>
            new(AccountId, id, name, null, isDefault, true, true,
                new GrantAdminProtectionDto(false, "FixedDuration", 30, 1), 1, DateTimeOffset.UtcNow);

        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(value) };
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Body);
}
