using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Setup;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class SetupSessionCodeTests : IDisposable
{
    private const string Code = "ABCD-EFGH";
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima-setup-session-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly SetupCodeRepository _codes;
    private readonly SetupSessionService _sessions;
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UtcNow);

    public SetupSessionCodeTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _codes = new SetupCodeRepository(_database);
        _sessions = new SetupSessionService(new OnboardingRepository(_database), new NoAdministratorIdentity(), _time);
    }

    [Fact]
    public async Task HomeNetwork_WithoutACode_IsToldACodeIsRequired()
    {
        var result = await _sessions.BeginAsync(ClientIngress.HomeNetwork, null, CancellationToken.None);

        Assert.Null(result.Started);
        Assert.Equal(SetupBeginRefusalReasons.CodeRequired, result.Refusal?.Reason);
    }

    [Fact]
    public async Task HomeNetwork_WithAValidCode_StartsSetup()
    {
        await _codes.IssueAsync(Code, _time.GetUtcNow(), CancellationToken.None);

        var result = await _sessions.BeginAsync(ClientIngress.HomeNetwork, Code, CancellationToken.None);

        Assert.NotNull(result.Started);
        Assert.True(await _sessions.ValidateSessionAsync(result.Started!.SetupSessionToken, CancellationToken.None));
    }

    [Fact]
    public async Task SameCodeASecondTime_IsRefused()
    {
        await _codes.IssueAsync(Code, _time.GetUtcNow(), CancellationToken.None);
        await _sessions.BeginAsync(ClientIngress.HomeNetwork, Code, CancellationToken.None);

        var second = await _sessions.BeginAsync(ClientIngress.HomeNetwork, Code, CancellationToken.None);

        Assert.Null(second.Started);
        Assert.Equal(SetupBeginRefusalReasons.CodeInvalid, second.Refusal?.Reason);
    }

    [Fact]
    public async Task ExpiredCode_IsRefused()
    {
        await _codes.IssueAsync(Code, _time.GetUtcNow(), CancellationToken.None);
        _time.Advance(SetupCodeRepository.Lifetime + TimeSpan.FromMinutes(1));

        var result = await _sessions.BeginAsync(ClientIngress.HomeNetwork, Code, CancellationToken.None);

        Assert.Null(result.Started);
        Assert.Equal(SetupBeginRefusalReasons.CodeInvalid, result.Refusal?.Reason);
    }

    [Fact]
    public async Task FiveWrongCodes_KillTheCode()
    {
        await _codes.IssueAsync(Code, _time.GetUtcNow(), CancellationToken.None);
        for (var i = 0; i < SetupCodeRepository.MaxFailedAttempts; i++)
        {
            var wrong = await _sessions.BeginAsync(ClientIngress.HomeNetwork, "ZZZZ-ZZZZ", CancellationToken.None);
            Assert.Equal(SetupBeginRefusalReasons.CodeInvalid, wrong.Refusal?.Reason);
        }

        var right = await _sessions.BeginAsync(ClientIngress.HomeNetwork, Code, CancellationToken.None);

        Assert.Null(right.Started);
        Assert.Equal(SetupBeginRefusalReasons.CodeInvalid, right.Refusal?.Reason);
    }

    [Fact]
    public async Task ThisComputer_NeedsNoCode()
    {
        var result = await _sessions.BeginAsync(ClientIngress.ThisComputer, null, CancellationToken.None);

        Assert.NotNull(result.Started);
    }

    [Theory]
    [InlineData(ClientIngressValues.Remote)]
    [InlineData(null)]
    [InlineData("something-else")]
    public async Task Remote_IsRefusedEvenWithAValidCode(string? ingress)
    {
        await _codes.IssueAsync(Code, _time.GetUtcNow(), CancellationToken.None);

        var result = await _sessions.BeginAsync(ingress, Code, CancellationToken.None);

        Assert.Null(result.Started);
        Assert.Equal(SetupBeginRefusalReasons.RemoteRefused, result.Refusal?.Reason);
        // The refused visitor did not use up the code, so the person at home still can.
        var home = await _sessions.BeginAsync(ClientIngress.HomeNetwork, Code, CancellationToken.None);
        Assert.NotNull(home.Started);
    }

    [Fact]
    public async Task SecondSuccessfulBegin_EndsTheFirstSession()
    {
        var first = await _sessions.BeginAsync(ClientIngress.ThisComputer, null, CancellationToken.None);
        var second = await _sessions.BeginAsync(ClientIngress.ThisComputer, null, CancellationToken.None);

        Assert.False(await _sessions.ValidateSessionAsync(first.Started!.SetupSessionToken, CancellationToken.None));
        Assert.True(await _sessions.ValidateSessionAsync(second.Started!.SetupSessionToken, CancellationToken.None));
    }

    [Fact]
    public async Task AfterAnAdministratorExists_BeginIsConflictNotCodeFlow()
    {
        var sessions = new SetupSessionService(
            new OnboardingRepository(_database), new NoAdministratorIdentity(administratorConfigured: true), _time);

        var result = await sessions.BeginAsync(ClientIngress.ThisComputer, null, CancellationToken.None);

        Assert.True(result.IsAlreadyConfigured);
        Assert.Null(result.Started);
        Assert.Null(result.Refusal);
    }

    [Fact]
    public async Task BeginEndpoint_WithoutABody_IsTreatedAsRemote()
    {
        var (status, body) = await PostBeginAsync(requestBody: null);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal(SetupBeginRefusalReasons.RemoteRefused, body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task BeginEndpoint_HomeNetworkWithoutACode_Returns403WithTheReason()
    {
        var (status, body) = await PostBeginAsync(new SetupBeginRequest { OriginalClientIngress = ClientIngress.HomeNetwork });

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal(SetupBeginRefusalReasons.CodeRequired, body.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task BeginEndpoint_ThisComputer_ReturnsTheSetupSession()
    {
        var (status, body) = await PostBeginAsync(new SetupBeginRequest { OriginalClientIngress = ClientIngress.ThisComputer });

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("setup_session_token").GetString()));
    }

    private async Task<(int Status, JsonElement Body)> PostBeginAsync(SetupBeginRequest? requestBody)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton(_sessions);
        // The other setup routes are mapped too; their services only need to be known so they bind as services, not bodies.
        builder.Services.AddSingleton<IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton<IFileOrganizer>(_ => null!);
        builder.Services.AddSingleton<IFirstPartyIdentityService>(_ => null!);
        builder.Services.AddSingleton<OnboardingRepository>(_ => null!);
        builder.Services.AddSingleton<ProviderCredentialService>(_ => null!);
        builder.Services.AddSingleton<DatabaseBackupService>(_ => null!);
        builder.Services.AddSingleton<ServerFolderBrowserService>(_ => null!);
        builder.Services.AddSingleton<SetupMediaLocationValidationService>(_ => null!);
        builder.Services.AddSingleton<SetupPreflightService>(_ => null!);
        builder.Services.AddSingleton<IContainerProbe>(_ => null!);
        await using var app = builder.Build();
        app.MapSetupEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == "/setup/v1/begin" &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);
        var bytes = requestBody is null ? [] : JsonSerializer.SerializeToUtf8Bytes(requestBody);
        var responseBody = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            Request = { Method = "POST", Path = "/setup/v1/begin", ContentType = "application/json", ContentLength = bytes.Length, Body = new MemoryStream(bytes) },
            Response = { Body = responseBody },
        };
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(bytes.Length > 0));

        await endpoint.RequestDelegate!(context);

        responseBody.Position = 0;
        using var document = await JsonDocument.ParseAsync(responseBody);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }

    private sealed class BodyDetection(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => canHaveBody;
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class NoAdministratorIdentity(bool administratorConfigured = false) : IFirstPartyIdentityService
    {
        public Task<bool> IsAdministratorConfiguredAsync(CancellationToken ct = default) => Task.FromResult(administratorConfigured);
        public Task<SessionIssueResult> BootstrapAdministratorAsync(string email, string password, string displayName, string deviceId, string deviceName, string client, CancellationToken ct = default, string? pin = null, string ingress = ClientIngress.HomeNetwork) => throw new NotSupportedException();
        public Task<SessionIssueResult> BootstrapThisComputerAdministratorAsync(string email, string displayName, string deviceId, string deviceName, string client, CancellationToken ct = default, string? pin = null) => throw new NotSupportedException();
        public Task<SessionIssueResult?> SignInThisComputerAccountAsync(string deviceId, string deviceName, string client, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetThisComputerAccountNameAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AuthenticationAttemptResult> AuthenticatePasswordAsync(string email, string password, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork) => throw new NotSupportedException();
        public Task<SessionIssueResult> CreateExternalSessionAsync(Guid accountId, string provider, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork) => throw new NotSupportedException();
        public Task<SessionIssueResult> CreatePasskeySessionAsync(Guid accountId, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork) => throw new NotSupportedException();
        public Task<SessionIssueResult> AcceptInvitationAsync(string token, string password, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.HomeNetwork) => throw new NotSupportedException();
        public Task<SessionValidationResult?> ValidateSessionAsync(string plaintextToken, bool touch = true, CancellationToken ct = default, string? currentIngress = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<MediaEngine.Domain.Entities.AuthSession>> GetSessionsAsync(Guid accountId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RevokeSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RevokeOtherSessionsAsync(Guid accountId, Guid currentSessionId, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ChangePasswordAsync(Guid accountId, string newPassword, Guid? currentSessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ResetPasswordWithRecoveryCodeAsync(string email, string recoveryCode, string newPassword, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> BeginPasswordResetAsync(string email, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ResetPasswordWithTokenAsync(string token, string newPassword, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid accountId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateSecureThisComputerAccountAsync(Guid accountId, string? password, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionIssueResult> SecureThisComputerAccountAsync(Guid accountId, string? password, bool hasPasskey, string deviceId, string deviceName, string client, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsRecentlyAuthenticatedAsync(Guid sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ConfirmWithPasswordAsync(Guid accountId, Guid sessionId, string password, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ConfirmSessionAsync(Guid accountId, Guid sessionId, string method, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetProfilePinAsync(Guid profileId, string? pin, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionValidationResult> SwitchActiveProfileAsync(string sessionToken, Guid targetProfileId, string? pin, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ValidateServiceCredentialAsync(string plaintextToken, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
