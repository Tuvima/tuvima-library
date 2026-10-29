using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

// Vault-protected libraries on the Libraries pages (Library pages WP9, D10, D11, D26a, D34): who may make one, add a Vault folder, list them,
// switch their online lookup and ask where Move to Vault would go. A real temp data store and configuration folder, a real host.
// Whoever is not a signed-in server administrator gets the answer an unknown address gets, with nothing in the body that names the Vault,
// a folder or a title, and a Vault library never appears in the places ordinary libraries are listed or counted.
public sealed class VaultLibraryEndpointsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_vault_siblings_{Guid.NewGuid():N}");
    private readonly string _moviesFolder;
    private readonly string _privateMovies;
    private readonly string _privateMusic;
    private readonly string _managedFolder;
    private readonly string _ordinaryMovies;
    private readonly Guid _adminProfile = Guid.NewGuid();
    private readonly Guid _adminSession = Guid.NewGuid();
    private DatabaseConnection _database = default!;
    private ConfigurationDirectoryLoader _configuration = default!;
    private VaultLibraryRepository _vaultLibraries = default!;
    private SwitchableNsfwAccess _nsfw = default!;
    private WebApplication _app = default!;
    private HttpClient _client = default!;

    public VaultLibraryEndpointsTests()
    {
        _moviesFolder = Path.Combine(_root, "Movies");
        _ordinaryMovies = Path.Combine(_root, "OrdinaryMovies");
        _privateMovies = Path.Combine(_root, "PrivateMovies");
        _privateMusic = Path.Combine(_root, "PrivateMusic");
        _managedFolder = Path.Combine(_root, "Managed");
    }

    public async Task InitializeAsync()
    {
        foreach (var folder in new[] { _moviesFolder, _ordinaryMovies, _privateMovies, _privateMusic, _managedFolder })
        {
            Directory.CreateDirectory(folder);
        }

        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _vaultLibraries = new VaultLibraryRepository(_database);
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        _configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = _root, AllowWrite = true }],
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = Guid.NewGuid().ToString("D"), Name = "Movies", Category = "Movies", Kind = LibraryKinds.Catalogued, KindKey = "movies",
                    Area = "watch", MediaTypes = ["Movies"], IsVault = false,
                    Sources = [new LibrarySourceConfig { Id = Guid.NewGuid().ToString("D"), Path = _ordinaryMovies, ManagementMode = "existing_library", AccessMode = "read_only" }],
                },
            ],
        });
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@id,'Administrator','#7C4DFF','Administrator',@now);",
                new { id = _adminProfile, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        _nsfw = new SwitchableNsfwAccess();
        var identities = new IdentityRepository(_database);
        var profiles = new ProfileRepository(_database);
        var clock = TimeProvider.System;
        var real = new NsfwAccessRepository(_database);
        var identity = new FirstPartyIdentityService(
            identities, new AccountRepository(_database), profiles,
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(), clock, new FixedPolicy(),
            nsfwAccess: real);
        var administration = new VaultAdministrationService(_vaultLibraries, real, profiles, identities, identity, clock, NullLogger<VaultAdministrationService>.Instance);
        var attachment = new VaultFolderAttachmentService(
            administration, _vaultLibraries, _configuration, new ServerFolderBrowserService(_configuration, NullLogger<ServerFolderBrowserService>.Instance),
            NullLogger<VaultFolderAttachmentService>.Instance);
        var plugins = new PluginCatalog([], new PluginSettingsService(_configuration), _configuration, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        var kinds = new LibraryKindListService(_configuration, plugins);
        var siblings = new VaultSiblingLibraryService(_vaultLibraries, _configuration, attachment, kinds, clock, NullLogger<VaultSiblingLibraryService>.Instance);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, SignedInHandler>("test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new HeaderAuthorityResolver(_adminProfile, _adminSession));
        builder.Services.AddSingleton(new VaultAccessResolver(_vaultLibraries, _nsfw, clock));
        builder.Services.AddSingleton(siblings);
        builder.Services.AddSingleton(new VaultMoveService(
            _configuration, _vaultLibraries, new VaultMoveRepository(_database), new MediaEngine.Storage.Services.MediaTypeExtensionCatalog(), administration,
            new VaultMoveSignal(), clock, NullLogger<VaultMoveService>.Instance));
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGroup("/settings").MapVaultLibraries();
        _app.MapGroup("/settings").MapVaultMoves();
        await _app.StartAsync();
        var address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _client = new HttpClient { BaseAddress = address };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _configuration.Dispose();
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(Path.Combine(_root, "library.db"));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private HttpRequestMessage Request(HttpMethod method, string url, string who, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Test-Who", who);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string who, object? body = null) =>
        _client.SendAsync(Request(method, url, who, body));

    private static CreateVaultLibraryFromKindRequest Create(string kind, string name, string folder) => new(kind, name, folder);

    // Makes a Vault library as the unlocked administrator and answers its id (the answer to a create carries the id and nothing else).
    private async Task<string> MakeAsync(string kind, string name, string folder)
    {
        var response = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create(kind, name, folder));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<VaultLibraryActionResultDto>())!;
        Assert.True(result.Ok);
        return Assert.IsType<string>(result.LibraryId);
    }

    [Fact]
    public void EveryRoute_RequiresASignedInCaller()
    {
        var routes = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.Contains("vault-libraries", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(5, routes.Count);
        Assert.All(routes, route => Assert.Contains(
            route.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == AuthPolicies.Authenticated));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("kids")]
    [InlineData("app")]
    public async Task EveryRoute_AnswersNotFoundForAnyoneWhoIsNotAnAdministrator_WithNothingThatNamesTheVaultOrTheFolder(string who)
    {
        var id = Guid.NewGuid();
        var calls = new[]
        {
            await SendAsync(HttpMethod.Get, "/settings/vault-libraries", who),
            await SendAsync(HttpMethod.Post, "/settings/vault-libraries", who, Create("movies", "Private Movies", _privateMovies)),
            await SendAsync(HttpMethod.Post, "/settings/vault-libraries/folders", who, new AddVaultFolderRequest("movies", _privateMovies)),
            await SendAsync(HttpMethod.Get, "/settings/vault-libraries/destination?kind_key=movies", who),
            await SendAsync(HttpMethod.Put, $"/settings/vault-libraries/{id:D}/online-lookup", who, new SetVaultOnlineLookupRequest(true)),
            // A malformed body is not read before the caller is known, so it cannot tell this caller anything either.
            await SendAsync(HttpMethod.Post, "/settings/vault-libraries", who, "not a request"),
        };

        foreach (var response in calls)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("vault", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_root, body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(await _vaultLibraries.GetIdsAsync());
        Assert.Single(_configuration.LoadLibraries().Libraries);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("kids")]
    [InlineData("app")]
    public async Task EveryMoveRoute_AnswersNotFoundForAnyoneWhoIsNotAnAdministrator_WithNothingThatNamesTheVault(string who)
    {
        var calls = new[]
        {
            await SendAsync(HttpMethod.Post, "/settings/vault-moves/preview", who, new VaultMovePreviewRequest { Direction = "in", SourceId = Guid.NewGuid().ToString("D") }),
            await SendAsync(HttpMethod.Post, "/settings/vault-moves", who, new StartVaultMoveRequest { Direction = "in", SourceId = Guid.NewGuid().ToString("D") }),
            await SendAsync(HttpMethod.Get, "/settings/vault-moves", who),
            await SendAsync(HttpMethod.Get, $"/settings/vault-moves/{Guid.NewGuid():D}", who),
            await SendAsync(HttpMethod.Post, "/settings/vault-moves", who, "not a request"),
        };

        foreach (var response in calls)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("vault", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AMoveCannotBeStartedWithoutTheAdministratorsOwnPin_AndNothingIsSaved()
    {
        var response = await SendAsync(HttpMethod.Post, "/settings/vault-moves", "admin",
            new StartVaultMoveRequest { Direction = "in", SourceId = Guid.NewGuid().ToString("D"), AdminPin = "0000" });

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        var list = await SendAsync(HttpMethod.Get, "/settings/vault-moves", "admin");
        Assert.Equal("[]", (await list.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task APreviewOfAnUnknownFolder_IsRefusedInPlainWords_ForTheUnlockedAdministrator()
    {
        var response = await SendAsync(HttpMethod.Post, "/settings/vault-moves/preview", "admin",
            new VaultMovePreviewRequest { Direction = "in", SourceId = Guid.NewGuid().ToString("D") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<VaultMovePreviewDto>())!;
        Assert.Equal(VaultMovePreviewStates.Refused, preview.State);
        Assert.Equal(0, preview.Items);
    }

    [Fact]
    public async Task AnAdministrator_MakesTheVaultLibraryOfAContentTypeWithoutAPin_AndItLivesInTheVaultTables()
    {
        var response = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("movies", "Private Movies", _privateMovies));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<VaultLibraryActionResultDto>())!;
        Assert.True(result.Ok);
        var id = Assert.IsType<string>(result.LibraryId);
        Assert.Contains(id, await _vaultLibraries.GetIdsAsync());
        var saved = _configuration.LoadLibraries().Libraries.Single(item => item.Id == id);
        Assert.Equal("Private Movies", saved.Name);
        Assert.Equal("movies", saved.KindKey);
        Assert.Equal("local_only", saved.MetadataPolicy);
        Assert.Equal(_privateMovies, Assert.Single(saved.Sources).Path);
        Assert.Equal("read_only", saved.Sources[0].AccessMode);
        Assert.True(saved.IsVault);
        Assert.False(saved.OrganizationPolicy.WriteTags);
        Assert.Equal(LibraryOrganizationModes.KeepOriginalNames, saved.OrganizationPolicy.Mode);
        Assert.Equal(2, _configuration.LoadLibraries().Libraries.Count);
    }

    [Fact]
    public async Task ASecondVaultLibraryForTheSameContentType_IsRefusedPlainly_ButAnotherTypeIsFine()
    {
        await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("movies", "Private Movies", _privateMovies));

        var again = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("movies", "More movies", _moviesFolder));
        var music = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("music", "Private Music", _privateMusic));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(VaultSiblingLibraryService.AlreadyExistsMessage, await DetailOf(again));
        Assert.Equal(HttpStatusCode.OK, music.StatusCode);
        Assert.Equal(2, (await _vaultLibraries.GetIdsAsync()).Count);
    }

    [Fact]
    public async Task ARefusedRequest_NeverEchoesTheFolderOrTheTitle()
    {
        var missingFolder = Path.Combine(_root, "NoSuchSecretFolder");
        var onOrdinary = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("movies", "Secret Title Name", _ordinaryMovies));
        var missing = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("movies", "Secret Title Name", missingFolder));
        var noName = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("movies", "  ", _privateMovies));
        var badKind = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("photos", "Private Photos", _privateMovies));

        foreach (var (label, response) in new[] { ("ordinary", onOrdinary), ("missing", missing), ("noName", noName), ("badKind", badKind) })
        {
            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict, $"{label} -> {response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SecretFolder", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Secret Title Name", body, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, body, StringComparison.OrdinalIgnoreCase);
        }

        // Nothing half-made is left behind: no Vault row and no library entry.
        Assert.Empty(await _vaultLibraries.GetIdsAsync());
        Assert.Single(_configuration.LoadLibraries().Libraries);
    }

    [Fact]
    public async Task AFolderAlreadyInAnOrdinaryLibrary_CannotBecomeAVaultFolder()
    {
        var response = await SendAsync(HttpMethod.Post, "/settings/vault-libraries/folders", "admin", new AddVaultFolderRequest("movies", _ordinaryMovies));

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict);
        Assert.Empty(await _vaultLibraries.GetIdsAsync());
    }

    [Fact]
    public async Task AddingAVaultFolder_MakesTheVaultLibraryFirstWhenThereIsNone_AndThenJoinsIt()
    {
        var first = await SendAsync(HttpMethod.Post, "/settings/vault-libraries/folders", "admin", new AddVaultFolderRequest("movies", _privateMovies));
        var second = await SendAsync(HttpMethod.Post, "/settings/vault-libraries/folders", "admin", new AddVaultFolderRequest("movies", _moviesFolder));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var result = (await second.Content.ReadFromJsonAsync<VaultLibraryActionResultDto>())!;
        var saved = _configuration.LoadLibraries().Libraries.Single(item => item.Id == result.LibraryId);
        Assert.True(saved.IsVault);
        Assert.Equal(2, saved.Sources.Count);
        Assert.Single(await _vaultLibraries.GetIdsAsync());
        Assert.Equal(2, _configuration.LoadLibraries().Libraries.Count);
    }

    [Fact]
    public async Task TheListAndTheOnlineLookupSwitch_NeedTheAdministratorsVaultModeToBeOn()
    {
        var madeId = await MakeAsync("movies", "Private Movies", _privateMovies);

        _nsfw.Unlocked = false;
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/settings/vault-libraries", "admin")).StatusCode);
        var lockedSwitch = await SendAsync(HttpMethod.Put, $"/settings/vault-libraries/{madeId}/online-lookup", "admin", new SetVaultOnlineLookupRequest(true));
        Assert.Equal(HttpStatusCode.NotFound, lockedSwitch.StatusCode);
        Assert.Equal("local_only", _configuration.LoadLibraries().Libraries.Single(item => item.Id == madeId).MetadataPolicy);

        _nsfw.Unlocked = true;
        var list = (await (await SendAsync(HttpMethod.Get, "/settings/vault-libraries", "admin")).Content.ReadFromJsonAsync<List<LibraryFolderDto>>())!;
        Assert.Equal(madeId, Assert.Single(list).Id);
    }

    [Fact]
    public async Task TheOnlineLookupSwitch_IsOffByDefault_TurnsOnAndOffForThatLibraryOnly_AndRefusesAnOrdinaryLibrary()
    {
        _nsfw.Unlocked = true;
        var movies = await MakeAsync("movies", "Private Movies", _privateMovies);
        var music = await MakeAsync("music", "Private Music", _privateMusic);
        var ordinary = _configuration.LoadLibraries().Libraries.First(item => !item.IsVault).Id;

        var on = await SendAsync(HttpMethod.Put, $"/settings/vault-libraries/{movies}/online-lookup", "admin", new SetVaultOnlineLookupRequest(true));

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal("enriched", (await on.Content.ReadFromJsonAsync<LibraryFolderDto>())!.MetadataPolicy);
        var saved = _configuration.LoadLibraries().Libraries;
        Assert.Equal("enriched", saved.Single(item => item.Id == movies).MetadataPolicy);
        Assert.Equal("local_only", saved.Single(item => item.Id == music).MetadataPolicy);

        var off = await SendAsync(HttpMethod.Put, $"/settings/vault-libraries/{movies}/online-lookup", "admin", new SetVaultOnlineLookupRequest(false));
        Assert.Equal("local_only", (await off.Content.ReadFromJsonAsync<LibraryFolderDto>())!.MetadataPolicy);

        // An ordinary library, and an id that names nothing, are both "not found": the switch is never a way to touch other libraries.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/settings/vault-libraries/{ordinary}/online-lookup", "admin", new SetVaultOnlineLookupRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/settings/vault-libraries/{Guid.NewGuid():D}/online-lookup", "admin", new SetVaultOnlineLookupRequest(true))).StatusCode);
        Assert.False(_configuration.LoadLibraries().Libraries.Single(item => item.Id == ordinary).IsVault);
    }

    [Fact]
    public async Task TheMoveDestination_SaysNoneThenNeedsAManagedFolder_AndNamesNoFolder()
    {
        var none = (await (await SendAsync(HttpMethod.Get, "/settings/vault-libraries/destination?kind_key=movies", "admin")).Content.ReadFromJsonAsync<VaultMoveDestinationDto>())!;
        Assert.Equal(VaultMoveDestinationStates.None, none.State);
        Assert.Null(none.LibraryId);

        await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("movies", "Private Movies", _privateMovies));
        var raw = await (await SendAsync(HttpMethod.Get, "/settings/vault-libraries/destination?kind_key=movies", "admin")).Content.ReadAsStringAsync();
        var needs = System.Text.Json.JsonSerializer.Deserialize<VaultMoveDestinationDto>(raw)!;

        Assert.Equal(VaultMoveDestinationStates.NeedsManagedFolder, needs.State);
        Assert.Equal("Private Movies", needs.LibraryName);
        Assert.Equal(0, needs.ManagedFolders);
        Assert.DoesNotContain(_root, raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/settings/vault-libraries/destination", "admin")).StatusCode);
    }

    [Fact]
    public async Task AManagedVaultFolder_IsWritableAndManaged_AndMakesTheMoveDestinationReady_WhileAReadOnlyOneDoesNot()
    {
        await MakeAsync("movies", "Private Movies", _privateMovies);

        var managed = await SendAsync(HttpMethod.Post, "/settings/vault-libraries/folders", "admin", new AddVaultFolderRequest("movies", _managedFolder, Managed: true));

        Assert.Equal(HttpStatusCode.OK, managed.StatusCode);
        var entry = _configuration.LoadLibraries().Libraries.Single(item => item.KindKey == "movies" && item.IsVault);
        var readOnly = entry.Sources.Single(source => source.Path == _privateMovies);
        var writable = entry.Sources.Single(source => source.Path == _managedFolder);
        Assert.False(readOnly.AllowsFileMutation);
        Assert.True(writable.AllowsFileMutation);
        var destination = (await (await SendAsync(HttpMethod.Get, "/settings/vault-libraries/destination?kind_key=movies", "admin")).Content.ReadFromJsonAsync<VaultMoveDestinationDto>())!;
        Assert.Equal(VaultMoveDestinationStates.Ready, destination.State);
        Assert.Equal(1, destination.ManagedFolders);
    }

    [Fact]
    public async Task AVaultFolderHoldingALink_IsRefused_BecauseTheLinkCouldReachANormalLibrary()
    {
        await MakeAsync("movies", "Private Movies", _privateMovies);
        var withLink = Path.Combine(_root, "WithLink");
        Directory.CreateDirectory(withLink);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(withLink, "shortcut"), _ordinaryMovies);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var response = await SendAsync(HttpMethod.Post, "/settings/vault-libraries/folders", "admin", new AddVaultFolderRequest("movies", withLink));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(VaultFolderAttachmentService.FolderHoldsLinksMessage, await DetailOf(response));
        Assert.DoesNotContain(_configuration.LoadLibraries().Libraries.SelectMany(item => item.Sources), source => source.Path == withLink);
    }

    [Fact]
    public async Task AVaultFolderHoldingIndexedPhotos_IsRefused_LikeOneHoldingOrdinaryFiles()
    {
        await MakeAsync("movies", "Private Movies", _privateMovies);
        var photos = Path.Combine(_root, "Photos");
        Directory.CreateDirectory(photos);
        var fileId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO local_files(id,content_hash,byte_size,mime_type,extension,created_at) VALUES(@id,'abc123',1,'image/jpeg','jpg',@now);",
                new { id = fileId, now = DateTimeOffset.UtcNow.ToString("O") });
            connection.Execute(
                "INSERT INTO local_file_sources(id,file_id,library_id,file_path,modified_at,indexed_at) VALUES(@id,@file,@library,@path,@now,@now);",
                new { id = Guid.NewGuid(), file = fileId, library = Guid.NewGuid(), path = Path.Combine(photos, "a.jpg"), now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var response = await SendAsync(HttpMethod.Post, "/settings/vault-libraries/folders", "admin", new AddVaultFolderRequest("movies", photos));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(VaultFolderAttachmentService.FolderHoldsOrdinaryFilesMessage, await DetailOf(response));
    }

    [Fact]
    public async Task AVaultLibrary_NeverAppearsWhereOrdinaryLibrariesAreListedCountedOrGranted()
    {
        var madeId = await MakeAsync("movies", "Private Movies", _privateMovies);
        var vaultIds = await _vaultLibraries.GetIdsAsync();

        // The ordinary list of libraries, as the settings route and the Dashboard get it, holds no trace.
        var visible = VaultLibrarySettings.WithoutVault(_configuration.LoadLibraries(), vaultIds);
        Assert.DoesNotContain(visible.Libraries, library => library.Id == madeId);
        Assert.DoesNotContain(visible.Libraries.SelectMany(library => library.Sources), source => source.Path == _privateMovies);

        // And the visibility rule itself says no to everyone, administrators included: the Vault has its own doors.
        Assert.False(LibraryVisibilityRule.MaySee(isAdministrator: true, isVault: true, hasArea: true, isHidden: false));
        Assert.False(LibraryVisibilityRule.MaySee(isAdministrator: false, isVault: true, hasArea: true, isHidden: false));
    }

    [Fact]
    public async Task ASavedVaultLibrary_SurvivesAnOrdinarySaveOfTheLibrariesFile()
    {
        var madeId = await MakeAsync("movies", "Private Movies", _privateMovies);
        var vaultIds = await _vaultLibraries.GetIdsAsync();
        var current = _configuration.LoadLibraries();
        var proposed = VaultLibrarySettings.WithoutVault(current, vaultIds);

        VaultLibrarySettings.KeepVault(current, proposed, vaultIds);

        Assert.Contains(proposed.Libraries, library => library.Id == madeId && library.IsVault);
    }

    [Fact]
    public async Task AnAdministratorWhoseVaultModeIsOff_CanMakeOneButLearnsNothingAboutTheVault()
    {
        _nsfw.Unlocked = false;

        var created = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("movies", "Private Movies", _privateMovies));
        var createdBody = await created.Content.ReadAsStringAsync();
        var result = System.Text.Json.JsonSerializer.Deserialize<VaultLibraryActionResultDto>(createdBody)!;

        // It worked, and the answer says only that: no library, no id, no name, no folder.
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.True(result.Ok);
        Assert.Null(result.LibraryId);
        Assert.DoesNotContain("Private Movies", createdBody, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, createdBody, StringComparison.OrdinalIgnoreCase);

        var folder = await SendAsync(HttpMethod.Post, "/settings/vault-libraries/folders", "admin", new AddVaultFolderRequest("movies", _moviesFolder));
        var folderResult = (await folder.Content.ReadFromJsonAsync<VaultLibraryActionResultDto>())!;
        Assert.Equal(HttpStatusCode.OK, folder.StatusCode);
        Assert.Null(folderResult.LibraryId);
    }

    [Fact]
    public async Task AnAdministratorWhoseVaultModeIsOff_GetsOneGenericRefusalWhateverWentWrong_SoARefusalCannotTellWhetherAVaultLibraryExists()
    {
        // One Vault library exists, then the administrator's Vault mode goes off.
        await MakeAsync("movies", "Private Movies", _privateMovies);
        _nsfw.Unlocked = false;

        var duplicate = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("movies", "More movies", _moviesFolder));
        var ordinaryFolder = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("music", "Private Music", _ordinaryMovies));
        var noName = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("music", " ", _privateMusic));
        var badKind = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", Create("photos", "Photos", _privateMusic));
        var unreadable = await SendAsync(HttpMethod.Post, "/settings/vault-libraries", "admin", "not a request");
        var badFolder = await SendAsync(HttpMethod.Post, "/settings/vault-libraries/folders", "admin", new AddVaultFolderRequest("music", Path.Combine(_root, "NoSuchFolder")));

        foreach (var response in new[] { duplicate, ordinaryFolder, noName, badKind, unreadable, badFolder })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(VaultLibraryEndpoints.GenericRefusal, await DetailOf(response));
        }

        Assert.Single(await _vaultLibraries.GetIdsAsync());
    }

    [Fact]
    public async Task TheMoveDestination_AnswersALockedAdministratorExactlyAsAnyoneElse_AndNeverSaysWhetherAVaultLibraryExists()
    {
        await MakeAsync("movies", "Private Movies", _privateMovies);
        _nsfw.Unlocked = false;

        var locked = await SendAsync(HttpMethod.Get, "/settings/vault-libraries/destination?kind_key=movies", "admin");
        var stranger = await SendAsync(HttpMethod.Get, "/settings/vault-libraries/destination?kind_key=movies", "user");
        var lockedBody = await locked.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, locked.StatusCode);
        Assert.Equal(await stranger.Content.ReadAsStringAsync(), lockedBody);
        Assert.DoesNotContain("Private Movies", lockedBody, StringComparison.Ordinal);

        _nsfw.Unlocked = true;
        var open = (await (await SendAsync(HttpMethod.Get, "/settings/vault-libraries/destination?kind_key=movies", "admin")).Content.ReadFromJsonAsync<VaultMoveDestinationDto>())!;
        Assert.Equal("Private Movies", open.LibraryName);
    }

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
    }

    // Everyone is signed in; the "who" header decides what kind of caller the Engine sees.
    private sealed class SignedInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "test")], "test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(identity), "test")));
        }
    }

    private sealed class HeaderAuthorityResolver(Guid adminProfile, Guid adminSession) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default)
        {
            var who = context.Request.Headers["X-Test-Who"].ToString();
            var account = Guid.NewGuid();
            var household = Guid.NewGuid();
            RequestAuthority authority = who switch
            {
                "admin" => new RequestAuthority(
                    PrincipalKind.Human, true, account, adminProfile, SessionId: adminSession, AccountEnabled: true, ActiveProfileInUserHousehold: true,
                    ActiveProfileIsServerAdministrator: true, ActiveProfileHouseholdId: household),
                "kids" => new RequestAuthority(
                    PrincipalKind.Human, true, account, Guid.NewGuid(), SessionId: Guid.NewGuid(), AccountEnabled: true, ActiveProfileInUserHousehold: true,
                    ActiveProfileIsServerAdministrator: true, ActiveProfileIsRestricted: true, ActiveProfileHouseholdId: household),
                "app" => new RequestAuthority(PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: true, ApplicationIsAdministrator: true),
                _ => new RequestAuthority(
                    PrincipalKind.Human, true, account, Guid.NewGuid(), SessionId: Guid.NewGuid(), AccountEnabled: true, ActiveProfileInUserHousehold: true,
                    ActiveProfileHouseholdId: household),
            };
            return ValueTask.FromResult(authority);
        }
    }

    // Whether this device session has switched Vault mode on.
    private sealed class SwitchableNsfwAccess : INsfwAccessRepository
    {
        public bool Unlocked { get; set; } = true;

        public Task<ProfileNsfwAccess> GetAsync(Guid profileId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetAsync(Guid profileId, NsfwAccessMode mode, int? idleLockMinutes, Guid? changedByProfileId, DateTimeOffset now, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> IsUnlockedAsync(Guid sessionId, Guid profileId, DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(Unlocked);

        public Task UnlockAsync(Guid sessionId, Guid profileId, NsfwUnlockMethod method, int idleLockMinutes, DateTimeOffset now, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> TouchAsync(Guid sessionId, Guid profileId, int idleLockMinutes, DateTimeOffset now, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task LockSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task LockProfileAsync(Guid profileId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
