using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Activity;
using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Builds the pieces of the personal activity record over a real data store.</summary>
internal static class PersonalActivityTestKit
{
    public static PersonalActivityArtworkService CreateArtwork(IDatabaseConnection database) => new(
        new WorkRepository(database),
        new EntityAssetRepository(database),
        new PersonalActivityRepository(database),
        NullLogger<PersonalActivityArtworkService>.Instance);

    public static PersonalActivityRecorder CreateRecorder(
        IDatabaseConnection database,
        IPersonalActivityRepository? repository = null,
        ILogger<PersonalActivityRecorder>? logger = null,
        TimeProvider? clock = null) => new(
        repository ?? new PersonalActivityRepository(database),
        CreateArtwork(database),
        clock ?? TimeProvider.System,
        logger ?? NullLogger<PersonalActivityRecorder>.Instance);
}

/// <summary>A logger that remembers what was logged, so a test can see that a failure was reported and not swallowed.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception), exception));
}

internal sealed class ManualActivityClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Two households, a server administrator and a paired app over a temp data store, with the real scope authorizer, audit
/// writer and activity read service. Each person has an account (the audit log refers to accounts) and a profile.
/// </summary>
internal sealed class PersonalActivityWorld : IAsyncDisposable
{
    private readonly string _path;
    private readonly Guid _lockedSession = Guid.NewGuid();

    private PersonalActivityWorld(DatabaseConnection database, string path)
    {
        _path = path;
        Database = database;
        Repository = new PersonalActivityRepository(database);
        var accounts = new AccountRepository(database);
        Scope = new PersonalScopeAuthorizer(
            new Decisions(_lockedSession),
            new ProfileRepository(database),
            new HouseholdRepository(database),
            accounts,
            new AuthorizationAuditWriter(accounts),
            Clock);
        Service = new PersonalActivityReadService(Repository, Scope, new AuthorizationAuditWriter(accounts), Clock);
    }

    public DatabaseConnection Database { get; }
    public PersonalActivityRepository Repository { get; }
    public PersonalScopeAuthorizer Scope { get; }
    public PersonalActivityReadService Service { get; }
    public ManualActivityClock Clock { get; } = new(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));

    public Guid HouseholdA { get; } = Guid.NewGuid();
    public Guid HouseholdB { get; } = Guid.NewGuid();

    public Person ServerPerson { get; private set; } = null!;
    public Person AdminA { get; private set; } = null!;
    public Person MemberA { get; private set; } = null!;
    public Person KidA { get; private set; } = null!;
    public Person AdminB { get; private set; } = null!;
    public Person MemberB { get; private set; } = null!;

    public RequestAuthority ServerAdmin { get; private set; } = null!;
    public RequestAuthority LockedServerAdmin { get; private set; } = null!;
    public RequestAuthority AdminOfA { get; private set; } = null!;
    public RequestAuthority MemberOfA { get; private set; } = null!;
    public RequestAuthority KidOfA { get; private set; } = null!;
    public RequestAuthority AdminOfB { get; private set; } = null!;
    public RequestAuthority ServerAdminApp { get; private set; } = null!;

    public static PersonalActivityWorld Create()
    {
        DapperConfiguration.Configure();
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-activity-{Guid.NewGuid():N}.db");
        var database = new DatabaseConnection(path);
        database.InitializeSchema();
        database.RunStartupChecks();
        var world = new PersonalActivityWorld(database, path);
        world.SeedPeople();
        return world;
    }

    public async Task<Guid> AddAsync(
        Person who,
        string kind,
        string title,
        DateTimeOffset at,
        string? subtitle = null,
        string? parentTitle = null,
        string mediaType = "Movies",
        bool finished = false,
        double? percent = 10,
        long played = 600,
        string? artwork = null,
        string? device = "Dashboard",
        Guid? work = null)
    {
        var session = Guid.NewGuid();
        if (kind == PersonalActivityKinds.MyList)
        {
            await Repository.AddMyListAsync(who.ProfileId, at,
                new PersonalActivitySnapshot(mediaType, title, subtitle, parentTitle, null, work ?? Guid.NewGuid(), null, artwork));
            return session;
        }

        await Repository.AddPlaybackAsync(who.ProfileId, kind, mediaType == "Music", session, at,
            new PersonalActivityDevice(device, "web"),
            new PersonalActivitySnapshot(mediaType, title, subtitle, parentTitle, 2000, work ?? Guid.NewGuid(), Guid.NewGuid(), artwork),
            new PersonalActivityProgress(at.AddSeconds(played), played, percent, finished));
        return session;
    }

    public int AuditCount(string eventType)
    {
        using var connection = Database.CreateConnection();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM authorization_audit_events WHERE event_type = @eventType;", new { eventType });
    }

    public IReadOnlyList<AuditRow> AuditRows(string eventType)
    {
        using var connection = Database.CreateConnection();
        return connection.Query<AuditRow>("""
            SELECT actor_profile_id AS ActorProfileId, subject_type AS SubjectType, subject_id AS SubjectId, changes_json AS ChangesJson
            FROM authorization_audit_events WHERE event_type = @eventType ORDER BY id;
            """, new { eventType }).ToList();
    }

    public ValueTask DisposeAsync()
    {
        Database.Dispose();
        TestTemp.ReleaseSqlitePool(_path);
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        return ValueTask.CompletedTask;
    }

    private void SeedPeople()
    {
        using var connection = Database.CreateConnection();
        var now = Clock.GetUtcNow().ToString("O");
        connection.Execute(
            "INSERT INTO households (id, name, created_at) VALUES (@a, 'Household A', @now), (@b, 'Household B', @now);",
            new { a = HouseholdA, b = HouseholdB, now });

        ServerPerson = SeedPerson(connection, "Server", null, isServerAdmin: true, isHouseholdAdmin: false);
        AdminA = SeedPerson(connection, "Alex", HouseholdA, isServerAdmin: false, isHouseholdAdmin: true);
        MemberA = SeedPerson(connection, "Friend of Alex", HouseholdA, isServerAdmin: false, isHouseholdAdmin: false);
        KidA = SeedPerson(connection, "Kid of Alex", HouseholdA, isServerAdmin: false, isHouseholdAdmin: false);
        AdminB = SeedPerson(connection, "Bea", HouseholdB, isServerAdmin: false, isHouseholdAdmin: true);
        MemberB = SeedPerson(connection, "Friend of Bea", HouseholdB, isServerAdmin: false, isHouseholdAdmin: false);

        ServerAdmin = Authority(ServerPerson);
        LockedServerAdmin = Authority(ServerPerson, session: _lockedSession);
        AdminOfA = Authority(AdminA);
        MemberOfA = Authority(MemberA);
        KidOfA = Authority(KidA, restricted: true);
        AdminOfB = Authority(AdminB);
        ServerAdminApp = ServerAdmin with
        {
            PrincipalKind = PrincipalKind.DelegatedUserClient,
            ApplicationId = Guid.NewGuid(),
            ApplicationEnabled = true,
        };
    }

    private static Person SeedPerson(
        System.Data.IDbConnection connection, string name, Guid? household, bool isServerAdmin, bool isHouseholdAdmin)
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        connection.Execute("""
            INSERT INTO accounts(id,email,normalized_email,is_enabled,is_administrator,authorization_version,created_at,updated_at)
            VALUES(@account,@email,@normalized,1,@admin,1,@now,@now);
            INSERT INTO profiles (id, display_name, created_at, household_id) VALUES (@profile, @name, @now, @household);
            """, new
        {
            account,
            profile,
            name,
            household,
            admin = isServerAdmin ? 1 : 0,
            email = $"{account:N}@example.test",
            normalized = $"{account:N}@EXAMPLE.TEST",
            now,
        });
        return new Person(name, account, profile, household, isServerAdmin, isHouseholdAdmin);
    }

    private static RequestAuthority Authority(Person person, Guid? session = null, bool restricted = false) => new(
        PrincipalKind.Human, true,
        AccountId: person.AccountId,
        ActiveProfileId: person.ProfileId,
        SessionId: session ?? Guid.NewGuid(),
        AccountEnabled: true,
        GrantEnabled: true,
        AccountIsAdministrator: person.IsServerAdmin,
        GrantAdminEnabled: person.IsServerAdmin,
        ActiveProfileIsRestricted: restricted,
        AccountHouseholdId: person.Household,
        AccountIsHouseholdAdmin: person.IsHouseholdAdmin);

    internal sealed record Person(string Name, Guid AccountId, Guid ProfileId, Guid? Household, bool IsServerAdmin, bool IsHouseholdAdmin);

    internal sealed class AuditRow
    {
        public Guid? ActorProfileId { get; init; }
        public string SubjectType { get; init; } = string.Empty;
        public string SubjectId { get; init; } = string.Empty;
        public string ChangesJson { get; init; } = string.Empty;
    }

    /// <summary>The real rule for who is an administrator; one session can be marked as not yet unlocked.</summary>
    private sealed class Decisions(Guid lockedSession) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateHouseholdAdministratorAsync(RequestAuthority authority,
            bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(!authority.IsEffectiveHouseholdAdministrator
                ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired)
                : requireSurfaceUnlock && authority.SessionId == lockedSession
                    ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorUnlockRequired)
                    : AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(!authority.IsEffectiveAdministrator
                ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired)
                : requireSurfaceUnlock && authority.SessionId == lockedSession
                    ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorUnlockRequired)
                    : AuthorizationDecision.Allow());
    }
}
