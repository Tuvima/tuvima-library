using System.Security.Claims;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Api.Services.Security;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The routes that move people between Libraries (backup and restore spec D19, users spec D9 and D16), run in memory against a real data
/// store and the real "confirm it's you" check: a stale sign-in is asked to confirm first on every route, application and service callers
/// cannot export anyone's sign-in material, a household administrator exports only their own household, and only the server administrator
/// exports another person or imports.
/// </summary>
public sealed class UserTransferEndpointTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-user-transfer-endpoints-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly FirstPartyIdentityService _identity;
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingAudit _audit = new();
    private readonly RecordingLogs _logs = new();
    private Guid _administrator;
    private IUserTransferService? _transfers;

    public UserTransferEndpointTests()
    {
        Directory.CreateDirectory(_root);
        _database = BackupTestKit.OpenStore(Path.Combine(_root, "library.db"));
        _identity = new FirstPartyIdentityService(
            new IdentityRepository(_database), new AccountRepository(_database), new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            _clock, new FixedPolicy());
    }

    [Theory]
    [InlineData("/access/me/export")]
    [InlineData("/access/accounts/{accountId:guid}/export")]
    [InlineData("/access/households/{householdId:guid}/export")]
    [InlineData("/access/accounts/import/inspect")]
    [InlineData("/access/accounts/import/{operationId:guid}")]
    public async Task EveryRoute_AsksAStaleSignInToConfirmFirst_AndDoesNothing(string route)
    {
        var issued = await SignInAsync();
        await using var app = BuildApplication();

        _clock.Advance(TimeSpan.FromMinutes(11));
        var stale = await SendAsync(app, route, HumanUser(issued), account: Guid.NewGuid(), operation: Guid.NewGuid(), upload: [], household: Guid.NewGuid());

        Assert.Equal(StatusCodes.Status403Forbidden, stale.Status);
        Assert.Equal("confirm_its_you", stale.Json.GetProperty("code").GetString());
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task ExportMyData_AfterConfirming_ReturnsTheZipForTheSignedInPersonOnly()
    {
        var issued = await SignInAsync();
        AddOtherPerson("other-person@example.com");
        await using var app = BuildApplication();

        var result = await SendAsync(app, "/access/me/export", HumanUser(issued));

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.Equal("application/zip", result.ContentType);
        Assert.Matches(@"^tuvima-user-\d{8}-\d{6}\.zip$", result.DownloadName);
        Assert.Contains("owner@example.com", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("other-person@example.com", result.Text, StringComparison.OrdinalIgnoreCase);
        var exported = Assert.Single(_audit.Events);
        Assert.Equal("account.exported", exported.EventType);
        Assert.Equal(issued.Account.Id, exported.ActorAccountId);
    }

    [Theory]
    [InlineData(nameof(PrincipalKind.ServiceApplication))]
    [InlineData(nameof(PrincipalKind.DelegatedUserClient))]
    public async Task ExportMyData_IsRefusedForAnApplicationOrServiceCaller(string kind)
    {
        var issued = await SignInAsync();
        await using var app = BuildApplication();

        var result = await SendAsync(app, "/access/me/export", ApplicationUser(kind), account: issued.Account.Id, kind: Enum.Parse<PrincipalKind>(kind));

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task ExportAccount_ForAnotherPerson_IsRefusedForANonAdministrator_AndGivenToAnAdministrator()
    {
        var issued = await SignInAsync();
        var other = AddOtherPerson("other-person@example.com");
        await using var app = BuildApplication();

        // The signed-in person is the administrator.
        _administrator = issued.Account.Id;
        var given = await SendAsync(app, "/access/accounts/{accountId:guid}/export", HumanUser(issued), account: other);
        Assert.Equal(StatusCodes.Status200OK, given.Status);
        Assert.Contains("other-person@example.com", given.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("account.exported", Assert.Single(_audit.Events).EventType);

        _audit.Events.Clear();
        _administrator = Guid.Empty;
        var refused = await SendAsync(app, "/access/accounts/{accountId:guid}/export", HumanUser(issued), account: other);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.Status);
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task InspectThenImport_AsAnAdministrator_ReturnsThePreviewAndTheResult_AndANonAdministratorIsRefused()
    {
        var issued = await SignInAsync();
        _administrator = issued.Account.Id;
        var exportingPerson = AddOtherPerson("maya@example.com");
        await using var app = BuildApplication();
        var file = await SendAsync(app, "/access/accounts/{accountId:guid}/export", HumanUser(issued), account: exportingPerson);
        Assert.Equal(StatusCodes.Status200OK, file.Status);

        // Move the person out of this data store so the import has somewhere to put them.
        Guid household;
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM accounts WHERE email = 'maya@example.com'");
            connection.Execute("DELETE FROM profiles WHERE display_name = 'Maya'");
            household = EnsureHousehold(connection);
        }

        _administrator = Guid.Empty;
        var refusedInspect = await SendAsync(app, "/access/accounts/import/inspect", HumanUser(issued), upload: file.Bytes);
        Assert.Equal(StatusCodes.Status403Forbidden, refusedInspect.Status);

        _administrator = issued.Account.Id;
        var preview = await SendAsync(app, "/access/accounts/import/inspect", HumanUser(issued), upload: file.Bytes);
        Assert.Equal(StatusCodes.Status200OK, preview.Status);
        Assert.Equal("profile", preview.Json.GetProperty("scope").GetString());
        Assert.Equal("maya@example.com", preview.Json.GetProperty("sign_ins")[0].GetProperty("email").GetString());
        Assert.False(preview.Json.GetProperty("sign_ins")[0].GetProperty("email_in_use").GetBoolean());
        Assert.Equal(JsonValueKind.Null, preview.Json.GetProperty("blocked_reason").ValueKind);
        Assert.Equal("Maya", preview.Json.GetProperty("profiles")[0].GetProperty("name").GetString());
        Assert.Contains(preview.Json.GetProperty("households").EnumerateArray(), item => item.GetProperty("id").GetGuid() == household);
        Assert.Equal(8, preview.Json.GetProperty("max_profiles_per_household").GetInt32());
        var operation = Guid.Parse(preview.Json.GetProperty("operation_id").GetString()!);
        var choices = new UserImportRequestDto(TargetHouseholdId: household);

        _administrator = Guid.Empty;
        var refusedImport = await SendAsync(app, "/access/accounts/import/{operationId:guid}", HumanUser(issued), operation: operation, body: choices);
        Assert.Equal(StatusCodes.Status403Forbidden, refusedImport.Status);

        _administrator = issued.Account.Id;
        var imported = await SendAsync(app, "/access/accounts/import/{operationId:guid}", HumanUser(issued), operation: operation, body: choices);
        Assert.Equal(StatusCodes.Status200OK, imported.Status);
        Assert.Equal(household, imported.Json.GetProperty("household_id").GetGuid());
        Assert.False(imported.Json.GetProperty("created_household").GetBoolean());
        Assert.Equal("maya@example.com", imported.Json.GetProperty("profiles")[0].GetProperty("sign_in_email").GetString());
        Assert.False(imported.Json.GetProperty("profiles")[0].GetProperty("is_household_administrator").GetBoolean());

        var again = await SendAsync(app, "/access/accounts/import/{operationId:guid}", HumanUser(issued), operation: operation, body: choices);
        Assert.Equal(StatusCodes.Status404NotFound, again.Status);
        Assert.Contains(_audit.Events, item => item.EventType == "account.imported" && item.SubjectId == household.ToString("D"));
    }

    [Fact]
    public async Task Import_WithAnEmailThatIsAlreadyUsed_IsRefusedBeforeAnythingIsWritten_KeepsTheFile_AndWorksWithADifferentEmail()
    {
        var issued = await SignInAsync();
        _administrator = issued.Account.Id;
        var maya = AddOtherPerson("maya@example.com");
        await using var app = BuildApplication();
        var file = await SendAsync(app, "/access/accounts/{accountId:guid}/export", HumanUser(issued), account: maya);
        Guid household;
        using (var connection = _database.CreateConnection())
        {
            household = EnsureHousehold(connection);
        }

        var preview = await SendAsync(app, "/access/accounts/import/inspect", HumanUser(issued), upload: file.Bytes);
        // Maya is still here, so the file's email is used, and her ids are too: the import will give the copy new ones.
        Assert.Equal(StatusCodes.Status200OK, preview.Status);
        Assert.True(preview.Json.GetProperty("sign_ins")[0].GetProperty("email_in_use").GetBoolean());
        Assert.Equal(JsonValueKind.Null, preview.Json.GetProperty("blocked_reason").ValueKind);
        var operation = Guid.Parse(preview.Json.GetProperty("operation_id").GetString()!);
        var signIn = preview.Json.GetProperty("sign_ins")[0].GetProperty("id").GetGuid();

        var refused = await SendAsync(app, "/access/accounts/import/{operationId:guid}", HumanUser(issued), operation: operation, body: new UserImportRequestDto(TargetHouseholdId: household));
        Assert.Equal(StatusCodes.Status409Conflict, refused.Status);
        Assert.Contains("Someone in this Library already signs in with maya@example.com. Nothing was imported.", refused.Text, StringComparison.Ordinal);
        using (var connection = _database.CreateConnection())
        {
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE display_name = 'Maya'"));
        }

        var badEmail = await SendAsync(app, "/access/accounts/import/{operationId:guid}", HumanUser(issued), operation: operation,
            body: new UserImportRequestDto(TargetHouseholdId: household, Emails: new Dictionary<Guid, string> { [signIn] = "not an email" }));
        Assert.Equal(StatusCodes.Status400BadRequest, badEmail.Status);

        var imported = await SendAsync(app, "/access/accounts/import/{operationId:guid}", HumanUser(issued), operation: operation,
            body: new UserImportRequestDto(TargetHouseholdId: household, Emails: new Dictionary<Guid, string> { [signIn] = "maya.two@example.com" }));
        Assert.Equal(StatusCodes.Status200OK, imported.Status);
        Assert.Equal("maya.two@example.com", imported.Json.GetProperty("profiles")[0].GetProperty("sign_in_email").GetString());
        using (var connection = _database.CreateConnection())
        {
            Assert.Equal(2, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE display_name = 'Maya'"));
            Assert.Equal(2, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM accounts WHERE email LIKE 'maya%'"));
        }
    }

    [Fact]
    public async Task ExportHousehold_IsForTheServerAdministratorAndTheHouseholdsOwnAdministrator_AndNoOneElse()
    {
        var issued = await SignInAsync();
        var home = Guid.NewGuid();
        var other = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            foreach (var (id, name) in new[] { (home, "Home"), (other, "Elsewhere") })
            {
                connection.Execute("INSERT INTO households (id, name, created_at) VALUES (@id, @name, '2026-10-21T10:00:00Z')", new { id = GuidSql.ToBlob(id), name });
            }
        }

        var mom = AddOtherPerson("mom@example.com", home);
        AddOtherPerson("kid@example.com", home);
        AddOtherPerson("stranger@example.com", other);
        await using var app = BuildApplication();

        // The household's own administrator gets their household and nothing of another one.
        var given = await SendAsync(app, "/access/households/{householdId:guid}/export", HumanUser(issued), household: home, householdAdministratorOf: home);
        Assert.Equal(StatusCodes.Status200OK, given.Status);
        Assert.Matches(@"^tuvima-household-\d{8}-\d{6}\.zip$", given.DownloadName);
        Assert.Contains("mom@example.com", given.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kid@example.com", given.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stranger@example.com", given.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"scope\":\"household\"", given.Text, StringComparison.Ordinal);
        var exported = Assert.Single(_audit.Events);
        Assert.Equal("household.exported", exported.EventType);
        Assert.Equal(home.ToString("D"), exported.SubjectId);

        // Another household, and one that does not exist, get the same answer.
        _audit.Events.Clear();
        var anotherHousehold = await SendAsync(app, "/access/households/{householdId:guid}/export", HumanUser(issued), household: other, householdAdministratorOf: home);
        var unknown = await SendAsync(app, "/access/households/{householdId:guid}/export", HumanUser(issued), household: Guid.NewGuid(), householdAdministratorOf: home);
        Assert.Equal(StatusCodes.Status404NotFound, anotherHousehold.Status);
        Assert.Equal(StatusCodes.Status404NotFound, unknown.Status);
        Assert.Equal(anotherHousehold.Text, unknown.Text);

        // A member who is not an administrator is refused; the server administrator may export any household.
        var member = await SendAsync(app, "/access/households/{householdId:guid}/export", HumanUser(issued), household: home);
        Assert.Equal(StatusCodes.Status403Forbidden, member.Status);
        Assert.Empty(_audit.Events);
        _administrator = issued.Account.Id;
        var any = await SendAsync(app, "/access/households/{householdId:guid}/export", HumanUser(issued), household: other);
        Assert.Equal(StatusCodes.Status200OK, any.Status);
        Assert.Contains("stranger@example.com", any.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mom@example.com", any.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Guid.Empty, mom);
    }

    [Theory]
    [InlineData(nameof(PrincipalKind.ServiceApplication))]
    [InlineData(nameof(PrincipalKind.DelegatedUserClient))]
    public async Task ExportHousehold_IsRefusedForAnApplicationOrServiceCaller_EvenOneActingForAnAdministrator(string kind)
    {
        var issued = await SignInAsync();
        _administrator = issued.Account.Id;
        var home = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO households (id, name, created_at) VALUES (@id, 'Home', '2026-10-21T10:00:00Z')", new { id = GuidSql.ToBlob(home) });
        }

        await using var app = BuildApplication();

        var result = await SendAsync(app, "/access/households/{householdId:guid}/export", ApplicationUser(kind), account: issued.Account.Id, household: home, kind: Enum.Parse<PrincipalKind>(kind));

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task ExportAccount_OfSomeoneWhoIsNotInThisLibrary_Is404WithOnePlainSentence()
    {
        var issued = await SignInAsync();
        _administrator = issued.Account.Id;
        await using var app = BuildApplication();

        var missing = await SendAsync(app, "/access/accounts/{accountId:guid}/export", HumanUser(issued), account: Guid.NewGuid());

        Assert.Equal(StatusCodes.Status404NotFound, missing.Status);
        Assert.Contains("That user isn't in this Library.", missing.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Account not found", missing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProblemOnThisComputer_Is500WithAPlainSentence_ItsInternalMessageIsLoggedNotShown()
    {
        var issued = await SignInAsync();
        _administrator = issued.Account.Id;
        _transfers = new FailingTransfers(new InvalidOperationException("internal: key ring c:\\keys\\secret.xml is unreadable"));
        await using var app = BuildApplication();

        var failed = await SendAsync(app, "/access/accounts/{accountId:guid}/export", HumanUser(issued), account: Guid.NewGuid());

        Assert.Equal(StatusCodes.Status500InternalServerError, failed.Status);
        Assert.Contains("The file couldn't be made on this computer.", failed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.xml", failed.Text, StringComparison.Ordinal);
        Assert.Contains(_logs.Lines, line => line.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task ARealClash_Is409WithTheRefusalSentence()
    {
        var issued = await SignInAsync();
        _administrator = issued.Account.Id;
        _transfers = new FailingTransfers(new UserTransferRefusedException("Someone in this Library already signs in with maya@example.com. Nothing was imported."));
        await using var app = BuildApplication();

        var clash = await SendAsync(app, "/access/accounts/import/{operationId:guid}", HumanUser(issued), operation: Guid.NewGuid());

        Assert.Equal(StatusCodes.Status409Conflict, clash.Status);
        Assert.Contains("Someone in this Library already signs in with maya@example.com. Nothing was imported.", clash.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private async Task<SessionIssueResult> SignInAsync() =>
        await _identity.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");

    /// <summary>A household in this data store: the first one that exists, or a new one.</summary>
    private static Guid EnsureHousehold(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var existing = connection.ExecuteScalar<Guid?>("SELECT id FROM households ORDER BY created_at LIMIT 1");
        if (existing is { } found)
        {
            return found;
        }

        var created = Guid.NewGuid();
        connection.Execute("INSERT INTO households (id, name, created_at) VALUES (@id, 'Home', '2026-10-21T10:00:00Z')", new { id = GuidSql.ToBlob(created) });
        return created;
    }

    private Guid AddOtherPerson(string email, Guid? household = null)
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, role, created_at, household_id) VALUES (@profile, @name, 'StandardUser', @now, @household)",
            new
            {
                profile = GuidSql.ToBlob(profile),
                name = email.StartsWith("maya", StringComparison.Ordinal) ? "Maya" : "Other " + email,
                now = "2026-10-21T10:00:00Z",
                household = household is { } home ? GuidSql.ToBlob(home) : null,
            });
        connection.Execute(
            "INSERT INTO accounts (id, email, normalized_email, created_at, updated_at, profile_id) VALUES (@account, @email, @normalized, @now, @now, @profile)",
            new { account = GuidSql.ToBlob(account), profile = GuidSql.ToBlob(profile), email, normalized = email.ToUpperInvariant(), now = "2026-10-21T10:00:00Z" });
        return account;
    }

    private static ClaimsPrincipal HumanUser(SessionIssueResult issued) => new(new ClaimsIdentity(
    [
        new Claim(TuvimaClaimTypes.PrincipalKind, nameof(PrincipalKind.Human)),
        new Claim(TuvimaClaimTypes.AccountId, issued.Account.Id.ToString("D")),
        new Claim(TuvimaClaimTypes.SessionId, issued.Session.Id.ToString("D")),
    ], "test"));

    private static ClaimsPrincipal ApplicationUser(string kind) => new(new ClaimsIdentity(
        [new Claim(TuvimaClaimTypes.PrincipalKind, kind)], "test"));

    private WebApplication BuildApplication()
    {
        var provider = new EphemeralDataProtectionProvider();
        var codec = new DataProtectionBackupPortableValueCodec(new DataProtectionTwoStepSecretProtector(provider), provider);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IFirstPartyIdentityService>(_identity);
        builder.Services.AddScoped<RecentSignInGuard>();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new ClaimsAuthorityResolver());
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSingleton<IUserTransferService>(_ => _transfers ?? new UserTransferService(
            new PersonalStateBackupRepository(_database, codec),
            new UserImportRepository(_database),
            codec,
            new UserImportStagingArea(Path.Combine(_root, "backups"), _clock, NullLogger<UserImportStagingArea>.Instance),
            new AccountRepository(_database),
            new HouseholdRepository(_database),
            new AdministratorDecisions(() => _administrator),
            _audit,
            _clock));
        var app = builder.Build();
        app.MapUserTransferEndpoints();
        return app;
    }

    private static async Task<Reply> SendAsync(
        WebApplication app,
        string pattern,
        ClaimsPrincipal user,
        Guid? account = null,
        Guid? operation = null,
        byte[]? upload = null,
        PrincipalKind kind = PrincipalKind.Human,
        Guid? household = null,
        Guid? householdAdministratorOf = null,
        UserImportRequestDto? body = null)
    {
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == pattern &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);
        var responseBody = new MemoryStream();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = user,
            Request = { Method = "POST", Path = pattern },
            Response = { Body = responseBody },
        };
        if (account is { } accountId)
        {
            context.Request.RouteValues["accountId"] = accountId.ToString("D");
        }

        if (operation is { } operationId)
        {
            context.Request.RouteValues["operationId"] = operationId.ToString("D");
        }

        if (household is { } householdId)
        {
            context.Request.RouteValues["householdId"] = householdId.ToString("D");
        }

        if (body is not null)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(body, MediaEngineJson.Web);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = payload.Length;
            context.Request.Body = new MemoryStream(payload);
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
        }

        if (householdAdministratorOf is { } administeredHousehold)
        {
            context.Items["householdAdministratorOf"] = administeredHousehold;
        }

        if (upload is not null)
        {
            context.Request.ContentType = "multipart/form-data; boundary=test";
            var files = new FormFileCollection { new FormFile(new MemoryStream(upload), 0, upload.Length, "file", "tuvima-user-20261022-090000.zip") };
            context.Request.Form = new FormCollection(new Dictionary<string, StringValues>(), files);
        }

        context.Items["kind"] = kind;
        await endpoint.RequestDelegate!(context);
        responseBody.Position = 0;
        var bytes = responseBody.ToArray();
        var text = context.Response.ContentType == "application/zip" ? ZipText(bytes) : System.Text.Encoding.UTF8.GetString(bytes);
        var json = bytes.Length > 0 && bytes[0] == (byte)'{' ? JsonDocument.Parse(bytes).RootElement.Clone() : default;
        return new Reply(context.Response.StatusCode, json, text, bytes, context.Response.ContentType, FileName(context.Response.Headers.ContentDisposition.ToString()));
    }

    private static string ZipText(byte[] content)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(content), System.IO.Compression.ZipArchiveMode.Read);
        var text = new System.Text.StringBuilder();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
            text.AppendLine(reader.ReadToEnd());
        }

        return text.ToString();
    }

    private static string? FileName(string disposition)
    {
        const string marker = "filename=";
        var start = disposition.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return start < 0 ? null : disposition[(start + marker.Length)..].Split(';')[0].Trim('"');
    }

    public void Dispose()
    {
        _database.Dispose();

        // Best-effort cleanup of temporary files; a locked file must not fail the test run.
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed record Reply(int Status, JsonElement Json, string Text, byte[] Bytes, string? ContentType, string? DownloadName);

    private sealed class ClaimsAuthorityResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default)
        {
            var kind = context.Items.TryGetValue("kind", out var value) && value is PrincipalKind principal ? principal : PrincipalKind.Human;
            var account = Guid.TryParse(context.User.FindFirstValue(TuvimaClaimTypes.AccountId), out var id) ? id : (Guid?)null;
            var administered = context.Items.TryGetValue("householdAdministratorOf", out var home) && home is Guid householdId ? householdId : (Guid?)null;
            return ValueTask.FromResult(new RequestAuthority(
                kind, true, AccountId: account, ActiveProfileId: Guid.NewGuid(), ApplicationId: kind == PrincipalKind.Human ? null : Guid.NewGuid(),
                AccountEnabled: administered is not null, ActiveProfileInUserHousehold: administered is not null,
                ActiveProfileHouseholdId: administered, ActiveProfileIsHouseholdAdministrator: administered is not null));
        }
    }

    private sealed class AdministratorDecisions(Func<Guid> administrator) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(authority.AccountId == administrator()
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }

    private sealed class RecordingAudit : IAuthorizationAuditWriter
    {
        public List<AuthorizationAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingTransfers(Exception failure) : IUserTransferService
    {
        public Task<UserExportDownload> ExportOwnAsync(RequestAuthority actor, CancellationToken ct) => throw failure;

        public Task<UserExportDownload> ExportAsync(RequestAuthority actor, Guid accountId, CancellationToken ct) => throw failure;

        public Task<UserExportDownload> ExportHouseholdAsync(RequestAuthority actor, Guid householdId, CancellationToken ct) => throw failure;

        public Task<UserImportPreviewDto> InspectAsync(RequestAuthority actor, Stream upload, string originalFileName, CancellationToken ct) => throw failure;

        public Task<UserImportResultDto> ImportAsync(RequestAuthority actor, Guid operationId, UserImportRequestDto request, CancellationToken ct) => throw failure;
    }

    /// <summary>Keeps what is logged, whatever the category.</summary>
    private sealed class RecordingLogs : ILoggerProvider, ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));

        public void Dispose()
        {
        }
    }

    private sealed class ClockStub(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
