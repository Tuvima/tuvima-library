using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Playback;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class PlaybackTelemetryAuthorizationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-telemetry-auth-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public PlaybackTelemetryAuthorizationTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
    }

    [Fact]
    public async Task DelegatedReadsCarryExactAccountProfileLibraryAndFeatureIntersectionToRepository()
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var allowedLibrary = Guid.NewGuid();
        SeedAccess(accountId, profileId, allowedLibrary);
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, accountId, profileId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ApplicationEnabled: true);
        var repository = new CapturingRepository();
        var service = Service(authority, AuthorizationDecision.Allow(), repository);

        Assert.NotNull(await service.GetPlaybackAsync(ApplicationPermissionIds.AnalyticsPlaybackRead, null, null, default));
        var scope = Assert.IsType<PlaybackTelemetryReadScope>(repository.Scope);
        Assert.False(scope.AllAccounts);
        Assert.Equal(accountId, scope.AccountId);
        Assert.Equal(profileId, scope.ProfileId);
        Assert.False(scope.AllLibraries);
        Assert.Equal([allowedLibrary], scope.LibraryIds);
        Assert.Equal([AccountFeatureId.Watch], scope.Features);
    }

    [Fact]
    public async Task MissingApplicationPermissionFailsBeforeAnyTelemetryQuery()
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        SeedAccess(accountId, profileId, Guid.NewGuid());
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, accountId, profileId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ApplicationEnabled: true);
        var repository = new CapturingRepository();
        var service = Service(authority,
            AuthorizationDecision.Deny(AuthorizationDenialReason.MissingPermission), repository);

        Assert.Null(await service.GetActiveAsync(ApplicationPermissionIds.PlaybackSessionsRead, 50, null, default));
        Assert.Null(repository.Scope);
    }

    [Fact]
    public async Task EffectiveAdministratorAndServiceApplicationHaveExplicitGlobalScope()
    {
        var repository = new CapturingRepository();
        var administrator = new RequestAuthority(
            PrincipalKind.Human, true, Guid.NewGuid(), Guid.NewGuid(), SessionId: Guid.NewGuid(),
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ActiveProfileIsServerAdministrator: true);
        Assert.NotNull(await Service(administrator, AuthorizationDecision.Allow(), repository)
            .GetPlaybackAsync(ApplicationPermissionIds.AnalyticsPlaybackRead, null, null, default));
        Assert.True(repository.Scope!.AllAccounts);
        Assert.True(repository.Scope.AllLibraries);

        repository.Scope = null;
        var application = new RequestAuthority(
            PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: true);
        Assert.NotNull(await Service(application, AuthorizationDecision.Allow(), repository)
            .GetPlaybackAsync(ApplicationPermissionIds.AnalyticsPlaybackRead, null, null, default));
        Assert.True(repository.Scope!.AllAccounts);
        Assert.True(repository.Scope.AllLibraries);
    }

    [Fact]
    public async Task SixReadFamiliesHaveDistinctApplicationPolicies()
    {
        await using var app = WebApplication.CreateBuilder().Build();
        app.MapPlaybackTelemetryEndpoints();
        var expected = new Dictionary<string, ApplicationPermissionId>
        {
            ["GetActivePlaybackTelemetrySessions"] = ApplicationPermissionIds.PlaybackSessionsRead,
            ["GetPlaybackTelemetryHistory"] = ApplicationPermissionIds.PlaybackHistoryRead,
            ["GetPlaybackAnalytics"] = ApplicationPermissionIds.AnalyticsPlaybackRead,
            ["GetPlaybackUserAnalytics"] = ApplicationPermissionIds.AnalyticsUsersRead,
            ["GetPlaybackLibraryAnalytics"] = ApplicationPermissionIds.AnalyticsLibraryRead,
            ["GetPlaybackDeviceAnalytics"] = ApplicationPermissionIds.AnalyticsDevicesRead,
        };
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToDictionary(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName!);
        foreach (var pair in expected)
        {
            var policy = Assert.Single(endpoints[pair.Key].Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            Assert.Equal(pair.Value,
                Assert.Single(policy.Requirements.OfType<AdministratorOrApplicationRequirement>()).Permission);
            Assert.Null(endpoints[pair.Key].Metadata.GetMetadata<IAllowAnonymous>());
        }
    }

    private PlaybackTelemetryReadService Service(
        RequestAuthority authority,
        AuthorizationDecision decision,
        CapturingRepository repository)
    {
        var context = new DefaultHttpContext();
        return new(new HttpContextAccessor { HttpContext = context }, new AuthorityResolver(authority),
            new Evaluator(decision), new ProfileAccessRepository(_database), repository);
    }

    private void SeedAccess(Guid accountId, Guid profileId, Guid libraryId)
    {
        using var connection = _database.CreateConnection();
        var now = DateTimeOffset.UtcNow.ToString("O");
        connection.Execute("""
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at)
            VALUES(@profileId,'Viewer','#000000','RestrictedProfile',@now);
            INSERT INTO accounts(id,email,normalized_email,is_enabled,authorization_version,created_at,updated_at,profile_id)
            VALUES(@accountId,@email,@normalized,1,1,@now,@now,@profileId);
            INSERT INTO profile_feature_grants(profile_id,feature_id,granted_at) VALUES(@profileId,'watch',@now);
            INSERT INTO profile_library_grants(profile_id,library_id,granted_at) VALUES(@profileId,@libraryId,@now);
            """, new
        {
            accountId,
            profileId,
            libraryId,
            now,
            email = $"{accountId:N}@example.test",
            normalized = $"{accountId:N}@EXAMPLE.TEST",
        });
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_path);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(_path + suffix))
            {
                File.Delete(_path + suffix);
            }
        }
    }

    private sealed class AuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class Evaluator(AuthorizationDecision decision) : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource, CancellationToken ct = default) =>
            ValueTask.FromResult(decision);
    }

    private sealed class CapturingRepository : IPlaybackTelemetryRepository
    {
        public PlaybackTelemetryReadScope? Scope { get; set; }
        public Task<IReadOnlyList<PlaybackTelemetryTransition>> ObserveAsync(PlaybackTelemetryObservation observation, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlaybackTelemetryTransition>>([]);
        public Task<PlaybackTelemetryTransition?> CloseAsync(Guid playerSessionId, DateTimeOffset endedAt, string reason, CancellationToken ct = default) =>
            Task.FromResult<PlaybackTelemetryTransition?>(null);
        public Task<IReadOnlyList<PlaybackTelemetryTransition>> CloseStaleAsync(DateTimeOffset staleBefore, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlaybackTelemetryTransition>>([]);
        public Task<PlaybackTelemetryPage> GetActiveAsync(PlaybackTelemetryReadScope scope, int limit, string? cursor, CancellationToken ct = default)
        { Scope = scope; return Task.FromResult(new PlaybackTelemetryPage([], null)); }
        public Task<PlaybackTelemetryPage> GetHistoryAsync(PlaybackTelemetryReadScope scope, DateTimeOffset? from, DateTimeOffset? to, int limit, string? cursor, CancellationToken ct = default)
        { Scope = scope; return Task.FromResult(new PlaybackTelemetryPage([], null)); }
        public Task<PlaybackTelemetryAggregate> GetPlaybackAggregateAsync(PlaybackTelemetryReadScope scope, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default)
        { Scope = scope; return Task.FromResult(new PlaybackTelemetryAggregate(0, 0, 0, 0, 0, 0, 0, 0, 0)); }
        public Task<IReadOnlyList<PlaybackTelemetryGroupAggregate>> GetGroupAggregatesAsync(PlaybackTelemetryReadScope scope, string dimension, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct = default)
        { Scope = scope; return Task.FromResult<IReadOnlyList<PlaybackTelemetryGroupAggregate>>([]); }
    }
}
