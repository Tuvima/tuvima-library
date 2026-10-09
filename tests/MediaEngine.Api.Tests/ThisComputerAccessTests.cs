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
using MediaEngine.Contracts.Setup;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
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
    private readonly SetupSessionService _setupSessions;
    private readonly FakeContainerProbe _container = new();

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
        _setupSessions = new SetupSessionService(_onboarding, _identity, TimeProvider.System);
    }

    // ---- Setup ----

    [Fact]
    public async Task Setup_ThisComputer_OutsideAContainer_NeedsOnlyANameAndEmail()
    {
        await using var app = BuildSetupApplication();
        var token = await BeginSetupAsync(ClientIngress.ThisComputer);

        var response = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            SignIn = SetupSignInModes.ThisComputer,
            Email = "owner@example.com",
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
        Assert.Null(await _identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password));
        Assert.Equal(WhoCanConnectModes.ThisComputer, _configuration.LoadNetwork().WhoCanConnect);
    }

    [Theory]
    [InlineData(ClientIngress.HomeNetwork, false)]
    [InlineData(ClientIngress.Remote, false)]
    [InlineData(ClientIngress.ThisComputer, true)]
    public async Task Setup_ThisComputer_IsRefusedFromHomeNetworkRemoteAndContainers(string ingress, bool inContainer)
    {
        _container.InContainer = inContainer;
        await using var app = BuildSetupApplication();
        var token = await BeginSetupAsync(ClientIngress.ThisComputer);

        var response = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            SignIn = SetupSignInModes.ThisComputer,
            Email = "owner@example.com",
            DisplayName = "Owner",
            OriginalClientIngress = ingress,
        }, setupToken: token);

        Assert.Equal(StatusCodes.Status409Conflict, response.Status);
        Assert.Equal(SetupAdministratorRefusalCodes.NotAvailableHere, response.Json.GetProperty("code").GetString());
        Assert.False(await _identity.IsAdministratorConfiguredAsync());
        Assert.Equal(WhoCanConnectModes.HomeNetwork, _configuration.LoadNetwork().WhoCanConnect);
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
        var withoutEmail = await SendAsync(app, "POST", "/setup/v1/administrator", new SetupAdministratorRequest
        {
            SignIn = SetupSignInModes.ThisComputer,
            DisplayName = "Owner",
            OriginalClientIngress = ClientIngress.ThisComputer,
        }, setupToken: token);

        Assert.Equal(StatusCodes.Status400BadRequest, withPassword.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, withoutEmail.Status);
        Assert.False(await _identity.IsAdministratorConfiguredAsync());
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
        Assert.Equal(WhoCanConnectModes.HomeNetwork, _configuration.LoadNetwork().WhoCanConnect);
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

    [Theory]
    [InlineData(ClientIngress.HomeNetwork)]
    [InlineData(ClientIngress.Remote)]
    [InlineData(null)]
    public async Task SignInAgain_FromElsewhere_IsAPlain404ThatNeverNamesTheAccount(string? ingress)
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var name = await SendAsync(app, "GET", "/auth/this-computer", ingress: ingress);
        var session = await SendAsync(app, "POST", "/auth/this-computer",
            new ThisComputerSignInRequest { DeviceId = "d2", DeviceName = "Second", Client = "Dashboard" },
            ingress: ingress);

        Assert.Equal(StatusCodes.Status404NotFound, name.Status);
        Assert.Equal(StatusCodes.Status404NotFound, session.Status);
        Assert.DoesNotContain("Owner", name.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("owner@example.com", name.Text, StringComparison.Ordinal);
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
    public async Task Setup_ThisComputer_IsRefusedWhenSetupBeganOnTheHomeNetwork()
    {
        await new SetupCodeRepository(_database).IssueAsync("ABCD-EFGH", DateTimeOffset.UtcNow, CancellationToken.None);
        var begun = await _setupSessions.BeginAsync(ClientIngress.HomeNetwork, "ABCD-EFGH", CancellationToken.None);
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

    [Fact]
    public async Task SessionValidate_RefusesAThisComputerAccountFromTheHomeNetwork()
    {
        var issued = await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAuthenticationApplication();

        var here = await SendAsync(app, "POST", "/auth/session/validate", ingress: ClientIngress.ThisComputer, sessionToken: issued.PlaintextToken);
        var home = await SendAsync(app, "POST", "/auth/session/validate", ingress: ClientIngress.HomeNetwork, sessionToken: issued.PlaintextToken);

        Assert.Equal(StatusCodes.Status200OK, here.Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, home.Status);
        Assert.Equal(ClientIngressValues.SignInAgainHere, home.Json.GetProperty("reason").GetString());
    }

    // ---- Locked until secured ----

    [Fact]
    public async Task OnlyThisComputerAdministrators_IsTrueUntilAnotherAdministratorIsSecured()
    {
        var gate = CreateGate();
        Assert.False(await gate.IsLockedAsync(CancellationToken.None));

        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        Assert.True(await gate.IsLockedAsync(CancellationToken.None));
    }

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
    public async Task RaisingWhoCanConnect_IsRefusedUntilSecured()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        var network = _configuration.LoadNetwork();
        network.WhoCanConnect = WhoCanConnectModes.ThisComputer;
        _configuration.SaveNetwork(network);
        await using var app = BuildNetworkApplication();

        foreach (var wider in new[] { WhoCanConnectModes.HomeNetwork, WhoCanConnectModes.Anywhere })
        {
            var response = await SendAsync(app, "PUT", "/settings/network/", new { who_can_connect = wider });

            AssertSecureAccountFirst(response);
            Assert.Equal(WhoCanConnectModes.ThisComputer, _configuration.LoadNetwork().WhoCanConnect);
        }
    }

    [Fact]
    public async Task TurningOnAppAccess_IsRefusedUntilSecured()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        var network = _configuration.LoadNetwork();
        network.WhoCanConnect = WhoCanConnectModes.ThisComputer;
        _configuration.SaveNetwork(network);
        await using var app = BuildNetworkApplication();

        var response = await SendAsync(app, "PUT", "/settings/network/", new
        {
            who_can_connect = WhoCanConnectModes.ThisComputer,
            native_app_access = new { enabled = true },
        });

        AssertSecureAccountFirst(response);
    }

    [Fact]
    public async Task KeepingThisComputer_AndResettingNetwork_BehaveSensibly()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        var network = _configuration.LoadNetwork();
        network.WhoCanConnect = WhoCanConnectModes.ThisComputer;
        _configuration.SaveNetwork(network);
        await using var app = BuildNetworkApplication();

        var keep = await SendAsync(app, "PUT", "/settings/network/", new { who_can_connect = WhoCanConnectModes.ThisComputer });
        // Resetting would put the door back to the home network, which is wider than this computer.
        var reset = await SendAsync(app, "POST", "/network/reset");

        Assert.Equal(StatusCodes.Status200OK, keep.Status);
        AssertSecureAccountFirst(reset);
        Assert.Equal(WhoCanConnectModes.ThisComputer, _configuration.LoadNetwork().WhoCanConnect);
    }

    [Fact]
    public async Task RaisingWhoCanConnect_IsAllowedOnceTheAccountIsSecured()
    {
        await _identity.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "d", "Browser", "Dashboard");
        var network = _configuration.LoadNetwork();
        network.WhoCanConnect = WhoCanConnectModes.ThisComputer;
        _configuration.SaveNetwork(network);
        await using var app = BuildNetworkApplication();

        var response = await SendAsync(app, "PUT", "/settings/network/", new { who_can_connect = WhoCanConnectModes.HomeNetwork });

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal(WhoCanConnectModes.HomeNetwork, _configuration.LoadNetwork().WhoCanConnect);
    }

    [Fact]
    public async Task DeviceApproval_IsRefusedUntilSecured_ButADenialIsNot()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildPairingApplication();

        var approve = await SendAsync(app, "POST", "/api/v1/pairing/decision",
            new PairingDecisionRequest { UserCode = "ABCD-EFGH", Approved = true });

        AssertSecureAccountFirst(approve);
    }

    [Fact]
    public async Task CreatingAccountsAndInvitations_IsRefusedUntilSecured()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");
        await using var app = BuildAccountApplication();

        var account = await SendAsync(app, "POST", "/access/accounts/", new { email = "friend@example.com" });
        var invitation = await SendAsync(app, "POST", "/access/invitations", new { email = "friend@example.com" });

        AssertSecureAccountFirst(account);
        AssertSecureAccountFirst(invitation);
    }

    [Fact]
    public void ThisComputerAccess_IsOfferedOnlyOnThisComputerOutsideAContainer()
    {
        Assert.True(ThisComputerAccess.IsAvailable(ClientIngress.ThisComputer, inContainer: false));
        Assert.False(ThisComputerAccess.IsAvailable(ClientIngress.ThisComputer, inContainer: true));
        Assert.False(ThisComputerAccess.IsAvailable(ClientIngress.HomeNetwork, inContainer: false));
        Assert.False(ThisComputerAccess.IsAvailable(ClientIngress.Remote, inContainer: false));
        Assert.False(ThisComputerAccess.IsAvailable(null, inContainer: false));
    }

    [Theory]
    [InlineData("this_computer", "home_network", true)]
    [InlineData("this_computer", "anywhere", true)]
    [InlineData("home_network", "anywhere", true)]
    [InlineData("home_network", "home_network", false)]
    [InlineData("anywhere", "this_computer", false)]
    [InlineData("this_computer", "something-else", false)]
    public void RaisingWhoCanConnect_IsOnlyAWiderDoor(string current, string proposed, bool raises)
    {
        // Closed (this computer) < home network < anywhere; an unknown value counts as the most closed.
        Assert.Equal(raises, SecureAccountGate.Raises(current, proposed));
    }

    // ---- Harness ----

    private SecureAccountGate CreateGate() => new(CreateUsableAdministrators());

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
        var begun = await _setupSessions.BeginAsync(ingress, null, CancellationToken.None);
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
        var app = builder.Build();
        app.MapSetupEndpoints();
        return app;
    }

    private WebApplication BuildAuthenticationApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton<IConfigurationLoader>(_configuration);
        builder.Services.AddSingleton<IFirstPartyIdentityService>(_identity);
        builder.Services.AddSingleton<IAccountRepository>(_accounts);
        builder.Services.AddSingleton<IIdentityRepository>(_identities);
        builder.Services.AddSingleton<IAccountExternalLoginService>(_ => null!);
        builder.Services.AddSingleton<IAccountSignInMethodRepository>(_ => null!);
        builder.Services.AddSingleton<ISelfServiceAuthorizationService>(_ => null!);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(_ => null!);
        builder.Services.AddSingleton<IPasskeyHandler<Account>>(_ => null!);
        builder.Services.AddSingleton<UserManager<Account>>(_ => null!);
        builder.Services.AddSingleton(new DashboardAuthorityProjector(
            _accounts,
            new ProfileRepository(_database),
            new GrantAdminUnlockService(_accounts, new PasswordHasher<GrantAdminProtection>(), TimeProvider.System),
            TimeProvider.System));
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
        builder.Services.AddScoped<MediaEngine.Api.Services.Security.RecentSignInGuard>();
        builder.Services.AddSingleton(CreateGate());
        builder.Services.AddSingleton(new RemoteAccessReadinessService(
            CreateUsableAdministrators(), Unused<INetworkTopologyService>(), [], new HttpClient(),
            NullLogger<RemoteAccessReadinessService>.Instance));
        builder.Services.AddSingleton<NetworkStatusService>(_ => null!);
        builder.Services.AddSingleton<INetworkDiagnosticsService>(_ => null!);
        builder.Services.AddSingleton<RouterPortMappingCoordinator>(_ => null!);
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
        Dictionary<string, string>? extraHeaders = null)
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
                [new Claim(TuvimaClaimTypes.DashboardService, "true")], "test"));
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
