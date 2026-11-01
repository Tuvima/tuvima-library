using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Packs (Collections and lists spec D14 and 4e): the starter packs create only what the library supports, a set
/// exported as a pack file creates the same collections on another library, and removing a set takes its collections
/// and their lane placements with it. Runs the real routes against real data stores; only the caller's identity is faked.
/// </summary>
public sealed class CollectionPackEndpointTests : IDisposable
{
    private readonly List<TestLibrary> _libraries = [];

    private TestLibrary NewLibrary()
    {
        var library = new TestLibrary();
        library.Seed();
        _libraries.Add(library);
        return library;
    }

    [Fact]
    public async Task List_OffersOnlyTheDecadesGenresAwardsAndPeopleTheLibraryHasEnoughOf()
    {
        await using var app = await NewLibrary().StartAsync();

        var list = await GetAsync<CollectionPackListResponse>(app, "/collections/packs");

        Assert.Equal(["1980s", "1990s"], Names(list, "decades"));
        Assert.Equal(["Horror to watch"], Names(list, "genres"));
        Assert.Equal(["Oscar Best Picture winners"], Names(list, "award-winners"));
        Assert.Equal(["Directed by Dir One"], Names(list, "people-you-collect"));
        Assert.Equal(["Halloween"], Names(list, "seasonal"));
        Assert.Equal(["Short and sweet", "Unwatched classics"], Names(list, "pick-up-again").Order().ToList());
        Assert.Equal(2, list.Packs.Single(pack => pack.Id == "decades").WouldCreateCount);
        Assert.True(list.Packs.Single(pack => pack.Id == "pick-up-again").UsesPerProfileFields);
        Assert.Equal(3, list.Packs.Single(pack => pack.Id == "decades").Collections.Single(c => c.Name == "1980s").ItemCount);
    }

    [Fact]
    public async Task Create_Decades_MakesOneSetWithAChildForEachSupportedDecade()
    {
        var library = NewLibrary();
        await using var app = await library.StartAsync();

        var created = await CreateAsync(app, new CollectionPackCreateRequest { PackIds = ["decades"], OwnerKind = "Library", Audience = "Everyone" });

        var set = Assert.Single(created.Sets);
        Assert.Equal("Decades", set.Name);
        Assert.Equal(["1980s", "1990s"], created.Collections.Select(collection => collection.Name).Order().ToList());
        Assert.All(created.Collections, collection => Assert.Equal(set.Id, collection.SetId));
        using var connection = library.Database.CreateConnection();
        Assert.Equal("Set", connection.ExecuteScalar<string>("SELECT collection_type FROM collections WHERE id = @id", new { id = set.Id }));
        Assert.Equal(2, connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM collections WHERE parent_collection_id = @id AND collection_type = 'Custom' AND membership_mode = 'Smart' AND rule_json IS NOT NULL",
            new { id = set.Id }));
    }

    [Fact]
    public async Task Create_RunTwice_MakesNothingNewTheSecondTime()
    {
        await using var app = await NewLibrary().StartAsync();
        var request = new CollectionPackCreateRequest { PackIds = ["decades"], OwnerKind = "Library", Audience = "Everyone" };
        await CreateAsync(app, request);

        var again = await CreateAsync(app, request);

        Assert.Empty(again.Sets);
        Assert.Empty(again.Collections);
        Assert.Equal(2, again.Skipped.Count(skipped => skipped.Reason.StartsWith("You already have", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Create_AwardWinners_MakesOnlyTheAwardsTheLibraryHasEnoughOf()
    {
        await using var app = await NewLibrary().StartAsync();

        var created = await CreateAsync(app, new CollectionPackCreateRequest { PackIds = ["award-winners"], OwnerKind = "Library", Audience = "Everyone" });

        Assert.Equal(["Oscar Best Picture winners"], created.Collections.Select(collection => collection.Name).ToList());
    }

    [Fact]
    public async Task Create_SeveralPacksAtOnce_MakesEachPacksCollectionsAndPutsThemOnTheChosenLanes()
    {
        var library = NewLibrary();
        await using var app = await library.StartAsync();

        var created = await CreateAsync(app, new CollectionPackCreateRequest
        {
            PackIds = ["decades", "seasonal"],
            OwnerKind = "Library",
            Audience = "Everyone",
            ShowOn = ["home", "watch"],
        });

        Assert.Equal(["Decades", "Seasonal"], created.Sets.Select(set => set.Name).Order().ToList());
        var halloween = created.Collections.Single(collection => collection.Name == "Halloween");
        using var connection = library.Database.CreateConnection();
        Assert.Equal(["area:watch", "home"], connection.Query<string>(
            "SELECT location FROM collection_placements WHERE collection_id = @id ORDER BY location", new { id = halloween.Id }).ToList());
        var window = connection.QuerySingle<(string From, string To)>(
            "SELECT season_from AS [From], season_to AS [To] FROM collections WHERE id = @id", new { id = halloween.Id });
        Assert.Equal(("10-01", "11-01"), window);
    }

    [Fact]
    public async Task RemoveSet_RemovesTheChildrenAndTheirPlacements()
    {
        var library = NewLibrary();
        await using var app = await library.StartAsync();
        var created = await CreateAsync(app, new CollectionPackCreateRequest
        {
            PackIds = ["decades"],
            OwnerKind = "Library",
            Audience = "Everyone",
            ShowOn = ["home"],
        });
        var setId = created.Sets.Single().Id;
        using var connection = library.Database.CreateConnection();
        Assert.Equal(2, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM collection_placements"));

        using var response = await app.Client.DeleteAsync($"/collections/{setId}/set");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<CollectionSetRemovedResponse>())!.RemovedCollections);
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM collections WHERE is_enabled = 1 AND (id = @id OR parent_collection_id = @id)", new { id = setId }));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM collection_placements"));
    }

    [Fact]
    public async Task RemoveSet_OnACollectionThatIsNotASet_IsRefused()
    {
        var library = NewLibrary();
        await using var app = await library.StartAsync();
        var created = await CreateAsync(app, new CollectionPackCreateRequest { PackIds = ["decades"], OwnerKind = "Library", Audience = "Everyone" });

        using var response = await app.Client.DeleteAsync($"/collections/{created.Collections[0].Id}/set");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PackFile_ExportedFromASet_ImportsOnASecondLibraryAndMakesTheSameCollections()
    {
        var first = NewLibrary();
        string fileText;
        List<string> firstNames;
        await using (var app = await first.StartAsync())
        {
            var created = await CreateAsync(app, new CollectionPackCreateRequest
            {
                PackIds = ["pick-up-again", "people-you-collect"],
                OwnerKind = "Library",
                Audience = "Everyone",
            });
            var setId = created.Sets.Single(set => set.Name == "Pick up again").Id;
            var export = await GetAsync<CollectionPackExportResponse>(app, $"/collections/{setId}/pack-file");
            Assert.Empty(export.Omitted);
            Assert.Equal(CollectionPackFileDto.FormatName, export.File.Format);
            fileText = System.Text.Json.JsonSerializer.Serialize(export.File);
            firstNames = created.Collections.Where(collection => collection.SetId == setId).Select(collection => collection.Name).Order().ToList();
        }

        var second = NewLibrary();
        await using var secondApp = await second.StartAsync();
        var imported = await CreateAsync(secondApp, new CollectionPackCreateRequest { FileContent = fileText, OwnerKind = "Library", Audience = "Everyone" });

        Assert.Equal(["Pick up again"], imported.Sets.Select(set => set.Name).ToList());
        Assert.Equal(firstNames, imported.Collections.Select(collection => collection.Name).Order().ToList());
        Assert.Equal(2, firstNames.Count);
        Assert.Empty(imported.Skipped);
    }

    [Fact]
    public async Task PackFile_Preview_NamesTheCollectionsWithTheirCountsOnThisLibrary()
    {
        await using var app = await NewLibrary().StartAsync();
        var file = new CollectionPackFileDto
        {
            Name = "Mine",
            Collections =
            [
                FileEntry("Eighties", "decade", "eq", "1980s"),
                FileEntry("Nineties", "decade", "eq", "1990s"),
                FileEntry("Fifties", "decade", "eq", "1950s"),
            ],
        };

        using var response = await app.Client.PostAsJsonAsync("/collections/packs/preview-file", new CollectionPackFilePreviewRequest
        {
            Content = System.Text.Json.JsonSerializer.Serialize(file),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<CollectionPackSummaryDto>())!;
        Assert.Equal(2, preview.WouldCreateCount);
        Assert.Equal([("Eighties", 3), ("Nineties", 4)], preview.Collections.Select(entry => (entry.Name, entry.ItemCount)).ToList());
    }

    [Fact]
    public async Task PackFile_ThatIsNotAPack_IsRefusedWithAPlainSentenceAndCreatesNothing()
    {
        var library = NewLibrary();
        await using var app = await library.StartAsync();

        using var response = await app.Client.PostAsJsonAsync("/collections/packs", new CollectionPackCreateRequest
        {
            FileContent = """{"hello":"world"}""",
            OwnerKind = "Library",
            Audience = "Everyone",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Couldn't read this file", await DetailAsync(response), StringComparison.Ordinal);
        using var connection = library.Database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM collections"));
    }

    [Theory]
    [InlineData("{\"format\":\"tuvima-collection-pack\",\"version\":99,\"name\":\"x\",\"collections\":[{\"name\":\"a\"}]}", "newer version")]
    [InlineData("{\"format\":\"tuvima-collection-pack\",\"version\":1,\"name\":\"x\",\"collections\":[]}", "no collections")]
    [InlineData("{\"format\":\"tuvima-collection-pack\",\"version\":1,\"name\":\"x\",\"collections\":[{\"name\":\"a\",\"rule_definition\":{\"version\":1,\"groups\":[{\"conditions\":[{\"field\":\"genre\",\"op\":\"DROP TABLE\",\"value\":\"x\"}]}]}}]}", "field or condition Library does not know")]
    [InlineData("{\"format\":\"tuvima-collection-pack\",\"version\":1,\"name\":\"x\",\"collections\":[{\"name\":\"a\",\"rule_definition\":{\"version\":1,\"groups\":[{\"conditions\":[{\"field\":\"genre\",\"op\":\"eq\"}]}]}}]}", "no value")]
    [InlineData("{\"format\":\"tuvima-collection-pack\",\"version\":1,\"name\":\"x\",\"collections\":[{\"name\":\"a\",\"season_from\":\"13-40\",\"season_to\":\"11-01\",\"rule_definition\":{\"version\":1,\"groups\":[{\"conditions\":[{\"field\":\"genre\",\"op\":\"eq\",\"value\":\"x\"}]}]}}]}", "seasonal window")]
    public async Task PackFile_ThatBreaksTheFormat_IsRefusedBeforeAnythingIsCreated(string content, string expected)
    {
        var library = NewLibrary();
        await using var app = await library.StartAsync();

        using var response = await app.Client.PostAsJsonAsync("/collections/packs/preview-file", new CollectionPackFilePreviewRequest { Content = content });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await DetailAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ForAMusicOnlyFlow_LeavesOutPacksThatNeedFilmsAndBooks()
    {
        await using var app = await NewLibrary().StartAsync();

        var list = await GetAsync<CollectionPackListResponse>(app, "/collections/packs?media_types=Music");

        Assert.All(list.Packs, pack => Assert.Equal(0, pack.WouldCreateCount));
    }

    [Fact]
    public async Task Create_WithNoPackChosen_IsRefused()
    {
        await using var app = await NewLibrary().StartAsync();

        using var response = await app.Client.PostAsJsonAsync("/collections/packs", new CollectionPackCreateRequest { OwnerKind = "Library", Audience = "Everyone" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_OfAProfilesOwnPack_StaysPrivateToThatProfile()
    {
        var library = NewLibrary();
        await using var app = await library.StartAsync();

        var created = await CreateAsync(app, new CollectionPackCreateRequest { PackIds = ["decades"], OwnerKind = "Profile", Audience = "Private" });

        using var connection = library.Database.CreateConnection();
        Assert.Equal(3, connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM collections WHERE owner_kind = 'Profile' AND audience = 'Private' AND profile_id = @profile",
            new { profile = library.Profile }));
        Assert.Equal(2, created.Collections.Count);
    }

    // An error answer is a problem document; the sentence the person reads is its detail.
    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return problem.GetProperty("detail").GetString() ?? string.Empty;
    }

    private static CollectionPackFileEntryDto FileEntry(string name, string field, string op, string value) => new()
    {
        Name = name,
        RuleDefinition = new CollectionRuleDefinitionDto
        {
            Groups = [new CollectionRuleGroupDto { Conditions = [new CollectionRulePredicateDto { Field = field, Op = op, Value = value }] }],
        },
    };

    private static List<string> Names(CollectionPackListResponse list, string packId) =>
        list.Packs.Single(pack => pack.Id == packId).Collections.Select(collection => collection.Name).ToList();

    private static async Task<T> GetAsync<T>(TestApplication app, string path)
    {
        using var response = await app.Client.GetAsync(path);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<CollectionPackCreateResponse> CreateAsync(TestApplication app, CollectionPackCreateRequest request)
    {
        using var response = await app.Client.PostAsJsonAsync("/collections/packs", request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Create answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<CollectionPackCreateResponse>())!;
    }

    public void Dispose()
    {
        foreach (var library in _libraries)
        {
            library.Dispose();
        }
    }

    /// <summary>One small generated library: nine films with years, genres, a director, an award, ratings and runtimes.</summary>
    private sealed class TestLibrary : IDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_packs_{Guid.NewGuid():N}.db");
        private readonly Guid _libraryId = Guid.NewGuid();
        private readonly Guid _account = Guid.NewGuid();

        public TestLibrary()
        {
            DapperConfiguration.Configure();
            Database = new DatabaseConnection(_databasePath);
            Database.InitializeSchema();
            Database.RunStartupChecks();
            ReadyWorks.TreatEveryWorkAsReady(Database);
            using var connection = Database.CreateConnection();
            connection.Execute(
                "INSERT INTO profiles (id, display_name, avatar_color, role, created_at) VALUES (@id, 'Alice', '#7C4DFF', 'Administrator', CURRENT_TIMESTAMP);",
                new { id = Profile });
        }

        public DatabaseConnection Database { get; }

        public Guid Profile { get; } = Guid.NewGuid();

        /// <summary>
        /// 1980s: A1 to A3 (rated 8.0, A1 and A2 horror). 1990s: B1 to B4 (B1 to B3 directed by Dir One, won Best Picture, 95 minutes
        /// long; B1 horror). 2000s: C1 and C2 (C1 horror; too few for a decade). Two Hugo winners (too few for an award).
        /// </summary>
        public void Seed()
        {
            Work("A1", 1981, rating: "8.0", genres: ["Horror"]);
            Work("A2", 1982, rating: "8.0", genres: ["Horror"]);
            Work("A3", 1983, rating: "8.0", genres: ["Comedy"]);
            Work("B1", 1991, runtime: "95", genres: ["Horror"], director: true, bestPicture: true);
            Work("B2", 1992, runtime: "95", director: true, bestPicture: true, hugo: true);
            Work("B3", 1993, runtime: "95", director: true, bestPicture: true, hugo: true);
            Work("B4", 1994, genres: ["Comedy"]);
            Work("C1", 2001, genres: ["Horror"]);
            Work("C2", 2002);
        }

        private Guid Work(
            string title,
            int year,
            string? rating = null,
            string? runtime = null,
            string[]? genres = null,
            bool director = false,
            bool bestPicture = false,
            bool hugo = false)
        {
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            using var connection = Database.CreateConnection();
            connection.Execute("""
                INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Movies', 'standalone', 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @library);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'year', @year, CURRENT_TIMESTAMP);
                """, new
            {
                workId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{assetId:N}.mkv",
                library = _libraryId.ToString("D"),
                title,
                year = year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            if (rating is not null)
            {
                Value(connection, workId, "rating", rating);
            }

            if (runtime is not null)
            {
                Value(connection, workId, "runtime", runtime);
            }

            foreach (var genre in genres ?? [])
            {
                Array(connection, workId, "genre", genre, null);
            }

            if (director)
            {
                Array(connection, workId, "director", "Dir One", "Q111");
            }

            if (bestPicture)
            {
                Array(connection, workId, "award_received", "Academy Award for Best Picture", "Q900");
            }

            if (hugo)
            {
                Array(connection, workId, "award_received", "Hugo Award for Best Novel", "Q901");
            }

            return workId;
        }

        private static void Value(System.Data.IDbConnection connection, Guid entity, string key, string value) =>
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entity, @key, @value, CURRENT_TIMESTAMP);",
                new { entity, key, value });

        private static void Array(System.Data.IDbConnection connection, Guid entity, string key, string value, string? qid) =>
            connection.Execute(
                "INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid) VALUES (@entity, @key, (SELECT COUNT(*) FROM canonical_value_arrays WHERE entity_id = @entity AND key = @key), @value, @qid);",
                new { entity, key, value, qid });

        public async Task<TestApplication> StartAsync()
        {
            var authority = new RequestAuthority(
                PrincipalKind.Human,
                true,
                AccountId: _account,
                ActiveProfileId: Profile,
                SessionId: Guid.NewGuid(),
                DeviceId: Guid.NewGuid(),
                AccountEnabled: true,
                ActiveProfileInUserHousehold: true,
                AccountAuthorizationVersion: 1,
                ProfileAuthorizationVersion: 1,
                ApplicationAuthorizationVersion: 1);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAuthentication("test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
            builder.Services.AddAuthorization(options =>
                options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
            builder.Services.AddSingleton<IAuthorizationHandler, AllowAdministratorOrApplicationHandler>();
            builder.Services.AddLogging();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddHttpClient();
            builder.Services.AddMemoryCache();
            builder.Services.AddSingleton<IDatabaseConnection>(Database);
            builder.Services.AddTuvimaStorage();
            builder.Services.AddTuvimaDisplay();
            builder.Services.AddSingleton<IVaultLibraryRepository>(new FixedVaultLibraries());
            builder.Services.AddSingleton<IHouseholdLibraryAccessRepository, TestLibraryVisibility.NoHouseholdLibraries>();
            builder.Services.AddSingleton<LibraryVisibilityRule>();
            builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
            builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(_libraryId));
            builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
            builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
            builder.Services.AddSingleton<IArtworkPaletteService, MediaEngine.Providers.Services.ArtworkPaletteService>();
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
            try { Database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
            try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
        }
    }

    private sealed class AllowAdministratorOrApplicationHandler : AuthorizationHandler<AdministratorOrApplicationRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            AdministratorOrApplicationRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
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
            ValueTask.FromResult(AuthorizationDecision.Allow());
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
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "pack-test")], Scheme.Name)),
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
