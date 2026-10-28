using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
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

// Library pages D9, WP6: the administrator's read and save of the profiles chosen for a Selected-profiles library, and what the libraries
// file may do with a library's access value. Real temp SQLite and a real configuration folder.
public sealed class LibraryAccessEndpointsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_library_access_{Guid.NewGuid():N}");
    private readonly Guid _selectedLibrary = Guid.NewGuid();
    private readonly Guid _standardLibrary = Guid.NewGuid();
    private readonly Guid _vaultLibrary = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
    private DatabaseConnection _database = default!;
    private ConfigurationDirectoryLoader _configuration = default!;
    private WebApplication _app = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "libraries.json"), System.Text.Json.JsonSerializer.Serialize(new LibrariesConfiguration
        {
            Libraries =
            [
                Library(_selectedLibrary, "Family films", LibraryAccessLevels.Selected),
                Library(_standardLibrary, "Everyone's shows", LibraryAccessLevels.Standard, "tv", "TV"),
                Library(_vaultLibrary, "Vault", LibraryAccessLevels.Standard, "other_videos", "Videos"),
            ],
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = Path.Combine(_root, "library"), AllowWrite = true }],
        }));
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',@now,@now,@now);",
                new { vault = _vaultLibrary.ToString("D"), now = _now.ToString("O") });
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Administrator, policy => policy.RequireClaim("administrator", "1"));
        });
        builder.Services.AddSingleton<IConfigurationLoader>(_configuration);
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IVaultLibraryRepository>(new VaultLibraryRepository(_database));
        builder.Services.AddSingleton<ILibraryProfileGrantRepository>(new LibraryProfileGrantRepository(_database));
        builder.Services.AddSingleton<IProfileRepository>(new ProfileRepository(_database));
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver());
        builder.Services.AddSingleton(TimeProvider.System);
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGroup("/settings").MapLibraryAccess();
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
    public void BothAccessRoutes_AreAdministratorOnly()
    {
        var routes = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.Contains("/access", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, routes.Count);
        Assert.All(routes, route => Assert.Contains(
            route.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == AuthPolicies.Administrator));
    }

    [Fact]
    public async Task ANonAdministrator_CannotReadOrSaveTheChosenProfiles()
    {
        using var other = new HttpClient { BaseAddress = _client.BaseAddress };
        var profile = await CreateProfileAsync("someone");

        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/settings/libraries/{_selectedLibrary:D}/access")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync(
            $"/settings/libraries/{_selectedLibrary:D}/access/profiles", new SetLibraryProfileGrantsRequest([profile]))).StatusCode);
        Assert.Empty(await new LibraryProfileGrantRepository(_database).GetGrantedProfilesAsync(_selectedLibrary));
    }

    [Fact]
    public async Task ASelectedLibrary_StartsWithNobodyChosen_AndTheAdministratorCanChooseProfiles()
    {
        var first = await CreateProfileAsync("first");
        var second = await CreateProfileAsync("second");

        var before = await _client.GetFromJsonAsync<LibraryAccessGrantsDto>($"/settings/libraries/{_selectedLibrary:D}/access");
        Assert.Equal(LibraryAccessLevels.Selected, before!.Access);
        Assert.Empty(before.ProfileIds);

        var saved = await _client.PutAsJsonAsync(
            $"/settings/libraries/{_selectedLibrary:D}/access/profiles", new SetLibraryProfileGrantsRequest([first]));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal([first], (await saved.Content.ReadFromJsonAsync<LibraryAccessGrantsDto>())!.ProfileIds);

        await _client.PutAsJsonAsync($"/settings/libraries/{_selectedLibrary:D}/access/profiles", new SetLibraryProfileGrantsRequest([second]));
        var after = await _client.GetFromJsonAsync<LibraryAccessGrantsDto>($"/settings/libraries/{_selectedLibrary:D}/access");
        Assert.Equal([second], after!.ProfileIds);
    }

    [Fact]
    public async Task ChoosingProfiles_RaisesTheAuthorizationVersionOfOnlyTheProfilesThatChanged()
    {
        var gained = await CreateProfileAsync("gained");
        var untouched = await CreateProfileAsync("untouched");
        var gainedBefore = await VersionAsync(gained);
        var untouchedBefore = await VersionAsync(untouched);

        await _client.PutAsJsonAsync($"/settings/libraries/{_selectedLibrary:D}/access/profiles", new SetLibraryProfileGrantsRequest([gained]));

        Assert.Equal(gainedBefore + 1, await VersionAsync(gained));
        Assert.Equal(untouchedBefore, await VersionAsync(untouched));
    }

    [Fact]
    public async Task ChoosingProfiles_IsRefused_ForAStandardLibrary_AnUnknownProfile_AnUnknownLibrary()
    {
        var profile = await CreateProfileAsync("someone");

        var standard = await _client.PutAsJsonAsync(
            $"/settings/libraries/{_standardLibrary:D}/access/profiles", new SetLibraryProfileGrantsRequest([profile]));
        Assert.Equal(HttpStatusCode.Conflict, standard.StatusCode);

        var unknownProfile = await _client.PutAsJsonAsync(
            $"/settings/libraries/{_selectedLibrary:D}/access/profiles", new SetLibraryProfileGrantsRequest([Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.BadRequest, unknownProfile.StatusCode);

        var unknownLibrary = await _client.PutAsJsonAsync(
            $"/settings/libraries/{Guid.NewGuid():D}/access/profiles", new SetLibraryProfileGrantsRequest([profile]));
        Assert.Equal(HttpStatusCode.NotFound, unknownLibrary.StatusCode);
        Assert.Empty(await new LibraryProfileGrantRepository(_database).GetGrantedProfilesAsync(_selectedLibrary));
    }

    [Fact]
    public async Task AVaultLibrary_IsNeverListedOrAcceptedAsAGrant()
    {
        var profile = await CreateProfileAsync("someone");

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/settings/libraries/{_vaultLibrary:D}/access")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync(
            $"/settings/libraries/{_vaultLibrary:D}/access/profiles", new SetLibraryProfileGrantsRequest([profile]))).StatusCode);
        Assert.Empty(await new LibraryProfileGrantRepository(_database).GetGrantedProfilesAsync(_vaultLibrary));
    }

    [Fact]
    public async Task AStandardLibrary_ReportsNoChosenProfiles()
    {
        var result = await _client.GetFromJsonAsync<LibraryAccessGrantsDto>($"/settings/libraries/{_standardLibrary:D}/access");

        Assert.Equal(LibraryAccessLevels.Standard, result!.Access);
        Assert.Empty(result.ProfileIds);
    }

    [Theory]
    [InlineData(LibraryAccessLevels.Standard, LibraryAccessLevels.Selected, true)]
    [InlineData(LibraryAccessLevels.Selected, LibraryAccessLevels.Standard, true)]
    [InlineData(LibraryAccessLevels.Standard, LibraryAccessLevels.Vault, false)]
    [InlineData(LibraryAccessLevels.Selected, LibraryAccessLevels.Vault, false)]
    [InlineData(LibraryAccessLevels.Vault, LibraryAccessLevels.Standard, false)]
    [InlineData(LibraryAccessLevels.Vault, LibraryAccessLevels.Selected, false)]
    [InlineData(LibraryAccessLevels.Standard, LibraryAccessLevels.Standard, true)]
    public void TheLibrariesFile_MaySwitchBetweenStandardAndSelected_ButNeverIntoOrOutOfVault(string from, string to, bool allowed)
    {
        var id = Guid.NewGuid();
        var current = new LibrariesConfiguration { Libraries = [Library(id, "Films", from)] };
        var proposed = new LibrariesConfiguration { Libraries = [Library(id, "Films", to)] };

        var error = LibraryAccessChangeService.Validate(current, proposed, new HashSet<string>());

        Assert.Equal(allowed, error is null);
    }

    [Fact]
    public void ANewLibraryCannotBeCreatedAsVaultProtectedThroughTheLibrariesFile()
    {
        var current = new LibrariesConfiguration();
        var proposed = new LibrariesConfiguration { Libraries = [Library(Guid.NewGuid(), "Sneaky", LibraryAccessLevels.Vault)] };

        Assert.NotNull(LibraryAccessChangeService.Validate(current, proposed, new HashSet<string>()));
    }

    [Fact]
    public async Task SwitchingALibraryToSelected_OrBackToStandard_ClearsWhoWasChosen()
    {
        var profile = await CreateProfileAsync("someone");
        var grants = new LibraryProfileGrantRepository(_database);
        var service = new LibraryAccessChangeService(grants);
        await grants.ReplaceGrantedProfilesAsync(_selectedLibrary, new HashSet<Guid> { profile }, _now, null);
        var current = _configuration.LoadLibraries();

        // An unrelated edit keeps the choice.
        var renamed = _configuration.LoadLibraries();
        renamed.Libraries.First(library => library.Id == _selectedLibrary.ToString("D")).Name = "Renamed";
        await service.ClearChangedAsync(current, renamed);
        Assert.Equal([profile], (await grants.GetGrantedProfilesAsync(_selectedLibrary)).ToArray());

        // Switching to Standard and later back to Selected starts again with nobody.
        var standard = _configuration.LoadLibraries();
        standard.Libraries.First(library => library.Id == _selectedLibrary.ToString("D")).Access = LibraryAccessLevels.Standard;
        await service.ClearChangedAsync(current, standard);
        Assert.Empty(await grants.GetGrantedProfilesAsync(_selectedLibrary));
    }

    [Fact]
    public async Task ARemovedLibrary_ClearsItsChosenProfiles()
    {
        var profile = await CreateProfileAsync("someone");
        var grants = new LibraryProfileGrantRepository(_database);
        await grants.ReplaceGrantedProfilesAsync(_selectedLibrary, new HashSet<Guid> { profile }, _now, null);
        var current = _configuration.LoadLibraries();
        var proposed = new LibrariesConfiguration { Libraries = [.. current.Libraries.Where(library => library.Id != _selectedLibrary.ToString("D"))] };

        await new LibraryAccessChangeService(grants).ClearChangedAsync(current, proposed);

        Assert.Empty(await grants.GetGrantedProfilesAsync(_selectedLibrary));
    }

    private static LibraryFolderConfig Library(Guid id, string name, string access, string kindKey = "movies", string mediaType = "Movies") => new()
    {
        Id = id.ToString("D"),
        Name = name,
        Category = mediaType,
        Kind = LibraryKinds.Catalogued,
        KindKey = kindKey,
        Area = "watch",
        MediaTypes = [mediaType],
        Access = access,
    };

    private async Task<Guid> CreateProfileAsync(string name)
    {
        var profile = new MediaEngine.Domain.Aggregates.Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            Role = ProfileRole.StandardUser,
            CreatedAt = _now,
        };
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = $"{name}@example.test",
            NormalizedEmail = $"{name}@EXAMPLE.TEST".ToUpperInvariant(),
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        await new AccountRepository(_database).CreateAccountAsync(
            account,
            new TestGrant(account.Id, profile.Id),
            new HashSet<MediaEngine.Domain.Authorization.AccountFeatureId> { MediaEngine.Domain.Authorization.AccountFeatureId.Watch },
            new HashSet<Guid>(),
            newProfile: profile,
            newHouseholdName: $"{name} home");
        return profile.Id;
    }

    private async Task<long> VersionAsync(Guid profileId)
    {
        using var connection = _database.CreateConnection();
        return await Task.FromResult(connection.ExecuteScalar<long>("SELECT authorization_version FROM profiles WHERE id=@profileId;", new { profileId }));
    }

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
