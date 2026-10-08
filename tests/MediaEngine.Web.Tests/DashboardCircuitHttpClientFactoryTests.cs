using System.Net;
using System.Security.Claims;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class DashboardCircuitHttpClientFactoryTests : IDisposable
{
    private const string ProtectorPurpose = "Tuvima.DashboardEngineCredential.v1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-circuit-forwarding-{Guid.NewGuid():N}");

    [Fact]
    public async Task NamedFactoryPipelineUsesCurrentCircuitTokenAndFailsClosedAfterClear()
    {
        var keyDirectory = Directory.CreateDirectory(Path.Combine(_root, "keys"));
        var protection = DataProtectionProvider.Create(keyDirectory);
        WriteBundle(protection, "service-token");
        var capture = new HeaderCaptureHandler();
        var stalePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, "stale-request-token")], "cookie"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddScoped<DashboardSessionAccessor>();
        services.AddSingleton(protection);
        services.AddSingleton(new DashboardServiceCredentialProviderOptions(Path.Combine(_root, "config")));
        services.AddSingleton<DashboardServiceCredentialProvider>();
        services.AddTransient<DashboardEngineAuthenticationHandler>();
        services.AddHttpClient("EngineApi", client => client.BaseAddress = new Uri("http://engine.test"))
            .AddHttpMessageHandler<DashboardEngineAuthenticationHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => capture);
        services.AddScoped<DashboardCircuitHttpClientFactory>();

        using var provider = services.BuildServiceProvider();
        var contextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        contextAccessor.HttpContext = new DefaultHttpContext { User = stalePrincipal };

        using var firstScope = provider.CreateScope();
        var firstSession = firstScope.ServiceProvider.GetRequiredService<DashboardSessionAccessor>();
        firstSession.Set("circuit-a-token", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);
        var firstFactory = firstScope.ServiceProvider.GetRequiredService<DashboardCircuitHttpClientFactory>();
        using (var firstClient = firstFactory.CreateClient("EngineApi"))
        {
            using var response = await firstClient.GetAsync("/first");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        firstSession.Set("rotated-circuit-a-token", firstSession.AccountId, firstSession.ActiveProfileId,
            firstSession.SessionId, null);
        using (var rotatedClient = firstFactory.CreateClient("EngineApi"))
        {
            using var response = await rotatedClient.GetAsync("/rotated");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var explicitClient = firstFactory.CreateClient("EngineApi"))
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/explicit"))
            {
                request.Headers.TryAddWithoutValidation(DashboardEngineAuthenticationHandler.SessionHeader, "explicit-token");
                using var response = await explicitClient.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        Assert.True(firstSession.ClearIfCurrent(firstSession.SnapshotForRefresh()));
        using (var clearedClient = firstFactory.CreateClient("EngineApi"))
        {
            using var response = await clearedClient.GetAsync("/cleared");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var secondScope = provider.CreateScope();
        var secondSession = secondScope.ServiceProvider.GetRequiredService<DashboardSessionAccessor>();
        secondSession.Set("circuit-b-token", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);
        var secondFactory = secondScope.ServiceProvider.GetRequiredService<DashboardCircuitHttpClientFactory>();
        using (var secondClient = secondFactory.CreateClient("EngineApi"))
        {
            using var response = await secondClient.GetAsync("/second");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(
            new string?[] { "circuit-a-token", "rotated-circuit-a-token", "explicit-token", null, "circuit-b-token" },
            capture.SessionTokens);
        Assert.All(capture.ServiceTokens, token => Assert.Equal("service-token", token));
    }

    [Fact]
    public void AuthorityChangedOnlyFiresForChangedSessionOrSemanticAuthority()
    {
        var dashboard = new DashboardSessionAccessor();
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var authority = MakeAuthority(accountId, profileId);
        var changes = 0;
        dashboard.OnAuthorityChanged += () => changes++;

        dashboard.Set("token-a", accountId, profileId, sessionId, authority);
        dashboard.Set("token-a", accountId, profileId, sessionId, MakeAuthority(accountId, profileId));
        Assert.Equal(1, changes);

        dashboard.Set("token-b", accountId, profileId, sessionId, MakeAuthority(accountId, profileId));
        Assert.Equal(2, changes);
        dashboard.Set("token-b", accountId, profileId, sessionId,
            MakeAuthority(accountId, profileId, grantVersion: 2));
        Assert.Equal(3, changes);

        dashboard.Set("token-b", accountId, profileId, sessionId,
            MakeAuthority(accountId, profileId, grantVersion: 2, capabilities: []));
        Assert.Equal(4, changes);
        dashboard.Set("token-b", accountId, profileId, sessionId,
            MakeAuthority(accountId, profileId, grantVersion: 2, profileEnabled: false, capabilities: []));
        Assert.Equal(5, changes);
        dashboard.Set("token-b", accountId, profileId, sessionId, null);
        Assert.Equal(6, changes);
        dashboard.Set("token-b", accountId, profileId, sessionId, null);
        Assert.Equal(6, changes);
    }

    private string CredentialPath => Path.Combine(_root, "config", ".secrets", "dashboard-engine.credential.json");

    private void WriteBundle(IDataProtectionProvider protection, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialPath)!);
        var bundle = new DashboardServiceCredentialBundle(
            Guid.NewGuid().ToString("N"), protection.CreateProtector(ProtectorPurpose).Protect(token),
            DateTimeOffset.UtcNow);
        File.WriteAllText(CredentialPath, JsonSerializer.Serialize(bundle));
    }

    private static DashboardAuthorityResponse MakeAuthority(
        Guid accountId,
        Guid profileId,
        long grantVersion = 1,
        bool profileEnabled = true,
        IReadOnlyList<string>? capabilities = null) =>
        new(accountId, profileId, true, true, 1, grantVersion, false, false, null, 1,
            [new AccountProfileGrantDto(accountId, profileId, "Profile", null, true, profileEnabled, false,
                new GrantAdminProtectionDto(false, "UntilProfileSwitch", 30, 1, false, null), grantVersion,
                DateTimeOffset.UnixEpoch)], ["listen"], capabilities ?? ["playback.control"]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class HeaderCaptureHandler : HttpMessageHandler
    {
        public List<string?> SessionTokens { get; } = [];
        public List<string?> ServiceTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SessionTokens.Add(Header(request, DashboardEngineAuthenticationHandler.SessionHeader));
            ServiceTokens.Add(Header(request, DashboardServiceCredentialHandler.ServiceHeader));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
    }
}
