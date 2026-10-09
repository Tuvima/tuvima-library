using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

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
        Assert.Equal([_handler.Library.Id], payload.LibraryIds);
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
    public async Task ProfileCreateAndRename_UseTypedMutations_AndRenderSuccess()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Manage profiles");
        var createSection = cut.FindAll(".access-form-section").Single(section => section.TextContent.Contains("Create a profile", StringComparison.Ordinal));
        createSection.QuerySelectorAll("input[type='text']")[0].Input("Kids");
        await cut.InvokeAsync(() => cut.FindAll(".access-form-section")
            .Single(section => section.TextContent.Contains("Create a profile", StringComparison.Ordinal))
            .QuerySelectorAll("button").Single(button => button.TextContent.Trim() == "Create profile").Click());

        cut.WaitForAssertion(() => Assert.Contains("Kids was created and granted.", cut.Markup));
        Assert.Contains(_handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/access/profiles");

        var rename = cut.FindAll(".access-grant__rename").First();
        rename.QuerySelector("input")!.Input("Owner renamed");
        await cut.InvokeAsync(() => cut.FindAll(".access-grant__rename").First().QuerySelectorAll("button")
            .Single(button => button.TextContent.Trim() == "Rename").Click());

        cut.WaitForAssertion(() => Assert.Contains("Profile renamed to Owner renamed.", cut.Markup));
        Assert.Contains(_handler.Requests, request => request.Method == HttpMethod.Put && request.Path == $"/access/profiles/{_handler.OwnerProfileId:D}");
    }

    [Fact]
    public void ProtectionInitiallyOff_StagesPinAndChosenDurationBeforeEnabling()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Manage profiles");

        cut.Find("input[type='password']").Input("2468");
        cut.Find("select[aria-label='PIN unlock duration for Owner']").Change("FixedDuration:60");
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Enable protection").Click();

        cut.WaitForAssertion(() => Assert.Contains("Admin protection for Owner was updated.", cut.Markup));
        var request = Assert.Single(_handler.Requests,
            request => request.Path.EndsWith("/admin-protection", StringComparison.Ordinal));
        var payload = JsonSerializer.Deserialize<SetGrantAdminProtectionRequest>(request.Body, JsonOptions)!;
        Assert.True(payload.Enabled);
        Assert.Equal("2468", payload.Pin);
        Assert.Equal("FixedDuration", payload.UnlockMode);
        Assert.Equal(60, payload.UnlockMinutes);
    }

    [Fact]
    public void Invitation_ShowsCodeLinkAndQrOnlyInTheOpenResultDrawer()
    {
        var cut = RenderUsers();
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Invite user").Click();
        cut.Find("input[type='text']").Input("friend@example.test");
        cut.Find("input[type='checkbox']").Change(true);
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
    public void DeleteAccount_UsesDeletionEndpoint_AndExplainsThatFilesAreKept()
    {
        var cut = RenderUsers();
        OpenAction(cut, "Delete user");
        Assert.Contains("Personal media files are kept", cut.Markup);
        cut.FindAll("button").Last(button => button.TextContent.Trim() == "Delete user").Click();

        cut.WaitForAssertion(() => Assert.Contains("was deleted. Personal media files were kept.", cut.Markup));
        Assert.Contains(_handler.Requests, request => request.Method == HttpMethod.Delete && request.Path == $"/access/accounts/{_handler.AccountId:D}");
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
        public Guid AccountId { get; } = Guid.NewGuid();
        public Guid OwnerProfileId { get; } = Guid.NewGuid();
        public AccessLibraryOptionDto Library { get; } = new(Guid.NewGuid(), "Books", "Books", "read");
        public HttpStatusCode AccessStatus { get; set; } = HttpStatusCode.NoContent;
        public TaskCompletionSource? AccessGate { get; set; }
        public TaskCompletionSource? AccessStarted { get; set; }
        public string Email { get; set; } = "owner@example.test";
        public int ProfileCount { get; set; } = 1;
        public List<RequestRecord> Requests { get; } = [];

        private string _profileName = "Owner";
        private bool _deleted;
        private bool _hasKids;
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
                return Json(new[] { Library });
            }

            if (request.Method == HttpMethod.Put && path.EndsWith("/access", StringComparison.Ordinal))
            {
                AccessStarted?.TrySetResult();
                if (AccessGate is not null)
                {
                    await AccessGate.Task.WaitAsync(cancellationToken);
                }

                return new HttpResponseMessage(AccessStatus);
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("/admin-protection", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Post && path == "/access/profiles")
            {
                var value = JsonSerializer.Deserialize<CreateManagedProfileRequest>(body, JsonOptions)!;
                _hasKids = true;
                return Json(new ManagedProfileResponse(KidsProfileId, value.DisplayName, value.AvatarColor ?? "#7C4DFF", null, DateTimeOffset.UtcNow), HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Put && path == $"/access/profiles/{OwnerProfileId:D}")
            {
                var value = JsonSerializer.Deserialize<UpdateManagedProfileRequest>(body, JsonOptions)!;
                _profileName = value.DisplayName;
                return Json(new ManagedProfileResponse(OwnerProfileId, _profileName, value.AvatarColor ?? "#7C4DFF", null, DateTimeOffset.UtcNow));
            }
            if (request.Method == HttpMethod.Post && path == "/access/invitations")
            {
                return Json(new AccountInvitationResponse(Guid.NewGuid(), InvitationCode, DateTimeOffset.UtcNow.AddDays(1), "https://tuvima.example"));
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

        private Guid KidsProfileId { get; } = Guid.NewGuid();

        private IReadOnlyList<ManagedProfileResponse> Profiles()
        {
            var profiles = new List<ManagedProfileResponse> { new(OwnerProfileId, _profileName, "#7C4DFF", null, DateTimeOffset.UtcNow) };
            profiles.AddRange(_extraProfileIds.Take(ProfileCount - 1).Select((id, index) =>
                new ManagedProfileResponse(id, $"Profile {index + 2}", "#7C4DFF", null, DateTimeOffset.UtcNow)));
            if (_hasKids)
            {
                profiles.Add(new(KidsProfileId, "Kids", "#7C4DFF", null, DateTimeOffset.UtcNow));
            }

            return profiles;
        }

        private AccountAccessResponse Account()
        {
            var grants = new List<AccountProfileGrantDto> { Grant(OwnerProfileId, _profileName, true) };
            grants.AddRange(_extraProfileIds.Take(ProfileCount - 1).Select((id, index) => Grant(id, $"Profile {index + 2}", false)));
            if (_hasKids)
            {
                grants.Add(Grant(KidsProfileId, "Kids", false));
            }

            return new(AccountId, Email, true, true, 1,
                [new("read", true), new("watch", true), new("listen", true), new("view", true)],
                [new(Library.Id, Library.DisplayName, true)], grants,
                DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(-2),
                MustChangePassword: TemporaryPasswordSet,
                TemporaryPasswordExpiresAt: TemporaryPasswordSet ? DateTimeOffset.UtcNow.AddDays(7) : null);
        }

        private AccountProfileGrantDto Grant(Guid id, string name, bool isDefault) =>
            new(AccountId, id, name, null, isDefault, true, true,
                new GrantAdminProtectionDto(false, "FixedDuration", 30, 1, false, null), 1, DateTimeOffset.UtcNow);

        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(value) };
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Body);
}
