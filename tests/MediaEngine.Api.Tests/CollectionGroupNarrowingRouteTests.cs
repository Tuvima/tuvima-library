using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The group reads behind the grouped browse views (<c>/collections/content-groups</c> and <c>/collections/system-views</c>) narrow to one
/// library and to its Unmatched files after authorization, through the real route table, the real authorized display projection and a real
/// data store: a library the profile holds no grant on answers like an empty one, even when it has Unmatched files.
/// </summary>
public sealed class CollectionGroupNarrowingRouteTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_group_narrowing_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _account = Guid.NewGuid();
    private readonly Guid _granted = Guid.NewGuid();
    private readonly Guid _hidden = Guid.NewGuid();
    private Guid _grantedAsset;
    private Guid _hiddenAsset;

    public CollectionGroupNarrowingRouteTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
        new AccountRepository(_database).CreateAccountAsync(
            new Account
            {
                Id = _account,
                Email = $"{_account:N}@example.com",
                NormalizedEmail = $"{_account:N}@EXAMPLE.COM",
                IsEnabled = true,
                AuthorizationVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            new TestGrant(_account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId)).GetAwaiter().GetResult();
        // A library is on or off per household: the household of this profile turns the hidden one off.
        var household = new ProfileRepository(_database).GetByIdAsync(MediaEngine.Domain.Aggregates.Profile.SeedProfileId).GetAwaiter().GetResult()!.HouseholdId!.Value;
        new HouseholdLibraryAccessRepository(_database)
            .ReplaceHiddenLibrariesAsync(household, new HashSet<Guid> { _hidden }, DateTimeOffset.UtcNow, null).GetAwaiter().GetResult();
        // Each library holds one identified book and one Unmatched book, in different series, so the lane answers and the Unmatched answers differ.
        SeedBook(_granted, "Granted Saga", "Granted Book");
        SeedBook(_hidden, "Hidden Saga", "Hidden Book");
        _grantedAsset = SeedBook(_granted, "Granted Unmatched Saga", "Granted Unmatched Book");
        _hiddenAsset = SeedBook(_hidden, "Hidden Unmatched Saga", "Hidden Unmatched Book");
        var unmatched = new UnmatchedAssetRepository(_database);
        var now = DateTimeOffset.UtcNow;
        unmatched.MarkUnmatchedAsync(_grantedAsset, _granted.ToString("D"), UnmatchedReasons.NoMatch, now, now.AddDays(7)).GetAwaiter().GetResult();
        unmatched.MarkUnmatchedAsync(_hiddenAsset, _hidden.ToString("D"), UnmatchedReasons.NoMatch, now, now.AddDays(7)).GetAwaiter().GetResult();
    }

    // One book in its own series collection, in the given library, with the series name a system view groups by.
    private Guid SeedBook(Guid library, string series, string title)
    {
        var collection = Guid.NewGuid();
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled)
            VALUES (@collection, @series, 'Series', 'library', 'query', 1);
            INSERT INTO works (id, media_type, work_kind, curator_state, collection_id)
            VALUES (@work, 'Books', 'standalone', 'accepted', @collection);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP, @library);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@asset, 'title', @title, CURRENT_TIMESTAMP),
                   (@asset, 'series', @series, CURRENT_TIMESTAMP),
                   (@asset, 'series_index', '1', CURRENT_TIMESTAMP);
            """,
            new
            {
                collection,
                work,
                edition,
                asset,
                series,
                title,
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{asset:N}.epub",
                library = library.ToString("D"),
            });
        return asset;
    }

    public static TheoryData<string> Routes => new() { "/collections/content-groups", "/collections/system-views?mediaType=Books&groupField=series" };

    private static string With(string route, string query) => route + (route.Contains('?') ? "&" : "?") + query;

    private async Task<string[]> GroupNamesAsync(TestApplication app, string path)
    {
        using var response = await app.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} returned {(int)response.StatusCode}: {body}");
        return [.. JsonNode.Parse(body)!.AsArray().Select(group => group!["displayName"]?.GetValue<string>() ?? group["display_name"]!.GetValue<string>()).Order()];
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ALibraryHiddenFromTheProfileAnswersEmpty_AndNeverLeaksItsGroupsIntoTheUnfilteredAnswer(string route)
    {
        await using var app = await StartAsync();

        Assert.Empty(await GroupNamesAsync(app, With(route, $"libraryId={_hidden:D}")));
        Assert.Equal(["Granted Saga"], await GroupNamesAsync(app, route));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task AVisibleLibraryAnswersItsOwnGroups(string route)
    {
        await using var app = await StartAsync();

        Assert.Equal(["Granted Saga"], await GroupNamesAsync(app, With(route, $"libraryId={_granted:D}")));
        Assert.Empty(await GroupNamesAsync(app, With(route, $"libraryId={Guid.NewGuid():D}")));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task TheUnmatchedFilterAnswersNothingForALibraryHiddenFromTheProfile_EvenWhenItHasUnmatchedFiles(string route)
    {
        await using var app = await StartAsync();

        Assert.Empty(await GroupNamesAsync(app, With(route, $"libraryId={_hidden:D}&unmatched=true")));
        Assert.Equal(["Granted Unmatched Saga"], await GroupNamesAsync(app, With(route, "unmatched=true")));
        Assert.Equal(["Granted Unmatched Saga"], await GroupNamesAsync(app, With(route, $"libraryId={_granted:D}&unmatched=true")));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task AKeptUnmatchedFileStaysInTheUnmatchedGroups_AndNeverReachesTheLanes(string route)
    {
        await new UnmatchedAssetRepository(_database).KeepAsIsAsync(_grantedAsset, DateTimeOffset.UtcNow);
        await using var app = await StartAsync();

        Assert.Equal(["Granted Unmatched Saga"], await GroupNamesAsync(app, With(route, $"libraryId={_granted:D}&unmatched=true")));
        Assert.Equal(["Granted Saga"], await GroupNamesAsync(app, With(route, $"libraryId={_granted:D}")));
    }

    // Host -------------------------------------------------------------------------------------------------------

    private async Task<TestApplication> StartAsync()
    {
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient,
            true,
            AccountId: _account,
            ActiveProfileId: MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationId: Guid.NewGuid(),
            DeviceId: Guid.NewGuid(),
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: 1,
            ProfileAuthorizationVersion: 1,
            ApplicationAuthorizationVersion: 1);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        RegisterUnusedRouteServices(builder.Services);
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(_granted));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddSingleton<IAccountRepository, AccountRepository>();
        builder.Services.AddSingleton<IProfileAccessRepository, ProfileAccessRepository>();
        builder.Services.AddSingleton<IProfileRepository, ProfileRepository>();
        builder.Services.AddSingleton<ICollectionRepository, CollectionRepository>();
        builder.Services.AddSingleton<IUnmatchedAssetRepository, UnmatchedAssetRepository>();
        builder.Services.AddSingleton<ICollectionBrowseReadService>(provider => new CollectionBrowseReadService(
            provider.GetRequiredService<ICollectionRepository>(), _database, NullLogger<CollectionBrowseReadService>.Instance));
        builder.Services.AddSingleton<DisplayWorkProjectionReader>();
        builder.Services.AddSingleton<IRawDisplayProjectionReadService, LiveProjection>();
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IVaultLibraryRepository>(new FixedVaultLibraries());
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IHouseholdLibraryAccessRepository, HouseholdLibraryAccessRepository>();
        builder.Services.AddSingleton<MediaEngine.Api.Security.LibraryVisibilityRule>();
        builder.Services.AddScoped<AuthorizedDisplayProjectionReadService>();
        builder.Services.AddScoped<IDisplayProjectionReadService>(provider =>
            provider.GetRequiredService<AuthorizedDisplayProjectionReadService>());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCollectionEndpoints();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }

    /// <summary>Reads the projection fresh every time so a new row shows on the next request.</summary>
    private sealed class LiveProjection(DisplayWorkProjectionReader reader) : IRawDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => reader.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) => reader.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }

    // The collection routes share one group with routes this test never calls. Their parameters only need to be known services for the
    // route table to build; they are never resolved. Registered before the real services so those win for the routes under test.
    private static void RegisterUnusedRouteServices(IServiceCollection services)
    {
        var assemblies = new[]
        {
            typeof(CollectionEndpoints).Assembly,
            typeof(IProfileRepository).Assembly,
            typeof(MediaEngine.Application.Services.IPersonAliasReadService).Assembly,
            typeof(MediaEngine.Contracts.Paging.PagedRequest).Assembly,
        };
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetExportedTypes()))
        {
            if (type.IsGenericTypeDefinition || (type.IsClass && type.IsAbstract) || type.IsEnum || type.IsValueType)
            {
                continue;
            }

            services.AddSingleton(type, _ => null!);
        }
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class LibraryAccess(Guid libraryId) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(
            RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(
            RequestAuthority authority, Guid requestedLibraryId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(requestedLibraryId == libraryId
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.LibraryNotVisible));

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(
            RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }

    private sealed class AllowAuthorizationEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "group-narrowing-test")], Scheme.Name)),
                Scheme.Name)));
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = address };

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
