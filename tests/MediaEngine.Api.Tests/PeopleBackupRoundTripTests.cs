using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A full backup carries the people (users, profiles, households, roles and access) as configuration, and a backup or user file made
/// with another data structure is refused in one plain sentence (users spec D1, D2, D11). Real data stores, the real backup and restore.
/// </summary>
public sealed class PeopleBackupRoundTripTests : IDisposable
{
    private const string Stamp = "2026-10-21T10:00:00Z";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-people-backup-{Guid.NewGuid():N}");

    public PeopleBackupRoundTripTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AFullBackupOfShysExample_RestoresIntoAFreshLibrary_WithEveryUserOpeningTheirOwnProfile_AndRolesAndAccessIntact()
    {
        var (config, dbPath) = NewLibrary("shy");
        var people = new People();
        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            people.Seed(database);
            var service = BackupTestKit.Service(database, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        using var fresh = BackupTestKit.OpenStore(dbPath);
        Assert.Equal(PendingRestoreImportOutcome.Imported, BackupTestKit.Importer(fresh, config).ImportIfPending());

        var accounts = new AccountRepository(fresh);
        foreach (var (user, profile, email) in new[]
        {
            (people.ShyUser, people.Shy, "shy@tuvima.com"),
            (people.MomUser, people.Mom, "mom@home.com"),
            (people.Kid1User, people.Kid1, "kid@home.com"),
            (people.PatUser, people.Pat, "pat@other.com"),
        })
        {
            // Every user signs in to their own profile, with the same password.
            Assert.Equal(profile, await accounts.GetDefaultProfileIdAsync(user));
            using var connection = fresh.CreateConnection();
            Assert.Equal(email, connection.ExecuteScalar<string>("SELECT email FROM accounts WHERE id = @user", new { user = GuidSql.ToBlob(user) }));
            Assert.Equal("HASH-" + email, connection.ExecuteScalar<string>("SELECT secret_hash FROM account_credentials WHERE account_id = @user", new { user = GuidSql.ToBlob(user) }));
        }

        // Roles.
        var shy = (await accounts.GetActiveProfileAccessAsync(people.ShyUser, people.Shy))!;
        Assert.True(shy.IsServerAdministrator);
        Assert.True(shy.IsHouseholdAdministrator);
        var mom = (await accounts.GetActiveProfileAccessAsync(people.MomUser, people.Mom))!;
        Assert.False(mom.IsServerAdministrator);
        Assert.True(mom.IsHouseholdAdministrator);
        var kid = (await accounts.GetActiveProfileAccessAsync(people.Kid1User, people.Kid1))!;
        Assert.False(kid.IsServerAdministrator);
        Assert.False(kid.IsHouseholdAdministrator);
        Assert.Equal(MediaEngine.Domain.Enums.ProfileRole.RestrictedProfile, kid.Role);
        Assert.Equal(people.Household1, shy.HouseholdId);
        Assert.Equal(people.Household2, (await accounts.GetActiveProfileAccessAsync(people.PatUser, people.Pat))!.HouseholdId);

        // Access, content limits, the default household and the optional administrator PIN question.
        using var check = fresh.CreateConnection();
        // A library is on or off per household: the household's list comes back, and no per-profile table exists.
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM household_library_hidden WHERE household_id = @id", new { id = GuidSql.ToBlob(people.Household1) }));
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM household_library_hidden WHERE household_id = @id", new { id = GuidSql.ToBlob(people.Household2) }));
        // The household's Kid settings come back with the household; a Kid's profile has no limit of its own.
        var kidSettings = check.QuerySingle<(string Watch, string Read, string Listen, long Unrated)>(
            "SELECT kid_watch_level, kid_read_level, kid_listen_level, kid_show_unrated FROM households WHERE id = @id", new { id = GuidSql.ToBlob(people.Household1) });
        Assert.Equal(("PG-13", "G", "R", 0L), kidSettings);
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT is_default FROM households WHERE id = @id", new { id = GuidSql.ToBlob(people.Household1) }));
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT is_default FROM households WHERE id = @id", new { id = GuidSql.ToBlob(people.Household2) }));
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT is_enabled FROM profile_admin_protections WHERE profile_id = @id", new { id = GuidSql.ToBlob(people.Shy) }));
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM accounts WHERE profile_id = @id", new { id = GuidSql.ToBlob(people.Kid2) }));
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE id = @id", new { id = GuidSql.ToBlob(people.Kid2) }));
    }

    [Fact]
    public async Task ABackupMadeWithAnEarlierDataStructure_IsRefusedWithTheOlderVersionSentence_OnEveryRoute()
    {
        var (config, dbPath) = NewLibrary("older");
        using var database = BackupTestKit.OpenStore(dbPath);
        new People().Seed(database);
        var service = BackupTestKit.Service(database, config);
        var path = await service.CreateAsync(CancellationToken.None);
        var older = Path.Combine(Path.GetDirectoryName(path)!, "tuvima-backup-20200101-000000.zip");
        File.WriteAllBytes(older, WithManifestEpoch(File.ReadAllBytes(path), "guid-blob-v12-account-held-access"));

        Assert.Equal(RestoreArchiveValidator.OlderVersionMessage, Assert.Throws<InvalidDataException>(() => RestoreArchiveValidator.Validate(older)).Message);
        Assert.Equal(RestoreArchiveValidator.OlderVersionMessage, Assert.Throws<InvalidDataException>(() => service.ScheduleRestore(Path.GetFileName(older))).Message);
        Assert.Equal(RestoreArchiveValidator.OlderVersionMessage, Assert.Throws<InvalidDataException>(() => service.ValidateRestore(Path.GetFileName(older))).Message);

        // A manifest with no epoch at all is just as old; a later one is not "older".
        File.WriteAllBytes(older, WithManifestEpoch(File.ReadAllBytes(path), null));
        Assert.Equal(RestoreArchiveValidator.OlderVersionMessage, Assert.Throws<InvalidDataException>(() => RestoreArchiveValidator.Validate(older)).Message);
        File.WriteAllBytes(older, WithManifestEpoch(File.ReadAllBytes(path), "guid-blob-v99-future"));
        Assert.Contains("different version", Assert.Throws<InvalidDataException>(() => RestoreArchiveValidator.Validate(older)).Message, StringComparison.Ordinal);

        // The current one is accepted.
        RestoreArchiveValidator.Validate(path);
    }

    /// <summary>The archive with its manifest's <c>database_epoch</c> set (or removed when null), as a file from another version would carry it.</summary>
    private static byte[] WithManifestEpoch(byte[] archiveBytes, string? epoch)
    {
        using var stream = new MemoryStream();
        stream.Write(archiveBytes);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry("manifest.json")!;
            string json;
            using (var reader = new StreamReader(entry.Open()))
            {
                json = reader.ReadToEnd();
            }

            var node = JsonNode.Parse(json)!.AsObject();
            if (epoch is null)
            {
                node.Remove("database_epoch");
            }
            else
            {
                node["database_epoch"] = epoch;
            }

            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false));
            writer.Write(node.ToJsonString());
        }

        return stream.ToArray();
    }

    private (string Config, string DbPath) NewLibrary(string name)
    {
        var config = Path.Combine(_root, name + "-config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        return (config, Path.Combine(_root, name + ".db"));
    }

    public void Dispose()
    {
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

    /// <summary>
    /// Shy's example: Household 1 (the default) with Shy (server and household administrator), Mom (household administrator), Kid 1 (Kids,
    /// with a sign-in) and Kid 2 (no sign-in); Household 2 with Pat, who looks after it.
    /// </summary>
    private sealed class People
    {
        public Guid Household1 { get; } = Guid.NewGuid();

        public Guid Household2 { get; } = Guid.NewGuid();

        public Guid Shy { get; } = Guid.NewGuid();

        public Guid Mom { get; } = Guid.NewGuid();

        public Guid Kid1 { get; } = Guid.NewGuid();

        public Guid Kid2 { get; } = Guid.NewGuid();

        public Guid Pat { get; } = Guid.NewGuid();

        public Guid ShyUser { get; } = Guid.NewGuid();

        public Guid MomUser { get; } = Guid.NewGuid();

        public Guid Kid1User { get; } = Guid.NewGuid();

        public Guid PatUser { get; } = Guid.NewGuid();

        public void Seed(DatabaseConnection database)
        {
            using var c = database.CreateConnection();
            c.Execute("DELETE FROM households");
            foreach (var (id, name, isDefault) in new[] { (Household1, "Household 1", 1), (Household2, "Household 2", 0) })
            {
                c.Execute("INSERT INTO households(id,name,created_at,is_default) VALUES(@id,@name,@now,@isDefault)", new { id = GuidSql.ToBlob(id), name, now = Stamp, isDefault });
            }

            c.Execute(
                "UPDATE households SET kid_watch_level='PG-13', kid_read_level='G', kid_listen_level='R', kid_show_unrated=0 WHERE id=@id",
                new { id = GuidSql.ToBlob(Household1) });
            AddProfile(c, Shy, "Shy", Household1, "StandardUser", server: 1, householdAdmin: 1);
            AddProfile(c, Mom, "Mom", Household1, "StandardUser", householdAdmin: 1);
            AddProfile(c, Kid1, "Kid 1", Household1, "RestrictedProfile");
            AddProfile(c, Kid2, "Kid 2", Household1, "RestrictedProfile");
            AddProfile(c, Pat, "Pat", Household2, "StandardUser", householdAdmin: 1);
            AddUser(c, ShyUser, Shy, "shy@tuvima.com");
            AddUser(c, MomUser, Mom, "mom@home.com");
            AddUser(c, Kid1User, Kid1, "kid@home.com");
            AddUser(c, PatUser, Pat, "pat@other.com");
            c.Execute(
                "INSERT INTO household_library_hidden(household_id,library_id,created_at) VALUES(@id,@library,@now)",
                new { id = GuidSql.ToBlob(Household1), library = GuidSql.ToBlob(Guid.NewGuid()), now = Stamp });
            c.Execute(
                "INSERT INTO profile_admin_protections(profile_id,is_enabled,unlock_mode,unlock_minutes,protection_version,updated_at) VALUES(@id,1,'FixedDuration',30,1,@now)",
                new { id = GuidSql.ToBlob(Shy), now = Stamp });
        }

        private static void AddProfile(
            Microsoft.Data.Sqlite.SqliteConnection c, Guid id, string name, Guid household, string role, int server = 0, int householdAdmin = 0) =>
            c.Execute(
                """
                INSERT INTO profiles(id,display_name,avatar_color,role,created_at,household_id,is_server_admin,is_household_admin)
                VALUES(@id,@name,'#7C4DFF',@role,@now,@household,@server,@householdAdmin)
                """,
                new { id = GuidSql.ToBlob(id), name, role, now = Stamp, household = GuidSql.ToBlob(household), server, householdAdmin });

        private static void AddUser(Microsoft.Data.Sqlite.SqliteConnection c, Guid id, Guid profile, string email)
        {
            c.Execute(
                "INSERT INTO accounts(id,email,normalized_email,created_at,updated_at,profile_id) VALUES(@id,@email,@normalized,@now,@now,@profile)",
                new { id = GuidSql.ToBlob(id), email, normalized = email.ToUpperInvariant(), now = Stamp, profile = GuidSql.ToBlob(profile) });
            c.Execute(
                """
                INSERT INTO account_credentials(id,account_id,credential_kind,secret_hash,hash_scheme,hash_version,security_stamp,created_at,updated_at)
                VALUES(@credential,@id,'Password',@hash,'pbkdf2',1,'stamp',@now,@now)
                """,
                new { credential = GuidSql.ToBlob(Guid.NewGuid()), id = GuidSql.ToBlob(id), hash = "HASH-" + email, now = Stamp });
        }
    }
}
