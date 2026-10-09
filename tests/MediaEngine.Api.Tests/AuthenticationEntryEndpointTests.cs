using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class AuthenticationEntryEndpointTests
{
    [Fact]
    public async Task PasswordLogin_RemoteClientNeverReachesIdentityServiceWhenRemoteSignInIsOff()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"tuvima-auth-entry-{Guid.NewGuid():N}");
        try
        {
            using var configuration = new ConfigurationDirectoryLoader(configPath);
            var core = configuration.LoadCore();
            core.Auth.Mode = "Local";
            core.Auth.PasswordSignInEnabled = true;
            configuration.SaveCore(core);
            var identity = new TrackingIdentityService();
            await using var app = BuildApplication(configuration, identity);

            var response = await InvokeAsync(app, "/auth/login", new LocalLoginRequest
            {
                Email = "owner@example.com",
                Password = "correct horse battery staple",
                DeviceId = "remote-browser",
                DeviceName = "Remote browser",
                Client = "Tuvima Dashboard",
                OriginalClientIngress = ClientIngressValues.Remote,
                OriginalClientIsHttps = true,
            });

            Assert.True(response.StatusCode == StatusCodes.Status401Unauthorized, await DescribeAsync(response));
            Assert.Equal(0, identity.PasswordLoginCalls);
        }
        finally
        {
            if (Directory.Exists(configPath))
            {
                Directory.Delete(configPath, recursive: true);
            }
        }
    }

    [Fact]
    public void LoginRequest_CarriesOnlyAnEmailAndPassword()
    {
        var names = typeof(LocalLoginRequest).GetProperties().Select(property => property.Name).ToHashSet();

        Assert.Contains("Email", names);
        Assert.Contains("Password", names);
        Assert.DoesNotContain("ProfileId", names);
        Assert.DoesNotContain("Pin", names);
    }

    [Fact]
    public async Task OAuthTransaction_UsesPrivateSecretOverlayAtVerificationBoundary()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"tuvima-auth-entry-{Guid.NewGuid():N}");
        try
        {
            using var configuration = new ConfigurationDirectoryLoader(configPath);
            var core = configuration.LoadCore();
            core.Auth.Mode = "Required";
            core.Auth.ExternalSignInEnabled = true;
            configuration.SaveNetwork(new NetworkSettings { Remote = { PublicHostname = "https://library.example" } });
            core.Auth.ExternalProviders =
            [
                new ExternalAuthProviderSettings
                {
                    Id = "github",
                    Kind = ExternalAuthProviderKinds.OAuth,
                    Enabled = true,
                    DisplayName = "GitHub",
                    Issuer = "https://github.com",
                    ClientId = "client",
                    AuthorizationEndpoint = "https://github.com/login/oauth/authorize",
                    TokenEndpoint = "https://github.com/login/oauth/access_token",
                    UserInformationEndpoint = "https://api.github.com/user",
                },
            ];
            configuration.SaveCore(core);
            var providers = new AuthenticationProviderConfigurationService(configuration);
            await providers.UpdateSecretAsync("github", "private-secret", clear: false, CancellationToken.None);
            await using var app = BuildApplication(configuration, new TrackingIdentityService());

            var response = await InvokeAsync(app, "/auth/external-transactions", new BeginExternalIdentityTransactionRequest
            {
                Purpose = ExternalIdentityTransactionPurposes.SignIn,
                Provider = "github",
                Issuer = "https://github.com",
                Subject = "verified-subject",
            });

            Assert.True(response.StatusCode == StatusCodes.Status200OK, await DescribeAsync(response));
            response.Body.Position = 0;
            Assert.False(string.IsNullOrWhiteSpace(
                (await JsonSerializer.DeserializeAsync<ExternalIdentityTransactionResponse>(response.Body))?.Ticket));
        }
        finally
        {
            if (Directory.Exists(configPath))
            {
                Directory.Delete(configPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvitationAndRecoveryIssuance_DenyRemoteClientBeforeIdentityMutation()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"tuvima-auth-entry-{Guid.NewGuid():N}");
        try
        {
            using var configuration = new ConfigurationDirectoryLoader(configPath);
            var core = configuration.LoadCore();
            core.Auth.Mode = "Required";
            core.Auth.PasswordSignInEnabled = true;
            configuration.SaveCore(core);
            var identity = new TrackingIdentityService();
            await using var app = BuildApplication(configuration, identity);

            var invitation = await InvokeAsync(app, "/auth/invitations/accept",
                new AcceptAccountInvitationRequest("invitation", "password", "browser", "Browser", ClientIngressValues.Remote, true));
            var recovery = await InvokeAsync(app, "/auth/password/recover", new RecoverPasswordRequest
            {
                Email = "person@example.com",
                RecoveryCode = "code",
                NewPassword = "replacement",
                OriginalClientIngress = ClientIngressValues.Remote,
                OriginalClientIsHttps = true,
            });
            var resetComplete = await InvokeAsync(app, "/auth/password/reset/complete",
                new ResetPasswordTokenRequest("reset", "replacement", ClientIngressValues.Remote, true));

            Assert.True(invitation.StatusCode == StatusCodes.Status401Unauthorized, await DescribeAsync(invitation));
            Assert.True(recovery.StatusCode == StatusCodes.Status401Unauthorized, await DescribeAsync(recovery));
            Assert.True(resetComplete.StatusCode == StatusCodes.Status401Unauthorized, await DescribeAsync(resetComplete));
            Assert.Equal(0, identity.AcceptInvitationCalls);
            Assert.Equal(0, identity.RecoveryCodeResetCalls);
            Assert.Equal(0, identity.TokenResetCalls);
        }
        finally
        {
            if (Directory.Exists(configPath))
            {
                Directory.Delete(configPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PasswordResetBegin_PreservesAntiEnumerationWhileSkippingDeniedRemoteRequest()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"tuvima-auth-entry-{Guid.NewGuid():N}");
        try
        {
            using var configuration = new ConfigurationDirectoryLoader(configPath);
            var core = configuration.LoadCore();
            core.Auth.Mode = "Required";
            core.Auth.PasswordSignInEnabled = true;
            configuration.SaveCore(core);
            var identity = new TrackingIdentityService();
            await using var app = BuildApplication(configuration, identity);

            var response = await InvokeAsync(app, "/auth/password/reset/begin",
                new BeginPasswordResetRequest("person@example.com", ClientIngressValues.Remote, true));

            Assert.True(response.StatusCode == StatusCodes.Status202Accepted, await DescribeAsync(response));
            response.Body.Position = 0;
            var payload = await JsonSerializer.DeserializeAsync<BeginPasswordResetResponse>(response.Body);
            Assert.NotNull(payload);
            Assert.Null(payload.Token);
            Assert.Equal(0, identity.BeginPasswordResetCalls);
        }
        finally
        {
            if (Directory.Exists(configPath))
            {
                Directory.Delete(configPath, recursive: true);
            }
        }
    }

    private static WebApplication BuildApplication(
        IConfigurationLoader configuration,
        TrackingIdentityService identity)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton<IFirstPartyIdentityService>(identity);
        builder.Services.AddScoped<MediaEngine.Api.Services.Security.RecentSignInGuard>();
        builder.Services.AddSingleton<IAccountRepository>(_ => null!);
        builder.Services.AddSingleton<IIdentityRepository>(_ => null!);
        builder.Services.AddSingleton<IAccountExternalLoginService>(_ => null!);
        builder.Services.AddSingleton<IAccountSignInMethodRepository>(_ => null!);
        builder.Services.AddSingleton<ISelfServiceAuthorizationService>(_ => null!);
        builder.Services.AddSingleton<IRequestAuthorityResolver, AnonymousAuthorityResolver>();
        builder.Services.AddSingleton<IPasskeyHandler<Account>>(_ => null!);
        builder.Services.AddSingleton<UserManager<Account>>(_ => null!);
        builder.Services.AddSingleton(new DashboardAuthorityProjector(null!, null!, null!, TimeProvider.System));
        builder.Services.AddSingleton(new ExternalIdentityTransactionService(TimeProvider.System));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AuthenticationPolicyMutationGate>();
        builder.Services.AddSingleton<AuthenticationProviderConfigurationService>();
        var app = builder.Build();
        app.MapAuthenticationEndpoints();
        return app;
    }

    private static async Task<(int StatusCode, MemoryStream Body)> InvokeAsync(
        WebApplication app,
        string pattern,
        object request)
    {
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == pattern &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);
        var requestBody = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request, request.GetType()));
        var responseBody = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            Request =
            {
                Method = "POST",
                Path = pattern,
                ContentType = "application/json",
                ContentLength = requestBody.Length,
                Body = requestBody,
            },
            Response = { Body = responseBody },
        };
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());

        await endpoint.RequestDelegate!(context);
        return (context.Response.StatusCode, responseBody);
    }

    private static async Task<string> DescribeAsync((int StatusCode, MemoryStream Body) response)
    {
        response.Body.Position = 0;
        return $"HTTP {response.StatusCode}: {await new StreamReader(response.Body, leaveOpen: true).ReadToEndAsync()}";
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class AnonymousAuthorityResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(new RequestAuthority(PrincipalKind.Anonymous, false));
    }

    private sealed class TrackingIdentityService : IFirstPartyIdentityService
    {
        public int AcceptInvitationCalls { get; private set; }
        public int RecoveryCodeResetCalls { get; private set; }
        public int BeginPasswordResetCalls { get; private set; }
        public int TokenResetCalls { get; private set; }
        public int PasswordLoginCalls { get; private set; }

        public Task<InvitationPreview?> PreviewInvitationAsync(string code, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetTemporaryPasswordAsync(Guid accountId, string password, DateTimeOffset expiresAt, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<SessionIssueResult> ChangeTemporaryPasswordAsync(Guid accountId, string currentPassword, string newPassword, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.Remote) => throw new NotSupportedException();

        public Task<SessionIssueResult> AcceptInvitationAsync(string code, string password, string deviceId,
            string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork)
        {
            AcceptInvitationCalls++;
            throw new InvalidOperationException("Denied requests must not reach identity issuance.");
        }

        public Task<IReadOnlyList<string>> ResetPasswordWithRecoveryCodeAsync(string email, string recoveryCode,
            string newPassword, CancellationToken ct = default)
        {
            RecoveryCodeResetCalls++;
            throw new InvalidOperationException("Denied requests must not reach password mutation.");
        }

        public Task<string?> BeginPasswordResetAsync(string email, CancellationToken ct = default)
        {
            BeginPasswordResetCalls++;
            throw new InvalidOperationException("Denied requests must not create reset tokens.");
        }

        public Task ResetPasswordWithTokenAsync(string token, string newPassword, CancellationToken ct = default)
        {
            TokenResetCalls++;
            throw new InvalidOperationException("Denied requests must not reach password mutation.");
        }

        public Task<bool> IsAdministratorConfiguredAsync(CancellationToken ct = default) => throw NotSupported();
        public Task<SessionIssueResult> BootstrapAdministratorAsync(string email, string password, string displayName, string deviceId, string deviceName, string client, CancellationToken ct = default, string? pin = null, string ingress = ClientIngress.HomeNetwork) => throw NotSupported();
        public Task<SessionIssueResult> BootstrapThisComputerAdministratorAsync(string email, string displayName, string deviceId, string deviceName, string client, CancellationToken ct = default, string? pin = null) => throw NotSupported();
        public Task<SessionIssueResult?> SignInThisComputerAccountAsync(string deviceId, string deviceName, string client, CancellationToken ct = default) => throw NotSupported();
        public Task<string?> GetThisComputerAccountNameAsync(CancellationToken ct = default) => throw NotSupported();
        public Task<AuthenticationAttemptResult> AuthenticatePasswordAsync(string email, string password, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork)
        {
            PasswordLoginCalls++;
            throw new InvalidOperationException("Denied requests must not reach password sign-in.");
        }
        public Task<SessionIssueResult> CreateExternalSessionAsync(Guid accountId, string provider, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork) => throw NotSupported();
        public Task<SessionIssueResult> CreatePasskeySessionAsync(Guid accountId, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork) => throw NotSupported();
        public Task<SessionValidationResult?> ValidateSessionAsync(string plaintextToken, bool touch = true, CancellationToken ct = default, string? currentIngress = null) => throw NotSupported();
        public Task<IReadOnlyList<AuthSession>> GetSessionsAsync(Guid accountId, CancellationToken ct = default) => throw NotSupported();
        public Task<bool> RevokeSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => throw NotSupported();
        public Task<int> RevokeOtherSessionsAsync(Guid accountId, Guid currentSessionId, string reason, CancellationToken ct = default) => throw NotSupported();
        public Task ChangePasswordAsync(Guid accountId, string newPassword, Guid? currentSessionId = null, CancellationToken ct = default) => throw NotSupported();
        public Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid accountId, CancellationToken ct = default) => throw NotSupported();
        public Task ValidateSecureThisComputerAccountAsync(Guid accountId, string? password, CancellationToken ct = default) => throw NotSupported();
        public Task<SessionIssueResult> SecureThisComputerAccountAsync(Guid accountId, string? password, bool hasPasskey, string deviceId, string deviceName, string client, CancellationToken ct = default) => throw NotSupported();
        public Task<bool> IsRecentlyAuthenticatedAsync(Guid sessionId, CancellationToken ct = default) => throw NotSupported();
        public Task<bool> ConfirmWithPasswordAsync(Guid accountId, Guid sessionId, string password, CancellationToken ct = default) => throw NotSupported();
        public Task<bool> ConfirmSessionAsync(Guid accountId, Guid sessionId, string method, CancellationToken ct = default) => throw NotSupported();
        public Task<Guid?> GetDeviceProfilePreferenceAsync(Guid accountId, Guid sessionId, CancellationToken ct = default) => throw NotSupported();
        public Task SetDeviceProfilePreferenceAsync(Guid accountId, Guid sessionId, Guid profileId, CancellationToken ct = default) => throw NotSupported();
        public Task<bool> ClearDeviceProfilePreferenceAsync(Guid accountId, Guid sessionId, CancellationToken ct = default) => throw NotSupported();
        public Task<IReadOnlySet<Guid>> GetProfileIdsWithPinAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken ct = default) => throw NotSupported();
        public Task SetProfilePinAsync(Guid profileId, string? pin, CancellationToken ct = default) => throw NotSupported();
        public Task<SessionValidationResult> SwitchActiveProfileAsync(string sessionToken, Guid targetProfileId, string? pin, CancellationToken ct = default) => throw NotSupported();
        public Task<bool> ValidateServiceCredentialAsync(string plaintextToken, CancellationToken ct = default) => throw NotSupported();

        private static NotSupportedException NotSupported() => new();
    }
}
