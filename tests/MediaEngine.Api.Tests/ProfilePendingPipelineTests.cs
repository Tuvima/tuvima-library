using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The pipeline order, proved end to end (users, profiles and households spec D3). A real sign-in leaves the session "choose a profile";
/// in a host built with the Engine's own sign-in handler, policies, data store and the same middleware order as <c>Program.cs</c>, an
/// ordinary route answers 403 <c>profile_required</c> until a profile is chosen, while the picker's own routes work. A guardrail
/// keeps this host's order the same as the Engine's.
/// </summary>
public sealed class ProfilePendingPipelineTests : IDisposable
{
    private const string ServiceKey = "profile-pending-pipeline-service-key";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_pending_pipeline_{Guid.NewGuid():N}.db");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima_pending_pipeline_cfg_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;

    public ProfilePendingPipelineTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Fact]
    public async Task APendingSession_IsRefusedOnAnOrdinaryRoute_UntilAProfileIsChosen_WhileThePickersRoutesWork()
    {
        await using var app = await StartAsync();
        // Setup is over.
        using (var connection = _database.CreateConnection())
        {
            Dapper.SqlMapper.Execute(connection, "UPDATE onboarding_workflows SET state = 'complete';");
        }

        var identity = app.Services.GetRequiredService<IFirstPartyIdentityService>();
        var owner = await identity.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Dad", "setup-device", "Setup", "Dashboard");
        // A household of one with no PIN goes straight in; a PIN keeps the picker (and its PIN step) in front of the profile.
        await identity.SetProfilePinAsync(owner.Profile.Id, "2468");
        var signedIn = await identity.SignInThisComputerAccountAsync("laptop", "Laptop", "Dashboard");
        Assert.NotNull(signedIn);
        Assert.True(signedIn!.Session.ProfilePending);
        var ownProfile = signedIn.ActiveProfile.Id;

        // The ordinary route is refused with the code the Dashboard looks for, and answers nothing about the profile.
        using var refused = await app.SendAsync(HttpMethod.Get, $"/profiles/{ownProfile:D}", signedIn.PlaintextToken);
        Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(ProfilePendingMiddleware.ProfileRequiredCode, problem.RootElement.GetProperty("code").GetString());

        // The picker's own routes still answer: the household's profiles, and the choice itself.
        using var list = await app.SendAsync(HttpMethod.Get, "/profiles", signedIn.PlaintextToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var profiles = await list.Content.ReadFromJsonAsync<List<ProfileResponseDto>>();
        Assert.Equal(owner.Profile.Id, Assert.Single(profiles!).Id);

        using var chosen = await app.SendAsync(
            HttpMethod.Post, "/auth/session/switch-profile", signedIn.PlaintextToken, new SwitchProfileRequest { ProfileId = ownProfile, Secret = "2468" });
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);

        // Once someone is picked the same route works for the same session.
        using var allowed = await app.SendAsync(HttpMethod.Get, $"/profiles/{ownProfile:D}", signedIn.PlaintextToken);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public void TheProfilePendingStep_SitsAfterSignInAndBeforeAuthorizationAndEveryRoute_InTheEnginesOwnPipeline()
    {
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "Program.cs"));

        var authentication = program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var pending = program.IndexOf("app.UseMiddleware<ProfilePendingMiddleware>();", StringComparison.Ordinal);
        var authorization = program.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);
        var firstRoute = program.IndexOf("app.MapHealthChecks(", StringComparison.Ordinal);

        Assert.True(authentication >= 0 && pending > authentication, "The profile-pending step must run after sign-in is read.");
        Assert.True(authorization > pending, "The profile-pending step must run before authorization and any route.");
        Assert.True(firstRoute > authorization, "Routes are mapped after the pipeline steps.");
    }

    private async Task<TestApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddHttpClient();
        builder.Services.AddMemoryCache();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IConfigurationLoader>(new ConfigurationDirectoryLoader(_configPath));

        // The Engine's own composition for sign-in and who-may-do-what (Program.cs), over the real data store.
        builder.Services.AddTuvimaStorage();
        builder.Services.AddTuvimaSecurity();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<ITwoStepSecretProtector, DataProtectionTwoStepSecretProtector>();
        builder.Services.AddSingleton<IPermissionRegistry, PermissionRegistry>();
        builder.Services.AddScoped<IRequestAuthorityResolver, RequestAuthorityResolver>();
        builder.Services.AddScoped<DashboardAuthorityProjector>();
        builder.Services.AddScoped<IGrantAdminUnlockService, GrantAdminUnlockService>();
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IHouseholdLibraryAccessRepository>(new MediaEngine.Storage.HouseholdLibraryAccessRepository(_database));
        builder.Services.AddSingleton<LibraryVisibilityRule>();
        builder.Services.AddScoped<IAccountAccessDecisionService, AccountAccessDecisionService>();
        builder.Services.AddScoped<ISelfServiceAuthorizationService>(sp => (AccountAccessDecisionService)sp.GetRequiredService<IAccountAccessDecisionService>());
        builder.Services.AddScoped<IAuthorizationHandler, EffectiveAdministratorHandler>();
        builder.Services.AddScoped<IAuthorizationHandler, HumanSelfServiceHandler>();
        builder.Services.AddAuthentication(TuvimaAuthDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TuvimaAuthenticationHandler>(TuvimaAuthDefaults.Scheme, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(AuthPolicies.HumanSelfService, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new HumanSelfServiceRequirement()));
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        // The two route groups under test hold many other endpoints that take services this host does not build, and mapping needs every
        // such parameter registered (or it reads it as a body). Register a placeholder for each; a request resolves only what its route uses.
        AddPlaceholdersForUnbuiltServices(builder.Services);

        // The shipped modules also start background workers; only the web server is needed here.
        foreach (var worker in builder.Services
                     .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                         && descriptor.ImplementationType?.Name != "GenericWebHostService")
                     .ToList())
        {
            builder.Services.Remove(worker);
        }

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync(ex.ToString());
            }
        });

        // The same order as Program.cs (guarded by the second test).
        app.UseAuthentication();
        app.UseMiddleware<PasswordChangeRequiredMiddleware>();
        app.UseMiddleware<ProfilePendingMiddleware>();
        app.UseAuthorization();
        app.MapProfileEndpoints();
        app.MapAuthenticationEndpoints();

        // The Dashboard's service key: the sign-in handler reads a person's session only beside it.
        await app.Services.GetRequiredService<IIdentityRepository>().InsertServiceCredentialAsync(new ServiceCredential
        {
            Id = Guid.NewGuid(),
            Purpose = DashboardServiceCredentialOptions.Purpose,
            KeyId = "pipeline-test",
            TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ServiceKey))),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
        try { Directory.Delete(_configPath, recursive: true); } catch (IOException) { /* The OS temp sweep removes it. */ }
    }

    private static readonly string[] ServiceSuffixes =
    [
        "Service", "Repository", "Guard", "Gate", "Resolver", "Projector", "Reader", "Provider", "Evaluator", "Factory", "Store", "Writer",
        "Validator", "Handler", "Recognizer", "Limiter", "Probe", "Registry", "Cache", "Authorizer", "Throttle", "Bootstrapper", "Hasher",
    ];

    private static void AddPlaceholdersForUnbuiltServices(IServiceCollection services)
    {
        var assemblies = new[] { typeof(ProfileEndpoints).Assembly, typeof(IProfileService).Assembly, typeof(IAccountRepository).Assembly, typeof(MediaEngine.Application.Services.IProfileOverviewReadService).Assembly };
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetExportedTypes()))
        {
            if (type.IsGenericTypeDefinition || type.IsEnum || type.IsValueType || type.IsSealed && type.IsAbstract ||
                typeof(Exception).IsAssignableFrom(type) || typeof(Attribute).IsAssignableFrom(type) ||
                type.Namespace?.StartsWith("MediaEngine.Contracts", StringComparison.Ordinal) != false ||
                !type.IsInterface && (type.IsAbstract || !ServiceSuffixes.Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal))))
            {
                continue;
            }

            services.TryAddSingleton(type, _ => throw new InvalidOperationException($"{type.Name} is not built in this host."));
        }
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        private readonly HttpClient _client = new() { BaseAddress = address };

        public IServiceProvider Services => app.Services;

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string sessionToken, object? body = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add(TuvimaAuthDefaults.ServiceHeader, ServiceKey);
            request.Headers.Add(TuvimaAuthDefaults.SessionHeader, sessionToken);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType());
            }

            return _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
