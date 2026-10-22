using System.Globalization;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Backup;

/// <summary>Why a user export cannot be imported into this data store.</summary>
public enum UserImportRefusal
{
    /// <summary>Nothing stands in the way.</summary>
    None,

    /// <summary>Someone here already signs in with the file's email.</summary>
    EmailInUse,

    /// <summary>The account, a profile, a collection or a passkey in the file already exists here.</summary>
    AlreadyHere,

    /// <summary>The person's outside sign-in is already linked to someone here.</summary>
    OutsideSignInInUse,

    /// <summary>The file holds a two-step secret that was not made readable for any computer, so it would lock the person out.</summary>
    TwoStepCannotMove,
}

/// <summary>Raised when an import is refused; nothing was written.</summary>
public sealed class UserImportRefusedException(UserImportRefusal refusal) : Exception("The user could not be imported.")
{
    /// <summary>Why the import was refused.</summary>
    public UserImportRefusal Refusal { get; } = refusal;
}

/// <summary>What a user import did, in counts and names the administrator already saw in the preview.</summary>
/// <param name="AccountId">The new account.</param>
/// <param name="Email">The email the person signs in with.</param>
/// <param name="ProfileNames">Their profiles.</param>
/// <param name="WaitingItems">Items that wait for their title to be found.</param>
/// <param name="AppliedRows">Rows put straight into place.</param>
public sealed record UserImportResult(Guid AccountId, string Email, IReadOnlyList<string> ProfileNames, int WaitingItems, int AppliedRows);

/// <summary>
/// Puts one user's export (<see cref="UserExportScope"/>) into this data store as a new user, in one write transaction (backup and restore
/// spec D19). Access rights are never carried: the person is not an administrator, has no feature or library grants and no grant is an
/// administrator grant, so they can sign in and see nothing until an administrator chooses. Their profile's Vault access is switched off
/// for the same reason. If the household already exists here the person joins it; otherwise it is created with them as its primary account.
/// Rows are written with a plain INSERT (never REPLACE), so anything that already exists fails the whole import. Rows that point at titles
/// wait, exactly as in a restore, and attach when their title is found.
/// </summary>
public sealed class UserImportRepository
{
    private readonly IDatabaseConnection _db;

    /// <param name="db">The data store.</param>
    public UserImportRepository(IDatabaseConnection db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Why <paramref name="summary"/> cannot be imported right now, or <see cref="UserImportRefusal.None"/>. Writes nothing.</summary>
    public UserImportRefusal Check(UserExportSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        using var connection = _db.CreateConnection();
        return Preflight(connection, null, summary);
    }

    /// <summary>The name of a household that already exists in this data store, or null when there is none with that id.</summary>
    public string? FindHouseholdName(Guid householdId)
    {
        using var connection = _db.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM households WHERE id = $id";
        command.Parameters.AddWithValue("$id", GuidSql.ToBlob(householdId));
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Imports the export as a new user. Throws <see cref="UserImportRefusedException"/> when something clashes at this moment, and
    /// <see cref="InvalidDataException"/> when the file is not one user's export; in both cases nothing was written.
    /// </summary>
    /// <param name="export">What the file holds.</param>
    /// <param name="fileName">The file's name, recorded with the import (it names no title and no person).</param>
    /// <param name="fileCreatedAt">When the file was made, when known.</param>
    /// <param name="codec">Protects the two-step secret with this computer's key.</param>
    /// <param name="now">The time of the import.</param>
    /// <param name="ct">Cancels the import.</param>
    public UserImportResult Import(
        BackupExport export,
        string fileName,
        DateTimeOffset? fileCreatedAt,
        IBackupPortableValueCodec codec,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var summary = UserExportReader.Read(export);

        try
        {
            return _db.ExecuteWriteAsync(
                (connection, transaction, token) => ImportCore(connection, transaction, export, summary, fileName, fileCreatedAt, codec, now, token),
                ct).GetAwaiter().GetResult();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // A row that already exists (a collection or passkey id), or any other rule the data store holds: nothing was written.
            throw new UserImportRefusedException(UserImportRefusal.AlreadyHere);
        }
    }

    private static UserImportResult ImportCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BackupExport export,
        UserExportSummary summary,
        string fileName,
        DateTimeOffset? fileCreatedAt,
        IBackupPortableValueCodec codec,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Checked again inside the write transaction, so a person who signed up a moment ago still wins.
        var refusal = Preflight(connection, transaction, summary);
        if (refusal != UserImportRefusal.None)
        {
            throw new UserImportRefusedException(refusal);
        }

        var householdExists = summary.HouseholdId is { } householdId && Exists(connection, transaction, "households", "id", GuidSql.ToBlob(householdId));
        var prepared = Prepare(export, summary, householdExists);
        var result = BackupRestoreRepository.ImportCore(connection, transaction, prepared, fileName, fileCreatedAt, codec, now, Guid.NewGuid(), ct, plainInsert: true);

        // Nothing is removed quietly: a row left pointing at something that does not exist means the file was not a whole user.
        foreach (var table in prepared.Tables.Where(table => table.Rows.Count > 0))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA foreign_key_check({RestoredRowSql.Quote(table.Table)})";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidDataException("This file isn't a user export.");
            }
        }

        return new UserImportResult(summary.AccountId, summary.Email, summary.ProfileNames, result.WaitingRows, result.AppliedRows);
    }

    private static UserImportRefusal Preflight(SqliteConnection connection, SqliteTransaction? transaction, UserExportSummary summary)
    {
        if (!summary.TwoStepSecretIsPortable)
        {
            return UserImportRefusal.TwoStepCannotMove;
        }

        if (Exists(connection, transaction, "accounts", "id", GuidSql.ToBlob(summary.AccountId))
            || summary.ProfileIds.Any(id => Exists(connection, transaction, "profiles", "id", GuidSql.ToBlob(id)))
            || summary.CollectionIds.Any(id => Exists(connection, transaction, "collections", "id", GuidSql.ToBlob(id)))
            || summary.PasskeyIds.Any(id => Exists(connection, transaction, "account_passkeys", "credential_id", id)))
        {
            return UserImportRefusal.AlreadyHere;
        }

        if (Exists(connection, transaction, "accounts", "normalized_email", NormalizedEmail(summary.Email)))
        {
            return UserImportRefusal.EmailInUse;
        }

        foreach (var login in summary.ExternalLogins)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM account_external_logins WHERE provider = $provider AND issuer = $issuer AND subject = $subject";
            command.Parameters.AddWithValue("$provider", login.Provider);
            command.Parameters.AddWithValue("$issuer", login.Issuer);
            command.Parameters.AddWithValue("$subject", login.Subject);
            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                return UserImportRefusal.OutsideSignInInUse;
            }
        }

        return UserImportRefusal.None;
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction? transaction, string table, string column, object value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {RestoredRowSql.Quote(table)} WHERE {RestoredRowSql.Quote(column)} = $value";
        command.Parameters.AddWithValue("$value", value);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>The form sign-in looks an email up by (upper case, as the unique index holds it).</summary>
    internal static string NormalizedEmail(string email) => email.Trim().ToUpperInvariant();

    /// <summary>
    /// The export with the access rights taken out: not an administrator, no administrator grant, no follow-another-account link, no Vault
    /// access, no administrator profile role; the household joined or created; the household's administrator flag only when this import
    /// creates the household. Every excluded column (a file path, someone else's id) is emptied whatever the file says, so no file can
    /// make the Engine open, serve or delete a path it chose; collections arrive private and not featured, and keep a parent only when
    /// the parent is in the same file.
    /// </summary>
    private static BackupExport Prepare(BackupExport export, UserExportSummary summary, bool householdExists)
    {
        var accountBlob = GuidSql.ToBlob(summary.AccountId);
        var keepHousehold = summary.HouseholdId is not null && (householdExists || summary.HouseholdName is not null);
        var createdHousehold = keepHousehold && !householdExists;
        var collectionIds = summary.CollectionIds.ToHashSet();
        var tables = new List<BackupExportTable>();
        foreach (var original in export.Tables)
        {
            var table = ClearExcluded(original);
            switch (table.Table.ToLowerInvariant())
            {
                case "accounts":
                    tables.Add(Rewrite(table, new Dictionary<string, BackupValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["email"] = BackupValue.FromText(summary.Email),
                        ["normalized_email"] = BackupValue.FromText(NormalizedEmail(summary.Email)),
                        ["is_administrator"] = BackupValue.FromInteger(0),
                        ["grants_inherit_from_account_id"] = BackupValue.Null,
                        ["household_admin"] = BackupValue.FromInteger(createdHousehold && summary.HouseholdAdmin ? 1 : 0),
                        ["household_id"] = keepHousehold ? BackupValue.FromBlob(GuidSql.ToBlob(summary.HouseholdId!.Value)) : BackupValue.Null,
                    }));
                    break;
                case "households":
                    // A household that already exists is joined as it is; its primary account is not changed.
                    tables.Add(householdExists || !keepHousehold
                        ? table with { Rows = [] }
                        : Rewrite(table, new Dictionary<string, BackupValue>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["primary_account_id"] = BackupValue.FromBlob(accountBlob),
                        }));
                    break;
                case "account_profile_grants":
                    tables.Add(Rewrite(table, new Dictionary<string, BackupValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["admin_enabled"] = BackupValue.FromInteger(0),
                    }));
                    break;
                case "profiles":
                    tables.Add(Rewrite(
                        table,
                        new Dictionary<string, BackupValue>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["household_id"] = keepHousehold ? BackupValue.FromBlob(GuidSql.ToBlob(summary.HouseholdId!.Value)) : BackupValue.Null,
                        },
                        ("role", value => value is BackupValue.Text { Value: "Administrator" } ? BackupValue.FromText("StandardUser") : value)));
                    break;
                case "collections":
                    tables.Add(Rewrite(
                        table,
                        new Dictionary<string, BackupValue>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["is_featured"] = BackupValue.FromInteger(0),
                        },
                        ("audience", value => value is BackupValue.Text { Value: "Everyone" } ? BackupValue.FromText("Private") : value),
                        ("parent_collection_id", value => UserExportReader.GuidOf(value) is { } parent && collectionIds.Contains(parent) ? value : BackupValue.Null)));
                    break;
                case "profile_nsfw_settings":
                    tables.Add(Rewrite(table, new Dictionary<string, BackupValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["access_mode"] = BackupValue.FromText("off"),
                    }));
                    break;
                default:
                    tables.Add(table);
                    break;
            }
        }

        return export with { Tables = tables };
    }

    /// <summary>The table with every column its rules exclude (the backup rule's and the user export's own) set to NULL in every row.</summary>
    private static BackupExportTable ClearExcluded(BackupExportTable table)
    {
        var excluded = (BackupTableClassification.Find(table.Table)?.ExcludedColumns ?? [])
            .Concat(UserExportScope.Find(table.Table)?.ExcludedColumns ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexes = table.Columns
            .Select((column, index) => (column, index))
            .Where(entry => excluded.Contains(entry.column))
            .Select(entry => entry.index)
            .ToList();
        if (indexes.Count == 0)
        {
            return table;
        }

        var rows = table.Rows
            .Select(row =>
            {
                var values = row.ToList();
                foreach (var index in indexes)
                {
                    values[index] = BackupValue.Null;
                }

                return (IReadOnlyList<BackupValue>)values;
            })
            .ToList();
        return table with { Rows = rows };
    }

    /// <summary>The table with each named column set to the given value in every row (a column the table lacks is added).</summary>
    private static BackupExportTable Rewrite(
        BackupExportTable table,
        Dictionary<string, BackupValue> set,
        params (string Column, Func<BackupValue, BackupValue> Change)[] change)
    {
        var columns = table.Columns.ToList();
        foreach (var name in set.Keys.Where(name => !columns.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            columns.Add(name);
        }

        var rows = new List<IReadOnlyList<BackupValue>>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var values = row.ToList();
            while (values.Count < columns.Count)
            {
                values.Add(BackupValue.Null);
            }

            foreach (var (name, value) in set)
            {
                values[columns.FindIndex(column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase))] = value;
            }

            foreach (var (name, apply) in change)
            {
                var index = columns.FindIndex(column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    values[index] = apply(values[index]);
                }
            }

            rows.Add(values);
        }

        return table with { Columns = columns, Rows = rows };
    }
}
