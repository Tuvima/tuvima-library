using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Imports;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Services.ImportUploads;
using MediaEngine.Storage;
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

/// <summary>
/// The resumable upload routes over HTTP (Library setup spec D44) on a real server: the real route table and authorization handler, the
/// real upload service on a real Import folder and data store. Only the identity behind each request and the Engine's filing queue are
/// stand-ins.
/// </summary>
public sealed class ImportUploadEndpointTests : IDisposable
{
    private const string PrincipalHeader = "X-Test-Principal";
    private const int MiB = 1024 * 1024;
    private const string MoviesLibraryId = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string MoviesSourceId = "aaaaaaaa-1111-4111-8111-000000000001";

    private static readonly Guid Account = Guid.NewGuid();
    private static readonly Guid AdminProfile = Guid.NewGuid();
    private static readonly Guid OtherAdminProfile = Guid.NewGuid();
    private static readonly Guid MemberProfile = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<string, RequestAuthority> Principals = new Dictionary<string, RequestAuthority>
    {
        ["admin"] = new(PrincipalKind.Human, true, Account, AdminProfile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true),
        ["other-admin"] = new(PrincipalKind.Human, true, Guid.NewGuid(), OtherAdminProfile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true),
        ["member"] = new(PrincipalKind.Human, true, Account, MemberProfile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true),
        // A paired app acting for an administrator's household member, and an application holding every permission: neither is a person at the Dashboard.
        ["paired-app"] = new(PrincipalKind.DelegatedUserClient, true, Account, MemberProfile, BuiltInApplicationIds.NativeClient, null, Guid.NewGuid(),
            AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true),
        ["service"] = new(PrincipalKind.ServiceApplication, true, null, null, Guid.NewGuid(),
            ApplicationEnabled: true, ApplicationIsAdministrator: true),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-import-upload-http-{Guid.NewGuid():N}");
    private readonly string _importDir;
    private readonly string _moviesDir;
    private readonly string _configDir;
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ConfigurationDirectoryLoader _config;
    private readonly IIngestionEngine _engine = EngineProxy.Create();

    public ImportUploadEndpointTests()
    {
        DapperConfiguration.Configure();
        _importDir = Path.Combine(_root, "Import");
        _moviesDir = Path.Combine(_root, "library", "Movies");
        _configDir = Path.Combine(_root, "config");
        _dbPath = Path.Combine(_root, "test.db");
        Directory.CreateDirectory(_importDir);
        Directory.CreateDirectory(_moviesDir);
        Directory.CreateDirectory(_configDir);
        WriteLibraries(withImportFolder: true);
        File.WriteAllText(
            Path.Combine(_configDir, "maintenance.json"),
            JsonSerializer.Serialize(new MaintenanceSettings { UploadPieceBytes = MiB }));
        _config = new ConfigurationDirectoryLoader(_configDir);
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        _config.Dispose();
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp folder goes next */ }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    [Fact]
    public async Task AnAdministrator_UploadsAFileInPieces_AndItLandsInTheKindsImportFolder()
    {
        var content = Content(2 * MiB + 100);
        await using var app = await StartAsync();
        using var client = app.CreateClient("admin");

        var start = await client.PostAsync("/imports/uploads", Json(
            $$"""{"kind_key":"movies","file_name":"Film.mkv","total_bytes":{{content.Length}},"library_id":"{{MoviesLibraryId}}"}"""));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var started = JsonSerializer.Deserialize<ImportUploadStartResponse>(await start.Content.ReadAsStringAsync())!;
        Assert.Equal(3, started.PieceCount);

        foreach (var index in new[] { 0, 2 })
        {
            using var piece = await client.PutAsync($"/imports/uploads/{started.UploadId}/pieces/{index}", Piece(content, started, index));
            Assert.Equal(HttpStatusCode.OK, piece.StatusCode);
        }

        // The connection dropped: ask what arrived, send the missing piece, and send one that was already there again.
        var status = JsonSerializer.Deserialize<ImportUploadStatusResponse>(
            await client.GetStringAsync($"/imports/uploads/{started.UploadId}"))!;
        Assert.Equal([0, 2], status.ReceivedPieces);
        using var missing = await client.PutAsync($"/imports/uploads/{started.UploadId}/pieces/1", Piece(content, started, 1));
        using var again = await client.PutAsync($"/imports/uploads/{started.UploadId}/pieces/1", Piece(content, started, 1));
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        using var done = await client.PostAsync($"/imports/uploads/{started.UploadId}/complete", null);

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var completed = JsonSerializer.Deserialize<ImportUploadCompleteResponse>(await done.Content.ReadAsStringAsync())!;
        Assert.Equal(Path.Combine(_importDir, "Movies", "Film.mkv"), completed.DestinationPath);
        Assert.Equal(content, await File.ReadAllBytesAsync(completed.DestinationPath));
        Assert.Equal(completed.DestinationPath, Assert.Single(((EngineProxy)(object)_engine).Requests).Path);
    }

    [Fact]
    public async Task Start_WithNoImportFolder_AnswersAPlainRefusalWithAReasonCode_AndCreatesNothing()
    {
        WriteLibraries(withImportFolder: false);
        await using var app = await StartAsync();
        using var client = app.CreateClient("admin");

        using var response = await client.PostAsync("/imports/uploads", Json(
            """{"kind_key":"movies","file_name":"Film.mkv","total_bytes":1000}"""));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ImportUploadRefusalCodes.NoImportFolder, body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("detail").GetString()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_importDir));
        Assert.Equal(0, CountUploadRows());
    }

    [Fact]
    public async Task Start_AboveTheWarningSize_AnswersAConfirmationRefusalWithTheFolderPath_AndGoesAheadOnceConfirmed()
    {
        _config.SaveMaintenance(new MaintenanceSettings { UploadPieceBytes = MiB, LargeUploadWarningBytes = 2 * MiB });
        await using var app = await StartAsync();
        using var client = app.CreateClient("admin");
        var size = 3 * MiB;

        using var asked = await client.PostAsync("/imports/uploads", Json($$"""{"kind_key":"movies","file_name":"Big.mkv","total_bytes":{{size}}}"""));

        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        using var body = JsonDocument.Parse(await asked.Content.ReadAsStringAsync());
        Assert.Equal(ImportUploadRefusalCodes.LargeUploadNeedsConfirm, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(Path.Combine(_importDir, "Movies"), body.RootElement.GetProperty("import_folder_path").GetString());
        Assert.Equal(0, CountUploadRows());

        using var confirmed = await client.PostAsync("/imports/uploads", Json($$"""{"kind_key":"movies","file_name":"Big.mkv","total_bytes":{{size}},"confirm_large":true}"""));

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(1, CountUploadRows());
    }

    [Theory]
    [InlineData("member")]
    [InlineData("paired-app")]
    [InlineData("service")]
    public async Task EveryRoute_RefusesACallerWhoIsNotAnAdministratorSignedInAsAPerson(string principal)
    {
        var upload = Guid.NewGuid();
        await using var app = await StartAsync();
        using var client = app.CreateClient(principal);
        using var piece = new ByteArrayContent(new byte[16]);

        var responses = new[]
        {
            await client.PostAsync("/imports/uploads", Json("""{"kind_key":"movies","file_name":"Film.mkv","total_bytes":1000}""")),
            await client.GetAsync($"/imports/uploads/{upload}"),
            await client.PutAsync($"/imports/uploads/{upload}/pieces/0", piece),
            await client.PostAsync($"/imports/uploads/{upload}/complete", null),
            await client.DeleteAsync($"/imports/uploads/{upload}"),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        Assert.Equal(0, CountUploadRows());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_importDir));
    }

    [Fact]
    public async Task AnotherAdministrator_CannotSeeSendToCompleteOrCancelAnUploadTheyDidNotStart()
    {
        await using var app = await StartAsync();
        using var owner = app.CreateClient("admin");
        using var other = app.CreateClient("other-admin");
        var start = await owner.PostAsync("/imports/uploads", Json("""{"kind_key":"movies","file_name":"Film.mkv","total_bytes":1000}"""));
        var id = JsonSerializer.Deserialize<ImportUploadStartResponse>(await start.Content.ReadAsStringAsync())!.UploadId;
        using var piece = new ByteArrayContent(new byte[1000]);

        var responses = new[]
        {
            await other.GetAsync($"/imports/uploads/{id}"),
            await other.PutAsync($"/imports/uploads/{id}/pieces/0", piece),
            await other.PostAsync($"/imports/uploads/{id}/complete", null),
            await other.DeleteAsync($"/imports/uploads/{id}"),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_importDir, ".data", "uploads")));
    }

    [Fact]
    public async Task Cancel_DeletesTheWorkingFile()
    {
        await using var app = await StartAsync();
        using var client = app.CreateClient("admin");
        var start = await client.PostAsync("/imports/uploads", Json("""{"kind_key":"movies","file_name":"Film.mkv","total_bytes":1000}"""));
        var id = JsonSerializer.Deserialize<ImportUploadStartResponse>(await start.Content.ReadAsStringAsync())!.UploadId;
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_importDir, ".data", "uploads")));

        using var cancelled = await client.DeleteAsync($"/imports/uploads/{id}");

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_importDir, ".data", "uploads")));
    }

    [Fact]
    public async Task OnlyThePieceRoute_AcceptsABodyBiggerThanTheNormalRequestLimit()
    {
        // A piece of 31 MB is over the server's normal 30 MB request limit; the setting allows it for the piece route only.
        _config.SaveMaintenance(new MaintenanceSettings { UploadPieceBytes = 31 * MiB });
        await using var app = await StartAsync();
        using var client = app.CreateClient("admin");
        var content = new byte[31 * MiB];
        Random.Shared.NextBytes(content);
        var start = await client.PostAsync("/imports/uploads", Json($$"""{"kind_key":"movies","file_name":"Big.mkv","total_bytes":{{content.Length}}}"""));
        var started = JsonSerializer.Deserialize<ImportUploadStartResponse>(await start.Content.ReadAsStringAsync())!;
        Assert.Equal(1, started.PieceCount);

        using var piece = await client.PutAsync($"/imports/uploads/{started.UploadId}/pieces/0", Piece(content, started, 0));
        // The server refuses the oversize body on any other route: either with 413, or by closing the connection while it is still being sent.
        HttpStatusCode? otherRoute = null;
        var cutOff = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.PostAsync("/imports/uploads", new ByteArrayContent(content)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            });
            otherRoute = response.StatusCode;
        });

        Assert.Equal(HttpStatusCode.OK, piece.StatusCode);
        Assert.True(
            otherRoute == HttpStatusCode.RequestEntityTooLarge || cutOff is HttpRequestException,
            $"The start route took a {content.Length}-byte body (answer {otherRoute}).");
    }

    [Fact]
    public async Task OnlyThePieceRoute_IsLeftOutOfTheGeneralRateLimit()
    {
        await using var app = await StartAsync();
        var endpoints = ((IEndpointRouteBuilder)app.Host).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => (endpoint.RoutePattern.RawText ?? string.Empty).StartsWith("/imports/uploads", StringComparison.Ordinal))
            .ToArray();

        var exempt = endpoints.Where(endpoint => ImportUploadEndpoints.IsPieceRoute(WithEndpoint(endpoint))).ToArray();

        Assert.Equal(5, endpoints.Length);
        var piece = Assert.Single(exempt);
        Assert.Equal("/imports/uploads/{uploadId:guid}/pieces/{index:int}", piece.RoutePattern.RawText);
        Assert.Contains("PUT", piece.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "Program.cs"));
        var limiter = program[program.IndexOf("options.GlobalLimiter", StringComparison.Ordinal)..];
        Assert.Contains("ImportUploadEndpoints.IsPieceRoute(context)", limiter[..limiter.IndexOf("GetNoLimiter", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    private static HttpContext WithEndpoint(Endpoint endpoint)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(endpoint);
        return context;
    }

    private static HttpContent Piece(byte[] content, ImportUploadStartResponse upload, int index)
    {
        var start = (long)index * upload.PieceBytes;
        var length = (int)Math.Min(upload.PieceBytes, content.Length - start);
        var body = new ByteArrayContent(content, (int)start, length);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return body;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static byte[] Content(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31 + i / 7) % 251);
        }

        return bytes;
    }

    private int CountUploadRows()
    {
        using var connection = _db.CreateConnection();
        return Dapper.SqlMapper.ExecuteScalar<int>(connection, "SELECT COUNT(*) FROM import_uploads;");
    }

    private void WriteLibraries(bool withImportFolder)
    {
        var movies = new LibraryFolderConfig
        {
            Id = MoviesLibraryId,
            Name = "Movies",
            Category = "Movies",
            Kind = LibraryKinds.Catalogued,
            KindKey = "movies",
            Area = LibraryAreas.Watch,
            MediaTypes = ["Movies"],
            AcceptedIntakeModes = [LibraryIntakeModes.IncomingFolder],
            Sources =
            [
                new LibrarySourceConfig
                {
                    Id = MoviesSourceId,
                    Path = _moviesDir,
                    ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                    AccessMode = LibrarySourceAccessModes.Writable,
                },
            ],
            PrimaryDestinationSourceId = MoviesSourceId,
        };
        var libraries = new LibrariesConfiguration
        {
            Libraries = [movies],
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = Path.Combine(_root, "library"), AllowWrite = true }],
        };
        if (withImportFolder)
        {
            libraries.StorageLocations.Add(
                new ServerStorageLocationConfig { Id = ImportFolderRules.LocationId, Label = "Import folder", Path = _importDir, AllowWrite = true });
        }

        File.WriteAllText(Path.Combine(_configDir, "libraries.json"), JsonSerializer.Serialize(libraries));
    }

    private async Task<TestApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Administrator, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new EffectiveAdministratorRequirement(true)));
        });
        builder.Services.AddScoped<IAuthorizationHandler, EffectiveAdministratorHandler>();
        builder.Services.AddSingleton<IRequestAuthorityResolver, PrincipalHeaderResolver>();
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AdministratorDecisions());
        builder.Services.AddSingleton<IConfigurationLoader>(_config);
        builder.Services.AddSingleton<IImportFolderService>(sp => new ImportFolderService(
            sp.GetRequiredService<IConfigurationLoader>(), sp.GetRequiredService<ILogger<ImportFolderService>>()));
        builder.Services.AddSingleton<IImportUploadRepository>(new ImportUploadRepository(_db));
        builder.Services.AddSingleton(_engine);
        builder.Services.AddSingleton<IVaultIngestionHook>(new NoVaultHook());
        builder.Services.AddSingleton(Options.Create(new IngestionOptions { UploadFreeSpaceBufferBytes = 0 }));
        builder.Services.AddSingleton<ImportUploadService>();

        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapImportUploadEndpoints();
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

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class PrincipalHeaderResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Principals[context.Request.Headers[PrincipalHeader].ToString()]);
    }

    private sealed class NoVaultHook : IVaultIngestionHook
    {
        public Task<bool> IsVaultLibraryAsync(string? libraryId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> IsVaultAssetAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult(false);

        public Task OnFileRegisteredAsync(VaultRegisteredFile file, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Signs every request in as the principal named by the <c>X-Test-Principal</c> header.</summary>
    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "import-upload-test")], Scheme.Name)), Scheme.Name)));
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public WebApplication Host => app;

        public HttpClient CreateClient(string principal)
        {
            var client = new HttpClient { BaseAddress = address };
            client.DefaultRequestHeaders.Add(PrincipalHeader, principal);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    public class EngineProxy : DispatchProxy
    {
        public List<IntakeFileRequest> Requests { get; } = [];

        public static IIngestionEngine Create() => DispatchProxy.Create<IIngestionEngine, EngineProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IIngestionEngine.EnqueueIntakeAsync))
            {
                Requests.Add((IntakeFileRequest)args![0]!);
                return Task.CompletedTask;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
