using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Backup screen's two actions (<c>GET /system/backups/state</c> and <c>PUT /system/backups/waiting-period</c>) and the
/// "what is in this backup" counts on a restore test and a first-run upload. Counts only: nothing here names a title.
/// </summary>
public sealed class BackupStateEndpointTests : IDisposable
{
    private const string StateRoute = "/system/backups/state";
    private const string PeriodRoute = "/system/backups/waiting-period";
    private const string PrincipalClaim = "test-principal";
    private const string Now = "2026-10-21T12:00:00.0000000+00:00";

    private static readonly Guid Account = Guid.NewGuid();
    private static readonly Guid Profile = Guid.NewGuid();
    private static readonly Guid Household = Guid.NewGuid();
    private static readonly Guid PlainApp = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<string, RequestAuthority> Principals = new Dictionary<string, RequestAuthority>
    {
        ["server-admin"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ActiveProfileIsServerAdministrator: true),
        ["household-member"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ActiveProfileHouseholdId: Household),
        ["household-admin"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ActiveProfileHouseholdId: Household, ActiveProfileIsHouseholdAdministrator: true),
        ["service-without-backup-read"] = new(PrincipalKind.ServiceApplication, true, null, null, PlainApp,
            ApplicationEnabled: true),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-backup-state-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _disposables = [];

    public BackupStateEndpointTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task OnALibraryThatWasNeverRestored_StateIsEmptyWithTheStandardWaitingPeriod()
    {
        var (config, dbPath) = NewLibrary("fresh");
        await using var app = BuildApplication(BackupTestKit.OpenStore(dbPath), config);

        var (status, body) = await SendAsync(app, "GET", StateRoute);

        Assert.Equal(StatusCodes.Status200OK, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(0, root.GetProperty("file_only_titles").GetInt32());
        Assert.Equal(90, root.GetProperty("waiting_period_days").GetInt32());
        Assert.Equal(0, root.GetProperty("still_waiting").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("latest_restore").ValueKind);
    }

    [Fact]
    public async Task AnItemKnownByItsFileOnly_IsCountedAsFileOnly_AndNamedNowhere()
    {
        var (config, dbPath) = NewLibrary("file-only");
        var database = BackupTestKit.OpenStore(dbPath);
        var maya = Guid.NewGuid();
        AddProfile(database, maya);
        var scan = AddMovie(database, "Secret Scan Title", "hash-scan", withIds: false);
        AddProgress(database, maya, scan.Asset, 12.5);
        await using var app = BuildApplication(database, config);

        var (_, body) = await SendAsync(app, "GET", StateRoute);

        Assert.Equal(1, JsonDocument.Parse(body).RootElement.GetProperty("file_only_titles").GetInt32());
        Assert.DoesNotContain("Secret Scan Title", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hash-scan", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterARestore_StateReportsTheLatestRestoreAndHowManyItemsStillWait_ThenWhatCameBack()
    {
        var (config, dbPath) = NewLibrary("restored");
        var maya = Guid.NewGuid();
        using (var original = BackupTestKit.OpenStore(dbPath))
        {
            AddProfile(original, maya);
            var movie = AddMovie(original, "Dune", "hash-original", withIds: true);
            AddProgress(original, maya, movie.Asset, 42.5);
            AddBookmark(original, maya, movie, 120.0);
            var service = BackupTestKit.Service(original, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        var fresh = BackupTestKit.OpenStore(dbPath);
        Assert.Equal(PendingRestoreImportOutcome.Imported, BackupTestKit.Importer(fresh, config).ImportIfPending());
        await using var app = BuildApplication(fresh, config);

        var before = JsonDocument.Parse((await SendAsync(app, "GET", StateRoute)).Text).RootElement;
        var restoreBefore = before.GetProperty("latest_restore");
        Assert.True(before.GetProperty("still_waiting").GetInt32() >= 2);
        Assert.Equal(0, restoreBefore.GetProperty("attached").GetInt32());
        Assert.StartsWith("tuvima-backup-", restoreBefore.GetProperty("backup_file_name").GetString(), StringComparison.Ordinal);

        AddMovie(fresh, "Dune (rescanned)", "hash-reencoded", withIds: true);
        new WaitingPersonalStateRepository(fresh).RunPass(DateTimeOffset.UtcNow.AddMinutes(1), 100, 90);

        var after = JsonDocument.Parse((await SendAsync(app, "GET", StateRoute)).Text).RootElement;
        Assert.Equal(0, after.GetProperty("still_waiting").GetInt32());
        Assert.Equal(2, after.GetProperty("latest_restore").GetProperty("attached").GetInt32());
    }

    [Fact]
    public async Task ChangingTheWaitingPeriod_SavesItToTheMaintenanceFile_AndReturnsTheNewState()
    {
        var (config, dbPath) = NewLibrary("period");
        await using var app = BuildApplication(BackupTestKit.OpenStore(dbPath), config);

        var (status, body) = await SendAsync(app, "PUT", PeriodRoute, """{"days":180}""");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(180, JsonDocument.Parse(body).RootElement.GetProperty("waiting_period_days").GetInt32());
        Assert.Equal(180, new ConfigurationDirectoryLoader(config).LoadMaintenance().MissingFileRetentionDays);
        var (_, again) = await SendAsync(app, "GET", StateRoute);
        Assert.Equal(180, JsonDocument.Parse(again).RootElement.GetProperty("waiting_period_days").GetInt32());
    }

    [Theory]
    [InlineData(6)]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(3651)]
    public async Task AWaitingPeriodOutsideSevenToThreeThousandSixHundredFiftyDays_IsRefusedWithOneSentence_AndNothingIsSaved(int days)
    {
        var (config, dbPath) = NewLibrary("range");
        await using var app = BuildApplication(BackupTestKit.OpenStore(dbPath), config);

        var (status, body) = await SendAsync(app, "PUT", PeriodRoute, $$"""{"days":{{days}}}""");

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("Choose between 7 and 3650 days.", body, StringComparison.Ordinal);
        Assert.Equal(90, new ConfigurationDirectoryLoader(config).LoadMaintenance().MissingFileRetentionDays);
    }

    [Theory]
    [InlineData("household-member")]
    [InlineData("household-admin")]
    [InlineData("service-without-backup-read")]
    public async Task TheStateAction_IsRefusedToEveryoneButAServerAdministrator(string principal)
    {
        var (config, dbPath) = NewLibrary("gate-" + principal);
        await using var app = BuildApplication(BackupTestKit.OpenStore(dbPath), config);

        Assert.False(await PassesAsync(app, StateRoute, "GET", principal), $"{principal} must not read the backup state.");
        Assert.False(await PassesAsync(app, "/system/backups", "GET", principal), $"{principal} must not list backups.");
    }

    [Fact]
    public async Task TheStateAction_ServesAServerAdministrator_WithTheSameGateAsTheBackupList()
    {
        var (config, dbPath) = NewLibrary("gate-admin");
        await using var app = BuildApplication(BackupTestKit.OpenStore(dbPath), config);

        Assert.True(await PassesAsync(app, StateRoute, "GET", "server-admin"));
        var state = RequirementOf(app, StateRoute, "GET");
        var list = RequirementOf(app, "/system/backups", "GET");
        Assert.Equal(ApplicationPermissionIds.BackupRead, state.Permission);
        Assert.Equal(list.Permission, state.Permission);
        Assert.Equal(list.AllowHouseholdAdministrator, state.AllowHouseholdAdministrator);
    }

    [Fact]
    public async Task TheWaitingPeriodAction_RequiresAnEffectiveAdministrator_LikeTheOtherBackupSettings()
    {
        var (config, dbPath) = NewLibrary("gate-put");
        await using var app = BuildApplication(BackupTestKit.OpenStore(dbPath), config);

        var period = EndpointOf(app, PeriodRoute, "PUT").Metadata.GetOrderedMetadata<IAuthorizeData>();
        var settings = EndpointOf(app, "/backup/settings", "PUT").Metadata.GetOrderedMetadata<IAuthorizeData>();

        Assert.Equal(AuthPolicies.Administrator, Assert.Single(period).Policy);
        Assert.Equal(Assert.Single(settings).Policy, Assert.Single(period).Policy);
    }

    [Fact]
    public async Task ARestoreTest_ReportsWhatTheBackupHoldsAsCounts_AndNamesNothing()
    {
        var (config, dbPath) = NewLibrary("drill");
        using var database = BackupTestKit.OpenStore(dbPath);
        var maya = Guid.NewGuid();
        AddProfile(database, maya);
        var movie = AddMovie(database, "Dune", "hash-original", withIds: true);
        AddProgress(database, maya, movie.Asset, 42.5);
        AddBookmark(database, maya, movie, 120.0);
        var scan = AddMovie(database, "Secret Scan Title", "hash-scan", withIds: false);
        AddProgress(database, maya, scan.Asset, 5);
        var service = BackupTestKit.Service(database, config);
        var archive = Path.GetFileName(await service.CreateAsync(CancellationToken.None));

        var result = service.ValidateRestore(archive);

        var contents = Assert.IsType<MediaEngine.Contracts.System.BackupContentsDto>(result.Contents);
        Assert.Equal(ProfileCount(database), contents.Profiles);
        Assert.Equal(3, contents.PersonalItems);
        // Dune is known twice (its bookmark names the film, its progress names the file); the scan is known by its file only.
        Assert.Equal(3, contents.Titles);
        Assert.Equal(1, contents.FileOnlyTitles);
        Assert.True(contents.ConfigurationRows >= 0);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("Secret Scan Title", json, StringComparison.Ordinal);
        Assert.Contains("\"personal_items\":3", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFirstRunUpload_CarriesTheSameCountsAsTheRestoreTest()
    {
        var (config, dbPath) = NewLibrary("upload");
        using var database = BackupTestKit.OpenStore(dbPath);
        var maya = Guid.NewGuid();
        AddProfile(database, maya);
        var movie = AddMovie(database, "Dune", "hash-original", withIds: true);
        AddProgress(database, maya, movie.Asset, 42.5);
        var service = BackupTestKit.Service(database, config);
        var archive = await service.CreateAsync(CancellationToken.None);

        await using var stream = File.OpenRead(archive);
        var inspection = await service.UploadAndInspectAsync(stream, "mine.zip", new OnboardingRepository(database), CancellationToken.None);

        var contents = Assert.IsType<MediaEngine.Contracts.System.BackupContentsDto>(inspection.Contents);
        Assert.Equal(ProfileCount(database), contents.Profiles);
        Assert.Equal(1, contents.PersonalItems);
        Assert.Equal(1, contents.Titles);
        Assert.Equal(0, contents.FileOnlyTitles);
        Assert.Contains("profile", inspection.Warning, StringComparison.Ordinal);
    }

    // -- helpers -------------------------------------------------------------------------------------------

    private WebApplication BuildApplication(DatabaseConnection database, string config)
    {
        _disposables.Add(database);
        var loader = new ConfigurationDirectoryLoader(config);
        _disposables.Add(loader);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new PrincipalClaimResolver());
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AdministratorDecisions());
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator>(new DenyAllEvaluator());
        builder.Services.AddScoped<IAuthorizationHandler, AdministratorOrApplicationHandler>();
        builder.Services.AddSingleton<IDatabaseConnection>(database);
        builder.Services.AddSingleton<IConfigurationLoader>(loader);
        builder.Services.AddSingleton(new PersonalStateBackupRepository(database, BackupTestKit.Codec()));
        builder.Services.AddSingleton(new WaitingPersonalStateRepository(database));
        // The other system and backup routes are mapped too; their services only need to be known so they bind as services.
        builder.Services.AddSingleton<DataStoreVersionRepository>();
        builder.Services.AddSingleton<StartupReadinessService>(_ => null!);
        builder.Services.AddSingleton<IScheduledTaskRegistry>(_ => null!);
        builder.Services.AddSingleton<IScheduledTaskRunRepository>(_ => null!);
        builder.Services.AddSingleton<IMediaOperationRepository>(_ => null!);
        builder.Services.AddSingleton<IFileWatcher>(_ => null!);
        builder.Services.AddSingleton<AssetStoreCleanupService>(_ => null!);
        builder.Services.AddSingleton<DatabaseBackupService>(_ => null!);
        builder.Services.AddSingleton<SystemResourcesService>(_ => null!);
        builder.Services.AddSingleton<BackupSettingsManager>(_ => null!);
        builder.Services.AddSingleton<BackupOrchestrator>(_ => null!);
        var app = builder.Build();
        app.MapSystemEndpoints();
        app.MapBackupDestinationEndpoints();
        return app;
    }

    private (string Config, string DbPath) NewLibrary(string name)
    {
        var config = Path.Combine(_root, name + "-config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        return (config, Path.Combine(_root, name + ".db"));
    }

    private static void AddProfile(DatabaseConnection database, Guid id)
    {
        using var connection = database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@id,'Maya','#7C4DFF','Administrator',NULL,@now);",
            new { id, now = Now });
    }

    private static int ProfileCount(DatabaseConnection database)
    {
        using var connection = database.CreateConnection();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles;");
    }

    private static (Guid Work, Guid Asset) AddMovie(DatabaseConnection database, string title, string hash, bool withIds)
    {
        var (work, edition, asset) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var connection = database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state, ready_at) VALUES (@work, 'Movies', 'standalone', 'accepted', @now);
            INSERT INTO editions (id, work_id, format_label) VALUES (@edition, @work, '4K Remux');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id, is_orphaned)
                VALUES (@asset, @edition, @hash, '/mnt/movies/film.mkv', CURRENT_TIMESTAMP, 'movies', 0);
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES(@work,'title',@title,@now);
            INSERT INTO playback_inspection_cache(asset_id, source_hash, inspected_at, duration_secs) VALUES(@asset, @hash, @now, 9300.5);
            """,
            new { work, edition, asset, hash, title, now = Now });
        if (withIds)
        {
            connection.Execute(
                """
                INSERT INTO bridge_ids(id, entity_id, id_type, id_value) VALUES(@bridge1, @work, 'imdb_id', 'tt1160419');
                INSERT INTO bridge_ids(id, entity_id, id_type, id_value) VALUES(@bridge2, @work, 'tmdb_id', '438631');
                """,
                new { work, bridge1 = Guid.NewGuid(), bridge2 = Guid.NewGuid() });
        }

        return (work, asset);
    }

    private static void AddProgress(DatabaseConnection database, Guid profile, Guid asset, double progress)
    {
        using var connection = database.CreateConnection();
        connection.Execute(
            "INSERT INTO user_states(user_id, asset_id, progress_pct, last_accessed) VALUES(@profile,@asset,@progress,@now);",
            new { profile, asset, progress, now = Now });
    }

    private static void AddBookmark(DatabaseConnection database, Guid profile, (Guid Work, Guid Asset) movie, double position)
    {
        using var connection = database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO audiobook_bookmarks(id, profile_id, work_id, asset_id, position_seconds, duration_seconds, label, is_global, created_at)
                VALUES(@id,@profile,@work,@asset,@position,7200,'bookmark',0,@now);
            """,
            new { id = Guid.NewGuid(), profile, work = movie.Work, asset = movie.Asset, position, now = Now });
    }

    private static RouteEndpoint EndpointOf(WebApplication app, string route, string method) =>
        Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

    private static AdministratorOrApplicationRequirement RequirementOf(WebApplication app, string route, string method) =>
        Assert.Single(EndpointOf(app, route, method).Metadata.OfType<AuthorizationPolicy>()
            .SelectMany(policy => policy.Requirements)
            .OfType<AdministratorOrApplicationRequirement>());

    private static async Task<bool> PassesAsync(WebApplication app, string route, string method, string principal)
    {
        var endpoint = EndpointOf(app, route, method);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(PrincipalClaim, principal)], "test"));
        var context = new DefaultHttpContext { RequestServices = app.Services, User = user };
        var provider = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(
            provider,
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
        Assert.NotNull(policy);
        var result = await app.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, context, policy);
        return result.Succeeded;
    }

    private static async Task<(int Status, string Text)> SendAsync(WebApplication app, string method, string route, string? json = null)
    {
        var responseBody = new MemoryStream();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Request = { Method = method, Path = route },
            Response = { Body = responseBody },
        };
        if (json is not null)
        {
            context.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
        }

        await EndpointOf(app, route, method).RequestDelegate!(context);
        responseBody.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(responseBody).ReadToEndAsync());
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        TestTemp.ReleaseSqlitePoolsIn(_root);
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class PrincipalClaimResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Principals[context.User.FindFirstValue(PrincipalClaim)!]);
    }

    private sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class DenyAllEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }
}
