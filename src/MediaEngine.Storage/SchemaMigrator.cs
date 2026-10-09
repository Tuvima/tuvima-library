using Dapper;
using MediaEngine.Domain;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

internal sealed class SchemaMigrator
{
    /// <summary>The most profiles one account's household can hold.</summary>
    internal const int MaximumProfilesPerAccount = 8;

    private readonly List<string> _notes = [];

    /// <summary>
    /// One line for each change a startup migration made to existing data (for example a profile that moved),
    /// so the host can write them to its log. Empty when nothing was changed.
    /// </summary>
    public IReadOnlyList<string> Notes => _notes;

    public void RunStartupTasks(SqliteConnection conn)
    {
        EnsureIdentitySchema(conn);
        RetireLocalOnlyAccounts(conn);
        EnsureHouseholds(conn);
        EnsureOnboardingSchema(conn);
        EnsureProviderConnectionCheckSchema(conn);
        EnsureAdaptiveDeliverySchema(conn);
        EnsureExpandedArtworkAssetTypes(conn);
        EnsureEditionArtworkOwnerSchema(conn);
        EnsureCanonicalArtworkSchema(conn);
        EnsureArtworkWritebackSchema(conn);
        EnsureMediaEditorCommitSchema(conn);
        EnsureAssetRenditionSchema(conn);
        var sessionsGainedIngress = !ColumnExists(conn, "auth_sessions", "issued_ingress");
        EnsureCurrentColumns(conn);
        EnsureHouseholdAdministrators(conn);
        EnsurePerHouseholdSharedLibrary(conn);
        if (sessionsGainedIngress)
        {
            // Runs once, in the same upgrade that introduces the child-profile rule.
            PromoteRestrictedAdministratorProfiles(conn);
        }

        EnsureCurrentIndexes(conn);
        using (var recordingIndex = conn.CreateCommand())
        {
            recordingIndex.CommandText = "SELECT sql FROM sqlite_master WHERE name='ux_works_child_parent_ordinal_sort'";
            var definition = recordingIndex.ExecuteScalar() as string;
            if (definition?.Contains("parent_key IS NULL", StringComparison.Ordinal) != true)
            {
                recordingIndex.CommandText = """
                    DROP INDEX IF EXISTS ux_works_child_parent_ordinal_sort;
                    CREATE UNIQUE INDEX ux_works_child_parent_ordinal_sort ON works(parent_work_id,ordinal_sort)
                    WHERE work_kind IN ('child','catalog') AND parent_work_id IS NOT NULL AND ordinal_sort IS NOT NULL
                    AND (media_type != 'Audiobooks' OR parent_key IS NULL);
                    """;
                recordingIndex.ExecuteNonQuery();
            }
        }
        SeedMetadataProviders(conn);
        SeedDefaultProfile(conn);
        MigrateLegacyProfileLists(conn);
    }

    /// <summary>
    /// Every account signs in with an email, so email-less "local-only" accounts are retired. Each one's profiles
    /// move into the household of the server administrator account (history stays attached to the profile), the
    /// local-only account and its sessions are removed, and <c>accounts</c> is rebuilt with a required email and
    /// without the <c>is_local_only</c> column. Safe to run on every startup: once the column is gone it does nothing.
    /// </summary>
    private void RetireLocalOnlyAccounts(SqliteConnection conn)
    {
        if (!ColumnExists(conn, "accounts", "is_local_only"))
        {
            return;
        }

        MoveLocalOnlyProfilesToAdministrator(conn);
        RebuildAccountsTable(conn);
    }

    private void MoveLocalOnlyProfilesToAdministrator(SqliteConnection conn)
    {
        var movedNotes = new List<string>();
        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            bool hasLocalOnly;
            using (var count = conn.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM accounts WHERE is_local_only = 1;";
                hasLocalOnly = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
            }

            if (!hasLocalOnly)
            {
                return;
            }

            // Profiles that someone could still open through an enabled grant on a local-only account.
            var movingRows = new List<(Guid ProfileId, string Name, Guid FromAccountId)>();
            using (var read = conn.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = """
                    SELECT DISTINCT p.id, p.display_name, a.id
                    FROM account_profile_grants g
                    JOIN accounts a ON a.id = g.account_id
                    JOIN profiles p ON p.id = g.profile_id
                    WHERE a.is_local_only = 1 AND g.is_enabled = 1
                    ORDER BY p.display_name, p.id, a.id;
                    """;
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    movingRows.Add((GuidSql.FromDb(reader.GetValue(0)), reader.GetString(1), GuidSql.FromDb(reader.GetValue(2))));
                }
            }

            if (movingRows.Count > 0)
            {
                MoveProfiles(conn, transaction, movingRows, movedNotes);
            }

            using var cleanup = conn.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = """
                DELETE FROM auth_sessions WHERE account_id IN (SELECT id FROM accounts WHERE is_local_only = 1);
                DELETE FROM accounts WHERE is_local_only = 1;
                """;
            cleanup.ExecuteNonQuery();
        });
        _notes.AddRange(movedNotes);
    }

    private static void MoveProfiles(
        SqliteConnection conn,
        SqliteTransaction transaction,
        List<(Guid ProfileId, string Name, Guid FromAccountId)> movingRows,
        List<string> notes)
    {
        Guid administratorId;
        string administratorEmail;
        using (var find = conn.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = """
                SELECT id, email FROM accounts
                WHERE is_local_only = 0 AND is_administrator = 1 AND is_enabled = 1
                ORDER BY created_at, id
                LIMIT 1;
                """;
            using var reader = find.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    "Startup stopped: email-less local-only accounts were found, but no enabled administrator account with an email exists to take over their profiles ("
                    + string.Join(", ", movingRows.Select(row => $"'{row.Name}'").Distinct())
                    + "). Nothing was changed. Run `tuvima-admin auth reset-password --email <address>` for an administrator account, then start the Engine again.");
            }

            administratorId = GuidSql.FromDb(reader.GetValue(0));
            administratorEmail = reader.GetString(1);
        }

        var held = new HashSet<Guid>();
        using (var existing = conn.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT profile_id FROM account_profile_grants WHERE account_id = @account;";
            existing.Parameters.Add("@account", SqliteType.Blob).Value = GuidSql.ToBlob(administratorId);
            using var reader = existing.ExecuteReader();
            while (reader.Read())
            {
                held.Add(GuidSql.FromDb(reader.GetValue(0)));
            }
        }

        var toMove = movingRows
            .Where(row => !held.Contains(row.ProfileId))
            .Select(row => (row.ProfileId, row.Name))
            .Distinct()
            .ToList();
        if (held.Count + toMove.Count > MaximumProfilesPerAccount)
        {
            throw new InvalidOperationException(
                $"Startup stopped: moving the profiles of email-less local-only accounts ({string.Join(", ", toMove.Select(profile => $"'{profile.Name}'"))}) "
                + $"into the administrator account {administratorEmail} would give it {held.Count + toMove.Count} profiles, and a household holds at most {MaximumProfilesPerAccount}. "
                + "Nothing was changed. Delete profiles you no longer use (with the previous release, or from a restored copy of your data store), then start the Engine again. "
                + $"If you are locked out of {administratorEmail}, `tuvima-admin auth reset-password --email {administratorEmail}` restores sign-in.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var profile in toMove)
        {
            using var grant = conn.CreateCommand();
            grant.Transaction = transaction;
            grant.CommandText = """
                INSERT OR IGNORE INTO account_profile_grants
                    (account_id, profile_id, is_default, is_enabled, admin_enabled, authorization_version, granted_at)
                VALUES (@account, @profile, 0, 1, 0, 1, @grantedAt);
                """;
            grant.Parameters.Add("@account", SqliteType.Blob).Value = GuidSql.ToBlob(administratorId);
            grant.Parameters.Add("@profile", SqliteType.Blob).Value = GuidSql.ToBlob(profile.ProfileId);
            grant.Parameters.AddWithValue("@grantedAt", now);
            grant.ExecuteNonQuery();
        }

        if (toMove.Count > 0)
        {
            using var bump = conn.CreateCommand();
            bump.Transaction = transaction;
            bump.CommandText = "UPDATE accounts SET authorization_version = authorization_version + 1, updated_at = @now WHERE id = @account;";
            bump.Parameters.Add("@account", SqliteType.Blob).Value = GuidSql.ToBlob(administratorId);
            bump.Parameters.AddWithValue("@now", now);
            bump.ExecuteNonQuery();
        }

        // Only profiles that were actually granted to the administrator get a note; ones already in the household did not move.
        foreach (var row in movingRows.Where(row => !held.Contains(row.ProfileId)))
        {
            notes.Add(
                $"Retired email-less account {row.FromAccountId:D}: profile '{row.Name}' ({row.ProfileId:D}) now belongs to administrator account {administratorEmail}.");
        }
    }

    /// <summary>
    /// Households: one per existing account, named after the account's default profile. Every profile granted to
    /// an account joins that account's household. A profile granted to two accounts joins the household of the
    /// account where it is the default grant, otherwise the oldest account (a note is written either way).
    /// Safe to run on every startup: only accounts and profiles without a household are touched, so a second run
    /// changes nothing.
    /// </summary>
    private void EnsureHouseholds(SqliteConnection conn)
    {
        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS households (
                    id         BLOB NOT NULL PRIMARY KEY,
                    name       TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        foreach (var table in new[] { "profiles", "accounts" })
        {
            if (!ColumnExists(conn, table, "household_id"))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE {table} ADD COLUMN household_id BLOB REFERENCES households(id);";
                alter.ExecuteNonQuery();
            }
        }

        using (var indexes = conn.CreateCommand())
        {
            indexes.CommandText = """
                CREATE INDEX IF NOT EXISTS ix_accounts_household ON accounts(household_id);
                CREATE INDEX IF NOT EXISTS ix_profiles_household ON profiles(household_id);
                """;
            indexes.ExecuteNonQuery();
        }

        var notes = new List<string>();
        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            var pending = new List<(Guid AccountId, string Name)>();
            using (var find = conn.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = """
                    SELECT a.id,
                           COALESCE(
                               (SELECT p.display_name FROM account_profile_grants g
                                JOIN profiles p ON p.id = g.profile_id
                                WHERE g.account_id = a.id
                                ORDER BY g.is_default DESC, g.granted_at, p.created_at LIMIT 1),
                               a.email)
                    FROM accounts a
                    WHERE a.household_id IS NULL
                    ORDER BY a.created_at, a.id;
                    """;
                using var reader = find.ExecuteReader();
                while (reader.Read())
                {
                    pending.Add((GuidSql.FromDb(reader.GetValue(0)), reader.GetString(1)));
                }
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            foreach (var (accountId, name) in pending)
            {
                var householdId = Guid.NewGuid();
                using var insert = conn.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO households (id, name, created_at) VALUES (@id, @name, @now);
                    UPDATE accounts SET household_id = @id WHERE id = @account;
                    """;
                insert.Parameters.Add("@id", SqliteType.Blob).Value = GuidSql.ToBlob(householdId);
                insert.Parameters.AddWithValue("@name", Domain.Entities.Household.DefaultNameFor(name));
                insert.Parameters.AddWithValue("@now", now);
                insert.Parameters.Add("@account", SqliteType.Blob).Value = GuidSql.ToBlob(accountId);
                insert.ExecuteNonQuery();
            }

            // Profiles: join the household of the default-grant account, else the oldest account that holds them.
            var shared = new List<(Guid ProfileId, string Name, int Holders)>();
            using (var assign = conn.CreateCommand())
            {
                assign.Transaction = transaction;
                assign.CommandText = """
                    SELECT p.id, p.display_name, (SELECT COUNT(*) FROM account_profile_grants x WHERE x.profile_id = p.id)
                    FROM profiles p
                    WHERE p.household_id IS NULL
                      AND EXISTS (SELECT 1 FROM account_profile_grants g WHERE g.profile_id = p.id);
                    """;
                using var reader = assign.ExecuteReader();
                while (reader.Read())
                {
                    shared.Add((GuidSql.FromDb(reader.GetValue(0)), reader.GetString(1), reader.GetInt32(2)));
                }
            }

            foreach (var profile in shared)
            {
                using var update = conn.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE profiles SET household_id = (
                        SELECT a.household_id FROM account_profile_grants g
                        JOIN accounts a ON a.id = g.account_id
                        WHERE g.profile_id = profiles.id AND a.household_id IS NOT NULL
                        ORDER BY g.is_default DESC, a.created_at, a.id
                        LIMIT 1)
                    WHERE id = @profile;
                    """;
                update.Parameters.Add("@profile", SqliteType.Blob).Value = GuidSql.ToBlob(profile.ProfileId);
                update.ExecuteNonQuery();
                if (profile.Holders > 1)
                {
                    notes.Add($"Profile '{profile.Name}' ({profile.ProfileId:D}) was granted to {profile.Holders} accounts; it now belongs to the household of the account where it is the default, else the oldest account.");
                }
            }

            // A grant that now crosses households (a profile shared by two accounts) is removed: a sign-in only opens
            // people from its own household. Authorization versions are bumped so open sessions re-check.
            var crossing = new List<(Guid AccountId, Guid ProfileId, string Name, string Email)>();
            using (var find = conn.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = """
                    SELECT g.account_id, g.profile_id, p.display_name, a.email
                    FROM account_profile_grants g
                    JOIN accounts a ON a.id = g.account_id
                    JOIN profiles p ON p.id = g.profile_id
                    WHERE a.household_id IS NOT NULL AND p.household_id IS NOT NULL AND a.household_id <> p.household_id;
                    """;
                using var reader = find.ExecuteReader();
                while (reader.Read())
                {
                    crossing.Add((GuidSql.FromDb(reader.GetValue(0)), GuidSql.FromDb(reader.GetValue(1)), reader.GetString(2), reader.GetString(3)));
                }
            }

            foreach (var grant in crossing)
            {
                using var remove = conn.CreateCommand();
                remove.Transaction = transaction;
                remove.CommandText = """
                    DELETE FROM account_profile_grants WHERE account_id = @account AND profile_id = @profile;
                    UPDATE accounts SET authorization_version = authorization_version + 1, updated_at = @now WHERE id = @account;
                    """;
                remove.Parameters.Add("@account", SqliteType.Blob).Value = GuidSql.ToBlob(grant.AccountId);
                remove.Parameters.Add("@profile", SqliteType.Blob).Value = GuidSql.ToBlob(grant.ProfileId);
                remove.Parameters.AddWithValue("@now", now);
                remove.ExecuteNonQuery();
                notes.Add($"Removed {grant.Email}'s access to profile '{grant.Name}' ({grant.ProfileId:D}): the profile belongs to another household.");
            }

            // Profiles nobody holds join the first enabled administrator's household, up to its limit of eight.
            Guid? adminHousehold = null;
            using (var find = conn.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = "SELECT household_id FROM accounts WHERE is_administrator = 1 AND is_enabled = 1 AND household_id IS NOT NULL ORDER BY created_at, id LIMIT 1;";
                if (find.ExecuteScalar() is byte[] bytes)
                {
                    adminHousehold = GuidSql.FromDb(bytes);
                }
            }

            if (adminHousehold is { } household)
            {
                var orphans = new List<(Guid Id, string Name)>();
                using (var find = conn.CreateCommand())
                {
                    find.Transaction = transaction;
                    find.CommandText = "SELECT id, display_name FROM profiles WHERE household_id IS NULL AND id <> @seed ORDER BY created_at, id;";
                    find.Parameters.Add("@seed", SqliteType.Blob).Value = GuidSql.ToBlob(Guid.Parse("00000000-0000-0000-0000-000000000001"));
                    using var reader = find.ExecuteReader();
                    while (reader.Read())
                    {
                        orphans.Add((GuidSql.FromDb(reader.GetValue(0)), reader.GetString(1)));
                    }
                }

                foreach (var orphan in orphans)
                {
                    using var join = conn.CreateCommand();
                    join.Transaction = transaction;
                    join.CommandText = """
                        UPDATE profiles SET household_id = @household
                        WHERE id = @profile AND household_id IS NULL
                          AND (SELECT COUNT(*) FROM profiles WHERE household_id = @household) < @max;
                        """;
                    join.Parameters.Add("@household", SqliteType.Blob).Value = GuidSql.ToBlob(household);
                    join.Parameters.Add("@profile", SqliteType.Blob).Value = GuidSql.ToBlob(orphan.Id);
                    join.Parameters.AddWithValue("@max", MaximumProfilesPerAccount);
                    if (join.ExecuteNonQuery() > 0)
                    {
                        notes.Add($"Profile '{orphan.Name}' ({orphan.Id:D}) had no household; it now belongs to the administrator's household.");
                    }
                }
            }
        });
        _notes.AddRange(notes);
    }

    /// <summary>
    /// Household administrators. Adds <c>households.primary_account_id</c> and <c>accounts.household_admin</c>. One time,
    /// when the flag column first appears, each household's main sign-in (its oldest enabled one) becomes its primary
    /// account and household administrator, and every server administrator also administers their own household.
    /// Later starts never promote anyone again, so a server administrator can take the role away for good.
    /// </summary>
    private void EnsureHouseholdAdministrators(SqliteConnection conn)
    {
        var addedPrimary = AddColumnIfMissing(conn, "households", "primary_account_id",
            "ALTER TABLE households ADD COLUMN primary_account_id BLOB REFERENCES accounts(id) ON DELETE SET NULL;");
        var addedFlag = AddColumnIfMissing(conn, "accounts", "household_admin",
            "ALTER TABLE accounts ADD COLUMN household_admin INTEGER NOT NULL DEFAULT 0 CHECK (household_admin IN (0, 1));");
        if (!addedPrimary && !addedFlag)
        {
            return;
        }

        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var primary = conn.CreateCommand();
            primary.Transaction = transaction;
            primary.CommandText = """
                UPDATE households SET primary_account_id = (
                    SELECT a.id FROM accounts a
                    WHERE a.household_id = households.id AND a.grants_inherit_from_account_id IS NULL
                    ORDER BY a.is_enabled DESC, a.created_at, a.id LIMIT 1)
                WHERE primary_account_id IS NULL;
                """;
            primary.ExecuteNonQuery();

            if (!addedFlag)
            {
                return;
            }

            using var admins = conn.CreateCommand();
            admins.Transaction = transaction;
            admins.CommandText = """
                UPDATE accounts SET household_admin = 1
                WHERE household_admin = 0 AND grants_inherit_from_account_id IS NULL AND household_id IS NOT NULL
                  AND (is_administrator = 1
                       OR id IN (SELECT primary_account_id FROM households WHERE primary_account_id IS NOT NULL));
                """;
            admins.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// The Shared library used to be one row for the whole server. It is now one row per household. One time, the
    /// existing row moves to the server administrator's household (else the oldest household) and keeps its library
    /// identity, so everything already shared stays where it is. A server with no household yet simply starts empty.
    /// The scope triggers that named the single row are recreated without that condition.
    /// </summary>
    private void EnsurePerHouseholdSharedLibrary(SqliteConnection conn)
    {
        if (!ColumnExists(conn, "view_shared_library", "singleton_key"))
        {
            return;
        }

        Guid? owner = null;
        using (var find = conn.CreateCommand())
        {
            find.CommandText = """
                SELECT COALESCE(
                    (SELECT household_id FROM accounts
                     WHERE is_administrator = 1 AND is_enabled = 1 AND household_id IS NOT NULL
                     ORDER BY created_at, id LIMIT 1),
                    (SELECT id FROM households ORDER BY created_at, id LIMIT 1));
                """;
            if (find.ExecuteScalar() is { } value and not DBNull)
            {
                owner = GuidSql.FromDb(value);
            }
        }

        var scopeTriggers = new List<(string Name, string Sql)>();
        foreach (var name in new[]
                 {
                     "trg_view_sources_scope_insert", "trg_view_sources_scope_update",
                     "trg_local_items_scope_insert", "trg_local_items_scope_update",
                 })
        {
            using var read = conn.CreateCommand();
            read.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = @name;";
            read.Parameters.AddWithValue("@name", name);
            if (read.ExecuteScalar() is string sql)
            {
                scopeTriggers.Add((name, sql.Replace("s.singleton_key=1 AND ", string.Empty, StringComparison.Ordinal)));
            }
        }

        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            void Run(string commandText, Action<SqliteCommand>? bind = null)
            {
                using var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = commandText;
                bind?.Invoke(command);
                command.ExecuteNonQuery();
            }

            Run("""
                CREATE TEMP TABLE shared_library_legacy AS
                SELECT library_id, created_at, updated_at FROM view_shared_library WHERE singleton_key = 1;
                DROP TRIGGER IF EXISTS trg_view_shared_library_collision_insert;
                DROP TRIGGER IF EXISTS trg_view_shared_library_identity_immutable;
                DROP TRIGGER IF EXISTS trg_view_shared_library_delete;
                DROP TRIGGER IF EXISTS trg_view_sources_scope_insert;
                DROP TRIGGER IF EXISTS trg_view_sources_scope_update;
                DROP TRIGGER IF EXISTS trg_local_items_scope_insert;
                DROP TRIGGER IF EXISTS trg_local_items_scope_update;
                DROP TABLE view_shared_library;
                CREATE TABLE view_shared_library (
                    household_id BLOB NOT NULL PRIMARY KEY REFERENCES households(id),
                    library_id BLOB NOT NULL UNIQUE,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TRIGGER IF NOT EXISTS trg_view_shared_library_collision_insert
                BEFORE INSERT ON view_shared_library WHEN EXISTS (
                    SELECT 1 FROM view_personal_spaces WHERE library_id=NEW.library_id)
                BEGIN SELECT RAISE(ABORT,'Shared library identity cannot be used by a Personal Space'); END;
                CREATE TRIGGER IF NOT EXISTS trg_view_shared_library_identity_immutable
                BEFORE UPDATE OF library_id ON view_shared_library WHEN NEW.library_id<>OLD.library_id
                BEGIN SELECT RAISE(ABORT,'Shared library identity is immutable'); END;
                CREATE TRIGGER IF NOT EXISTS trg_view_shared_library_delete
                BEFORE DELETE ON view_shared_library
                BEGIN SELECT RAISE(ABORT,'Shared library identity cannot be deleted'); END;
                """);
            if (owner is { } householdId)
            {
                Run("""
                    INSERT INTO view_shared_library (household_id, library_id, created_at, updated_at)
                    SELECT @household, library_id, created_at, updated_at FROM shared_library_legacy LIMIT 1;
                    """, command => command.Parameters.Add("@household", SqliteType.Blob).Value = GuidSql.ToBlob(householdId));
            }

            Run("DROP TABLE shared_library_legacy;");
            foreach (var (_, sql) in scopeTriggers)
            {
                Run(sql);
            }
        });
        _notes.Add(owner is null
            ? "The Shared library is now one per household; no household existed yet, so it starts empty."
            : "The Shared library is now one per household; the existing one moved to the server administrator's household.");
    }

    private static void RebuildAccountsTable(SqliteConnection conn)
    {
        // SQLite cannot change a column to NOT NULL or drop a column used in a CHECK, so the table is rebuilt.
        // Foreign keys must be off while the old table is dropped, or ON DELETE CASCADE would erase every grant,
        // credential and session that points at it. They are restored afterwards.
        bool foreignKeysWereOn;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "PRAGMA foreign_keys;";
            foreignKeysWereOn = Convert.ToInt32(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
        }

        SetForeignKeys(conn, false);
        try
        {
            DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    CREATE TABLE accounts_rebuild (
                        id               BLOB NOT NULL PRIMARY KEY,
                        email            TEXT NOT NULL,
                        normalized_email TEXT NOT NULL,
                        is_enabled       INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
                        is_administrator INTEGER NOT NULL DEFAULT 0 CHECK (is_administrator IN (0, 1)),
                        authorization_version INTEGER NOT NULL DEFAULT 1 CHECK (authorization_version > 0),
                        created_at       TEXT NOT NULL,
                        updated_at       TEXT NOT NULL
                    );

                    INSERT INTO accounts_rebuild
                        (id, email, normalized_email, is_enabled, is_administrator, authorization_version, created_at, updated_at)
                    SELECT id, email, normalized_email, is_enabled, is_administrator, authorization_version, created_at, updated_at
                    FROM accounts;

                    DROP TABLE accounts;
                    ALTER TABLE accounts_rebuild RENAME TO accounts;
                    CREATE UNIQUE INDEX IF NOT EXISTS ux_accounts_normalized_email ON accounts(normalized_email);

                    INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
                    VALUES ('006_accounts_always_have_email', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                    """;
                cmd.ExecuteNonQuery();
            });
        }
        finally
        {
            SetForeignKeys(conn, foreignKeysWereOn);
        }
    }

    private static void SetForeignKeys(SqliteConnection conn, bool enabled)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = enabled ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureAssetRenditionSchema(SqliteConnection conn)
    {
        AddColumnIfMissing(conn, "media_assets", "rendition_purpose",
            "ALTER TABLE media_assets ADD COLUMN rendition_purpose TEXT NOT NULL DEFAULT 'Original';");
        AddColumnIfMissing(conn, "media_assets", "derived_from_asset_id",
            "ALTER TABLE media_assets ADD COLUMN derived_from_asset_id BLOB REFERENCES media_assets(id) ON DELETE SET NULL;");
        AddColumnIfMissing(conn, "media_assets", "encoder_profile_version",
            "ALTER TABLE media_assets ADD COLUMN encoder_profile_version TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_width",
            "ALTER TABLE media_assets ADD COLUMN rendition_width INTEGER;");
        AddColumnIfMissing(conn, "media_assets", "rendition_height",
            "ALTER TABLE media_assets ADD COLUMN rendition_height INTEGER;");
        AddColumnIfMissing(conn, "media_assets", "rendition_bitrate_bps",
            "ALTER TABLE media_assets ADD COLUMN rendition_bitrate_bps INTEGER;");
        AddColumnIfMissing(conn, "media_assets", "rendition_video_codec",
            "ALTER TABLE media_assets ADD COLUMN rendition_video_codec TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_audio_codec",
            "ALTER TABLE media_assets ADD COLUMN rendition_audio_codec TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_dynamic_range",
            "ALTER TABLE media_assets ADD COLUMN rendition_dynamic_range TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_audio_layout",
            "ALTER TABLE media_assets ADD COLUMN rendition_audio_layout TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_generated_at",
            "ALTER TABLE media_assets ADD COLUMN rendition_generated_at TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_source_fingerprint",
            "ALTER TABLE media_assets ADD COLUMN rendition_source_fingerprint TEXT;");
        using var command = conn.CreateCommand();
        command.CommandText = "CREATE INDEX IF NOT EXISTS idx_media_assets_derived_from ON media_assets(derived_from_asset_id) WHERE derived_from_asset_id IS NOT NULL;";
        command.ExecuteNonQuery();
    }

    private static void EnsureProviderConnectionCheckSchema(SqliteConnection conn)
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS provider_connection_checks (
                provider_name TEXT NOT NULL PRIMARY KEY,
                status TEXT NOT NULL,
                message TEXT NOT NULL,
                checked_at TEXT NOT NULL,
                response_time_ms INTEGER
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureArtworkWritebackSchema(SqliteConnection conn)
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS media_artwork_writeback (
                media_asset_id BLOB NOT NULL PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
                desired_artwork_asset_id BLOB NOT NULL,
                desired_version TEXT,
                embedded_artwork_asset_id BLOB,
                status TEXT NOT NULL CHECK(status IN ('pending','writing','embedded','failed')),
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                file_modified_utc TEXT,
                file_size_bytes INTEGER,
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );
            CREATE INDEX IF NOT EXISTS idx_media_artwork_writeback_status
                ON media_artwork_writeback(status, updated_at);
            """;
        command.ExecuteNonQuery();
        AddColumnIfMissing(conn, "media_artwork_writeback", "desired_version",
            "ALTER TABLE media_artwork_writeback ADD COLUMN desired_version TEXT;");
    }

    private static void EnsureMediaEditorCommitSchema(SqliteConnection conn)
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS media_editor_commits (
                operation_token TEXT NOT NULL PRIMARY KEY,
                request_hash TEXT NOT NULL,
                asset_id BLOB NOT NULL,
                source_work_id BLOB NOT NULL,
                target_work_id BLOB NOT NULL,
                target_tvdb_episode_id TEXT NOT NULL,
                committed_at TEXT NOT NULL,
                sync_state TEXT NOT NULL DEFAULT 'pending'
            );
            CREATE INDEX IF NOT EXISTS ix_media_editor_commits_asset
                ON media_editor_commits(asset_id, committed_at);
            CREATE TABLE IF NOT EXISTS media_editor_commit_items (
                operation_token TEXT NOT NULL REFERENCES media_editor_commits(operation_token) ON DELETE CASCADE,
                asset_id BLOB NOT NULL,
                source_edition_id BLOB NOT NULL,
                source_work_id BLOB NOT NULL,
                target_work_id BLOB NOT NULL,
                source_season_work_id BLOB NOT NULL,
                target_season_work_id BLOB NOT NULL,
                PRIMARY KEY (operation_token, asset_id)
            );
            CREATE TABLE IF NOT EXISTS media_editor_music_pairing_commits (
                operation_token TEXT NOT NULL PRIMARY KEY,
                request_hash TEXT NOT NULL,
                committed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS media_editor_music_pairing_commit_items (
                operation_token TEXT NOT NULL REFERENCES media_editor_music_pairing_commits(operation_token) ON DELETE CASCADE,
                asset_id BLOB NOT NULL,
                edition_id BLOB NOT NULL,
                work_id BLOB NOT NULL,
                release_id TEXT NOT NULL,
                release_track_id TEXT NOT NULL,
                PRIMARY KEY (operation_token, asset_id)
            );
            CREATE TABLE IF NOT EXISTS media_editor_commit_artwork (
                operation_token TEXT NOT NULL PRIMARY KEY REFERENCES media_editor_commits(operation_token) ON DELETE CASCADE,
                owner_work_id BLOB NOT NULL,
                artwork_asset_id BLOB NOT NULL,
                expected_preference_revision TEXT NOT NULL,
                previous_preferred_ids_json TEXT NOT NULL,
                affected_asset_ids_json TEXT NOT NULL,
                committed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS media_editor_preferred_artwork_commits (
                operation_token TEXT NOT NULL PRIMARY KEY,
                request_hash TEXT NOT NULL,
                owner_work_id BLOB NOT NULL,
                owner_scope TEXT NOT NULL,
                role TEXT NOT NULL,
                artwork_asset_id BLOB NOT NULL,
                expected_owner_revision TEXT NOT NULL,
                previous_preferred_ids_json TEXT NOT NULL,
                affected_assets_json TEXT NOT NULL,
                committed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS media_editor_edition_artwork_commits (
                operation_token TEXT NOT NULL PRIMARY KEY,
                request_hash TEXT NOT NULL,
                edition_id BLOB NOT NULL,
                work_id BLOB NOT NULL,
                artwork_asset_id BLOB NOT NULL,
                expected_revision TEXT NOT NULL,
                previous_preferred_ids_json TEXT NOT NULL,
                affected_assets_json TEXT NOT NULL,
                committed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS media_file_write_intents (
                asset_id BLOB NOT NULL PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
                generation INTEGER NOT NULL DEFAULT 1,
                operation_token TEXT NOT NULL,
                trigger TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending'
                    CHECK(status IN ('pending','writing','verified','blocked','unsupported','failed')),
                attempts INTEGER NOT NULL DEFAULT 0,
                lease_expires_at TEXT,
                last_error TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_media_file_write_intents_dispatch
                ON media_file_write_intents(status, lease_expires_at, updated_at);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureCanonicalArtworkSchema(SqliteConnection conn)
    {
        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS artwork_assets (
                    id BLOB NOT NULL PRIMARY KEY,
                    content_hash TEXT NOT NULL UNIQUE,
                    original_path TEXT,
                    small_path TEXT,
                    medium_path TEXT,
                    large_path TEXT,
                    width_px INTEGER,
                    height_px INTEGER,
                    aspect_class TEXT NOT NULL DEFAULT 'UnsupportedRect',
                    primary_hex TEXT,
                    secondary_hex TEXT,
                    accent_hex TEXT,
                    source_provider TEXT,
                    source_url TEXT,
                    provider_reference TEXT,
                    perceptual_hash INTEGER,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    updated_at TEXT
                );

                CREATE TABLE IF NOT EXISTS entity_artwork_links (
                    id BLOB NOT NULL PRIMARY KEY,
                    entity_id BLOB NOT NULL,
                    entity_type TEXT NOT NULL,
                    artwork_asset_id BLOB NOT NULL REFERENCES artwork_assets(id) ON DELETE RESTRICT,
                    role TEXT NOT NULL CHECK(role IN ('Primary','Background','Portrait','Logo')),
                    context TEXT,
                    source_asset_type TEXT,
                    is_preferred INTEGER NOT NULL DEFAULT 0,
                    is_user_override INTEGER NOT NULL DEFAULT 0,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    updated_at TEXT,
                    UNIQUE(entity_id, entity_type, artwork_asset_id, role, context)
                );

                CREATE TABLE IF NOT EXISTS artwork_asset_context (
                    artwork_asset_id BLOB NOT NULL REFERENCES artwork_assets(id) ON DELETE CASCADE,
                    entity_id BLOB NOT NULL,
                    entity_type TEXT NOT NULL,
                    entity_label TEXT NOT NULL,
                    media_type TEXT,
                    year TEXT,
                    role TEXT,
                    provider TEXT,
                    canonical_id TEXT,
                    search_text TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    updated_at TEXT,
                    PRIMARY KEY(artwork_asset_id, entity_id, entity_type, role)
                );

                CREATE INDEX IF NOT EXISTS idx_entity_artwork_links_entity_role
                    ON entity_artwork_links(entity_id, entity_type, role, is_preferred DESC, sort_order);
                CREATE INDEX IF NOT EXISTS idx_entity_artwork_links_asset
                    ON entity_artwork_links(artwork_asset_id);
                CREATE INDEX IF NOT EXISTS idx_artwork_asset_context_search
                    ON artwork_asset_context(search_text COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_artwork_asset_context_facets
                    ON artwork_asset_context(entity_type, media_type, year, role, provider, entity_id);

                CREATE VIRTUAL TABLE IF NOT EXISTS artwork_asset_search USING fts5(
                    artwork_asset_id UNINDEXED,
                    search_text,
                    tokenize = 'trigram'
                );

                CREATE TRIGGER IF NOT EXISTS trg_artwork_asset_context_search_insert
                AFTER INSERT ON artwork_asset_context BEGIN
                    INSERT INTO artwork_asset_search(artwork_asset_id, search_text)
                    VALUES (new.artwork_asset_id, new.search_text);
                END;

                CREATE TRIGGER IF NOT EXISTS trg_artwork_asset_context_search_update
                AFTER UPDATE OF search_text, artwork_asset_id ON artwork_asset_context BEGIN
                    DELETE FROM artwork_asset_search
                    WHERE artwork_asset_id = old.artwork_asset_id AND search_text = old.search_text;
                    INSERT INTO artwork_asset_search(artwork_asset_id, search_text)
                    VALUES (new.artwork_asset_id, new.search_text);
                END;

                CREATE TRIGGER IF NOT EXISTS trg_artwork_asset_context_search_delete
                AFTER DELETE ON artwork_asset_context BEGIN
                    DELETE FROM artwork_asset_search
                    WHERE artwork_asset_id = old.artwork_asset_id AND search_text = old.search_text;
                END;

                INSERT OR IGNORE INTO artwork_assets (
                    id, content_hash, original_path, small_path, medium_path, large_path,
                    width_px, height_px, aspect_class, primary_hex, secondary_hex, accent_hex,
                    source_provider, source_url, perceptual_hash, created_at, updated_at)
                SELECT ea.id,
                       COALESCE(ic.content_hash, 'legacy:' || lower(hex(ea.id))),
                       ea.local_image_path, ea.local_image_path_s, ea.local_image_path_m, ea.local_image_path_l,
                       ea.width_px, ea.height_px, ea.aspect_class,
                       ea.primary_hex, ea.secondary_hex, ea.accent_hex,
                       ea.source_provider, ea.image_url, ic.phash, ea.created_at, ea.updated_at
                FROM entity_assets ea
                LEFT JOIN image_cache ic ON ic.file_path = ea.local_image_path
                WHERE COALESCE(ea.asset_class, 'Artwork') = 'Artwork';

                INSERT OR IGNORE INTO entity_artwork_links (
                    id, entity_id, entity_type, artwork_asset_id, role, context, source_asset_type,
                    is_preferred, is_user_override, created_at, updated_at)
                SELECT ea.id, ea.entity_id, ea.entity_type, aa.id,
                       CASE
                           WHEN ea.asset_type IN ('Headshot','CharacterPortrait') THEN 'Portrait'
                           WHEN ea.asset_type IN ('Background','Banner','SeasonThumb') THEN 'Background'
                           WHEN ea.asset_type IN ('Logo','NetworkLogo','StudioLogo') THEN 'Logo'
                           ELSE 'Primary'
                       END,
                       CASE
                           WHEN ea.asset_type = 'SeasonPoster' THEN 'Season'
                           WHEN ea.asset_type = 'EpisodeStill' THEN 'Episode'
                           ELSE NULL
                       END,
                       ea.asset_type, ea.is_preferred, ea.is_user_override, ea.created_at, ea.updated_at
                FROM entity_assets ea
                LEFT JOIN image_cache ic ON ic.file_path = ea.local_image_path
                JOIN artwork_assets aa ON aa.content_hash = COALESCE(ic.content_hash, 'legacy:' || lower(hex(ea.id)))
                WHERE COALESCE(ea.asset_class, 'Artwork') = 'Artwork';

                INSERT OR IGNORE INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label, media_type,
                    year, role, provider, search_text, created_at)
                SELECT link.artwork_asset_id, work.id, 'Work',
                       COALESCE((SELECT value FROM canonical_values WHERE entity_id=work.id AND key='title'), 'Untitled media'),
                       work.media_type,
                       (SELECT value FROM canonical_values WHERE entity_id=work.id AND key IN ('release_year','year') ORDER BY CASE key WHEN 'release_year' THEN 0 ELSE 1 END LIMIT 1),
                       link.role, asset.source_provider,
                       trim(COALESCE((SELECT value FROM canonical_values WHERE entity_id=work.id AND key='title'), 'Untitled media') || ' ' ||
                            work.media_type || ' ' || link.role || ' ' || COALESCE(asset.source_provider, '')),
                       link.created_at
                FROM entity_artwork_links link
                JOIN works work ON work.id = link.entity_id
                JOIN artwork_assets asset ON asset.id = link.artwork_asset_id
                WHERE link.entity_type = 'Work';

                INSERT OR IGNORE INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label, role, provider, search_text, created_at)
                SELECT link.artwork_asset_id, person.id, 'Person', person.name,
                       link.role, asset.source_provider,
                       trim(person.name || ' Person ' || link.role || ' ' || COALESCE(person.occupation, '') || ' ' || COALESCE(asset.source_provider, '')),
                       link.created_at
                FROM entity_artwork_links link
                JOIN persons person ON person.id = link.entity_id
                JOIN artwork_assets asset ON asset.id = link.artwork_asset_id
                WHERE link.entity_type = 'Person';

                INSERT OR IGNORE INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label, media_type,
                    role, provider, canonical_id, search_text, created_at)
                SELECT link.artwork_asset_id, collection.id, 'Collection', collection.display_name,
                       collection.primary_area, link.role, asset.source_provider, collection.wikidata_qid,
                       trim(collection.display_name || ' ' || collection.collection_type || ' ' ||
                            COALESCE(collection.primary_area, '') || ' ' || link.role || ' ' ||
                            COALESCE(collection.wikidata_qid, '') || ' ' || COALESCE(asset.source_provider, '')),
                       link.created_at
                FROM entity_artwork_links link
                JOIN collections collection ON collection.id = link.entity_id
                JOIN artwork_assets asset ON asset.id = link.artwork_asset_id
                WHERE link.entity_type = 'Collection';

                INSERT OR IGNORE INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label,
                    role, provider, canonical_id, search_text, created_at)
                SELECT link.artwork_asset_id, entity.id, 'FictionalEntity', entity.label,
                       link.role, asset.source_provider, entity.wikidata_qid,
                       trim(entity.label || ' ' || entity.entity_sub_type || ' ' ||
                            COALESCE(entity.fictional_universe_label, '') || ' ' || link.role || ' ' ||
                            entity.wikidata_qid || ' ' || COALESCE(asset.source_provider, '')),
                       link.created_at
                FROM entity_artwork_links link
                JOIN fictional_entities entity ON entity.id = link.entity_id
                JOIN artwork_assets asset ON asset.id = link.artwork_asset_id
                WHERE link.entity_type = 'FictionalEntity';

                INSERT INTO artwork_asset_search(artwork_asset_id, search_text)
                SELECT context.artwork_asset_id, context.search_text
                FROM artwork_asset_context context
                WHERE NOT EXISTS (
                    SELECT 1 FROM artwork_asset_search indexed_search
                    WHERE indexed_search.artwork_asset_id = context.artwork_asset_id
                      AND indexed_search.search_text = context.search_text);

                INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
                VALUES ('007_canonical_artwork_assets', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        });
    }

    private static void MigrateLegacyProfileLists(SqliteConnection conn)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE migration_id = '006_profile_state_for_me_cutover';";
            if (Convert.ToInt32(check.ExecuteScalar()) > 0)
            {
                return;
            }
        }

        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                -- The old Dashboard created these exact private Playlists as an
                -- implementation detail. Restrict by both name and product-owned
                -- description so a user Playlist with the same name is untouched.
                INSERT OR IGNORE INTO profile_saved_items(profile_id, entity_kind, entity_id, saved_at, position)
                SELECT c.profile_id,
                       CASE w.media_type
                           WHEN 'Movies' THEN 'Movie'
                           WHEN 'TV' THEN 'TvShow'
                           WHEN 'Books' THEN 'Book'
                           WHEN 'Comics' THEN 'Comic'
                           WHEN 'Audiobooks' THEN 'Audiobook'
                           ELSE 'Book'
                       END,
                       ci.work_id,
                       ci.added_at,
                       ci.sort_order
                FROM collections c
                JOIN collection_items ci ON ci.collection_id = c.id
                JOIN works w ON w.id = ci.work_id
                WHERE c.scope = 'user'
                  AND c.profile_id IS NOT NULL
                  AND c.collection_type = 'Playlist'
                  AND c.resolution = 'materialized'
                  AND (
                    (c.display_name = 'Favorites' AND c.description = 'Profile-level favorites across the library.' AND w.media_type <> 'Music')
                    OR (c.display_name = 'Watchlist' AND c.description = 'Quick-save shows and movies to watch later.')
                  );

                -- Music rows in the legacy Favorites Playlist represented the heart
                -- action, not saved-for-later intent. Preserve them as Likes.
                INSERT OR IGNORE INTO profile_reactions(profile_id, entity_kind, entity_id, reaction, updated_at)
                SELECT c.profile_id, 'Song', ci.work_id, 'Like', ci.added_at
                FROM collections c
                JOIN collection_items ci ON ci.collection_id = c.id
                JOIN works w ON w.id = ci.work_id
                WHERE c.scope = 'user'
                  AND c.profile_id IS NOT NULL
                  AND c.collection_type = 'Playlist'
                  AND c.resolution = 'materialized'
                  AND c.display_name = 'Favorites'
                  AND c.description = 'Profile-level favorites across the library.'
                  AND w.media_type = 'Music';

                INSERT OR REPLACE INTO profile_reactions(profile_id, entity_kind, entity_id, reaction, updated_at)
                SELECT c.profile_id,
                       CASE w.media_type
                           WHEN 'Movies' THEN 'Movie'
                           WHEN 'TV' THEN 'TvShow'
                           WHEN 'Books' THEN 'Book'
                           WHEN 'Comics' THEN 'Comic'
                           WHEN 'Audiobooks' THEN 'Audiobook'
                           WHEN 'Music' THEN 'Song'
                           ELSE 'Book'
                       END,
                       ci.work_id,
                       CASE c.display_name
                           WHEN 'Disliked Media' THEN 'Dislike'
                           WHEN 'Loved Media' THEN 'Love'
                           ELSE 'Like'
                       END,
                       ci.added_at
                FROM collections c
                JOIN collection_items ci ON ci.collection_id = c.id
                JOIN works w ON w.id = ci.work_id
                WHERE c.scope = 'user'
                  AND c.profile_id IS NOT NULL
                  AND c.collection_type = 'Playlist'
                  AND c.resolution = 'materialized'
                  AND (
                    (c.display_name = 'Liked Media' AND c.description = 'Profile-level positive feedback across the library.')
                    OR (c.display_name = 'Disliked Media' AND c.description = 'Profile-level dislikes across the library.')
                    OR (c.display_name = 'Loved Media' AND c.description = 'Profile-level strongest positive feedback across the library.')
                  );

                DELETE FROM collections
                WHERE scope = 'user'
                  AND profile_id IS NOT NULL
                  AND collection_type = 'Playlist'
                  AND resolution = 'materialized'
                  AND (
                    (display_name = 'Favorites' AND description = 'Profile-level favorites across the library.')
                    OR (display_name = 'Watchlist' AND description = 'Quick-save shows and movies to watch later.')
                    OR (display_name = 'Liked Media' AND description = 'Profile-level positive feedback across the library.')
                    OR (display_name = 'Disliked Media' AND description = 'Profile-level dislikes across the library.')
                    OR (display_name = 'Loved Media' AND description = 'Profile-level strongest positive feedback across the library.')
                  );

                INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
                VALUES ('006_profile_state_for_me_cutover', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        });
    }

    private static void EnsureExpandedArtworkAssetTypes(SqliteConnection conn)
    {
        using var inspect = conn.CreateCommand();
        inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'entity_assets';";
        var tableSql = inspect.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(tableSql)
            || tableSql.Contains("NetworkLogo", StringComparison.Ordinal))
        {
            return;
        }

        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                ALTER TABLE entity_assets RENAME TO entity_assets_legacy_asset_types;

                CREATE TABLE entity_assets (
                    id               BLOB PRIMARY KEY,
                    entity_id        BLOB NOT NULL,
                    entity_type      TEXT NOT NULL CHECK(entity_type IN ('Work','Person','Universe','FictionalEntity')),
                    asset_type       TEXT NOT NULL CHECK(asset_type IN ('CoverArt','Headshot','Banner','Logo','NetworkLogo','StudioLogo','Background','SeasonPoster','SeasonThumb','EpisodeStill','CharacterPortrait')),
                    image_url        TEXT,
                    local_image_path TEXT,
                    local_image_path_s TEXT,
                    local_image_path_m TEXT,
                    local_image_path_l TEXT,
                    source_provider  TEXT,
                    width_px         INTEGER,
                    height_px        INTEGER,
                    aspect_class     TEXT NOT NULL DEFAULT 'UnsupportedRect',
                    primary_hex      TEXT,
                    secondary_hex    TEXT,
                    accent_hex       TEXT,
                    asset_class      TEXT NOT NULL DEFAULT 'Artwork',
                    storage_location TEXT NOT NULL DEFAULT 'Central',
                    owner_scope      TEXT NOT NULL DEFAULT 'Unknown',
                    is_preferred     INTEGER NOT NULL DEFAULT 0,
                    is_user_override INTEGER NOT NULL DEFAULT 0,
                    is_locally_exported   INTEGER NOT NULL DEFAULT 0,
                    is_preferred_exported INTEGER NOT NULL DEFAULT 0,
                    created_at       TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at       TEXT
                );

                INSERT INTO entity_assets (
                    id, entity_id, entity_type, asset_type, image_url,
                    local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                    source_provider, width_px, height_px, aspect_class,
                    primary_hex, secondary_hex, accent_hex, asset_class, storage_location, owner_scope,
                    is_preferred, is_user_override, is_locally_exported, is_preferred_exported,
                    created_at, updated_at)
                SELECT
                    id, entity_id, entity_type, asset_type, image_url,
                    local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                    source_provider, width_px, height_px, aspect_class,
                    primary_hex, secondary_hex, accent_hex, asset_class, storage_location, owner_scope,
                    is_preferred, is_user_override, is_locally_exported, is_preferred_exported,
                    created_at, updated_at
                FROM entity_assets_legacy_asset_types;

                DROP TABLE entity_assets_legacy_asset_types;

                CREATE INDEX idx_entity_assets_entity
                    ON entity_assets(entity_id, entity_type);
                CREATE INDEX idx_entity_assets_type
                    ON entity_assets(entity_id, asset_type);
                CREATE UNIQUE INDEX ux_entity_assets_entity_type_source_url
                    ON entity_assets(entity_id, asset_type, image_url COLLATE NOCASE)
                    WHERE image_url IS NOT NULL AND length(trim(image_url)) > 0;

                INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
                VALUES ('005_expanded_artwork_asset_types', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        });
    }

    private static void EnsureAdaptiveDeliverySchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS adaptive_hls_packages (
                id BLOB NOT NULL PRIMARY KEY,
                asset_id BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
                source_hash TEXT NOT NULL,
                profile_key TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('preparing', 'ready', 'failed', 'deleting')),
                root_path TEXT NOT NULL,
                total_bytes INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                last_accessed TEXT NOT NULL,
                completed_at TEXT,
                last_error TEXT,
                UNIQUE(asset_id, source_hash, profile_key)
            );
            CREATE INDEX IF NOT EXISTS idx_adaptive_hls_packages_eviction
                ON adaptive_hls_packages(status, last_accessed);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureOnboardingSchema(SqliteConnection conn)
    {
        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS onboarding_workflows (
                workflow_version INTEGER NOT NULL PRIMARY KEY,
                state TEXT NOT NULL CHECK (state IN ('in_progress', 'complete')),
                current_step TEXT NOT NULL,
                administrator_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
                revision INTEGER NOT NULL DEFAULT 0,
                completed_at TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS onboarding_steps (
                workflow_version INTEGER NOT NULL REFERENCES onboarding_workflows(workflow_version) ON DELETE CASCADE,
                step_key TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('not_started', 'in_progress', 'passed', 'deferred', 'blocked')),
                detail TEXT,
                repair_target TEXT,
                completed_at TEXT,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (workflow_version, step_key)
            );

            CREATE TABLE IF NOT EXISTS onboarding_sessions (
                id BLOB NOT NULL PRIMARY KEY,
                workflow_version INTEGER NOT NULL REFERENCES onboarding_workflows(workflow_version) ON DELETE CASCADE,
                token_hash TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                last_used_at TEXT NOT NULL,
                revoked_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_onboarding_sessions_active
                ON onboarding_sessions(workflow_version, expires_at, revoked_at);

            CREATE TABLE IF NOT EXISTS setup_codes (
                id BLOB NOT NULL PRIMARY KEY,
                code_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                failed_attempts INTEGER NOT NULL DEFAULT 0,
                consumed_at TEXT,
                invalidated_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_setup_codes_active
                ON setup_codes(consumed_at, invalidated_at, expires_at);

            CREATE TABLE IF NOT EXISTS onboarding_restore_operations (
                id BLOB NOT NULL PRIMARY KEY,
                workflow_version INTEGER NOT NULL REFERENCES onboarding_workflows(workflow_version) ON DELETE CASCADE,
                archive_path TEXT NOT NULL,
                original_file_name TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('inspected', 'scheduled', 'applied', 'failed', 'cancelled')),
                manifest_version TEXT NOT NULL,
                database_epoch TEXT NOT NULL,
                summary_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            INSERT OR IGNORE INTO onboarding_workflows
                (workflow_version, state, current_step, revision, created_at, updated_at)
            VALUES
                (1, 'in_progress', 'preflight', 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now'));

            INSERT OR IGNORE INTO onboarding_steps (workflow_version, step_key, status, updated_at)
            VALUES
                (1, 'preflight', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'administrator', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'media-locations', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'providers', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'readiness', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
            cmd.ExecuteNonQuery();
        });
    }

    private static void EnsureIdentitySchema(SqliteConnection conn)
    {
        using var legacyIdentity = conn.CreateCommand();
        legacyIdentity.CommandText = """
            SELECT CASE WHEN
                EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'profile_external_logins')
                OR EXISTS (SELECT 1 FROM pragma_table_info('profile_credentials') WHERE name = 'normalized_username')
                OR EXISTS (SELECT 1 FROM pragma_table_info('auth_sessions') WHERE name = 'profile_id')
                OR EXISTS (SELECT 1 FROM pragma_table_info('password_recovery_codes') WHERE name = 'profile_id')
            THEN 1 ELSE 0 END;
            """;
        if (Convert.ToInt32(legacyIdentity.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
        {
            throw new InvalidOperationException(
                "Unsupported pre-beta profile-owned authentication schema was found. Reset the disposable development database and configure the new account/profile identity model.");
        }

        using var legacyPin = conn.CreateCommand();
        legacyPin.CommandText = "SELECT COUNT(1) FROM pragma_table_info('profiles') WHERE name = 'pin_hash';";
        if (Convert.ToInt32(legacyPin.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
        {
            using var populated = conn.CreateCommand();
            populated.CommandText = "SELECT COUNT(1) FROM profiles WHERE pin_hash IS NOT NULL AND trim(pin_hash) <> '';";
            if (Convert.ToInt32(populated.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException(
                    "Unsupported pre-beta SHA-256 profile PIN state was found. Reset the disposable development database and configure a new password or profile PIN.");
            }
        }

        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                migration_id TEXT NOT NULL PRIMARY KEY,
                applied_at TEXT NOT NULL
            );

            INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
            VALUES ('004_account_profile_identity', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
            cmd.ExecuteNonQuery();
        });
    }

    private static void EnsureCurrentColumns(SqliteConnection conn)
    {
        var addedMembershipMode = AddColumnIfMissing(conn, "collections", "membership_mode",
            "ALTER TABLE collections ADD COLUMN membership_mode TEXT NOT NULL DEFAULT 'Smart';");
        // A person's own sign-in follows the household's main sign-in for feature and library access.
        AddColumnIfMissing(conn, "accounts", "grants_inherit_from_account_id",
            "ALTER TABLE accounts ADD COLUMN grants_inherit_from_account_id BLOB REFERENCES accounts(id) ON DELETE SET NULL;");
        // An account made on this computer without a password works only on this computer until it is secured.
        AddColumnIfMissing(conn, "accounts", "this_computer_only",
            "ALTER TABLE accounts ADD COLUMN this_computer_only INTEGER NOT NULL DEFAULT 0 CHECK (this_computer_only IN (0, 1));");
        // An administrator-set temporary password: the person must choose their own before doing anything else.
        AddColumnIfMissing(conn, "accounts", "must_change_password",
            "ALTER TABLE accounts ADD COLUMN must_change_password INTEGER NOT NULL DEFAULT 0 CHECK (must_change_password IN (0, 1));");
        AddColumnIfMissing(conn, "accounts", "temporary_password_expires_at",
            "ALTER TABLE accounts ADD COLUMN temporary_password_expires_at TEXT;");
        // Sessions remember where they started; existing rows become home-only, which fails closed.
        AddColumnIfMissing(conn, "auth_sessions", "issued_ingress",
            "ALTER TABLE auth_sessions ADD COLUMN issued_ingress TEXT NOT NULL DEFAULT 'home_network';");
        // When the person last proved it was them on this session; sensitive actions need it to be recent.
        // Existing sessions are treated as signed in when they were created (the reader falls back to created_at).
        AddColumnIfMissing(conn, "auth_sessions", "authenticated_at",
            "ALTER TABLE auth_sessions ADD COLUMN authenticated_at TEXT;");
        // 1 while the person still has to pick who is using Tuvima (a remembered profile had a PIN); cleared by the next profile switch.
        AddColumnIfMissing(conn, "auth_sessions", "profile_pending",
            "ALTER TABLE auth_sessions ADD COLUMN profile_pending INTEGER NOT NULL DEFAULT 0;");
        AddColumnIfMissing(conn, "collections", "primary_area",
            "ALTER TABLE collections ADD COLUMN primary_area TEXT NOT NULL DEFAULT 'Mixed';");
        var addedOwnerKind = AddColumnIfMissing(conn, "collections", "owner_kind",
            "ALTER TABLE collections ADD COLUMN owner_kind TEXT NOT NULL DEFAULT 'Library';");
        var addedAudience = AddColumnIfMissing(conn, "collections", "audience",
            "ALTER TABLE collections ADD COLUMN audience TEXT NOT NULL DEFAULT 'Everyone';");
        if (addedMembershipMode || addedOwnerKind || addedAudience)
        {
            using var backfill = conn.CreateCommand();
            backfill.CommandText = """
                UPDATE collections
                SET membership_mode = CASE
                        WHEN collection_type IN ('Custom', 'Playlist')
                             AND (resolution = 'materialized' OR rule_json IS NULL OR trim(rule_json) = '')
                            THEN 'Manual'
                        ELSE membership_mode
                    END,
                    primary_area = CASE
                        WHEN collection_type = 'Playlist' THEN 'Listen'
                        ELSE primary_area
                    END,
                    owner_kind = CASE WHEN scope = 'user' THEN 'Profile' ELSE 'Library' END,
                    audience = CASE WHEN scope = 'user' THEN 'Private' ELSE 'Everyone' END;
                """;
            backfill.ExecuteNonQuery();
        }
        using (var audienceTable = conn.CreateCommand())
        {
            audienceTable.CommandText = """
                CREATE TABLE IF NOT EXISTS collection_profile_audience (
                    collection_id BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
                    profile_id BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    PRIMARY KEY(collection_id, profile_id)
                );
                """;
            audienceTable.ExecuteNonQuery();
        }

        AddColumnIfMissing(conn, "collections", "background_artwork_path",
            "ALTER TABLE collections ADD COLUMN background_artwork_path TEXT;");
        AddColumnIfMissing(conn, "collections", "background_artwork_mime_type",
            "ALTER TABLE collections ADD COLUMN background_artwork_mime_type TEXT;");
        AddColumnIfMissing(conn, "collections", "logo_artwork_path",
            "ALTER TABLE collections ADD COLUMN logo_artwork_path TEXT;");
        AddColumnIfMissing(conn, "collections", "logo_artwork_mime_type",
            "ALTER TABLE collections ADD COLUMN logo_artwork_mime_type TEXT;");
        AddColumnIfMissing(conn, "collections", "banner_artwork_path",
            "ALTER TABLE collections ADD COLUMN banner_artwork_path TEXT;");
        AddColumnIfMissing(conn, "collections", "banner_artwork_mime_type",
            "ALTER TABLE collections ADD COLUMN banner_artwork_mime_type TEXT;");
        AddColumnIfMissing(conn, "collections", "secondary_sort_field",
            "ALTER TABLE collections ADD COLUMN secondary_sort_field TEXT;");
        AddColumnIfMissing(conn, "collections", "secondary_sort_direction",
            "ALTER TABLE collections ADD COLUMN secondary_sort_direction TEXT;");
        AddColumnIfMissing(conn, "collections", "display_overrides_json",
            "ALTER TABLE collections ADD COLUMN display_overrides_json TEXT;");
        AddColumnIfMissing(conn, "view_galleries", "soundtrack_playlist_id",
            "ALTER TABLE view_galleries ADD COLUMN soundtrack_playlist_id BLOB REFERENCES collections(id) ON DELETE SET NULL;");
        AddColumnIfMissing(conn, "local_item_metadata", "location_city", "ALTER TABLE local_item_metadata ADD COLUMN location_city TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "location_region", "ALTER TABLE local_item_metadata ADD COLUMN location_region TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "location_country", "ALTER TABLE local_item_metadata ADD COLUMN location_country TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "location_country_code", "ALTER TABLE local_item_metadata ADD COLUMN location_country_code TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "location_source", "ALTER TABLE local_item_metadata ADD COLUMN location_source TEXT;");
        var addedLocationOverride = AddColumnIfMissing(conn, "local_item_metadata", "location_user_override", "ALTER TABLE local_item_metadata ADD COLUMN location_user_override INTEGER NOT NULL DEFAULT 0;");
        var addedEmbeddedLatitude = AddColumnIfMissing(conn, "local_item_metadata", "embedded_latitude", "ALTER TABLE local_item_metadata ADD COLUMN embedded_latitude REAL;");
        var addedEmbeddedLongitude = AddColumnIfMissing(conn, "local_item_metadata", "embedded_longitude", "ALTER TABLE local_item_metadata ADD COLUMN embedded_longitude REAL;");
        AddColumnIfMissing(conn, "local_item_metadata", "description", "ALTER TABLE local_item_metadata ADD COLUMN description TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "lens_model", "ALTER TABLE local_item_metadata ADD COLUMN lens_model TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "exposure_time", "ALTER TABLE local_item_metadata ADD COLUMN exposure_time TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "aperture", "ALTER TABLE local_item_metadata ADD COLUMN aperture REAL;");
        AddColumnIfMissing(conn, "local_item_metadata", "iso", "ALTER TABLE local_item_metadata ADD COLUMN iso INTEGER;");
        AddColumnIfMissing(conn, "local_item_metadata", "focal_length_mm", "ALTER TABLE local_item_metadata ADD COLUMN focal_length_mm REAL;");
        AddColumnIfMissing(conn, "local_item_metadata", "video_codec", "ALTER TABLE local_item_metadata ADD COLUMN video_codec TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "frame_rate", "ALTER TABLE local_item_metadata ADD COLUMN frame_rate REAL;");
        AddColumnIfMissing(conn, "profile_view_preferences", "viewer_info_open", "ALTER TABLE profile_view_preferences ADD COLUMN viewer_info_open INTEGER NOT NULL DEFAULT 1;");
        var addedEmbeddedCapturedAt = AddColumnIfMissing(conn, "local_items", "embedded_captured_at", "ALTER TABLE local_items ADD COLUMN embedded_captured_at TEXT;");
        AddColumnIfMissing(conn, "local_items", "captured_at_user_override", "ALTER TABLE local_items ADD COLUMN captured_at_user_override INTEGER NOT NULL DEFAULT 0;");
        if (addedEmbeddedCapturedAt)
        {
            conn.Execute("UPDATE local_items SET embedded_captured_at = captured_at WHERE captured_at IS NOT NULL;");
        }
        if (addedLocationOverride || addedEmbeddedLatitude || addedEmbeddedLongitude)
        {
            using var localLocationBackfill = conn.CreateCommand();
            localLocationBackfill.CommandText = """
                UPDATE local_item_metadata
                   SET embedded_latitude = COALESCE(embedded_latitude, latitude),
                       embedded_longitude = COALESCE(embedded_longitude, longitude),
                       location_source = CASE
                           WHEN location_source IS NOT NULL THEN location_source
                           WHEN latitude IS NOT NULL AND longitude IS NOT NULL THEN 'embedded'
                           ELSE NULL END;
                """;
            localLocationBackfill.ExecuteNonQuery();
        }
        AddColumnIfMissing(
            conn,
            "media_assets",
            "presented_at",
            "ALTER TABLE media_assets ADD COLUMN presented_at TEXT;");

        AddColumnIfMissing(
            conn,
            "works",
            "ordinal_sort",
            "ALTER TABLE works ADD COLUMN ordinal_sort REAL;");

        AddColumnIfMissing(
            conn,
            "series_manifest_items",
            "membership_scope",
            "ALTER TABLE series_manifest_items ADD COLUMN membership_scope TEXT NOT NULL DEFAULT 'MainSequence';");

        AddColumnIfMissing(
            conn,
            "series_manifest_items",
            "ordinal_scope_qid",
            "ALTER TABLE series_manifest_items ADD COLUMN ordinal_scope_qid TEXT;");

        AddColumnIfMissing(
            conn,
            "series_manifest_items",
            "duration",
            "ALTER TABLE series_manifest_items ADD COLUMN duration TEXT;");

        AddColumnIfMissing(
            conn,
            "metadata_claims",
            "decision_source_provider_id",
            "ALTER TABLE metadata_claims ADD COLUMN decision_source_provider_id BLOB REFERENCES metadata_providers(id);");

        AddColumnIfMissing(conn, "metadata_claims", "observation_set_id",
            "ALTER TABLE metadata_claims ADD COLUMN observation_set_id BLOB;");
        AddColumnIfMissing(conn, "metadata_claims", "is_current",
            "ALTER TABLE metadata_claims ADD COLUMN is_current INTEGER NOT NULL DEFAULT 1 CHECK (is_current IN (0, 1));");
        AddColumnIfMissing(conn, "metadata_claims", "superseded_at",
            "ALTER TABLE metadata_claims ADD COLUMN superseded_at TEXT;");

        AddColumnIfMissing(conn, "identity_jobs", "poison_attempt_count",
            "ALTER TABLE identity_jobs ADD COLUMN poison_attempt_count INTEGER NOT NULL DEFAULT 0;");
        AddColumnIfMissing(conn, "identity_jobs", "last_outcome_category",
            "ALTER TABLE identity_jobs ADD COLUMN last_outcome_category TEXT;");
        AddColumnIfMissing(conn, "media_operations", "last_outcome_category",
            "ALTER TABLE media_operations ADD COLUMN last_outcome_category TEXT;");
        AddColumnIfMissing(conn, "media_operations", "poison_attempt_count",
            "ALTER TABLE media_operations ADD COLUMN poison_attempt_count INTEGER NOT NULL DEFAULT 0;");
        AddColumnIfMissing(conn, "entity_capability_states", "last_outcome_category",
            "ALTER TABLE entity_capability_states ADD COLUMN last_outcome_category TEXT;");
        AddColumnIfMissing(conn, "ai_feature_artifacts", "last_outcome_category",
            "ALTER TABLE ai_feature_artifacts ADD COLUMN last_outcome_category TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "year",
            "ALTER TABLE player_queue_items ADD COLUMN year TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "content_rating",
            "ALTER TABLE player_queue_items ADD COLUMN content_rating TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "season_number",
            "ALTER TABLE player_queue_items ADD COLUMN season_number TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "episode_number",
            "ALTER TABLE player_queue_items ADD COLUMN episode_number TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "episode_title",
            "ALTER TABLE player_queue_items ADD COLUMN episode_title TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "quality",
            "ALTER TABLE player_queue_items ADD COLUMN quality TEXT;");
    }

    private static void SeedMetadataProviders(SqliteConnection conn)
    {
        ReadOnlySpan<(Guid Id, string Name, string Version)> providers =
        [
            (WellKnownProviders.LocalProcessor, "local_processor", "1.0"),
            (WellKnownProviders.LibraryScanner, "library_scanner", "1.0"),
            (WellKnownProviders.AppleApi, "apple_api", "2.0"),
            (WellKnownProviders.Wikidata, "wikidata", "1.0"),
            (WellKnownProviders.Wikipedia, "wikipedia", "1.0"),
            (WellKnownProviders.OpenLibrary, "open_library", "1.0"),
            (WellKnownProviders.MusicBrainz, "musicbrainz", "1.0"),
            (WellKnownProviders.Tmdb, "tmdb", "1.0"),
            (WellKnownProviders.ComicVine, "comicvine", "1.0"),
            (WellKnownProviders.Lrclib, "lrclib", "1.0"),
            (WellKnownProviders.OpenSubtitles, "opensubtitles", "1.0"),
            (WellKnownProviders.Subdl, "subdl", "2.0"),
            (WellKnownProviders.UserManual, "user_manual", "1.0"),
            (WellKnownProviders.AiProvider, "ai_provider", "1.0"),
        ];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
            VALUES (@id, @name, @version, 1);
            """;

        var pId = cmd.Parameters.Add("@id", SqliteType.Blob);
        var pName = cmd.Parameters.Add("@name", SqliteType.Text);
        var pVersion = cmd.Parameters.Add("@version", SqliteType.Text);

        foreach (var (id, name, version) in providers)
        {
            pId.Value = GuidSql.ToBlob(id);
            pName.Value = name;
            pVersion.Value = version;
            cmd.ExecuteNonQuery();
        }
    }

    private static void SeedDefaultProfile(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@id, @name, @color, @role, @created);
            """;
        cmd.Parameters.Add("@id", SqliteType.Blob).Value =
            GuidSql.ToBlob(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        cmd.Parameters.AddWithValue("@name", "Owner");
        cmd.Parameters.AddWithValue("@color", "#7C4DFF");
        cmd.Parameters.AddWithValue("@role", "Administrator");
        cmd.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @column;";
        cmd.Parameters.AddWithValue("@column", column);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// A child (restricted) profile never has administrator authority, and profiles used to be created restricted
    /// by default even for administrator accounts. One time, promote the profile an administrator actually uses as
    /// an administrator (default, admin-enabled grant) and that no non-administrator also has, so the new rule cannot
    /// lock an administrator out. A child profile shared with a parent stays restricted.
    /// </summary>
    private static void PromoteRestrictedAdministratorProfiles(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE profiles SET role = 'StandardUser'
            WHERE role = 'RestrictedProfile'
              AND id IN (
                  SELECT g.profile_id
                  FROM account_profile_grants g
                  JOIN accounts a ON a.id = g.account_id
                  WHERE a.is_administrator = 1
                    AND g.is_default = 1
                    AND g.admin_enabled = 1
                    AND NOT EXISTS (
                        SELECT 1 FROM account_profile_grants o
                        JOIN accounts oa ON oa.id = o.account_id
                        WHERE o.profile_id = g.profile_id AND oa.is_administrator = 0));
            """;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureCurrentIndexes(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH duplicate_pending_reviews AS (
                SELECT rowid,
                       ROW_NUMBER() OVER (
                           PARTITION BY entity_id, trigger
                           ORDER BY created_at ASC, rowid ASC
                       ) AS rn
                FROM review_queue
                WHERE status = 'Pending'
            )
            UPDATE review_queue
            SET status = 'Resolved',
                resolved_at = strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                resolved_by = 'system:review-dedupe'
            WHERE rowid IN (
                SELECT rowid
                FROM duplicate_pending_reviews
                WHERE rn > 1
            );

            CREATE INDEX IF NOT EXISTS idx_editions_work_id
                ON editions(work_id);

            CREATE INDEX IF NOT EXISTS idx_media_assets_edition_id
                ON media_assets(edition_id);

            CREATE INDEX IF NOT EXISTS idx_media_assets_presented
                ON media_assets(presented_at) WHERE presented_at IS NOT NULL;

            CREATE INDEX IF NOT EXISTS idx_media_assets_file_path_root
                ON media_assets(file_path_root COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS idx_media_assets_content_hash
                ON media_assets(content_hash);

            CREATE INDEX IF NOT EXISTS idx_media_assets_status
                ON media_assets(status);

            DELETE FROM file_hash_cache
            WHERE rowid NOT IN (
                SELECT MAX(rowid)
                FROM file_hash_cache
                GROUP BY absolute_path COLLATE NOCASE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_file_hash_cache_path_nocase
                ON file_hash_cache(absolute_path COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS idx_collection_items_collection_sort
                ON collection_items(collection_id, sort_order);

            CREATE INDEX IF NOT EXISTS idx_collection_items_work
                ON collection_items(work_id);

            CREATE INDEX IF NOT EXISTS idx_works_collection_ordinal_sort
                ON works(collection_id, ordinal_sort);

            CREATE INDEX IF NOT EXISTS idx_canonical_values_key_value_entity
                ON canonical_values(key, value, entity_id);

            CREATE INDEX IF NOT EXISTS idx_canonical_values_entity_key
                ON canonical_values(entity_id, key);

            CREATE INDEX IF NOT EXISTS idx_canonical_value_arrays_key_value_entity
                ON canonical_value_arrays(key, value, entity_id);

            CREATE INDEX IF NOT EXISTS idx_canonical_value_arrays_key_qid_entity
                ON canonical_value_arrays(key, value_qid, entity_id);

            CREATE INDEX IF NOT EXISTS idx_metadata_claims_current_lookup
                ON metadata_claims(entity_id, provider_id, claim_key, is_current);

            CREATE INDEX IF NOT EXISTS idx_person_media_links_person
                ON person_media_links(person_id);

            CREATE INDEX IF NOT EXISTS idx_person_media_links_asset_role_person
                ON person_media_links(media_asset_id, role, person_id);

            CREATE INDEX IF NOT EXISTS idx_persons_name_nocase
                ON persons(name COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS idx_works_curator_state
                ON works(curator_state) WHERE curator_state IS NOT NULL;

            CREATE INDEX IF NOT EXISTS idx_works_catalog_media_type
                ON works(is_catalog_only, media_type);

            CREATE INDEX IF NOT EXISTS idx_ingestion_log_run_created
                ON ingestion_log(ingestion_run_id, created_at);

            CREATE INDEX IF NOT EXISTS idx_identity_jobs_run_entity_updated
                ON identity_jobs(ingestion_run_id, entity_id, updated_at);

            CREATE INDEX IF NOT EXISTS idx_identity_jobs_activity_latest
                ON identity_jobs(ingestion_run_id, entity_id, updated_at, created_at);

            CREATE INDEX IF NOT EXISTS idx_identity_jobs_next_retry
                ON identity_jobs(next_retry_at) WHERE next_retry_at IS NOT NULL;

            CREATE INDEX IF NOT EXISTS idx_media_operations_source_path
                ON media_operations(operation_type, source_path, status);

            CREATE INDEX IF NOT EXISTS idx_media_operations_batch_entity_type
                ON media_operations(batch_id, entity_id, operation_type);

            CREATE INDEX IF NOT EXISTS idx_media_operation_events_batch_entity
                ON media_operation_events(batch_id, entity_id, occurred_at);

            CREATE INDEX IF NOT EXISTS idx_review_queue_status_entity_ready
                ON review_queue(status, entity_id, review_ready_at);

            CREATE INDEX IF NOT EXISTS idx_review_queue_status
                ON review_queue(status);

            CREATE INDEX IF NOT EXISTS idx_review_queue_entity_id
                ON review_queue(entity_id);

            CREATE INDEX IF NOT EXISTS idx_ingestion_batches_status
                ON ingestion_batches(status);

            CREATE INDEX IF NOT EXISTS idx_ingestion_batch_artifacts_batch
                ON ingestion_batch_artifacts(batch_id);

            CREATE INDEX IF NOT EXISTS ix_local_items_owner_timeline
                ON local_items(owner_profile_id, archived_at, trashed_at, COALESCE(captured_at, created_at) DESC, id DESC);

            CREATE INDEX IF NOT EXISTS ix_local_items_space_timeline
                ON local_items(personal_space_id, archived_at, trashed_at, COALESCE(captured_at, created_at) DESC, id DESC);

            CREATE INDEX IF NOT EXISTS ix_local_items_library_favorite_timeline
                ON local_items(library_id, favorite, archived_at, trashed_at, COALESCE(captured_at, created_at) DESC, id DESC);

            CREATE INDEX IF NOT EXISTS ix_local_item_metadata_location
                ON local_item_metadata(location_name, latitude, longitude, item_id)
                WHERE latitude IS NOT NULL AND longitude IS NOT NULL;

            CREATE INDEX IF NOT EXISTS ix_local_items_library_atlas_timeline
                ON local_items(library_id, hidden, archived_at, trashed_at, media_kind,
                               favorite, COALESCE(captured_at, created_at), id);

            CREATE INDEX IF NOT EXISTS ix_view_galleries_owner_order
                ON view_galleries(owner_profile_id, sort_order, updated_at DESC, id);

            CREATE INDEX IF NOT EXISTS ix_view_gallery_shares_profile
                ON view_gallery_shares(profile_id, shared_at DESC, gallery_id);

            CREATE INDEX IF NOT EXISTS ix_local_item_tags_tag
                ON local_item_tags(tag, item_id);

            CREATE INDEX IF NOT EXISTS ix_local_item_annotations_lookup
                ON local_item_annotations(annotation_kind, annotation_value, item_id);

            CREATE INDEX IF NOT EXISTS ix_collection_view_sources_collection
                ON collection_view_sources(collection_id, position, id);

            CREATE INDEX IF NOT EXISTS ix_collection_view_sources_owner
                ON collection_view_sources(owner_profile_id, source_kind, collection_id, id);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_view_sources_gallery
                ON collection_view_sources(collection_id, gallery_id)
                WHERE source_kind = 'gallery';

            CREATE INDEX IF NOT EXISTS idx_system_activity_run_entity_action
                ON system_activity(ingestion_run_id, entity_id, action_type, occurred_at);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_review_queue_pending_entity_trigger
                ON review_queue(entity_id, trigger)
                WHERE status = 'Pending';

            CREATE UNIQUE INDEX IF NOT EXISTS ux_collections_custom_rule_hash
                ON collections(rule_hash)
                WHERE rule_hash IS NOT NULL AND is_enabled = 1 AND collection_type = 'Custom';
            """;
        cmd.ExecuteNonQuery();
    }

    private static bool AddColumnIfMissing(
        SqliteConnection conn,
        string table,
        string column,
        string alterSql)
    {
        using var exists = conn.CreateCommand();
        exists.CommandText = $"PRAGMA table_info([{table}]);";
        using var reader = exists.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = alterSql;
        alter.ExecuteNonQuery();
        return true;
    }

    private static void EnsureEditionArtworkOwnerSchema(SqliteConnection conn)
    {
        using var inspect = conn.CreateCommand();
        inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='entity_assets';";
        var tableSql = inspect.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(tableSql)
            || tableSql.Contains("'Edition'", StringComparison.Ordinal))
        {
            return;
        }

        // Keep canonical and legacy preference stores in step. A canonical-only
        // Edition link would be invisible to readers still using entity_assets.
        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                ALTER TABLE entity_assets RENAME TO entity_assets_pre_edition_owner;

                CREATE TABLE entity_assets (
                    id BLOB PRIMARY KEY,
                    entity_id BLOB NOT NULL,
                    entity_type TEXT NOT NULL CHECK(entity_type IN ('Work','Edition','Person','Universe','FictionalEntity')),
                    asset_type TEXT NOT NULL CHECK(asset_type IN ('CoverArt','Headshot','Banner','Logo','NetworkLogo','StudioLogo','Background','SeasonPoster','SeasonThumb','EpisodeStill','CharacterPortrait')),
                    image_url TEXT,
                    local_image_path TEXT,
                    local_image_path_s TEXT,
                    local_image_path_m TEXT,
                    local_image_path_l TEXT,
                    source_provider TEXT,
                    width_px INTEGER,
                    height_px INTEGER,
                    aspect_class TEXT NOT NULL DEFAULT 'UnsupportedRect',
                    primary_hex TEXT,
                    secondary_hex TEXT,
                    accent_hex TEXT,
                    asset_class TEXT NOT NULL DEFAULT 'Artwork',
                    storage_location TEXT NOT NULL DEFAULT 'Central',
                    owner_scope TEXT NOT NULL DEFAULT 'Unknown',
                    is_preferred INTEGER NOT NULL DEFAULT 0,
                    is_user_override INTEGER NOT NULL DEFAULT 0,
                    is_locally_exported INTEGER NOT NULL DEFAULT 0,
                    is_preferred_exported INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT
                );

                INSERT INTO entity_assets (
                    id, entity_id, entity_type, asset_type, image_url,
                    local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                    source_provider, width_px, height_px, aspect_class,
                    primary_hex, secondary_hex, accent_hex, asset_class, storage_location, owner_scope,
                    is_preferred, is_user_override, is_locally_exported, is_preferred_exported,
                    created_at, updated_at)
                SELECT id, entity_id, entity_type, asset_type, image_url,
                    local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                    source_provider, width_px, height_px, aspect_class,
                    primary_hex, secondary_hex, accent_hex, asset_class, storage_location, owner_scope,
                    is_preferred, is_user_override, is_locally_exported, is_preferred_exported,
                    created_at, updated_at
                FROM entity_assets_pre_edition_owner;

                DROP TABLE entity_assets_pre_edition_owner;
                CREATE INDEX idx_entity_assets_entity ON entity_assets(entity_id, entity_type);
                CREATE INDEX idx_entity_assets_type ON entity_assets(entity_id, asset_type);
                CREATE UNIQUE INDEX ux_entity_assets_entity_type_source_url
                    ON entity_assets(entity_id, asset_type, image_url COLLATE NOCASE)
                    WHERE image_url IS NOT NULL AND length(trim(image_url)) > 0;
                INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
                VALUES ('007_edition_artwork_owner', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        });
    }
}
