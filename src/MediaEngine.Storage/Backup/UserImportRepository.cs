using System.Globalization;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Backup;

/// <summary>Why a user file cannot be imported into this data store, or why a choice was refused.</summary>
public enum UserImportRefusal
{
    /// <summary>Nothing stands in the way.</summary>
    None,

    /// <summary>Someone here already signs in with the email the sign-in would have.</summary>
    EmailInUse,

    /// <summary>A passkey in the file is already registered here, or the file clashes with a row that cannot take a new id.</summary>
    AlreadyHere,

    /// <summary>The person's outside sign-in is already linked to someone here.</summary>
    OutsideSignInInUse,

    /// <summary>The file holds a two-step secret that was not made readable for any computer, so it would lock the person out.</summary>
    TwoStepCannotMove,

    /// <summary>The administrator chose neither an existing household nor a new one, or both.</summary>
    HouseholdChoiceNeeded,

    /// <summary>The household chosen is not in this Library.</summary>
    HouseholdNotFound,

    /// <summary>The household chosen cannot hold this many more people.</summary>
    HouseholdFull,

    /// <summary>A new household needs one imported profile with a sign-in (not a Kids profile) to look after it.</summary>
    AdministratorChoiceNeeded,

    /// <summary>A second household is added only once every server administrator has an email and a password (users spec D10).</summary>
    SecureAdministratorsFirst,

    /// <summary>A choice names something that is not in the file, or does not apply to the household chosen.</summary>
    InvalidChoice,

    /// <summary>A sign-in would be imported with no email.</summary>
    EmailMissing,
}

/// <summary>Raised when an import is refused; nothing was written.</summary>
/// <param name="refusal">Why it was refused.</param>
/// <param name="email">The email the refusal is about, when it is about one.</param>
public sealed class UserImportRefusedException(UserImportRefusal refusal, string? email = null) : Exception("The user could not be imported.")
{
    /// <summary>Why the import was refused.</summary>
    public UserImportRefusal Refusal { get; } = refusal;

    /// <summary>The email the refusal is about, when it is about one.</summary>
    public string? Email { get; } = email;
}

/// <summary>What the administrator chose for an import (users spec D9). Nothing here can grant a role or library access.</summary>
/// <param name="TargetHouseholdId">An existing household to put the profiles in; null when <paramref name="NewHouseholdName"/> is given.</param>
/// <param name="NewHouseholdName">The name of a new household to make for them; null when an existing one is chosen.</param>
/// <param name="NewHouseholdAdministratorProfileId">
/// For a new household: the file's profile (one that keeps a sign-in) that looks after it. Never given for an existing household.
/// </param>
/// <param name="KeepAccess">True to keep Read/Watch/Listen/View as the file had it; false (the default) so they arrive with no access.</param>
/// <param name="Emails">Per sign-in in the file (by its id in the file): the email to use instead of the one in the file.</param>
/// <param name="WithoutSignIn">Sign-ins in the file (by id) to leave out: their profile arrives with no sign-in.</param>
public sealed record UserImportChoices(
    Guid? TargetHouseholdId,
    string? NewHouseholdName,
    Guid? NewHouseholdAdministratorProfileId = null,
    bool KeepAccess = false,
    IReadOnlyDictionary<Guid, string>? Emails = null,
    IReadOnlyCollection<Guid>? WithoutSignIn = null);

/// <summary>What the Engine can say about one sign-in of a file before anything is chosen.</summary>
/// <param name="SignInId">The sign-in's id in the file.</param>
/// <param name="EmailInUse">True when someone here already signs in with the file's email, so another one has to be chosen (or no sign-in).</param>
/// <param name="Problem">Why this sign-in cannot be imported as it is, or <see cref="UserImportRefusal.None"/>. A different email does not change it.</param>
public sealed record UserImportSignInStatus(Guid SignInId, bool EmailInUse, UserImportRefusal Problem);

/// <summary>A household that already exists here, as a place to put the imported profiles.</summary>
/// <param name="Id">The household.</param>
/// <param name="Name">Its name.</param>
/// <param name="Profiles">How many profiles it holds now.</param>
public sealed record UserImportHouseholdOption(Guid Id, string Name, int Profiles);

/// <summary>The Engine-side checks that go with a preview.</summary>
/// <param name="SignIns">One status per sign-in in the file.</param>
/// <param name="Households">The households that exist here.</param>
/// <param name="CanAddHousehold">False while a server administrator has no email and password yet and a household already exists (users spec D10).</param>
public sealed record UserImportInspection(
    IReadOnlyList<UserImportSignInStatus> SignIns,
    IReadOnlyList<UserImportHouseholdOption> Households,
    bool CanAddHousehold);

/// <summary>A profile an import made.</summary>
/// <param name="ProfileId">The new profile (its id in the file unless that was already used here).</param>
/// <param name="Name">Its name.</param>
/// <param name="SignInId">The sign-in that opens it, when one was imported.</param>
/// <param name="Email">That sign-in's email.</param>
/// <param name="IsHouseholdAdministrator">True for the one chosen to look after a new household.</param>
public sealed record UserImportedProfile(Guid ProfileId, string Name, Guid? SignInId, string? Email, bool IsHouseholdAdministrator);

/// <summary>What a user import did, in counts and names the administrator already saw in the preview.</summary>
/// <param name="HouseholdId">The household the profiles are in.</param>
/// <param name="HouseholdName">Its name.</param>
/// <param name="CreatedHousehold">True when the import made the household.</param>
/// <param name="Profiles">The profiles that came, in the file's order.</param>
/// <param name="WaitingItems">Items that wait for their title to be found.</param>
/// <param name="AppliedRows">Rows put straight into place.</param>
public sealed record UserImportResult(
    Guid HouseholdId,
    string HouseholdName,
    bool CreatedHousehold,
    IReadOnlyList<UserImportedProfile> Profiles,
    int WaitingItems,
    int AppliedRows);

/// <summary>
/// Puts a user file (<see cref="UserExportScope"/>: one profile with its sign-in, or a household) into this data store as new profiles in a
/// household the administrator chooses, in one write transaction (backup and restore spec D19, users spec D9). Roles are never carried: every
/// profile arrives as an ordinary member (Kids stays Kids), except the one chosen to look after a household the import creates. Read/Watch/
/// Listen/View arrive only when the administrator keeps them; library access never. Each profile's Vault access is switched off. A sign-in
/// keeps its password and passkeys but may get a different email, or none. Ids that already exist here are replaced by new ones, so nothing
/// existing is ever overwritten; rows are written with a plain INSERT (never REPLACE). Rows that point at titles wait, exactly as in a
/// restore, and attach when their title is found.
/// </summary>
public sealed class UserImportRepository
{
    private readonly IDatabaseConnection _db;

    /// <param name="db">The data store.</param>
    public UserImportRepository(IDatabaseConnection db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// What this Library can say about the file before the administrator chooses: per sign-in whether its email is used here or something else
    /// stops it, the households that exist, and whether a new one may be added. Writes nothing.
    /// </summary>
    public UserImportInspection Inspect(UserExportSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        using var connection = _db.CreateConnection();
        var signIns = summary.SignIns
            .Select(signIn => new UserImportSignInStatus(
                signIn.Id,
                Exists(connection, null, "accounts", "normalized_email", NormalizedEmail(signIn.Email)),
                SignInProblem(connection, null, signIn)))
            .ToList();

        var households = new List<UserImportHouseholdOption>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT h.id, h.name, (SELECT COUNT(*) FROM profiles p WHERE p.household_id = h.id) FROM households h ORDER BY h.is_default DESC, h.name COLLATE NOCASE, h.id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                households.Add(new UserImportHouseholdOption(GuidSql.FromDb(reader.GetValue(0)), reader.GetString(1), reader.GetInt32(2)));
            }
        }

        return new UserImportInspection(signIns, households, households.Count == 0 || AdministratorsSecured(connection, null));
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
    /// Imports the file's profiles under the chosen household. Throws <see cref="UserImportRefusedException"/> when a choice or something
    /// here stands in the way, and <see cref="InvalidDataException"/> when the file is not a user file; in both cases nothing was written.
    /// </summary>
    /// <param name="export">What the file holds.</param>
    /// <param name="fileName">The file's name, recorded with the import (it names no title and no person).</param>
    /// <param name="fileCreatedAt">When the file was made, when known.</param>
    /// <param name="codec">Protects the two-step secret with this computer's key.</param>
    /// <param name="now">The time of the import.</param>
    /// <param name="choices">Where the profiles go and what the administrator chose for each sign-in.</param>
    /// <param name="ct">Cancels the import.</param>
    public UserImportResult Import(
        BackupExport export,
        string fileName,
        DateTimeOffset? fileCreatedAt,
        IBackupPortableValueCodec codec,
        DateTimeOffset now,
        UserImportChoices choices,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var summary = UserExportReader.Read(export);

        try
        {
            return _db.ExecuteWriteAsync(
                (connection, transaction, token) => ImportCore(connection, transaction, export, summary, fileName, fileCreatedAt, codec, now, choices, token),
                ct).GetAwaiter().GetResult();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // A row that already exists (a passkey id, for example), or any other rule the data store holds: nothing was written.
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
        UserImportChoices choices,
        CancellationToken ct)
    {
        // Everything is checked inside the write transaction, so a person who signed up a moment ago still wins.
        var plan = Plan(connection, transaction, summary, choices);

        if (plan.NewHouseholdName is not null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO households (id, name, created_at, is_default) VALUES ($id, $name, $at, 0)";
            insert.Parameters.AddWithValue("$id", GuidSql.ToBlob(plan.HouseholdId));
            insert.Parameters.AddWithValue("$name", plan.NewHouseholdName);
            insert.Parameters.AddWithValue("$at", RestoredRowSql.Timestamp(now));
            insert.ExecuteNonQuery();
        }

        // Ids that already exist here get new ones (a household exported from this very Library, for example): nothing is overwritten.
        var renamed = RenamedIds(connection, transaction, export);
        var prepared = Prepare(export, summary, plan, choices, renamed);
        var result = BackupRestoreRepository.ImportCore(connection, transaction, prepared, fileName, fileCreatedAt, codec, now, Guid.NewGuid(), ct, plainInsert: true);

        // Nothing is removed quietly: a row left pointing at something that does not exist means the file was not whole.
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

        var imported = summary.Profiles
            .Select(profile =>
            {
                var signIn = profile.SignInId is { } id && plan.KeptEmails.TryGetValue(id, out var email) ? (Id: (Guid?)Renamed(renamed, id), Email: email) : (Id: null, Email: null);
                return new UserImportedProfile(
                    Renamed(renamed, profile.Id),
                    profile.Name,
                    signIn.Id,
                    signIn.Email,
                    plan.AdministratorProfileId == profile.Id);
            })
            .ToList();
        return new UserImportResult(plan.HouseholdId, plan.HouseholdName, plan.NewHouseholdName is not null, imported, result.WaitingRows, result.AppliedRows);
    }

    /// <summary>What an import will do, once every choice and every clash has been checked.</summary>
    private sealed record ImportPlan(
        Guid HouseholdId,
        string HouseholdName,
        string? NewHouseholdName,
        Guid? AdministratorProfileId,
        IReadOnlyDictionary<Guid, string> KeptEmails);

    private static ImportPlan Plan(SqliteConnection connection, SqliteTransaction transaction, UserExportSummary summary, UserImportChoices choices)
    {
        var existing = choices.TargetHouseholdId;
        var newName = string.IsNullOrWhiteSpace(choices.NewHouseholdName) ? null : choices.NewHouseholdName.Trim();
        if ((existing is null) == (newName is null))
        {
            throw new UserImportRefusedException(UserImportRefusal.HouseholdChoiceNeeded);
        }

        var fileSignIns = summary.SignIns.Select(signIn => signIn.Id).ToHashSet();
        var without = (choices.WithoutSignIn ?? []).ToHashSet();
        var overrides = choices.Emails ?? new Dictionary<Guid, string>();
        if (without.Any(id => !fileSignIns.Contains(id)) || overrides.Keys.Any(id => !fileSignIns.Contains(id)))
        {
            throw new UserImportRefusedException(UserImportRefusal.InvalidChoice);
        }

        Guid householdId;
        string householdName;
        Guid? administrator = null;
        if (existing is { } target)
        {
            if (choices.NewHouseholdAdministratorProfileId is not null)
            {
                throw new UserImportRefusedException(UserImportRefusal.InvalidChoice);
            }

            using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT name FROM households WHERE id = $id";
            lookup.Parameters.AddWithValue("$id", GuidSql.ToBlob(target));
            householdName = lookup.ExecuteScalar() as string ?? throw new UserImportRefusedException(UserImportRefusal.HouseholdNotFound);
            householdId = target;
            if (CountProfiles(connection, transaction, target) + summary.Profiles.Count > Household.MaximumProfiles)
            {
                throw new UserImportRefusedException(UserImportRefusal.HouseholdFull);
            }
        }
        else
        {
            householdId = Guid.NewGuid();
            householdName = newName!;

            // A second household is added only once every server administrator has an email and a password (D10).
            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM households";
                if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0 && !AdministratorsSecured(connection, transaction))
                {
                    throw new UserImportRefusedException(UserImportRefusal.SecureAdministratorsFirst);
                }
            }

            // The household needs someone who looks after it: an imported profile that keeps its sign-in and is not a Kids profile (D15).
            var chosen = summary.Profiles.FirstOrDefault(profile => profile.Id == choices.NewHouseholdAdministratorProfileId);
            if (chosen is null
                || chosen.SignInId is not { } chosenSignIn
                || without.Contains(chosenSignIn)
                || string.Equals(chosen.Role, "RestrictedProfile", StringComparison.OrdinalIgnoreCase))
            {
                throw new UserImportRefusedException(UserImportRefusal.AdministratorChoiceNeeded);
            }

            administrator = chosen.Id;
        }

        var kept = new Dictionary<Guid, string>();
        var usedNow = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signIn in summary.SignIns.Where(signIn => !without.Contains(signIn.Id)))
        {
            var email = (overrides.TryGetValue(signIn.Id, out var chosenEmail) ? chosenEmail : signIn.Email)?.Trim();
            if (string.IsNullOrEmpty(email))
            {
                throw new UserImportRefusedException(UserImportRefusal.EmailMissing);
            }

            var normalized = NormalizedEmail(email);
            if (!usedNow.Add(normalized) || Exists(connection, transaction, "accounts", "normalized_email", normalized))
            {
                throw new UserImportRefusedException(UserImportRefusal.EmailInUse, email);
            }

            if (SignInProblem(connection, transaction, signIn) is var problem and not UserImportRefusal.None)
            {
                throw new UserImportRefusedException(problem, email);
            }

            kept[signIn.Id] = email;
        }

        return new ImportPlan(householdId, householdName, newName, administrator, kept);
    }

    /// <summary>Why this sign-in cannot move at all (a different email does not help), or <see cref="UserImportRefusal.None"/>.</summary>
    private static UserImportRefusal SignInProblem(SqliteConnection connection, SqliteTransaction? transaction, UserExportSignIn signIn)
    {
        if (!signIn.TwoStepSecretIsPortable)
        {
            return UserImportRefusal.TwoStepCannotMove;
        }

        if (signIn.PasskeyIds.Any(id => Exists(connection, transaction, "account_passkeys", "credential_id", id)))
        {
            return UserImportRefusal.AlreadyHere;
        }

        foreach (var login in signIn.ExternalLogins)
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

    /// <summary>True when every server administrator profile has a user with an email and a password or passkey (users spec D10).</summary>
    private static bool AdministratorsSecured(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(*) FROM profiles p
            WHERE p.is_server_admin = 1
              AND NOT EXISTS (SELECT 1 FROM accounts a WHERE a.profile_id = p.id AND {AccountRepository.SecuredUserSql})
            """;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
    }

    private static int CountProfiles(SqliteConnection connection, SqliteTransaction transaction, Guid householdId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM profiles WHERE household_id = $id";
        command.Parameters.AddWithValue("$id", GuidSql.ToBlob(householdId));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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

    // ---------------------------------------------------------------------------------------------------------
    // New ids
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>For every row whose own id (an <c>id</c> column holding a 16-byte BLOB) already exists in the same table here: a new id.</summary>
    private static Dictionary<Guid, Guid> RenamedIds(SqliteConnection connection, SqliteTransaction transaction, BackupExport export)
    {
        var renamed = new Dictionary<Guid, Guid>();
        foreach (var table in export.Tables)
        {
            // The file's household row is never imported (the chosen household is used), so its id is never renamed.
            var index = string.Equals(table.Table, "households", StringComparison.OrdinalIgnoreCase) ? -1 : IndexOfColumn(table.Columns, "id");
            if (index < 0)
            {
                continue;
            }

            foreach (var row in table.Rows)
            {
                if (row[index] is BackupValue.Blob { Value.Length: 16 } blob
                    && GuidSql.FromDb(blob.Value) is var id
                    && !renamed.ContainsKey(id)
                    && Exists(connection, transaction, table.Table, "id", blob.Value))
                {
                    renamed[id] = Guid.NewGuid();
                }
            }
        }

        return renamed;
    }

    private static Guid Renamed(IReadOnlyDictionary<Guid, Guid> renamed, Guid id) => renamed.TryGetValue(id, out var replacement) ? replacement : id;

    /// <summary>
    /// Replaces every renamed id wherever it is written (as a BLOB, or as the TEXT some tables keep). A GUID names one thing, so a value
    /// equal to a renamed id is a reference to it.
    /// </summary>
    private static BackupExportTable Rename(BackupExportTable table, IReadOnlyDictionary<Guid, Guid> renamed)
    {
        if (renamed.Count == 0 || table.Rows.Count == 0)
        {
            return table;
        }

        BackupValue Change(BackupValue value)
        {
            switch (value)
            {
                case BackupValue.Blob { Value.Length: 16 } blob when renamed.TryGetValue(GuidSql.FromDb(blob.Value), out var replacement):
                    return BackupValue.FromBlob(GuidSql.ToBlob(replacement));
                case BackupValue.Text text when Guid.TryParse(text.Value, out var guid) && renamed.TryGetValue(guid, out var replacement):
                    var written = replacement.ToString("D", CultureInfo.InvariantCulture);
                    return BackupValue.FromText(text.Value.Any(char.IsUpper) ? written.ToUpperInvariant() : written);
                default:
                    return value;
            }
        }

        return table with { Rows = table.Rows.Select(row => (IReadOnlyList<BackupValue>)row.Select(Change).ToList()).ToList() };
    }

    // ---------------------------------------------------------------------------------------------------------
    // Preparing the rows
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The export with the access rights taken out (no administrator role of any kind, except the one chosen for a new household; no
    /// library access; Read/Watch/Listen/View only when kept; no Vault access), the profiles put in the household chosen, the sign-ins that
    /// are left out removed, and the emails the administrator chose written. Every excluded column (a file path, someone else's id) is
    /// emptied whatever the file says, so no file can make the Engine open, serve or delete a path it chose; collections arrive private
    /// and not featured, and keep a parent only when the parent is in the same file.
    /// </summary>
    private static BackupExport Prepare(
        BackupExport export,
        UserExportSummary summary,
        ImportPlan plan,
        UserImportChoices choices,
        IReadOnlyDictionary<Guid, Guid> renamed)
    {
        var household = BackupValue.FromBlob(GuidSql.ToBlob(plan.HouseholdId));
        var collectionIds = summary.CollectionIds.ToHashSet();
        var tables = new List<BackupExportTable>();
        foreach (var original in export.Tables)
        {
            var table = ClearExcluded(original);
            switch (table.Table.ToLowerInvariant())
            {
                case "accounts":
                    tables.Add(Edit(table, ["email", "normalized_email"], (row, column) =>
                    {
                        var id = UserExportReader.GuidOf(row[column("id")]);
                        if (id is not { } signIn || !plan.KeptEmails.TryGetValue(signIn, out var email))
                        {
                            return false;
                        }

                        row[column("email")] = BackupValue.FromText(email);
                        row[column("normalized_email")] = BackupValue.FromText(NormalizedEmail(email));
                        return true;
                    }));
                    break;
                case "account_credentials":
                case "account_two_step":
                case "account_passkeys":
                case "account_external_logins":
                case "account_notice_dismissals":
                    // A sign-in left out takes everything about how it signs in with it.
                    tables.Add(Edit(table, [], (row, column) =>
                        UserExportReader.GuidOf(row[column("account_id")]) is { } signIn && plan.KeptEmails.ContainsKey(signIn)));
                    break;
                case "households":
                    // The household is the one chosen (an existing one is joined as it is; a new one was made above), never the file's.
                    tables.Add(table with { Rows = [] });
                    break;
                case "profiles":
                    tables.Add(Edit(
                        table,
                        ["household_id", "is_server_admin", "is_household_admin", "role"],
                        (row, column) =>
                        {
                            var id = UserExportReader.GuidOf(row[column("id")]);
                            row[column("household_id")] = household;
                            // A user file never makes anyone an administrator, whatever the file says; only the person chosen to look
                            // after a household the import creates is one.
                            row[column("is_server_admin")] = BackupValue.FromInteger(0);
                            row[column("is_household_admin")] = BackupValue.FromInteger(id is not null && id == plan.AdministratorProfileId ? 1 : 0);
                            row[column("role")] = row[column("role")] is BackupValue.Text { Value: "RestrictedProfile" }
                                ? BackupValue.FromText("RestrictedProfile")
                                : BackupValue.FromText("StandardUser");
                            return true;
                        }));
                    break;
                case "profile_feature_grants":
                    // Read/Watch/Listen/View arrive only when the administrator keeps them.
                    tables.Add(choices.KeepAccess ? table : table with { Rows = [] });
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

        return export with { Tables = tables.Select(table => Rename(table, renamed)).ToList() };
    }

    private static int IndexOfColumn(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The table with each row changed by <paramref name="edit"/>, which returns false to drop the row. The named columns are added when the
    /// file lacks them (filled with NULL before the edit).
    /// </summary>
    private static BackupExportTable Edit(
        BackupExportTable table,
        string[] ensureColumns,
        Func<List<BackupValue>, Func<string, int>, bool> edit)
    {
        var columns = table.Columns.ToList();
        foreach (var name in ensureColumns.Where(name => IndexOfColumn(columns, name) < 0))
        {
            columns.Add(name);
        }

        int Column(string name) => IndexOfColumn(columns, name) is var index and >= 0
            ? index
            : throw new InvalidDataException("This file isn't a user export.");
        var rows = new List<IReadOnlyList<BackupValue>>(table.Rows.Count);
        foreach (var original in table.Rows)
        {
            var values = original.ToList();
            while (values.Count < columns.Count)
            {
                values.Add(BackupValue.Null);
            }

            if (edit(values, Column))
            {
                rows.Add(values);
            }
        }

        return table with { Columns = columns, Rows = rows };
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
