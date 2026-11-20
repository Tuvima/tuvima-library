using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Networking;
using MediaEngine.Api.Services.Security;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Use on this computer": a desktop owner starts with a name and email, the account works only on this computer,
/// and anything that would let others in waits until it has a password or passkey.
/// </summary>
public sealed class ThisComputerAccessTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-this-computer-{Guid.NewGuid():N}.db");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-this-computer-cfg-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly AccountRepository _accounts;
    private readonly IdentityRepository _identities;
    private readonly OnboardingRepository _onboarding;
    private readonly FirstPartyIdentityService _identity;
    private readonly FirstPartyIdentityService _identityWithPolicy;
    private readonly SetupSessionService _setupSessions;
    private readonly FakeContainerProbe _container = new();
    private readonly SpyRouterMapper _routerMapper = new();

    public ThisComputerAccessTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _configuration = new ConfigurationDirectoryLoader(_configPath);
        _accounts = new AccountRepository(_database);
        _identities = new IdentityRepository(_database);
        _onboarding = new OnboardingRepository(_database);
        _identity = new FirstPartyIdentityService(
            _identities, _accounts, new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            TimeProvider.System, new FixedPolicy());
        // The same service, but holding the Engine's real rule for away-from-home access (read from this test's network settings).
        _identityWithPolicy = new FirstPartyIdentityService(
            _identities, _accounts, new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            TimeProvider.System, new FixedPolicy(), outsideAccess: new ConfigurationOutsideAccessPolicy(_configuration));
        _setupSessions = new SetupSessionService(_onboarding, _identity, TimeProvider.System);
    }

    // ---- Setup ----

    [Fact]
    public async Task Setup_ThisComputer_OutsideAContainer_NeedsOnlyAName()
    {
        await using var app = BuildSetupApplication();
        var token = await BeginSetupAsync(ClientIngress.ThisComputer);

        var response = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            SignIn = SetupSignInModes.ThisComputer,
            DisplayName = "Owner",
            DeviceId = "browser",
            DeviceName = "Browser",
            OriginalClientIngress = ClientIngress.ThisComputer,
        }, setupToken: token);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.True(response.Json.GetProperty("created").GetBoolean());
        Assert.Equal(0, response.Json.GetProperty("recovery_codes").GetArrayLength());
        var account = Assert.Single(await _accounts.GetAllAsync());
        Assert.True(account.IsThisComputerOnly);
        Assert.Null(account.Email);
        Assert.Null(account.NormalizedEmail);
        Assert.Null(await _identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Setup_WithoutAPassword_IsAllowedOnlyFromThisComputerEvenInAContainer(bool inContainer)
    {
        _container.InContainer = inContainer;
        await using var app = BuildSetupApplication();
        var token = await BeginSetupAsync(ClientIngress.ThisComputer);

        var response = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            SignIn = SetupSignInModes.ThisComputer,
            Email = "owner@example.com",
            DisplayName = "Owner",
            OriginalClientIngress = ClientIngress.ThisComputer,
        }, setupToken: token);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.True(await _identity.IsAdministratorConfiguredAsync());
    }

    // Home network spec D8: the Dashboard listens on the home network, so a phone or laptop there must never be able to
    // start Library as an administrator without a password.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Setup_WithoutAPassword_IsRefusedFromTheHomeNetworkEvenInAContainer(bool inContainer)
    {
        _container.InContainer = inContainer;
        await using var app = BuildSetupApplication();
        var token = await BeginSetupAsync(ClientIngress.HomeNetwork);

        var response = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            SignIn = SetupSignInModes.ThisComputer,
            Email = "owner@example.com",
            DisplayName = "Owner",
            OriginalClientIngress = ClientIngress.HomeNetwork,
        }, setupToken: token);

        Assert.Equal(StatusCodes.Status409Conflict, response.Status);
        Assert.Equal(SetupAdministratorRefusalCodes.NotAvailableHere, response.Json.GetProperty("code").GetString());
        Assert.False(await _identity.IsAdministratorConfiguredAsync());
    }

    [Fact]
    public async Task Setup_ThisComputer_RefusesAPasswordAndMissingDetails()
    {
        await using var app = BuildSetupApplication();
        var token = await BeginSetupAsync(ClientIngress.ThisComputer);

        var withPassword = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            SignIn = SetupSignInModes.ThisComputer,
            Email = "owner@example.com",
            Password = "correct horse battery staple",
            DisplayName = "Owner",
            OriginalClientIngress = ClientIngress.ThisComputer,
        }, setupToken: token);
        var withoutName = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            SignIn = SetupSignInModes.ThisComputer,
            DisplayName = " ",
            OriginalClientIngress = ClientIngress.ThisComputer,
        }, setupToken: token);

        Assert.Equal(StatusCodes.Status400BadRequest, withPassword.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, withoutName.Status);
        Assert.False(await _identity.IsAdministratorConfiguredAsync());
    }

    [Fact]
    public async Task Setup_WithAPasswordButNoEmail_IsRefused()
    {
        await using var app = BuildSetupApplication();
        var token = await BeginSetupAsync(ClientIngress.ThisComputer);

        var response = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            Password = "correct horse battery staple",
            DisplayName = "Owner",
        }, setupToken: token);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        Assert.False(await _identity.IsAdministratorConfiguredAsync());
    }

    // ---- Securing an account that has no email ----

    [Fact]
    public async Task Secure_WithoutAnEmail_OnAnAccountWithNone_IsRefusedAndChangesNothing()
    {
        var issued = await _identity.BootstrapThisComputerAdministratorAsync(null, "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var response = await SendAsync(app, "POST", "/auth/account/secure",
            new SecureAccountRequest { Password = "correct horse battery staple", DeviceId = "d", DeviceName = "Browser" },
            ingress: ClientIngress.ThisComputer, accountId: issued.Account.Id);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        Assert.Contains("Add an email to secure this account.", response.Text);
        var account = (await _accounts.GetByIdAsync(issued.Account.Id))!;
        Assert.True(account.IsThisComputerOnly);
        Assert.Null(account.Email);
        Assert.Null(await _identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password));
    }

    [Fact]
    public async Task Secure_WithAnEmail_StoresItAndTheEmailThenSignsIn()
    {
        var issued = await _identity.BootstrapThisComputerAdministratorAsync(null, "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var response = await SendAsync(app, "POST", "/auth/account/secure",
            new SecureAccountRequest { Email = " Owner@Example.com ", Password = "correct horse battery staple", DeviceId = "d", DeviceName = "Browser" },
            ingress: ClientIngress.ThisComputer, accountId: issued.Account.Id);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        var account = (await _accounts.GetByIdAsync(issued.Account.Id))!;
        Assert.False(account.IsThisComputerOnly);
        Assert.Equal("Owner@Example.com", account.Email);
        Assert.Equal("OWNER@EXAMPLE.COM", account.NormalizedEmail);
        var signIn = await _identity.AuthenticatePasswordAsync("owner@example.com", "correct horse battery staple", "tv", "TV", "Dashboard", ingress: ClientIngress.HomeNetwork);
        Assert.True(signIn.Succeeded);
    }

    [Fact]
    public async Task Secure_WithAnEmailAnotherAccountUses_IsRefused()
    {
        var issued = await _identity.BootstrapThisComputerAdministratorAsync(null, "Owner", "d", "Browser", "Dashboard");
        var now = DateTimeOffset.UtcNow;
        var otherId = Guid.NewGuid();
        var otherProfile = new MediaEngine.Domain.Aggregates.Profile { Id = Guid.NewGuid(), DisplayName = "Other", CreatedAt = now };
        await _accounts.CreateAccountAsync(
            new Account { Id = otherId, Email = "taken@example.com", NormalizedEmail = "TAKEN@EXAMPLE.COM", IsEnabled = true, AuthorizationVersion = 1, CreatedAt = now, UpdatedAt = now },
            new TestGrant(otherId, otherProfile.Id, IsDefault: true, AdminEnabled: false), newProfile: otherProfile);
        await using var app = BuildAuthenticationApplication();

        var response = await SendAsync(app, "POST", "/auth/account/secure",
            new SecureAccountRequest { Email = "TAKEN@example.com", Password = "another long password here", DeviceId = "d", DeviceName = "Browser" },
            ingress: ClientIngress.ThisComputer, accountId: issued.Account.Id);

        Assert.Equal(StatusCodes.Status409Conflict, response.Status);
        var account = (await _accounts.GetByIdAsync(issued.Account.Id))!;
        Assert.Null(account.Email);
        Assert.True(account.IsThisComputerOnly);
    }

    [Fact]
    public async Task Setup_WithAPassword_BehavesAsBefore()
    {
        await using var app = BuildSetupApplication();
        var token = await BeginSetupAsync(ClientIngress.ThisComputer);

        var response = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            Email = "owner@example.com",
            Password = "correct horse battery staple",
            DisplayName = "Owner",
            DeviceId = "browser",
            DeviceName = "Browser",
        }, setupToken: token);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.True(response.Json.GetProperty("recovery_codes").GetArrayLength() > 0);
        var account = Assert.Single(await _accounts.GetAllAsync());
        Assert.False(account.IsThisComputerOnly);
        Assert.NotNull(await _identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password));
    }

    [Fact]
    public async Task Setup_WithAnUnknownSignInChoice_IsRefused()
    {
        await using var app = BuildSetupApplication();
        var token = await BeginSetupAsync(ClientIngress.ThisComputer);

        var response = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            SignIn = "anything-else",
            Email = "owner@example.com",
            DisplayName = "Owner",
        }, setupToken: token);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
    }

    // ---- Signing in again on this computer ----

    [Fact]
    public async Task SignInAgain_NameAndSession_AreOnlyOfferedOnThisComputer()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var name = await SendAsync(app, "GET", "/auth/this-computer", ingress: ClientIngress.ThisComputer);
        var session = await SendAsync(app, "POST", "/auth/this-computer",
            new ThisComputerSignInRequest { DeviceId = "d2", DeviceName = "Second", Client = "Dashboard" },
            ingress: ClientIngress.ThisComputer);

        Assert.Equal(StatusCodes.Status200OK, name.Status);
        Assert.Equal("Owner", name.Json.GetProperty("display_name").GetString());
        Assert.Equal(StatusCodes.Status200OK, session.Status);
        Assert.False(string.IsNullOrWhiteSpace(session.Json.GetProperty("session_token").GetString()));
        Assert.Equal("ThisComputer", session.Json.GetProperty("authentication_method").GetString());
    }

    [Fact]
    public async Task SignInAgain_IsRefusedWhenTheRequestWasForwarded()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var dashboardSaidForwarded = await SendAsync(app, "GET", "/auth/this-computer", ingress: ClientIngress.ThisComputer,
            extraHeaders: new() { [ClientIngressValues.ForwardedHeader] = "true" });
        var engineSawForwarding = await SendAsync(app, "GET", "/auth/this-computer", ingress: ClientIngress.ThisComputer,
            extraHeaders: new() { ["X-Forwarded-For"] = "203.0.113.9" });

        Assert.Equal(StatusCodes.Status404NotFound, dashboardSaidForwarded.Status);
        Assert.Equal(StatusCodes.Status404NotFound, engineSawForwarding.Status);
    }

    [Fact]
    public async Task Setup_WithoutAPassword_IsRefusedWhenSetupBeganOnTheHomeNetworkWhateverTheRequestSays()
    {
        var begun = await _setupSessions.BeginAsync(ClientIngress.HomeNetwork, CancellationToken.None);
        var token = Assert.IsType<SetupStartResponse>(begun.Started).SetupSessionToken;
        await using var app = BuildSetupApplication();

        var response = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            SignIn = SetupSignInModes.ThisComputer,
            Email = "owner@example.com",
            DisplayName = "Owner",
            OriginalClientIngress = ClientIngress.ThisComputer,
        }, setupToken: token);

        Assert.Equal(StatusCodes.Status409Conflict, response.Status);
        Assert.False(await _identity.IsAdministratorConfiguredAsync());
    }

    [Fact]
    public async Task SignInAgain_WorksFromTheHomeNetworkButIsRefusedFromOutsideIt()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        // A phone or TV on the home network goes straight in.
        var homeName = await SendAsync(app, "GET", "/auth/this-computer", ingress: ClientIngress.HomeNetwork);
        var homeSession = await SendAsync(app, "POST", "/auth/this-computer",
            new ThisComputerSignInRequest { DeviceId = "phone", DeviceName = "Phone", Client = "Dashboard" },
            ingress: ClientIngress.HomeNetwork);
        Assert.Equal(StatusCodes.Status200OK, homeName.Status);
        Assert.Equal(StatusCodes.Status200OK, homeSession.Status);

        // Nobody outside it does: a remote, unknown or missing place is refused with the plain 404.
        var name = await SendAsync(app, "GET", "/auth/this-computer", ingress: ClientIngress.Remote);
        var session = await SendAsync(app, "POST", "/auth/this-computer",
            new ThisComputerSignInRequest { DeviceId = "phone", DeviceName = "Phone", Client = "Dashboard" },
            ingress: ClientIngress.Remote);
        var unknown = await SendAsync(app, "GET", "/auth/this-computer", ingress: "something-else");
        var missingHeader = await SendAsync(app, "POST", "/auth/this-computer",
            new ThisComputerSignInRequest { DeviceId = "phone", DeviceName = "Phone", Client = "Dashboard" },
            ingress: null);

        Assert.Equal(StatusCodes.Status404NotFound, name.Status);
        Assert.Equal(StatusCodes.Status404NotFound, session.Status);
        Assert.Equal(StatusCodes.Status404NotFound, unknown.Status);
        Assert.Equal(StatusCodes.Status404NotFound, missingHeader.Status);
    }

    [Fact]
    public async Task ThisComputerSession_ContinuesOnTheHomeNetworkButNotFromOutsideIt()
    {
        var issued = await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        var token = issued.PlaintextToken;

        Assert.NotNull(await _identity.ValidateSessionAsync(token, false, CancellationToken.None, ClientIngress.ThisComputer));
        Assert.NotNull(await _identity.ValidateSessionAsync(token, false, CancellationToken.None, ClientIngress.HomeNetwork));
        Assert.Null(await _identity.ValidateSessionAsync(token, false, CancellationToken.None, ClientIngress.Remote));
        // No statement about where the caller is counts as outside for this kind of account.
        Assert.Null(await _identity.ValidateSessionAsync(token, false, CancellationToken.None, null));
    }

    [Fact]
    public async Task SignInThisComputerAccount_IsRefusedUnlessTheCallerIsOnTheHomeNetwork()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");

        Assert.NotNull(await _identity.SignInThisComputerAccountAsync("phone", "Phone", "Dashboard", ClientIngress.HomeNetwork));
        Assert.Null(await _identity.SignInThisComputerAccountAsync("phone", "Phone", "Dashboard", ClientIngress.Remote));
        Assert.Null(await _identity.SignInThisComputerAccountAsync("phone", "Phone", "Dashboard", string.Empty));
        Assert.NotNull(await _identity.SignInThisComputerAccountAsync("laptop", "This computer", "Dashboard", ClientIngress.ThisComputer));
    }

    [Fact]
    public async Task SignInAgain_IsNotOfferedToCallersWithoutTheDashboardCredential()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var response = await SendAsync(app, "GET", "/auth/this-computer", ingress: ClientIngress.ThisComputer, dashboard: false);

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
    }

    [Fact]
    public async Task SignInAgain_WithNoThisComputerAccount_Is404()
    {
        await _identity.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var response = await SendAsync(app, "GET", "/auth/this-computer", ingress: ClientIngress.ThisComputer);

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
    }

    // ---- Locked until secured ----

    [Fact]
    public async Task OnlyThisComputerAdministrators_IsFalseForAPasswordAdministrator()
    {
        await _identity.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "d", "Browser", "Dashboard");

        Assert.False(await CreateGate().IsLockedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ThisComputerOnlyAdministrator_IsNotUsableForRemoteAccess()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        var service = CreateUsableAdministrators();

        var remote = await service.EvaluateForRemoteAsync(CancellationToken.None);

        Assert.False(remote.HasUsableAdministrator);
        Assert.False(remote.HasRecoveryCodes);
        // It still counts as able to sign in on this computer, so ordinary settings edits are not blocked.
        Assert.True(await service.HasUsableAdministratorSignInAsync(new AuthSettings(), CancellationToken.None));
    }

    [Fact]
    public async Task APasswordlessAccount_CanSaveAndResetNetworkSettings_AndAnOldConnectionRuleIsIgnored()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildNetworkApplication();

        var save = await SendAsync(app, "PUT", "/settings/network/", new { who_can_connect = "anywhere" });
        var reset = await SendAsync(app, "POST", "/network/reset");

        Assert.Equal(StatusCodes.Status200OK, save.Status);
        Assert.Equal(StatusCodes.Status200OK, reset.Status);
        Assert.False(save.Json.TryGetProperty("who_can_connect", out _));
        Assert.DoesNotContain("who_can_connect", File.ReadAllText(Path.Combine(_configPath, "network.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInAgain_IsRefusedWhenTheHomeNetworkRequestWasForwarded_ATunnelIsNotTheHomeNetwork()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var response = await SendAsync(app, "GET", "/auth/this-computer", ingress: ClientIngress.HomeNetwork,
            extraHeaders: new() { [ClientIngressValues.ForwardedHeader] = "true" });

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
    }

    [Fact]
    public async Task SwitchingProfile_TakesThePlaceOnlyFromTheDashboard_NotFromAnyOtherCaller()
    {
        var owner = await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();
        var body = new SwitchProfileRequest { ProfileId = owner.Profile.Id };

        var fromThisComputer = await SendAsync(app, "POST", "/auth/session/switch-profile", body,
            ingress: ClientIngress.ThisComputer, sessionToken: owner.PlaintextToken);
        var fromOutside = await SendAsync(app, "POST", "/auth/session/switch-profile", body,
            ingress: ClientIngress.Remote, sessionToken: owner.PlaintextToken);
        // Without the Dashboard's service credential a caller's own claim about where it is counts for nothing.
        var claimsToBeThisComputer = await SendAsync(app, "POST", "/auth/session/switch-profile", body,
            ingress: ClientIngress.ThisComputer, sessionToken: owner.PlaintextToken, dashboard: false);

        Assert.Equal(StatusCodes.Status200OK, fromThisComputer.Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, fromOutside.Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, claimsToBeThisComputer.Status);
    }

    // ---- Away from home: the passwords gate (spec remote-access WP3) ----

    private const string StrongPassword = "correct horse battery staple";

    [Fact]
    public async Task TurningOnOutsideAccess_IsRefused_WhileNoAdministratorHasAPassword()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildNetworkApplication();

        var response = await SendAsync(app, "PUT", "/settings/network/", NetworkWith(NetworkConnectionModes.Tailscale));

        Assert.Equal(StatusCodes.Status409Conflict, response.Status);
        Assert.Equal(NetworkEndpoints.AdministratorNeedsPasswordCode, response.Json.GetProperty("code").GetString());
        Assert.Contains("password or passkey", response.Json.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(NetworkConnectionModes.LocalOnly, _configuration.LoadNetwork().Remote.ConnectionMode);
    }

    [Fact]
    public async Task TurningOnOutsideAccess_Works_OnceTheAdministratorHasAPassword()
    {
        await _identity.BootstrapAdministratorAsync("owner@example.com", StrongPassword, "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildNetworkApplication();

        var response = await SendAsync(app, "PUT", "/settings/network/", NetworkWith(NetworkConnectionModes.Tailscale));

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal(NetworkConnectionModes.Tailscale, _configuration.LoadNetwork().Remote.ConnectionMode);
    }

    [Fact]
    public async Task TurningOnOutsideAccess_IsRefused_WhenTheOnlyPasswordBelongsToAnAdministratorWhoIsOff()
    {
        var owner = await _identity.BootstrapAdministratorAsync("owner@example.com", StrongPassword, "Owner", "d", "Browser", "Dashboard");
        var account = (await _accounts.GetByIdAsync(owner.Account.Id))!;
        account.IsEnabled = false;
        await _accounts.UpdateAsync(account);
        await using var app = BuildNetworkApplication();

        var response = await SendAsync(app, "PUT", "/settings/network/", NetworkWith(NetworkConnectionModes.Tailscale));

        Assert.Equal(StatusCodes.Status409Conflict, response.Status);
    }

    [Fact]
    public async Task SwitchingOutsideAccessOffOrSavingOtherSettings_NeverWaitsForAPassword()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        _configuration.SaveNetwork(new NetworkSettings { Remote = { ConnectionMode = NetworkConnectionModes.Tailscale } });
        await using var app = BuildNetworkApplication();

        var unchangedWay = await SendAsync(app, "PUT", "/settings/network/", NetworkWith(NetworkConnectionModes.Tailscale, serverName: "renamed"));
        var off = await SendAsync(app, "PUT", "/settings/network/", NetworkWith(NetworkConnectionModes.LocalOnly));

        Assert.Equal(StatusCodes.Status200OK, unchangedWay.Status);
        Assert.Equal(StatusCodes.Status200OK, off.Status);
        Assert.Equal(NetworkConnectionModes.LocalOnly, _configuration.LoadNetwork().Remote.ConnectionMode);
    }

    [Fact]
    public async Task ProfileEntry_ForAPersonWithoutAPassword_IsRefusedFromOutsideAndRecordedForTheAdministrator()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();
        var body = new ThisComputerSignInRequest { DeviceId = "phone", DeviceName = "Phone", Client = "Dashboard" };

        var fromOutside = await SendAsync(app, "POST", "/auth/this-computer", body, ingress: ClientIngress.Remote);
        var viaProxy = await SendAsync(app, "POST", "/auth/this-computer", body, ingress: ClientIngress.HomeNetwork,
            extraHeaders: new() { ["X-Forwarded-For"] = "203.0.113.9" });
        var viaTunnel = await SendAsync(app, "POST", "/auth/this-computer", body, ingress: ClientIngress.ThisComputer,
            extraHeaders: new() { [ClientIngressValues.ForwardedHeader] = "true" });
        var atHome = await SendAsync(app, "POST", "/auth/this-computer", body, ingress: ClientIngress.HomeNetwork);

        Assert.Equal(StatusCodes.Status404NotFound, fromOutside.Status);
        Assert.Equal(StatusCodes.Status404NotFound, viaProxy.Status);
        Assert.Equal(StatusCodes.Status404NotFound, viaTunnel.Status);
        Assert.Equal(StatusCodes.Status200OK, atHome.Status);
        var history = await _identities.GetRecentOutsideSignInsAsync(20);
        Assert.Equal(3, history.Count);
        Assert.All(history, record =>
        {
            Assert.False(record.Succeeded);
            Assert.Equal("Owner", record.ProfileName);
            Assert.Equal(ClientIngress.Remote, record.IngressKind);
        });
    }

    [Fact]
    public async Task PasswordForm_FromOutside_CannotLetInAPersonWhoHasNoPassword()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var response = await SendAsync(app, "POST", "/auth/login", new LocalLoginRequest
        {
            Email = "owner@example.com",
            Password = StrongPassword,
            DeviceId = "phone",
            DeviceName = "Phone",
            OriginalClientIngress = ClientIngress.Remote,
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
        Assert.DoesNotContain("session_token", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionOfAPersonWithoutAPassword_IsNotValidFromOutside_ForwardedOrWhenThePlaceIsNotSaid()
    {
        var issued = await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var atHome = await SendAsync(app, "POST", "/auth/session/validate", ingress: ClientIngress.HomeNetwork, sessionToken: issued.PlaintextToken);
        var outside = await SendAsync(app, "POST", "/auth/session/validate", ingress: ClientIngress.Remote, sessionToken: issued.PlaintextToken);
        var homeButForwarded = await SendAsync(app, "POST", "/auth/session/validate", ingress: ClientIngress.HomeNetwork,
            sessionToken: issued.PlaintextToken, extraHeaders: new() { [ClientIngressValues.ForwardedHeader] = "true" });
        var homeButProxied = await SendAsync(app, "POST", "/auth/session/validate", ingress: ClientIngress.ThisComputer,
            sessionToken: issued.PlaintextToken, extraHeaders: new() { ["Forwarded"] = "for=203.0.113.9" });
        var placeNotSaid = await SendAsync(app, "POST", "/auth/session/validate", ingress: null, sessionToken: issued.PlaintextToken);

        Assert.Equal(StatusCodes.Status200OK, atHome.Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, outside.Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, homeButForwarded.Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, homeButProxied.Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, placeNotSaid.Status);
    }

    [Fact]
    public async Task SwitchingProfile_FromAForwardedRequest_IsRefusedForAPersonWithoutAPassword()
    {
        var owner = await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var response = await SendAsync(app, "POST", "/auth/session/switch-profile", new SwitchProfileRequest { ProfileId = owner.Profile.Id },
            ingress: ClientIngress.HomeNetwork, sessionToken: owner.PlaintextToken,
            extraHeaders: new() { ["X-Forwarded-For"] = "203.0.113.9" });

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
    }

    [Fact]
    public async Task InvitationRedeemedFromOutside_MakesAPasswordProtectedAccountThatCanSignInThere()
    {
        var owner = await _identity.BootstrapAdministratorAsync("owner@example.com", StrongPassword, "Owner", "d", "Browser", "Dashboard");
        var invited = await InsertInvitedAccountAsync("guest@example.com");
        const string code = "WD5N8RB3CJ";
        await _accounts.InsertInvitationAsync(new AccountInvitation
        {
            Id = Guid.NewGuid(),
            AccountId = invited.Id,
            TokenHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code))),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });
        // An invitation from outside works only while away-from-home access is on (spec remote-access WP5b).
        _configuration.SaveNetwork(new NetworkSettings { Remote = { ConnectionMode = NetworkConnectionModes.Tailscale } });
        await using var app = BuildAuthenticationApplication();

        var response = await SendAsync(app, "POST", "/auth/invitations/accept",
            new AcceptAccountInvitationRequest(code, "guest password 1", "phone", "Phone", ClientIngress.Remote));

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.True(await _accounts.IsUserSecuredAsync(invited.Id));
        Assert.Contains(await _identities.GetRecentOutsideSignInsAsync(20), record => record.Succeeded && record.ProfileName == "Guest");
        _ = owner;
    }

    // ---- Away from home: sign-in history and the off switch (spec remote-access WP6) ----

    [Fact]
    public async Task OutsideSignIns_ListsTheLastTwenty_NewestFirst_WithNoAddress()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        for (var attempt = 0; attempt < 25; attempt++)
        {
            await _identity.SignInThisComputerAccountAsync("phone", "Phone", "Dashboard", ClientIngress.Remote);
        }

        await using var app = BuildNetworkApplication();
        var response = await SendAsync(app, "GET", "/network/outside-sign-ins");

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        using var document = JsonDocument.Parse(response.Text);
        var items = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(20, items.Length);
        Assert.Equal(["at", "profile_name", "succeeded", "method", "refusal_reason"],
            items[0].EnumerateObject().Select(property => property.Name).ToArray());
        Assert.False(items[0].GetProperty("succeeded").GetBoolean());
        Assert.Equal("Owner", items[0].GetProperty("profile_name").GetString());
        Assert.Equal("no_password", items[0].GetProperty("method").GetString());
        Assert.Contains("password", items[0].GetProperty("refusal_reason").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("203.0", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutsideSignInsAndRemoteOff_AreAdministratorOnly_ByRouteMetadata()
    {
        await using var app = BuildNetworkApplication();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

        var list = Assert.Single(routes, route => route.RoutePattern.RawText == "/network/outside-sign-ins");
        var off = Assert.Single(routes, route => route.RoutePattern.RawText == "/network/remote-off");

        Assert.Contains(list.Metadata.OfType<MediaEngine.Api.Security.AuthorityRequirementMetadata>(),
            metadata => metadata.Requirement == "effective_administrator_surface");
        Assert.Contains(off.Metadata.OfType<Microsoft.AspNetCore.Authorization.AuthorizationPolicy>().SelectMany(policy => policy.Requirements),
            requirement => requirement is AdministratorOrApplicationRequirement { Permission: var permission }
                && permission == ApplicationPermissionIds.NetworkConfigWrite);
    }

    [Fact]
    public async Task RemoteOff_PutsOutsideAccessBackToLocalOnlyAndClosesTheRouterDoor_ButClearsNothingElse()
    {
        _configuration.SaveNetwork(new NetworkSettings
        {
            Local = { Port = 5016, DiscoveryEnabled = true, ReachOtherDevices = true, PreferredServerName = "home-server" },
            Remote =
            {
                ConnectionMode = NetworkConnectionModes.DirectOnly,
                AutomaticRouterConfiguration = true,
                TlsTerminationPort = 8443,
                PublicHostname = "https://library.example.com",
                ProxyPort = 8444,
                TrustedProxies = ["127.0.0.1"],
            },
        });
        await using var app = BuildNetworkApplication();
        var coordinator = app.Services.GetRequiredService<RouterPortMappingCoordinator>();
        await coordinator.EnsureMappingAsync(CancellationToken.None);
        Assert.Equal(1, _routerMapper.Created);

        var response = await SendAsync(app, "POST", "/network/remote-off");

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.True(response.Json.GetProperty("was_on").GetBoolean());
        Assert.Equal("local-only", response.Json.GetProperty("connection_mode").GetString());
        var saved = _configuration.LoadNetwork();
        Assert.Equal(NetworkConnectionModes.LocalOnly, saved.Remote.ConnectionMode);
        Assert.False(saved.Remote.AutomaticRouterConfiguration);
        Assert.Equal(1, _routerMapper.Removed);
        // Nothing else is cleared and home access is untouched.
        Assert.Equal("https://library.example.com", saved.Remote.PublicHostname);
        Assert.Equal(8443, saved.Remote.TlsTerminationPort);
        Assert.Equal(8444, saved.Remote.ProxyPort);
        Assert.Equal(["127.0.0.1"], saved.Remote.TrustedProxies);
        Assert.Equal(5016, saved.Local.Port);
        Assert.True(saved.Local.DiscoveryEnabled);
        Assert.True(saved.Local.ReachOtherDevices);
        Assert.Equal("home-server", saved.Local.PreferredServerName);
    }

    [Fact]
    public async Task RemoteOff_IsSafeToRepeat_AndSaysItWasAlreadyOff()
    {
        _configuration.SaveNetwork(new NetworkSettings { Remote = { ConnectionMode = NetworkConnectionModes.Tailscale } });
        await using var app = BuildNetworkApplication();

        var first = await SendAsync(app, "POST", "/network/remote-off");
        var second = await SendAsync(app, "POST", "/network/remote-off");

        Assert.True(first.Json.GetProperty("was_on").GetBoolean());
        Assert.Equal(StatusCodes.Status200OK, second.Status);
        Assert.False(second.Json.GetProperty("was_on").GetBoolean());
        Assert.Equal("local-only", second.Json.GetProperty("connection_mode").GetString());
        Assert.Equal(NetworkConnectionModes.LocalOnly, _configuration.LoadNetwork().Remote.ConnectionMode);
    }

    // ---- Away from home: the off switch is real (spec remote-access D6) ----

    [Fact]
    public async Task PasswordSignInFromOutside_IsRefusedWith_outside_access_off_WhileOutsideAccessIsOff()
    {
        await _identityWithPolicy.BootstrapAdministratorAsync("owner@example.com", StrongPassword, "Owner", "d", "Browser", "Dashboard", ingress: ClientIngress.HomeNetwork);
        await using var app = BuildAuthenticationApplication(withOutsideAccessRule: true);

        var response = await SendAsync(app, "POST", "/auth/login",
            new LocalLoginRequest { Email = "owner@example.com", Password = StrongPassword, DeviceId = "d", DeviceName = "Phone", Client = "Dashboard", OriginalClientIngress = ClientIngress.Remote });

        Assert.Equal(StatusCodes.Status403Forbidden, response.Status);
        Assert.Equal("outside_access_off", response.Json.GetProperty("code").GetString());
        // Only the bootstrap session exists: nothing was issued, and the attempt did not even learn whether the password was right.
        Assert.Single(await _identity.GetSessionsAsync(Account.SeedAccountId));
    }

    [Fact]
    public async Task PasswordSignInFromOutside_Works_WhileOutsideAccessIsOn_ForAPersonWithAPassword()
    {
        await _identityWithPolicy.BootstrapAdministratorAsync("owner@example.com", StrongPassword, "Owner", "d", "Browser", "Dashboard", ingress: ClientIngress.HomeNetwork);
        _configuration.SaveNetwork(new NetworkSettings { Remote = { ConnectionMode = NetworkConnectionModes.Tailscale } });
        await using var app = BuildAuthenticationApplication(withOutsideAccessRule: true);

        var response = await SendAsync(app, "POST", "/auth/login",
            new LocalLoginRequest { Email = "owner@example.com", Password = StrongPassword, DeviceId = "d", DeviceName = "Phone", Client = "Dashboard", OriginalClientIngress = ClientIngress.Remote });

        Assert.Equal(StatusCodes.Status200OK, response.Status);
    }

    [Fact]
    public async Task PasswordSignInFromTheHomeNetwork_IsUnchanged_WhileOutsideAccessIsOff()
    {
        await _identityWithPolicy.BootstrapAdministratorAsync("owner@example.com", StrongPassword, "Owner", "d", "Browser", "Dashboard", ingress: ClientIngress.HomeNetwork);
        await using var app = BuildAuthenticationApplication(withOutsideAccessRule: true);

        var response = await SendAsync(app, "POST", "/auth/login",
            new LocalLoginRequest { Email = "owner@example.com", Password = StrongPassword, DeviceId = "d", DeviceName = "TV", Client = "Dashboard", OriginalClientIngress = ClientIngress.HomeNetwork });

        Assert.Equal(StatusCodes.Status200OK, response.Status);
    }

    [Fact]
    public async Task RemoteOff_EndsSessionsSignedInFromOutside_AndLeavesHomeSessionsAlone()
    {
        await _identityWithPolicy.BootstrapAdministratorAsync("owner@example.com", StrongPassword, "Owner", "d", "Browser", "Dashboard", ingress: ClientIngress.HomeNetwork);
        _configuration.SaveNetwork(new NetworkSettings { Remote = { ConnectionMode = NetworkConnectionModes.Tailscale } });
        var home = await SignInAsync(ClientIngress.HomeNetwork, "tv");
        var outside = await SignInAsync(ClientIngress.Remote, "phone");
        Assert.NotNull(await _identityWithPolicy.ValidateSessionAsync(outside.PlaintextToken, false, currentIngress: ClientIngress.Remote));
        await using var app = BuildNetworkApplication();

        var response = await SendAsync(app, "POST", "/network/remote-off");

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Null(await _identityWithPolicy.ValidateSessionAsync(outside.PlaintextToken, false, currentIngress: ClientIngress.HomeNetwork));
        var ended = await _identities.GetSessionByIdAsync(outside.Session.Id);
        Assert.Equal("outside_access_off", ended!.RevokedReason);
        Assert.NotNull(await _identityWithPolicy.ValidateSessionAsync(home.PlaintextToken, false, currentIngress: ClientIngress.HomeNetwork));
    }

    [Fact]
    public async Task SavingLocalOnlySettings_EndsSessionsSignedInFromOutside_Too()
    {
        await _identityWithPolicy.BootstrapAdministratorAsync("owner@example.com", StrongPassword, "Owner", "d", "Browser", "Dashboard", ingress: ClientIngress.HomeNetwork);
        _configuration.SaveNetwork(new NetworkSettings { Remote = { ConnectionMode = NetworkConnectionModes.Tailscale } });
        var outside = await SignInAsync(ClientIngress.Remote, "phone");
        await using var app = BuildNetworkApplication();

        var response = await SendAsync(app, "PUT", "/settings/network/", NetworkWith(NetworkConnectionModes.LocalOnly));

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.False((await _identities.GetSessionByIdAsync(outside.Session.Id))!.IsActive(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ASessionFromOutside_StopsWorkingFromOutside_TheMomentOutsideAccessIsOff_EvenIfNothingEndedIt()
    {
        await _identityWithPolicy.BootstrapAdministratorAsync("owner@example.com", StrongPassword, "Owner", "d", "Browser", "Dashboard", ingress: ClientIngress.HomeNetwork);
        _configuration.SaveNetwork(new NetworkSettings { Remote = { ConnectionMode = NetworkConnectionModes.Tailscale } });
        var outside = await SignInAsync(ClientIngress.Remote, "phone");
        var homeSession = await SignInAsync(ClientIngress.HomeNetwork, "tv");

        _configuration.SaveNetwork(new NetworkSettings()); // switched off by hand, not through the Engine action

        Assert.Null(await _identityWithPolicy.ValidateSessionAsync(homeSession.PlaintextToken, false, currentIngress: ClientIngress.Remote));
        Assert.NotNull(await _identityWithPolicy.ValidateSessionAsync(homeSession.PlaintextToken, false, currentIngress: ClientIngress.HomeNetwork));
        Assert.Null(await _identityWithPolicy.ValidateSessionAsync(outside.PlaintextToken, false, currentIngress: ClientIngress.HomeNetwork));
        Assert.False((await _identities.GetSessionByIdAsync(outside.Session.Id))!.IsActive(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ThisComputerSignIn_IsRateLimitedLikeTheOtherSignInActions()
    {
        await using var app = BuildAuthenticationApplication();
        var route = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == "/auth/this-computer"
                && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);

        var limiter = route.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>();

        Assert.Equal("authentication", limiter?.PolicyName);
        await Task.CompletedTask;
    }

    private async Task<SessionIssueResult> SignInAsync(string ingress, string device)
    {
        var attempt = await _identityWithPolicy.AuthenticatePasswordAsync("owner@example.com", StrongPassword, device, device, "Dashboard", ingress: ingress);
        Assert.True(attempt.Succeeded, attempt.Error);
        return attempt.IssuedSession!;
    }

    private static NetworkSettingsDto NetworkWith(string mode, string serverName = "tuvima")
    {
        var settings = new NetworkSettings { Remote = { ConnectionMode = mode } };
        settings.Local.PreferredServerName = serverName;
        return NetworkContractMapper.ToContract(settings);
    }

    private async Task<Account> InsertInvitedAccountAsync(string email)
    {
        var now = DateTimeOffset.UtcNow;
        var invited = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var person = new Profile { Id = Guid.NewGuid(), DisplayName = "Guest", AvatarColor = "#7C4DFF", Role = ProfileRole.StandardUser, CreatedAt = now };
        await _accounts.CreateAccountAsync(invited, new TestGrant(invited.Id, person.Id, IsDefault: true), newProfile: person);
        return invited;
    }

    // ---- Harness ----

    private SecureAccountGate CreateGate() => new();

    private UsableAdministratorService CreateUsableAdministrators() => new(
        _accounts, _identities, null!, null!,
        new AuthenticationProviderConfigurationService(_configuration), _configuration, TimeProvider.System);

    private static void AssertSecureAccountFirst((int Status, JsonElement Json, string Text) response)
    {
        Assert.Equal(StatusCodes.Status409Conflict, response.Status);
        Assert.Equal(SecureAccountGate.Code, response.Json.GetProperty("code").GetString());
    }

    private async Task<string> BeginSetupAsync(string ingress)
    {
        var begun = await _setupSessions.BeginAsync(ingress, CancellationToken.None);
        return Assert.IsType<SetupStartResponse>(begun.Started).SetupSessionToken;
    }

    private WebApplication BuildSetupApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton(_setupSessions);
        builder.Services.AddSingleton<IConfigurationLoader>(_configuration);
        builder.Services.AddSingleton<IFirstPartyIdentityService>(_identity);
        builder.Services.AddSingleton(_onboarding);
        builder.Services.AddSingleton<IContainerProbe>(_container);
        // The other setup routes are mapped too; their services only need to be known so they bind as services.
        builder.Services.AddSingleton<IFileOrganizer>(_ => null!);
        builder.Services.AddSingleton<ProviderCredentialService>(_ => null!);
        builder.Services.AddSingleton<DatabaseBackupService>(_ => null!);
        builder.Services.AddSingleton<ServerFolderBrowserService>(_ => null!);
        builder.Services.AddSingleton<SetupMediaLocationValidationService>(_ => null!);
        builder.Services.AddSingleton<SetupPreflightService>(_ => null!);
        builder.Services.AddSingleton(new DashboardAuthorityProjector(
            _accounts,
            new ProfileRepository(_database),
            new ProfileAccessRepository(_database),
            new GrantAdminUnlockService(new ProfileAccessRepository(_database), new StubProfilePins(), TimeProvider.System),
            ProjectorDecisions()));
        var app = builder.Build();
        app.MapSetupEndpoints();
        return app;
    }

    private AccountAccessDecisionService ProjectorDecisions() =>
        new(new ProfileAccessRepository(_database), new GrantAdminUnlockService(
            new ProfileAccessRepository(_database), new StubProfilePins(), TimeProvider.System),
            TestLibraryVisibility.Rule(new VaultLibraryRepository(_database)));

    private WebApplication BuildAuthenticationApplication(bool withOutsideAccessRule = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton<IConfigurationLoader>(_configuration);
        builder.Services.AddSingleton<IFirstPartyIdentityService>(withOutsideAccessRule ? _identityWithPolicy : _identity);
        builder.Services.AddSingleton<IAccountRepository>(_accounts);
        builder.Services.AddSingleton<IProfileAccessRepository>(new ProfileAccessRepository(_database));
        builder.Services.AddSingleton<IIdentityRepository>(_identities);
        builder.Services.AddSingleton<IAccountExternalLoginService>(_ => null!);
        builder.Services.AddSingleton<IAccountSignInMethodRepository>(_ => null!);
        builder.Services.AddSingleton<ISelfServiceAuthorizationService>(_ => null!);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(_ => null!);
        builder.Services.AddSingleton<PhoneBackupProfileService>(_ => null!);
        // The secure endpoint binds both, but only touches them when a passkey is sent; these stand-ins are never called.
        builder.Services.AddSingleton(Unused<IPasskeyHandler<Account>>());
        builder.Services.AddSingleton((UserManager<Account>)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UserManager<Account>)));
        builder.Services.AddSingleton(new DashboardAuthorityProjector(
            _accounts,
            new ProfileRepository(_database),
            new ProfileAccessRepository(_database),
            new GrantAdminUnlockService(new ProfileAccessRepository(_database), new StubProfilePins(), TimeProvider.System),
            ProjectorDecisions()));
        builder.Services.AddSingleton(new ExternalIdentityTransactionService(TimeProvider.System));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AuthenticationPolicyMutationGate>();
        builder.Services.AddSingleton<AuthenticationProviderConfigurationService>();
        var app = builder.Build();
        app.MapAuthenticationEndpoints();
        return app;
    }

    private WebApplication BuildNetworkApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IConfigurationLoader>(_configuration);
        builder.Services.AddSingleton<IFirstPartyIdentityService>(_identity);
        builder.Services.AddSingleton<IOutsideSessionEnder>(_identity);
        builder.Services.AddScoped<MediaEngine.Api.Services.Security.RecentSignInGuard>();
        builder.Services.AddSingleton(CreateGate());
        builder.Services.AddSingleton<NetworkStatusService>(_ => null!);
        builder.Services.AddSingleton<INetworkDiagnosticsService>(_ => null!);
        builder.Services.AddSingleton(new RouterPortMappingCoordinator(
            _configuration, new FixedEnvironment(), new FixedTopology(), [_routerMapper], new NetworkRuntimeState(),
            NullLogger<RouterPortMappingCoordinator>.Instance));
        builder.Services.AddSingleton<IRemoteCheckService>(_ => null!);
        builder.Services.AddSingleton<IOutsideAccessGate>(new OutsideAccessGate(_accounts));
        builder.Services.AddSingleton<IOutsideSignInHistory>(new OutsideSignInHistory(_identities));
        var app = builder.Build();
        app.MapNetworkEndpoints();
        return app;
    }

    private WebApplication BuildPairingApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton<IConfigurationLoader>(_configuration);
        builder.Services.AddSingleton<IFirstPartyIdentityService>(_identity);
        builder.Services.AddScoped<MediaEngine.Api.Services.Security.RecentSignInGuard>();
        builder.Services.AddSingleton(CreateGate());
        builder.Services.AddSingleton(new ClientAuthorizationService(null!, null!, null!, null!, TimeProvider.System));
        var app = builder.Build();
        app.MapClientAuthorizationEndpoints();
        return app;
    }

    private WebApplication BuildAccountApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IFirstPartyIdentityService>(_identity);
        builder.Services.AddScoped<MediaEngine.Api.Services.Security.RecentSignInGuard>();
        builder.Services.AddSingleton(CreateGate());
        builder.Services.AddSingleton(Unused<IRequestAuthorityResolver>());
        builder.Services.AddSingleton<ISelfServiceAuthorizationService>(_ => null!);
        builder.Services.AddSingleton<IAccountRepository>(_accounts);
        builder.Services.AddSingleton<IProfileAccessRepository>(new ProfileAccessRepository(_database));
        builder.Services.AddSingleton<IIdentityRepository>(_identities);
        builder.Services.AddSingleton<IProfileRepository>(new ProfileRepository(_database));
        builder.Services.AddSingleton<IAccountExternalLoginService>(_ => null!);
        builder.Services.AddSingleton<IAccountSignInMethodRepository>(_ => null!);
        builder.Services.AddSingleton<IAuthorizationAuditWriter>(_ => null!);
        builder.Services.AddSingleton<IGrantAdminUnlockService>(_ => null!);
        builder.Services.AddSingleton(Unused<IAccountAccessMutationService>());
        builder.Services.AddSingleton<IConfigurationLoader>(_configuration);
        builder.Services.AddSingleton<AuthenticationPolicyMutationGate>();
        builder.Services.AddSingleton<AuthenticationProviderConfigurationService>();
        builder.Services.AddSingleton<UserManager<Account>>(_ => null!);
        builder.Services.AddSingleton(TimeProvider.System);
        var app = builder.Build();
        app.MapAccountEndpoints();
        return app;
    }

    private static async Task<(int Status, JsonElement Json, string Text)> SendAsync(
        WebApplication app,
        string method,
        string pattern,
        object? body = null,
        string? ingress = null,
        bool dashboard = true,
        string? setupToken = null,
        string? sessionToken = null,
        Dictionary<string, string>? extraHeaders = null,
        Guid? accountId = null)
    {
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == pattern &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        var requestBody = new MemoryStream(body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType()));
        var responseBody = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            Request =
            {
                Method = method,
                Path = pattern,
                ContentType = body is null ? null : "application/json",
                ContentLength = body is null ? null : requestBody.Length,
                Body = requestBody,
            },
            Response = { Body = responseBody },
        };
        if (body is not null)
        {
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
        }

        if (dashboard)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                accountId is { } id
                    ? [new Claim(TuvimaClaimTypes.DashboardService, "true"), new Claim(TuvimaClaimTypes.AccountId, id.ToString("D"))]
                    : [new Claim(TuvimaClaimTypes.DashboardService, "true")], "test"));
        }

        if (ingress is not null)
        {
            context.Request.Headers[ClientIngressValues.ValidateHeader] = ingress;
        }

        foreach (var (name, value) in extraHeaders ?? [])
        {
            context.Request.Headers[name] = value;
        }

        if (setupToken is not null)
        {
            context.Request.Headers[SetupSessionService.SessionHeader] = setupToken;
        }

        if (sessionToken is not null)
        {
            context.Request.Headers[TuvimaAuthDefaults.SessionHeader] = sessionToken;
        }

        await endpoint.RequestDelegate!(context);
        responseBody.Position = 0;
        var text = await new StreamReader(responseBody).ReadToEndAsync();
        var json = text.Length > 0 && text.TrimStart().StartsWith('{')
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
        return (context.Response.StatusCode, json, text);
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }

        try
        {
            if (Directory.Exists(_configPath))
            {
                Directory.Delete(_configPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    /// <summary>A stand-in that is never called: the request is refused before the handler uses it.</summary>
    private static T Unused<T>() where T : class => DispatchProxy.Create<T, UnusedProxy>();

    private class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException("This service must not be used when the request is refused.");
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class SpyRouterMapper : IRouterPortMapper
    {
        public int Created { get; private set; }
        public int Removed { get; private set; }
        public string Method => "Spy";
        public int Priority => 0;

        public Task<RouterMappingResult> TryCreateAsync(RouterMappingRequest request, CancellationToken ct)
        {
            Created++;
            return Task.FromResult(new RouterMappingResult(RouterMappingState.Active, Method, "Mapped.", request.ExternalPort, DateTimeOffset.UtcNow.AddHours(1), "203.0.113.7"));
        }

        public Task<RouterMappingResult> TryRenewAsync(RouterMappingRequest request, CancellationToken ct) => TryCreateAsync(request, ct);

        public Task RemoveOwnedAsync(RouterMappingRequest request, CancellationToken ct)
        {
            Removed++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTopology : INetworkTopologyService
    {
        public NetworkTopologySnapshot GetSnapshot() => new("native", true, "Router discovery can use the host's LAN gateway.", "192.168.1.1", "eth0");
    }

    private sealed class FixedEnvironment : INetworkEnvironmentService
    {
        public IReadOnlyList<NetworkAddressDto> GetUsableAddresses(bool includeIpv6) =>
            [new() { InterfaceId = "eth0", InterfaceName = "eth0", Address = "192.168.1.20", AddressFamily = "ipv4" }];
    }

    private sealed class FakeContainerProbe : IContainerProbe
    {
        public bool InContainer { get; set; }
        public bool IsContainer() => InContainer;
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
