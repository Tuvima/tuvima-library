using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Backups;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>Export my data, Export a user and Import people (backup and restore spec D19, WP6; users spec D9 and D16): only the server administrator exports.</summary>
public sealed class UserTransferSecurityPageTests : AsyncBunitContext
{
    private readonly DashboardSessionAccessor _session = new();
    private const string HelpLine = "Download a file with your sign-in, profiles, progress, bookmarks and favourites. Anyone with this file can sign in as you. Keep it somewhere safe.";
    private readonly ExportHandler _handler = new();
    private readonly Confirmer _confirmer = new();

    public UserTransferSecurityPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IHttpClientFactory>(new Factory(_handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<AccountNoticeState>();
        UseSession(serverAdministrator: true);
        Services.AddSingleton(_session);
        Services.AddSingleton<AuthenticationStateProvider>(new PasswordAccountState());
        Services.AddSingleton<IEngineApiClient>(System.Reflection.DispatchProxy.Create<IEngineApiClient, SecuritySettingsPageTests.PhotoViewsApiClient>());
        Services.AddScoped<IItsYouConfirmer>(_ => _confirmer);
        Services.AddScoped<ConfirmedActionRunner>();
        Services.AddSingleton<IReadOnlyList<RegisteredExternalAuthProvider>>([]);
        Render<MediaEngine.Web.Components.Shared.AppPopoverHost>();
    }

    [Fact]
    public void ExportMyData_IsOneRowInAccountDetails_WithTheHelpLineAndAnExportButton()
    {
        var cut = RenderPage();

        var row = cut.Find("[data-testid='export-my-data']");
        Assert.Contains("Export my data", row.TextContent, StringComparison.Ordinal);
        Assert.Contains(HelpLine, row.TextContent, StringComparison.Ordinal);
        Assert.Equal("Export", cut.Find("[data-testid='export-my-data-button']").TextContent.Trim());
        Assert.Contains("Account details", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportMyData_WhenItWorks_HandsTheFileToTheBrowserWithTheEnginesFileName()
    {
        var cut = RenderPage();

        cut.Find("[data-testid='export-my-data-button']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Your file was downloaded", cut.Find("[data-testid='export-my-data-result']").TextContent, StringComparison.Ordinal));
        var download = Assert.Single(JSInterop.Invocations, call => call.Identifier == "tuvimaDownloads.fromStream");
        Assert.Equal("tuvima-user-20261022-101500.zip", download.Arguments[0]);
        Assert.Equal("application/zip", download.Arguments[2]);
        Assert.Equal(1, _handler.Exports);
        Assert.Equal(0, _confirmer.Asked);
    }

    [Fact]
    public void ExportMyData_WhenTheSignInIsStale_AsksToConfirmItsYou_ThenRetriesOnce()
    {
        _handler.StaleSignIns = 1;
        var cut = RenderPage();

        cut.Find("[data-testid='export-my-data-button']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Your file was downloaded", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(1, _confirmer.Asked);
        Assert.Equal(2, _handler.Exports);
        Assert.Single(JSInterop.Invocations, call => call.Identifier == "tuvimaDownloads.fromStream");
    }

    [Fact]
    public void ExportMyData_WhenTheyDoNotConfirmItsThem_DownloadsNothingAndSaysSo()
    {
        _handler.StaleSignIns = 5;
        _confirmer.Confirms = false;
        var cut = RenderPage();

        cut.Find("[data-testid='export-my-data-button']").Click();

        cut.WaitForAssertion(() => Assert.Equal("Nothing was downloaded. Confirm it's you to continue.",
            cut.Find("[data-testid='export-my-data-result']").TextContent.Trim()));
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "tuvimaDownloads.fromStream");
        Assert.Equal(1, _handler.Exports);
    }

    [Fact]
    public void ExportMyData_WhenTheEngineSaysWhy_ShowsThatSentenceAsItIs()
    {
        _handler.FailWith = (HttpStatusCode.UnprocessableEntity, "The file couldn't be made on this computer.");
        var cut = RenderPage();

        cut.Find("[data-testid='export-my-data-button']").Click();

        cut.WaitForAssertion(() => Assert.Equal("The file couldn't be made on this computer.",
            cut.Find("[data-testid='export-my-data-result']").TextContent.Trim()));
        Assert.Equal("alert", cut.Find("[data-testid='export-my-data-result']").GetAttribute("role"));
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "tuvimaDownloads.fromStream");
    }

    [Fact]
    public void ExportMyData_AfterAServerError_NeverShowsTheErrorsText_OnlyThePlainSentence()
    {
        _handler.FailWith = (HttpStatusCode.InternalServerError, "System.IO.IOException at C:\\data\\export.zip");
        var cut = RenderPage();

        cut.Find("[data-testid='export-my-data-button']").Click();

        cut.WaitForAssertion(() => Assert.Equal("The file couldn't be made. Try again.",
            cut.Find("[data-testid='export-my-data-result']").TextContent.Trim()));
        Assert.DoesNotContain("IOException", cut.Markup);
    }

    [Fact]
    public void ExportMyData_WhenTheEngineGivesNoReason_ShowsOnePlainSentence()
    {
        _handler.FailWith = (HttpStatusCode.InternalServerError, null);
        var cut = RenderPage();

        cut.Find("[data-testid='export-my-data-button']").Click();

        cut.WaitForAssertion(() => Assert.Equal("The file couldn't be made. Try again.",
            cut.Find("[data-testid='export-my-data-result']").TextContent.Trim()));
    }

    private IRenderedComponent<SecuritySettingsPage> RenderPage()
    {
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-testid='export-my-data-button']");
        return cut;
    }

    private void UseSession(bool serverAdministrator)
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        _session.Set("session", accountId, profileId, Guid.NewGuid(), new DashboardAuthorityResponse(
            accountId, profileId, true, true, 1, 1, serverAdministrator, serverAdministrator, null, 1, [],
            serverAdministrator ? ["settings.administration"] : [], serverAdministrator ? ["access.manage"] : ["household.manage"]));
    }

    [Fact]
    public void ExportMyData_IsNotOffered_ToAnyoneButTheServerAdministrator_HouseholdAdministratorsIncluded()
    {
        UseSession(serverAdministrator: false);

        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Contains("Account details", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='export-my-data']"));
        Assert.DoesNotContain("Export my data", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(0, _handler.Exports);
    }

    private sealed class Confirmer : IItsYouConfirmer
    {
        public bool Confirms { get; set; } = true;
        public int Asked { get; private set; }

        public Task<bool> ConfirmAsync(CancellationToken ct = default)
        {
            Asked++;
            return Task.FromResult(Confirms);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://engine.example.test") };
    }

    private sealed class PasswordAccountState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "Owner"), new Claim("tuvima:authentication_method", "Password")], "Test"))));
    }

    private sealed class ExportHandler : HttpMessageHandler
    {
        private readonly Guid _account = Guid.NewGuid();
        private readonly Guid _profile = Guid.NewGuid();

        /// <summary>How many export calls are refused with "confirm it's you" before one is allowed.</summary>
        public int StaleSignIns { get; set; }
        public (HttpStatusCode Status, string? Detail)? FailWith { get; set; }
        public int Exports { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/access/me/export")
            {
                Exports++;
                if (Exports <= StaleSignIns)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = JsonContent.Create(new { code = "confirm_its_you", detail = "Confirm it's you first." }),
                    });
                }

                if (FailWith is { } failure)
                {
                    return Task.FromResult(new HttpResponseMessage(failure.Status)
                    {
                        Content = failure.Detail is null ? null : JsonContent.Create(new { detail = failure.Detail }),
                    });
                }

                var file = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([80, 75, 3, 4]) };
                file.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                file.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "tuvima-user-20261022-101500.zip" };
                return Task.FromResult(file);
            }

            HttpResponseMessage response = path switch
            {
                "/access/self-service" => Json(new AccountSelfServiceResponse(
                    _account, "owner@example.test", _profile, _profile, [], [],
                    new AccountSecurityCapabilitiesResponse(true, false, false, true, false, false, false, []))),
                "/access/self-service/notices" => Json(new AccountNoticesResponse([])),
                "/auth/sessions" => Json(Array.Empty<DeviceSessionResponse>()),
                "/auth/passkeys" => Json(Array.Empty<PasskeyCredentialResponse>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}

public sealed class UserTransferUsersTests : AsyncBunitContext
{
    private readonly TransferHandler _handler = new();
    private readonly ConfirmedActionRunnerTests.SpyConfirmer _confirmer = new(confirmed: true);
    private bool _administrator = true;

    public UserTransferUsersTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IHttpClientFactory>(new Factory(_handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped(_ => Session(_administrator));
        Services.AddScoped<IItsYouConfirmer>(_ => _confirmer);
        Services.AddScoped<ConfirmedActionRunner>();
    }

    // ----- Export a user -----

    [Fact]
    public void ExportInTheRowMenu_IsOfferedToAdministrators_ForAnAccountWithAnEmail()
    {
        var cut = RenderUsers();

        OpenMenu(cut);

        Assert.Contains(cut.FindAll("[role='menu'] button"), button => button.TextContent.Trim() == "Export");
    }

    [Fact]
    public void ExportInTheRowMenu_IsNotOfferedForAnAccountWithoutAnEmail()
    {
        _handler.OwnerEmail = null;
        var cut = RenderUsers("No email");

        OpenMenu(cut, "No email");

        Assert.Contains(cut.FindAll("[role='menu'] button"), button => button.TextContent.Trim() == "Change email");
        Assert.DoesNotContain(cut.FindAll("[role='menu'] button"), button => button.TextContent.Trim() == "Export");
    }

    [Fact]
    public async Task ExportInTheRowMenu_IsNotOfferedOnAPhone()
    {
        await UsePhoneAsync();

        var cut = RenderUsers();

        Assert.Empty(cut.FindAll("button[aria-label^='Actions for']"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Export");
    }

    [Fact]
    public void ExportInTheRowMenu_IsNotOfferedToAnyoneWhoCannotManageUsers()
    {
        _administrator = false;

        var cut = Render<ManagedAccessUsers>();

        cut.WaitForAssertion(() => Assert.Contains("Administrator access is required", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() is "Export" or "Import a user");
    }

    [Fact]
    public void ExportInTheRowMenu_DownloadsTheFileForThatAccount()
    {
        var cut = RenderUsers();
        OpenMenu(cut);

        ClickMenu(cut, "Export");

        cut.WaitForAssertion(() => Assert.Contains("was downloaded", cut.Markup, StringComparison.Ordinal));
        var download = Assert.Single(JSInterop.Invocations, call => call.Identifier == "tuvimaDownloads.fromStream");
        Assert.Equal("tuvima-user-20261022-101500.zip", download.Arguments[0]);
        Assert.Equal($"/access/accounts/{_handler.OwnerId:D}/export", _handler.Requests.Single(request => request.Path.EndsWith("/export", StringComparison.Ordinal)).Path);
    }

    [Fact]
    public void ExportInTheRowMenu_WhenTheSignInIsStale_AsksToConfirmItsYou_ThenRetriesOnce()
    {
        _handler.StaleExports = 1;
        var cut = RenderUsers();
        OpenMenu(cut);

        ClickMenu(cut, "Export");

        cut.WaitForAssertion(() => Assert.Contains("was downloaded", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(1, _confirmer.Asked);
        Assert.Equal(2, _handler.Requests.Count(request => request.Path.EndsWith("/export", StringComparison.Ordinal)));
    }

    [Fact]
    public void ExportInTheRowMenu_WhenTheEngineRefuses_ShowsItsSentenceAndDownloadsNothing()
    {
        _handler.ExportFailure = (HttpStatusCode.Conflict, "Another change is in progress. Try again in a moment.");
        var cut = RenderUsers();
        OpenMenu(cut);

        ClickMenu(cut, "Export");

        cut.WaitForAssertion(() => Assert.Equal("Another change is in progress. Try again in a moment.",
            cut.Find("[data-testid='access-export-error']").TextContent.Trim()));
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "tuvimaDownloads.fromStream");
    }

    [Fact]
    public void ExportInTheRowMenu_WhenTheBrowserRefusesTheDownload_SaysSoAndKeepsThePage()
    {
        JSInterop.SetupVoid("tuvimaDownloads.fromStream", _ => true).SetException(new JSException("Download blocked"));
        var cut = RenderUsers();
        OpenMenu(cut);

        ClickMenu(cut, "Export");

        cut.WaitForAssertion(() => Assert.Equal("The file couldn't be saved. Try again.",
            cut.Find("[data-testid='access-export-error']").TextContent.Trim()));
    }

    // ----- Import a user -----

    [Fact]
    public void ImportAUser_IsHiddenForNonAdministrators()
    {
        _administrator = false;

        var cut = Render<ManagedAccessUsers>();

        cut.WaitForAssertion(() => Assert.Contains("Administrator access is required", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='access-import-user']"));
    }

    [Fact]
    public async Task ImportAUser_IsHiddenOnAPhone()
    {
        await UsePhoneAsync();

        var cut = RenderUsers();

        Assert.Empty(cut.FindAll("[data-testid='access-import-user']"));
    }

    [Fact]
    public void ImportAUser_ShowsWhoIsInTheFile_BeforeAnythingIsAdded()
    {
        var cut = RenderUsers();

        ChooseFile(cut);

        var preview = cut.WaitForElement("[data-testid='user-import-preview']").TextContent;
        Assert.Contains("Household: Finch family", preview, StringComparison.Ordinal);
        Assert.Contains("Sam", preview, StringComparison.Ordinal);
        Assert.Contains("Kids profile, limited to PG", preview, StringComparison.Ordinal);
        Assert.Contains("no sign-in", preview, StringComparison.Ordinal);
        Assert.Contains("12 items of progress, bookmarks and favourites, which return as their titles are found.", preview, StringComparison.Ordinal);
        Assert.Equal("sam@example.test", cut.Find("[data-testid='user-import-email-0']").GetAttribute("value"));
        Assert.Equal(
            "They arrive as ordinary members (Kids profiles stay Kids) with no access, so they won't see any libraries until you give them access.",
            cut.Find("[data-testid='user-import-summary']").TextContent.Trim());
        Assert.Contains(cut.FindAll("[data-testid='user-import-confirm']"), button => button.TextContent.Trim() == "Import");
        Assert.DoesNotContain(_handler.Requests, request => request.Path.StartsWith("/access/accounts/import/", StringComparison.Ordinal) && request.Path != "/access/accounts/import/inspect");
    }

    [Fact]
    public void ImportAUser_WhenTheFileLeftTwoStepOut_SaysThePersonSetsItUpAgain()
    {
        _handler.Preview = Preview(twoStepToSetUp: true);
        var cut = RenderUsers();

        ChooseFile(cut);

        var line = cut.WaitForElement("[data-testid='user-import-two-step']");
        Assert.Equal("They'll need to set up two-step sign-in again after they sign in.", line.TextContent.Trim());
    }

    [Fact]
    public void ImportAUser_WhenTheFileKeptTwoStep_SaysNothingAboutSettingItUp()
    {
        var cut = RenderUsers();

        ChooseFile(cut);

        cut.WaitForElement("[data-testid='user-import-preview']");
        Assert.Empty(cut.FindAll("[data-testid='user-import-two-step']"));
        Assert.DoesNotContain("two-step", cut.Find("[data-testid='user-import']").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportAUser_ShowsAnAdministratorRoleInTheFileAsAStandardProfile_BecauseRolesAreNeverCarried()
    {
        _handler.Preview = Preview(profiles: [new("Pat", "Administrator", null, false)]);
        var cut = RenderUsers();

        ChooseFile(cut);

        var preview = cut.WaitForElement("[data-testid='user-import-preview']").TextContent;
        Assert.Contains("Pat", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("Administrator", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("Kids", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportAUser_WhenTheEngineBlocksIt_ShowsThatSentenceAndNoImportButton()
    {
        _handler.Preview = Preview(blockedReason: "sam@example.test already signs in to this Library.");
        var cut = RenderUsers();

        ChooseFile(cut);

        cut.WaitForAssertion(() => Assert.Equal("sam@example.test already signs in to this Library.",
            cut.Find("[data-testid='user-import-blocked']").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-testid='user-import-confirm']"));
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Cancel");
        Assert.Empty(cut.FindAll("[data-testid='user-import-where']"));
    }

    [Fact]
    public void ImportAUser_WhenTheSignInIsStale_AsksToConfirmItsYou_ThenSendsTheFileAgain()
    {
        _handler.StaleInspects = 1;
        var cut = RenderUsers();

        ChooseFile(cut);

        cut.WaitForElement("[data-testid='user-import-preview']");
        Assert.Equal(1, _confirmer.Asked);
        var inspections = _handler.Requests.Where(request => request.Path == "/access/accounts/import/inspect").ToArray();
        Assert.Equal(2, inspections.Length);
        Assert.All(inspections, request => Assert.Contains("user file bytes", request.Body, StringComparison.Ordinal));
        Assert.All(inspections, request => Assert.Contains("name=file", request.Body, StringComparison.Ordinal));
    }

    [Fact]
    public void ImportAUser_WhenTheFileCannotBeRead_ShowsTheEnginesSentenceAndNoPreview()
    {
        _handler.InspectFailure = (HttpStatusCode.BadRequest, "That isn't a user export file.");
        var cut = RenderUsers();

        ChooseFile(cut);

        cut.WaitForAssertion(() => Assert.Equal("That isn't a user export file.", cut.Find("[data-testid='user-import-error']").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-testid='user-import-preview']"));
    }

    [Fact]
    public void ImportAUser_OnSuccess_ListsTheNewSignInAndPointsToTheirProfileForAccess()
    {
        var cut = RenderUsers();
        ChooseFile(cut);
        cut.WaitForElement("[data-testid='user-import-confirm']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Sam, Sam (grown-up) were added to Home. Choose what they can see on their profile, under Profiles.", cut.Find("[data-testid='access-notice']").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid='user-import']"));
            Assert.Contains("Sam", cut.Find(".access-users").TextContent, StringComparison.Ordinal);
        });
        var import = Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/accounts/import/{_handler.OperationId:D}");
        using var body = JsonDocument.Parse(import.Body);
        Assert.Equal(TransferHandler.HomeId, body.RootElement.GetProperty("target_household_id").GetGuid());
        Assert.False(body.RootElement.GetProperty("keep_access").GetBoolean());
        Assert.Empty(body.RootElement.GetProperty("emails").EnumerateObject());
        Assert.Empty(body.RootElement.GetProperty("without_sign_in").EnumerateArray());
    }

    [Fact]
    public void ImportAUser_WhenSomethingClashesNow_ShowsTheEnginesSentence_AndKeepsTheChoicesSoOneCanBeChanged()
    {
        _handler.ImportFailure = (HttpStatusCode.Conflict, "sam@example.test was added a moment ago by someone else.");
        var cut = RenderUsers();
        ChooseFile(cut);
        cut.WaitForElement("[data-testid='user-import-confirm']").Click();

        cut.WaitForAssertion(() => Assert.Equal("sam@example.test was added a moment ago by someone else.",
            cut.Find("[data-testid='user-import-error']").TextContent.Trim()));
        Assert.NotEmpty(cut.FindAll("[data-testid='user-import-confirm']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='user-import-preview']"));
    }

    [Fact]
    public void ImportAUser_WhenTheEmailIsUsedHere_NeedsADifferentEmailOrNoSignIn_AndSendsTheChosenOne()
    {
        _handler.Preview = Preview(signInEmailInUse: true);
        var cut = RenderUsers();
        ChooseFile(cut);
        cut.WaitForElement("[data-testid='user-import-email-used']");
        Assert.True(cut.Find("[data-testid='user-import-confirm']").HasAttribute("disabled"));

        cut.Find("[data-testid='user-import-email-0']").Input("sam.new@example.test");

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid='user-import-confirm']").HasAttribute("disabled")));
        Assert.Empty(cut.FindAll("[data-testid='user-import-email-used']"));
        cut.Find("[data-testid='user-import-confirm']").Click();
        var body = ImportBody();
        Assert.Equal("sam.new@example.test", body.RootElement.GetProperty("emails").GetProperty(TransferHandler.SamSignIn.ToString("D")).GetString());
    }

    [Fact]
    public void ImportAUser_WithoutASignIn_SendsThatChoice_AndNoEmailForIt()
    {
        _handler.Preview = Preview(signInEmailInUse: true);
        var cut = RenderUsers();
        ChooseFile(cut);
        cut.WaitForElement("[data-testid='user-import-email-used']");

        cut.Find("[data-testid='user-import-without-0']").Change(true);

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid='user-import-confirm']").HasAttribute("disabled")));
        Assert.True(cut.Find("[data-testid='user-import-email-0']").HasAttribute("disabled"));
        cut.Find("[data-testid='user-import-confirm']").Click();
        var body = ImportBody();
        Assert.Equal(TransferHandler.SamSignIn, Assert.Single(body.RootElement.GetProperty("without_sign_in").EnumerateArray()).GetGuid());
        Assert.Empty(body.RootElement.GetProperty("emails").EnumerateObject());
    }

    [Fact]
    public void ImportAUser_ASignInThatCannotMove_ArrivesWithoutASignIn_AndSaysWhy()
    {
        _handler.Preview = Preview(signInProblem: "Their two-step sign-in can't be moved to this Library. They can still come without a sign-in.");
        var cut = RenderUsers();
        ChooseFile(cut);

        Assert.Equal("Their two-step sign-in can't be moved to this Library. They can still come without a sign-in.",
            cut.WaitForElement("[data-testid='user-import-signin-problem']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='user-import-email-0']"));
        cut.Find("[data-testid='user-import-confirm']").Click();
        var body = ImportBody();
        Assert.Equal(TransferHandler.SamSignIn, Assert.Single(body.RootElement.GetProperty("without_sign_in").EnumerateArray()).GetGuid());
    }

    [Fact]
    public void ImportAUser_KeepTheirAccess_IsOffByDefault_AndSentOnlyWhenTicked()
    {
        var cut = RenderUsers();
        ChooseFile(cut);
        cut.WaitForElement("[data-testid='user-import-keep-access']");
        Assert.False(cut.Find("[data-testid='user-import-keep-access']").HasAttribute("checked"));

        cut.Find("[data-testid='user-import-keep-access']").Change(true);

        cut.WaitForAssertion(() => Assert.Contains("with the Read, Watch, Listen and View access the file had", cut.Find("[data-testid='user-import-summary']").TextContent, StringComparison.Ordinal));
        cut.Find("[data-testid='user-import-confirm']").Click();
        Assert.True(ImportBody().RootElement.GetProperty("keep_access").GetBoolean());
    }

    [Fact]
    public void ImportAUser_WhenTheFileHasNoAccess_OffersNoKeepAccessChoice()
    {
        _handler.Preview = Preview(profiles: [new("Sam", "StandardUser", null, false, TransferHandler.SamProfile, TransferHandler.SamSignIn, [])]);
        var cut = RenderUsers();
        ChooseFile(cut);
        cut.WaitForElement("[data-testid='user-import-where']");

        Assert.Empty(cut.FindAll("[data-testid='user-import-keep-access']"));
    }

    [Fact]
    public void ImportAUser_IntoANewHousehold_NeedsANameAndSomeoneToLookAfterIt_AndSendsThem()
    {
        _handler.Preview = Preview(
            profiles:
            [
                new("Sam", "RestrictedProfile", "PG", false, TransferHandler.SamProfile, TransferHandler.SamSignIn, []),
                new("Pat", "StandardUser", null, false, TransferHandler.PatProfile, TransferHandler.PatSignIn, []),
            ],
            signIns: [Sign(TransferHandler.SamSignIn, "sam@example.test", TransferHandler.SamProfile), Sign(TransferHandler.PatSignIn, "pat@example.test", TransferHandler.PatProfile)]);
        var cut = RenderUsers();
        ChooseFile(cut);
        cut.WaitForElement("[data-testid='user-import-where']");

        cut.FindAll("[data-testid='user-import-where'] button").Single(button => button.TextContent.Trim() == "A new household").Click();

        cut.WaitForElement("[data-testid='user-import-new-household']");
        // The Kids profile can't look after a household, so only Pat is offered, and the only candidate is chosen already.
        var choices = cut.Find("[data-testid='user-import-administrator']").QuerySelectorAll("option").Select(option => option.TextContent.Trim()).ToList();
        Assert.Equal(["Who looks after it?", "Pat"], choices);
        cut.Find("[data-testid='user-import-new-household']").Input("  The Finches  ");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid='user-import-confirm']").HasAttribute("disabled")));
        cut.Find("[data-testid='user-import-confirm']").Click();

        var body = ImportBody();
        Assert.Equal("The Finches", body.RootElement.GetProperty("new_household_name").GetString());
        Assert.Equal(TransferHandler.PatProfile, body.RootElement.GetProperty("new_household_administrator_profile_id").GetGuid());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("target_household_id").ValueKind);
    }

    [Fact]
    public void ImportAUser_IntoAHouseholdWithNoRoom_SaysSoAndCannotImport()
    {
        _handler.Preview = Preview(
            households: [new(TransferHandler.HomeId, "Home", 8)],
            profiles: [new("Sam", "StandardUser", null, false, TransferHandler.SamProfile, TransferHandler.SamSignIn, [])]);
        var cut = RenderUsers();
        ChooseFile(cut);

        var line = cut.WaitForElement("[data-testid='user-import-full']").TextContent;

        Assert.Contains("That household has room for 0 more, and the file has 1.", line, StringComparison.Ordinal);
        Assert.Contains("up to 8 people", line, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid='user-import-confirm']").HasAttribute("disabled"));
    }

    [Fact]
    public void ImportAUser_AsASecondHouseholdBeforeTheAdministratorHasAPassword_ShowsTheEnginesSentenceAndAWayToFixIt()
    {
        _handler.Preview = Preview(canAddHousehold: false);
        var cut = RenderUsers();
        ChooseFile(cut);
        cut.WaitForElement("[data-testid='user-import-where']");

        cut.FindAll("[data-testid='user-import-where'] button").Single(button => button.TextContent.Trim() == "A new household").Click();

        Assert.Equal(
            "Set an email and password for your own sign-in first. A Library with several households must not be open to anyone who reaches it.",
            cut.WaitForElement("[data-testid='user-import-secure-first']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='user-import-new-household']"));
        Assert.Contains(cut.FindAll("[data-testid='user-import-where'] button"), button => button.TextContent.Trim() == "Set an email and password");
        Assert.True(cut.Find("[data-testid='user-import-confirm']").HasAttribute("disabled"));
    }

    [Fact]
    public void ImportAUser_CancelClosesTheDrawerWithoutAddingAnyone()
    {
        var cut = RenderUsers();
        ChooseFile(cut);
        cut.WaitForElement("[data-testid='user-import-preview']");

        cut.FindAll("[data-testid='user-import'] button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='user-import']")));
        Assert.DoesNotContain(_handler.Requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/accounts/import/{_handler.OperationId:D}");
    }

    [Theory]
    [InlineData("RestrictedProfile", "PG-13", false, "Kids profile, limited to PG-13")]
    [InlineData("StandardUser", "R", true, "limited to R, unrated titles allowed")]
    [InlineData("StandardUser", null, false, null)]
    [InlineData("Administrator", null, false, null)]
    public void ImportPreview_DescribesAProfileInPlainWords(string role, string? limit, bool allowUnrated, string? expected)
    {
        Assert.Equal(expected, ManagedAccessUserImport.ProfileNote(new("Sam", role, limit, allowUnrated)));
    }

    // ----- helpers -----

    private static UserImportPreviewDto Preview(
        IReadOnlyList<UserImportProfileDto>? profiles = null,
        string? blockedReason = null,
        bool twoStepToSetUp = false,
        IReadOnlyList<UserImportSignInDto>? signIns = null,
        IReadOnlyList<UserImportHouseholdDto>? households = null,
        bool canAddHousehold = true,
        bool signInEmailInUse = false,
        string? signInProblem = null) =>
        new(TransferHandler.DefaultOperationId, "profile", "Finch family",
            profiles ??
            [
                new("Sam", "RestrictedProfile", "PG", false, TransferHandler.SamProfile, TransferHandler.SamSignIn, ["read"]),
                new("Sam (grown-up)", "StandardUser", null, false, Guid.NewGuid(), null, []),
            ],
            signIns ?? [new(TransferHandler.SamSignIn, "sam@example.test", TransferHandler.SamProfile, signInEmailInUse, signInProblem)],
            households ?? [new(TransferHandler.HomeId, "Home", 1)],
            canAddHousehold,
            canAddHousehold ? null : "Set an email and password for your own sign-in first. A Library with several households must not be open to anyone who reaches it.",
            8,
            12,
            blockedReason,
            twoStepToSetUp);

    private static UserImportSignInDto Sign(Guid id, string email, Guid profile) => new(id, email, profile, false, null);

    private JsonDocument ImportBody()
    {
        var import = Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/accounts/import/{_handler.OperationId:D}");
        return JsonDocument.Parse(import.Body);
    }

    private IRenderedComponent<ManagedAccessUsers> RenderUsers(string expectedText = "owner@example.test")
    {
        var cut = Render<ManagedAccessUsers>();
        cut.WaitForAssertion(() => Assert.Contains(expectedText, cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static void OpenMenu(IRenderedComponent<ManagedAccessUsers> cut, string label = "owner@example.test")
    {
        cut.Find($"button[aria-label='Actions for {label}']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menu']")));
    }

    private static void ClickMenu(IRenderedComponent<ManagedAccessUsers> cut, string label) =>
        cut.FindAll("[role='menu'] button").Single(button => button.TextContent.Trim() == label).Click();

    private static void ChooseFile(IRenderedComponent<ManagedAccessUsers> cut)
    {
        cut.Find("[data-testid='access-import-user']").Click();
        cut.WaitForElement("[data-testid='user-import']");
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("user file bytes", "tuvima-user.zip"));
    }

    private async Task UsePhoneAsync()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        Services.AddScoped(_ => device);
    }

    private static DashboardSessionAccessor Session(bool administrator)
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var session = new DashboardSessionAccessor();
        session.Set("session", accountId, profileId, Guid.NewGuid(), new DashboardAuthorityResponse(
            accountId, profileId, true, true, 1, 1, administrator, administrator, null, 1, [],
            administrator ? ["settings.administration"] : [], administrator ? ["access.manage"] : []));
        return session;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Body);

    private sealed class TransferHandler : HttpMessageHandler
    {
        public static readonly Guid DefaultOperationId = Guid.Parse("8f1d1f0a-3b5e-4c6d-9a47-0d2f5a3c9e11");
        public static readonly Guid HomeId = Guid.Parse("5a0c1f10-2b3c-4d5e-8f60-7a8b9c0d1e2f");
        public static readonly Guid SamProfile = Guid.Parse("11111111-aaaa-4aaa-8aaa-111111111111");
        public static readonly Guid SamSignIn = Guid.Parse("22222222-aaaa-4aaa-8aaa-222222222222");
        public static readonly Guid PatProfile = Guid.Parse("33333333-aaaa-4aaa-8aaa-333333333333");
        public static readonly Guid PatSignIn = Guid.Parse("44444444-aaaa-4aaa-8aaa-444444444444");

        private readonly Guid _ownerProfile = Guid.NewGuid();
        private readonly Guid _importedProfile = Guid.NewGuid();
        private readonly Guid _household = Guid.NewGuid();
        private bool _imported;

        public Guid OwnerId { get; } = Guid.NewGuid();
        public Guid ImportedAccountId { get; } = Guid.NewGuid();
        public Guid OperationId => DefaultOperationId;
        public string? OwnerEmail { get; set; } = "owner@example.test";
        public UserImportPreviewDto Preview { get; set; } = UserTransferUsersTests.Preview();
        public int StaleExports { get; set; }
        public int StaleInspects { get; set; }
        public (HttpStatusCode Status, string Detail)? ExportFailure { get; set; }
        public (HttpStatusCode Status, string Detail)? InspectFailure { get; set; }
        public (HttpStatusCode Status, string Detail)? ImportFailure { get; set; }
        public List<RequestRecord> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, path, body));

            if (request.Method == HttpMethod.Get && path == "/access/users")
            {
                return Json(_imported ? new[] { User(OwnerId, OwnerEmail, _ownerProfile, "Owner"), User(ImportedAccountId, "sam@example.test", _importedProfile, "Sam") }
                    : [User(OwnerId, OwnerEmail, _ownerProfile, "Owner")]);
            }

            if (request.Method == HttpMethod.Get && path == "/access/profiles")
            {
                return Json(new[] { new ManagedProfileResponse(_ownerProfile, "Owner", "#7C4DFF", null, DateTimeOffset.UtcNow) });
            }

            if (request.Method == HttpMethod.Get && path == "/access/households")
            {
                return Json(new[] { new HouseholdResponse(_household, "Home", 1, 1, DateTimeOffset.UtcNow) });
            }

            if (request.Method == HttpMethod.Get && path == "/access/libraries")
            {
                return Json(new[] { new AccessLibraryOptionDto(Guid.NewGuid(), "Books", "Books", "read") });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/export", StringComparison.Ordinal))
            {
                if (StaleExports > 0)
                {
                    StaleExports--;
                    return ConfirmItsYou();
                }

                if (ExportFailure is { } failure)
                {
                    return Problem(failure);
                }

                var file = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([80, 75, 3, 4]) };
                file.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                file.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "tuvima-user-20261022-101500.zip" };
                return file;
            }

            if (request.Method == HttpMethod.Post && path == "/access/accounts/import/inspect")
            {
                if (StaleInspects > 0)
                {
                    StaleInspects--;
                    return ConfirmItsYou();
                }

                return InspectFailure is { } failure ? Problem(failure) : Json(Preview);
            }

            if (request.Method == HttpMethod.Post && path == $"/access/accounts/import/{DefaultOperationId:D}")
            {
                if (ImportFailure is { } failure)
                {
                    return Problem(failure);
                }

                _imported = true;
                return Json(new UserImportResultDto(
                    HomeId,
                    "Home",
                    false,
                    [new(_importedProfile, "Sam", "sam@example.test", false), new(Guid.NewGuid(), "Sam (grown-up)", null, false)],
                    12));
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private UserSummaryResponse User(Guid id, string? email, Guid profileId, string profileName) =>
            new(id, email, profileId, profileName, _household, "Home", true, false, true, false);

        private static HttpResponseMessage ConfirmItsYou() => new(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new { code = "confirm_its_you", detail = "Confirm it's you first." }),
        };

        private static HttpResponseMessage Problem((HttpStatusCode Status, string Detail) failure) =>
            new(failure.Status) { Content = JsonContent.Create(new { detail = failure.Detail }) };

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
