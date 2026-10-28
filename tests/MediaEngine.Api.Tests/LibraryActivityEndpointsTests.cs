using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
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
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

// Library pages D16, WP7: a library's Activity tab lists only that library's own events, in plain sentences, and never answers for another
// library, a Vault library, an unknown id or a profile that cannot see the library. Real temp SQLite and a real configuration folder.
public sealed class LibraryActivityEndpointsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_library_activity_{Guid.NewGuid():N}");
    private readonly Guid _films = Guid.NewGuid();
    private readonly Guid _shows = Guid.NewGuid();
    private readonly Guid _family = Guid.NewGuid();
    private readonly Guid _vault = Guid.NewGuid();
    private readonly Guid _filmsSource = Guid.NewGuid();
    private readonly Guid _showsSource = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
    private DatabaseConnection _database = default!;
    private WebApplication _app = default!;
    private HttpClient _client = default!;

    private string FilmsPath => Path.Combine(_root, "media", "films");
    private string ShowsPath => Path.Combine(_root, "media", "shows");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        var configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "libraries.json"), JsonSerializer.Serialize(new LibrariesConfiguration
        {
            Libraries =
            [
                Library(_films, "Films", LibraryAccessLevels.Standard, "Movies", _filmsSource, FilmsPath),
                Library(_shows, "Shows", LibraryAccessLevels.Standard, "TV", _showsSource, ShowsPath),
                Library(_family, "Family", LibraryAccessLevels.Selected, "Videos", Guid.NewGuid(), Path.Combine(_root, "media", "family")),
                Library(_vault, "Private", LibraryAccessLevels.Standard, "Videos", Guid.NewGuid(), Path.Combine(_root, "media", "private")),
            ],
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = Path.Combine(_root, "media"), AllowWrite = true }],
        }));
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',@now,@now,@now);",
                new { vault = _vault.ToString("D"), now = _now.ToString("O") });
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Administrator, policy => policy.RequireClaim("administrator", "1"));
        });
        var vaultLibraries = new VaultLibraryRepository(_database);
        builder.Services.AddSingleton<IConfigurationLoader>(configuration);
        builder.Services.AddSingleton<IVaultLibraryRepository>(vaultLibraries);
        builder.Services.AddSingleton<ILibraryActivityReadRepository>(new LibraryActivityReadRepository(_database));
        builder.Services.AddSingleton<LibraryActivityReadService>();
        builder.Services.AddSingleton(new LibraryVisibilityRule(
            new ProfileAccessRepository(_database), vaultLibraries, new TestLibraryVisibility.NoLibraryProfileGrants(), new FixedLibraryAccessCatalog(_family)));
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver());
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapLibraryActivity();
        await _app.StartAsync();
        var address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _client = new HttpClient { BaseAddress = address };
        _client.DefaultRequestHeaders.Add("X-Test-Administrator", "1");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "library.db")}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheActivityRoute_IsAdministratorOnly()
    {
        var route = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText!.EndsWith("/activity", StringComparison.Ordinal));

        Assert.Equal("/library/libraries/{libraryId:guid}/activity", route.RoutePattern.RawText);
        Assert.Contains(route.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == AuthPolicies.Administrator);
    }

    [Fact]
    public async Task ANonAdministrator_IsRefused()
    {
        using var other = new HttpClient { BaseAddress = _client.BaseAddress };

        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/library/libraries/{_films:D}/activity")).StatusCode);
    }

    [Fact]
    public async Task ALibrarysActivity_ListsOnlyItsOwnEvents_InPlainSentences()
    {
        var (filmWork, filmAsset) = InsertAsset(_films, Path.Combine(FilmsPath, "Dune.mkv"));
        var (showWork, showAsset) = InsertAsset(_shows, Path.Combine(ShowsPath, "Severance.s01e01.mkv"));
        await Log(SystemActionType.MediaAdded, "Dune", filmWork, """{"media_type":"Movies"}""");
        await Log(SystemActionType.MediaAdded, "Severance", showWork, """{"media_type":"TV"}""");
        await Log(SystemActionType.ReviewItemCreated, "Arrival", filmAsset);
        await Log(SystemActionType.ReviewItemCreated, "Andor", showAsset);
        await Log(SystemActionType.MediaFailed, "broken.mkv", null, JsonSerializer.Serialize(new { source_path = Path.Combine(FilmsPath, "broken.mkv"), source_file = "broken.mkv" }), "Something went wrong while reading this file. The technical log has the details.");
        await Log(SystemActionType.MediaFailed, "other.mkv", null, JsonSerializer.Serialize(new { source_path = Path.Combine(ShowsPath, "other.mkv"), source_file = "other.mkv" }), "Something went wrong while reading this file. The technical log has the details.");
        await Log(SystemActionType.ServerStarted, null, null, null);
        InsertFolderCheck(_films, _filmsSource, FilmsPath, tried: 126, unmatched: 3);
        InsertFolderCheck(_shows, _showsSource, ShowsPath, tried: 10, unmatched: 0);

        var result = await _client.GetFromJsonAsync<LibraryActivityDto>($"/library/libraries/{_films:D}/activity");

        Assert.Equal(_films, result!.LibraryId);
        var texts = result.Entries.Select(entry => entry.Text).ToList();
        Assert.Contains("Added Dune.", texts);
        Assert.Contains("Arrival needs review.", texts);
        Assert.Contains(texts, text => text.StartsWith("Couldn't take in broken.mkv", StringComparison.Ordinal));
        Assert.Contains("Checked films: 126 files looked at, 3 not matched.", texts);
        Assert.Equal(4, texts.Count);
        Assert.DoesNotContain(texts, text => text.Contains("Severance", StringComparison.Ordinal)
            || text.Contains("Andor", StringComparison.Ordinal)
            || text.Contains("other.mkv", StringComparison.Ordinal)
            || text.Contains("shows", StringComparison.Ordinal));

        var review = Assert.Single(result.Entries, entry => entry.Kind == LibraryActivityKinds.Review);
        Assert.True(review.NeedsReview);
        Assert.Equal("/settings/review", review.Route);
        Assert.All(result.Entries.Where(entry => entry.Kind != LibraryActivityKinds.Review), entry => Assert.False(entry.NeedsReview));
    }

    [Fact]
    public async Task ARenameJobAndAFolderMove_AreListedForTheirOwnLibraryOnly()
    {
        var operation = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            foreach (var (outcome, file) in new[] { ("moved", "a"), ("moved", "b"), ("failed", "c") })
            {
                connection.Execute(
                    "INSERT INTO folder_organise_log(operation_id, source_id, old_path, new_path, outcome, at) VALUES(@operation, @source, @old, '', @outcome, @at);",
                    new { operation, source = _filmsSource, old = file, outcome, at = _now.ToString("O") });
            }
        }

        await Log(SystemActionType.FolderKindChanged, "films", _filmsSource, JsonSerializer.Serialize(new { to_library_id = _films.ToString("D") }), "films changed from Other videos to Movies: 4 files");
        await Log(SystemActionType.FolderKindChanged, "shows", _showsSource, JsonSerializer.Serialize(new { to_library_id = _shows.ToString("D") }), "shows changed from Other videos to TV: 9 files");

        var films = await _client.GetFromJsonAsync<LibraryActivityDto>($"/library/libraries/{_films:D}/activity");
        var shows = await _client.GetFromJsonAsync<LibraryActivityDto>($"/library/libraries/{_shows:D}/activity");

        Assert.Contains(films!.Entries, entry => entry.Kind == LibraryActivityKinds.Conversion && entry.Text == "Renamed or moved 2 files in films. 1 file could not be moved.");
        Assert.Contains(films.Entries, entry => entry.Kind == LibraryActivityKinds.Move && entry.Text == "films changed from Other videos to Movies: 4 files.");
        Assert.DoesNotContain(films.Entries, entry => entry.Text.Contains("shows", StringComparison.Ordinal));
        Assert.Equal(["shows changed from Other videos to TV: 9 files."], shows!.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public async Task ALibraryWithNoEvents_ReturnsAnEmptyList()
    {
        var response = await _client.GetAsync($"/library/libraries/{_films:D}/activity");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<LibraryActivityDto>();

        Assert.Empty(result!.Entries);
    }

    [Fact]
    public async Task AVaultLibrary_AnsweredAsNotFound_AndItsTitlesNeverAppearElsewhere()
    {
        var (vaultWork, _) = InsertAsset(_vault, Path.Combine(_root, "media", "private", "secret.mkv"));
        // Written straight into the table: the normal writer already refuses Vault rows, and this proves the read does not depend on that.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO system_activity(occurred_at, action_type, collection_name, entity_id, entity_type, changes_json, detail) VALUES(@at, 'MediaAdded', 'Secret title', @work, 'Work', '{}', 'Added');",
                new { at = _now.ToString("O"), work = vaultWork });
        }

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/library/libraries/{_vault:D}/activity")).StatusCode);
        foreach (var library in new[] { _films, _shows })
        {
            var body = await _client.GetStringAsync($"/library/libraries/{library:D}/activity");
            Assert.DoesNotContain("Secret title", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnUnknownLibrary_IsNotFound()
    {
        var response = await _client.GetAsync($"/library/libraries/{Guid.NewGuid():D}/activity");
        Assert.True(response.StatusCode == HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ASelectedLibrary_IsNotFoundForAProfileThatWasNotChosen()
    {
        var (work, _) = InsertAsset(_family, Path.Combine(_root, "media", "family", "party.mkv"));
        await Log(SystemActionType.MediaAdded, "Birthday party", work, """{"media_type":"Movies"}""");

        var response = await _client.GetAsync($"/library/libraries/{_family:D}/activity");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Birthday party", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(10, 10)]
    [InlineData(9999, 200)]
    public void TheNumberOfLinesAsked_IsKeptInRange(int? asked, int expected) =>
        Assert.Equal(expected, LibraryActivityReadService.ClampTake(asked));

    [Fact]
    public async Task TakeLimitsTheLines_NewestFirst()
    {
        for (var index = 0; index < 5; index++)
        {
            var (work, _) = InsertAsset(_films, Path.Combine(FilmsPath, $"film{index}.mkv"));
            await Log(SystemActionType.MediaAdded, $"Film {index}", work, """{"media_type":"Movies"}""", at: _now.AddMinutes(index));
        }

        var result = await _client.GetFromJsonAsync<LibraryActivityDto>($"/library/libraries/{_films:D}/activity?take=2");

        Assert.Equal(["Added Film 4.", "Added Film 3."], result!.Entries.Select(entry => entry.Text));
    }

    private static LibraryFolderConfig Library(Guid id, string name, string access, string mediaType, Guid sourceId, string path) => new()
    {
        Id = id.ToString("D"),
        Name = name,
        Category = mediaType,
        Kind = LibraryKinds.Catalogued,
        KindKey = mediaType switch { "TV" => "tv", "Videos" => "other_videos", _ => "movies" },
        Area = "watch",
        MediaTypes = [mediaType],
        Access = access,
        Sources = [new LibrarySourceConfig { Id = sourceId.ToString("D"), Path = path }],
    };

    private (Guid Work, Guid Asset) InsertAsset(Guid library, string path)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@work, 'Movies', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP, @library);
            """,
            new { work, edition, asset, path, library = library.ToString("D"), hash = "hash-" + asset.ToString("N") });
        return (work, asset);
    }

    private void InsertFolderCheck(Guid library, Guid source, string path, int tried, int unmatched)
    {
        var prefix = FolderFirstCheckPaths.Prefix(path);
        var folded = FolderFirstCheckPaths.Folded(prefix);
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO folder_first_checks(source_id, library_id, path, path_prefix, path_prefix_folded, path_range_end_folded, state, tried, unmatched, started_at, ended_at)
            VALUES (@source, @library, @path, @prefix, @folded, @end, 'passed', @tried, @unmatched, @at, @at);
            """,
            new
            {
                source = source.ToString("D"),
                library = library.ToString("D"),
                path,
                prefix,
                folded,
                end = FolderFirstCheckPaths.RangeEnd(folded),
                tried,
                unmatched,
                at = _now.ToString("O"),
            });
    }

    private async Task Log(string kind, string? name, Guid? entity, string? changes = null, string? detail = null, DateTimeOffset? at = null) =>
        await new SystemActivityRepository(_database).LogAsync(new SystemActivityEntry
        {
            ActionType = kind,
            OccurredAt = at ?? _now,
            CollectionName = name,
            EntityId = entity,
            ChangesJson = changes,
            Detail = detail,
        });

    private sealed class FixedAuthorityResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(new RequestAuthority(PrincipalKind.Human, true, Guid.NewGuid(), MediaEngine.Domain.Aggregates.Profile.SeedProfileId, AccountEnabled: true));
    }

    // Everyone is signed in; only a request that sends the test header is an administrator.
    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, "test") };
            if (Request.Headers.ContainsKey("X-Test-Administrator"))
            {
                claims.Add(new Claim("administrator", "1"));
            }

            var identity = new ClaimsIdentity(claims, "test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
        }
    }
}
