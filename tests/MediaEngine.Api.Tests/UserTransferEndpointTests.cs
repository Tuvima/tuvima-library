using System.Security.Claims;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Api.Services.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The routes that move one user between Libraries (backup and restore spec D19), run in memory against a real data store and the real
/// "confirm it's you" check: a stale sign-in is asked to confirm first on every route, application and service callers cannot export
/// anyone's sign-in material, and only an administrator exports another person or imports one.
/// </summary>
public sealed class UserTransferEndpointTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-user-transfer-endpoints-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly FirstPartyIdentityService _identity;
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingAudit _audit = new();
    private Guid _administrator;

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
    [InlineData("/access/accounts/import/inspect")]
    [InlineData("/access/accounts/import/{operationId:guid}")]
    public async Task EveryRoute_AsksAStaleSignInToConfirmFirst_AndDoesNothing(string route)
    {
        var issued = await SignInAsync();
        await using var app = BuildApplication();

        _clock.Advance(TimeSpan.FromMinutes(11));
        var stale = await SendAsync(app, route, HumanUser(issued), account: Guid.NewGuid(), operation: Guid.NewGuid(), upload: []);

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
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM accounts WHERE email = 'maya@example.com'");
            connection.Execute("DELETE FROM profiles WHERE display_name = 'Maya'");
        }

        _administrator = Guid.Empty;
        var refusedInspect = await SendAsync(app, "/access/accounts/import/inspect", HumanUser(issued), upload: file.Bytes);
        Assert.Equal(StatusCodes.Status403Forbidden, refusedInspect.Status);

        _administrator = issued.Account.Id;
        var preview = await SendAsync(app, "/access/accounts/import/inspect", HumanUser(issued), upload: file.Bytes);
        Assert.Equal(StatusCodes.Status200OK, preview.Status);
        Assert.Equal("maya@example.com", preview.Json.GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, preview.Json.GetProperty("blocked_reason").ValueKind);
        Assert.Equal("Maya", preview.Json.GetProperty("profiles")[0].GetString());
        var operation = Guid.Parse(preview.Json.GetProperty("operation_id").GetString()!);

        _administrator = Guid.Empty;
        var refusedImport = await SendAsync(app, "/access/accounts/import/{operationId:guid}", HumanUser(issued), operation: operation);
        Assert.Equal(StatusCodes.Status403Forbidden, refusedImport.Status);

        _administrator = issued.Account.Id;
        var imported = await SendAsync(app, "/access/accounts/import/{operationId:guid}", HumanUser(issued), operation: operation);
        Assert.Equal(StatusCodes.Status200OK, imported.Status);
        Assert.Equal(exportingPerson, imported.Json.GetProperty("account_id").GetGuid());
        Assert.Equal("maya@example.com", imported.Json.GetProperty("display_name").GetString());

        var again = await SendAsync(app, "/access/accounts/import/{operationId:guid}", HumanUser(issued), operation: operation);
        Assert.Equal(StatusCodes.Status404NotFound, again.Status);
        Assert.Contains(_audit.Events, item => item.EventType == "account.imported" && item.SubjectId == exportingPerson.ToString("D"));
    }

    [Fact]
    public async Task Inspect_OfAFileForAUserWhoIsAlreadyHere_SaysSoInOneSentence()
    {
        var issued = await SignInAsync();
        _administrator = issued.Account.Id;
        var maya = AddOtherPerson("maya@example.com");
        await using var app = BuildApplication();
        var file = await SendAsync(app, "/access/accounts/{accountId:guid}/export", HumanUser(issued), account: maya);

        var preview = await SendAsync(app, "/access/accounts/import/inspect", HumanUser(issued), upload: file.Bytes);

        // Maya is still here, so the file clashes with her own account.
        Assert.Equal(StatusCodes.Status200OK, preview.Status);
        Assert.Equal("This user is already in this Library. Nothing was imported.", preview.Json.GetProperty("blocked_reason").GetString());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private async Task<SessionIssueResult> SignInAsync() =>
        await _identity.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");

    private Guid AddOtherPerson(string email)
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO accounts (id, email, normalized_email, created_at, updated_at) VALUES (@account, @email, @normalized, @now, @now)",
            new { account = GuidSql.ToBlob(account), email, normalized = email.ToUpperInvariant(), now = "2026-10-21T10:00:00Z" });
        connection.Execute(
            "INSERT INTO profiles (id, display_name, role, created_at) VALUES (@profile, @name, 'StandardUser', @now)",
            new { profile = GuidSql.ToBlob(profile), name = email.StartsWith("maya", StringComparison.Ordinal) ? "Maya" : "Other " + email, now = "2026-10-21T10:00:00Z" });
        connection.Execute(
            "INSERT INTO account_profile_grants (account_id, profile_id, is_default, is_enabled, admin_enabled, granted_at) VALUES (@account, @profile, 1, 1, 0, @now)",
            new { account = GuidSql.ToBlob(account), profile = GuidSql.ToBlob(profile), now = "2026-10-21T10:00:00Z" });
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
        builder.Services.AddSingleton<IUserTransferService>(_ => new UserTransferService(
            new PersonalStateBackupRepository(_database, codec),
            new UserImportRepository(_database),
            codec,
            new UserImportStagingArea(Path.Combine(_root, "backups"), _clock, NullLogger<UserImportStagingArea>.Instance),
            new AccountRepository(_database),
            new AdministratorDecisions(() => _administrator),
            _audit,
            _clock));
        var app = builder.Build();
        app.MapUserTransferEndpoints();
        return app;
    }

    private static async Task<Reply> SendAsync(
        WebApplication app, string pattern, ClaimsPrincipal user, Guid? account = null, Guid? operation = null, byte[]? upload = null, PrincipalKind kind = PrincipalKind.Human)
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

    private sealed record Reply(int Status, JsonElement Json, string Text, byte[] Bytes, string? ContentType, string? DownloadName);

    private sealed class ClaimsAuthorityResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default)
        {
            var kind = context.Items.TryGetValue("kind", out var value) && value is PrincipalKind principal ? principal : PrincipalKind.Human;
            var account = Guid.TryParse(context.User.FindFirstValue(TuvimaClaimTypes.AccountId), out var id) ? id : (Guid?)null;
            return ValueTask.FromResult(new RequestAuthority(
                kind, true, AccountId: account, ActiveProfileId: Guid.NewGuid(), ApplicationId: kind == PrincipalKind.Human ? null : Guid.NewGuid()));
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
