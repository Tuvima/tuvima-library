using System.Globalization;

namespace MediaEngine.Storage.Backup;

/// <summary>An outside sign-in link a user export carries.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Issuer">The issuer.</param>
/// <param name="Subject">The person's id at the provider.</param>
public sealed record UserExportExternalLogin(string Provider, string Issuer, string Subject);

/// <summary>A profile as the file states it, so an administrator can see the limits it carries before importing.</summary>
/// <param name="Name">The profile's name.</param>
/// <param name="Role">The role stored in the file (Administrator, StandardUser or RestrictedProfile).</param>
/// <param name="ContentLimit">The highest rating the profile may see (G, PG, PG-13 or R); null when there is none.</param>
/// <param name="AllowsUnrated">Whether unrated items stay visible although a content limit is set.</param>
public sealed record UserExportProfile(string Name, string Role, string? ContentLimit, bool AllowsUnrated);

/// <summary>
/// What a user export says about itself, read from its data (never from its manifest, which a person could have edited). Built only by
/// <see cref="UserExportReader.Read"/>, which refuses a file that does not hold exactly one user and nothing of anyone else.
/// </summary>
/// <param name="AccountId">The account.</param>
/// <param name="Email">The email the person signs in with.</param>
/// <param name="HouseholdId">The household the account belongs to, when it belongs to one.</param>
/// <param name="HouseholdName">The household's name, when the file holds the household.</param>
/// <param name="HouseholdAdmin">Whether the person looked after their household.</param>
/// <param name="ProfileIds">Every profile the account may use.</param>
/// <param name="ProfileNames">Those profiles' names, in the same order.</param>
/// <param name="Profiles">The same profiles with the role and content limit the file states.</param>
/// <param name="CollectionIds">The collections the profiles built.</param>
/// <param name="PasskeyIds">The passkeys' credential ids.</param>
/// <param name="ExternalLogins">The outside sign-in links.</param>
/// <param name="TwoStepSecretIsPortable">True when there is no two-step secret, or it was made readable for any computer.</param>
/// <param name="PersonalItems">Rows of the person's own state and what they built (progress, bookmarks, favourites, collection items).</param>
public sealed record UserExportSummary(
    Guid AccountId,
    string Email,
    Guid? HouseholdId,
    string? HouseholdName,
    bool HouseholdAdmin,
    IReadOnlyList<Guid> ProfileIds,
    IReadOnlyList<string> ProfileNames,
    IReadOnlyList<UserExportProfile> Profiles,
    IReadOnlyList<Guid> CollectionIds,
    IReadOnlyList<byte[]> PasskeyIds,
    IReadOnlyList<UserExportExternalLogin> ExternalLogins,
    bool TwoStepSecretIsPortable,
    int PersonalItems);

/// <summary>
/// Checks that a <see cref="BackupExport"/> read from a file is exactly one user's export (backup and restore spec D19) and summarises it.
/// A crafted file cannot smuggle in another account, another profile, or rows that belong to nobody in the file: every row of every
/// table must prove, by the checks in <see cref="UserExportScope"/>, that it belongs to the one account or one of its profiles.
/// </summary>
public static class UserExportReader
{
    /// <summary>
    /// The summary of a user export. Throws <see cref="InvalidDataException"/> when the export is not one: wrong scope, a table a user
    /// export never holds, no or several accounts, an account with no email, profiles that do not match the account's grants, or a row
    /// that belongs to someone else.
    /// </summary>
    public static UserExportSummary Read(BackupExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        if (export.Contents.Scope.AccountId is not { } scopeAccount)
        {
            throw NotAUserExport();
        }

        if (export.Choices.Count > 0 || export.Corrections.Count > 0)
        {
            throw NotAUserExport();
        }

        var tables = new Dictionary<string, BackupExportTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in export.Tables)
        {
            var rule = BackupTableClassification.Find(table.Table);
            if (UserExportScope.Find(table.Table) is null
                || rule is null
                || rule.Kind != table.Kind
                || !tables.TryAdd(table.Table, table))
            {
                throw NotAUserExport();
            }
        }

        var account = RowsOf(tables, "accounts") is [var onlyRow] ? onlyRow : throw NotAUserExport();
        var accountsTable = tables["accounts"];
        var accountId = GuidOf(Value(accountsTable, account, "id")) ?? throw NotAUserExport();
        if (accountId != scopeAccount || accountId == Guid.Empty)
        {
            throw NotAUserExport();
        }

        var email = Value(accountsTable, account, "email") is BackupValue.Text { Value: var rawEmail } && !string.IsNullOrWhiteSpace(rawEmail)
            ? rawEmail.Trim()
            : throw NotAUserExport();

        // The profiles are the account's grants, no more and no fewer.
        var grantsTable = tables.GetValueOrDefault("account_profile_grants") ?? throw NotAUserExport();
        var grantedProfiles = new List<Guid>();
        var defaults = 0;
        foreach (var row in grantsTable.Rows)
        {
            var profile = GuidOf(Value(grantsTable, row, "profile_id")) ?? throw NotAUserExport();
            if (grantedProfiles.Contains(profile))
            {
                throw NotAUserExport();
            }

            grantedProfiles.Add(profile);
            defaults += Value(grantsTable, row, "is_default") is BackupValue.Integer { Value: 1 } ? 1 : 0;
        }

        if (grantedProfiles.Count is 0 or > 8 || defaults != 1)
        {
            throw NotAUserExport();
        }

        var profilesTable = tables.GetValueOrDefault("profiles") ?? throw NotAUserExport();
        var names = new Dictionary<Guid, string>();
        var details = new Dictionary<Guid, UserExportProfile>();
        foreach (var row in profilesTable.Rows)
        {
            var id = GuidOf(Value(profilesTable, row, "id")) ?? throw NotAUserExport();
            var name = Value(profilesTable, row, "display_name") is BackupValue.Text { Value: var displayName } ? displayName : throw NotAUserExport();
            if (!names.TryAdd(id, name))
            {
                throw NotAUserExport();
            }

            details[id] = new UserExportProfile(
                name,
                Value(profilesTable, row, "role") is BackupValue.Text { Value: var role } ? role : "RestrictedProfile",
                Value(profilesTable, row, "content_limit") is BackupValue.Text { Value: { Length: > 0 } limit } ? limit : null,
                Value(profilesTable, row, "content_limit_allow_unrated") is BackupValue.Integer { Value: 1 });
        }

        if (names.Count != grantedProfiles.Count || grantedProfiles.Any(id => !names.ContainsKey(id)))
        {
            throw NotAUserExport();
        }

        CheckRows(tables, accountId, grantedProfiles.ToHashSet());

        // The default profile first, then the others by name, so the preview reads the same however the file was ordered.
        var defaultProfile = grantsTable.Rows
            .Where(row => Value(grantsTable, row, "is_default") is BackupValue.Integer { Value: 1 })
            .Select(row => GuidOf(Value(grantsTable, row, "profile_id"))!.Value)
            .Single();
        var ordered = grantedProfiles
            .OrderBy(id => id == defaultProfile ? 0 : 1)
            .ThenBy(id => names[id], StringComparer.OrdinalIgnoreCase)
            .ToList();

        Guid? householdId = Value(accountsTable, account, "household_id") is { } household && household is not BackupValue.NullValue
            ? GuidOf(household) ?? throw NotAUserExport()
            : null;
        string? householdName = null;
        if (tables.TryGetValue("households", out var householdsTable) && householdsTable.Rows.Count > 0)
        {
            if (householdId is null || householdsTable.Rows.Count != 1)
            {
                throw NotAUserExport();
            }

            householdName = Value(householdsTable, householdsTable.Rows[0], "name") is BackupValue.Text { Value: var nameText } ? nameText : throw NotAUserExport();
        }

        var twoStepPortable = true;
        if (tables.TryGetValue("account_two_step", out var twoStep))
        {
            twoStepPortable = twoStep.Rows.All(row => Value(twoStep, row, "secret_protected") is BackupValue.Portable);
        }

        var passkeys = new List<byte[]>();
        if (tables.TryGetValue("account_passkeys", out var passkeyTable))
        {
            foreach (var row in passkeyTable.Rows)
            {
                passkeys.Add(Value(passkeyTable, row, "credential_id") is BackupValue.Blob blob ? blob.Value : throw NotAUserExport());
            }
        }

        var logins = new List<UserExportExternalLogin>();
        if (tables.TryGetValue("account_external_logins", out var loginTable))
        {
            foreach (var row in loginTable.Rows)
            {
                logins.Add(new UserExportExternalLogin(
                    TextOf(loginTable, row, "provider"),
                    TextOf(loginTable, row, "issuer"),
                    TextOf(loginTable, row, "subject")));
            }
        }

        var collections = new List<Guid>();
        if (tables.TryGetValue("collections", out var collectionTable))
        {
            foreach (var row in collectionTable.Rows)
            {
                collections.Add(GuidOf(Value(collectionTable, row, "id")) ?? throw NotAUserExport());
            }
        }

        var personalItems = tables.Values.Where(table => table.Kind == BackupDataKind.PersonalState).Sum(table => table.Rows.Count)
            + (tables.TryGetValue("collection_items", out var items) ? items.Rows.Count : 0);

        return new UserExportSummary(
            accountId,
            email,
            householdId,
            householdName,
            Value(accountsTable, account, "household_admin") is BackupValue.Integer { Value: 1 },
            ordered,
            ordered.Select(id => names[id]).ToList(),
            ordered.Select(id => details[id]).ToList(),
            collections,
            passkeys,
            logins,
            twoStepPortable,
            personalItems);
    }

    /// <summary>Every row of every table proves it belongs to the one account or one of its profiles (see <see cref="UserRowCheck"/>).</summary>
    private static void CheckRows(Dictionary<string, BackupExportTable> tables, Guid accountId, HashSet<Guid> profiles)
    {
        foreach (var table in tables.Values)
        {
            var entry = UserExportScope.Find(table.Table)!;
            foreach (var check in entry.Checks)
            {
                var index = IndexOf(table, CheckColumn(check));
                if (index < 0)
                {
                    if (table.Rows.Count == 0)
                    {
                        continue;
                    }

                    throw NotAUserExport();
                }

                HashSet<string>? parentKeys = null;
                if (check is UserRowCheck.ParentRow parent)
                {
                    var parentTable = tables.GetValueOrDefault(parent.ParentTable);
                    var parentIndex = parentTable is null ? -1 : IndexOf(parentTable, parent.ParentColumn);
                    parentKeys = parentTable is null || parentIndex < 0
                        ? []
                        : parentTable.Rows
                            .Where(row => row[parentIndex] is not BackupValue.NullValue)
                            .Select(row => KeyOf(row[parentIndex]))
                            .ToHashSet(StringComparer.Ordinal);
                }

                foreach (var row in table.Rows)
                {
                    var value = row[index];
                    var proven = check switch
                    {
                        UserRowCheck.AccountColumn => GuidOf(value) == accountId,
                        UserRowCheck.ProfileColumn { Nullable: true } when value is BackupValue.NullValue => true,
                        UserRowCheck.ProfileColumn => GuidOf(value) is { } profile && profiles.Contains(profile),
                        UserRowCheck.ParentRow => parentKeys!.Contains(KeyOf(value)),
                        UserRowCheck.ColumnIs equals => value is BackupValue.Text { Value: var text } && string.Equals(text, equals.Value, StringComparison.Ordinal),
                        UserRowCheck.ColumnIn within => value is BackupValue.Text { Value: var member } && within.Values.Contains(member, StringComparer.Ordinal),
                        _ => false,
                    };
                    if (!proven)
                    {
                        throw NotAUserExport();
                    }
                }
            }
        }
    }

    private static string CheckColumn(UserRowCheck check) => check switch
    {
        UserRowCheck.AccountColumn account => account.Column,
        UserRowCheck.ProfileColumn profile => profile.Column,
        UserRowCheck.ParentRow parent => parent.Column,
        UserRowCheck.ColumnIs equals => equals.Column,
        UserRowCheck.ColumnIn within => within.Column,
        _ => throw new InvalidOperationException("Unknown user row check."),
    };

    /// <summary>A key that tells ids apart whether the file wrote them as BLOB or as TEXT.</summary>
    private static string KeyOf(BackupValue value) =>
        GuidOf(value) is { } guid ? guid.ToString("N", CultureInfo.InvariantCulture) : RestoredRowSql.ValueKey(value);

    private static IReadOnlyList<IReadOnlyList<BackupValue>> RowsOf(Dictionary<string, BackupExportTable> tables, string name) =>
        tables.TryGetValue(name, out var table) ? table.Rows : [];

    private static int IndexOf(BackupExportTable table, string column)
    {
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i], column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static BackupValue Value(BackupExportTable table, IReadOnlyList<BackupValue> row, string column)
    {
        var index = IndexOf(table, column);
        return index < 0 ? BackupValue.Null : row[index];
    }

    private static string TextOf(BackupExportTable table, IReadOnlyList<BackupValue> row, string column) =>
        Value(table, row, column) is BackupValue.Text { Value: var text } ? text : throw NotAUserExport();

    /// <summary>A GUID from a 16-byte BLOB or a TEXT in GUID form; null for anything else.</summary>
    internal static Guid? GuidOf(BackupValue value) => value switch
    {
        BackupValue.Blob { Value.Length: 16 } blob => GuidSql.FromDb(blob.Value),
        BackupValue.Text text when Guid.TryParse(text.Value, out var guid) => guid,
        _ => null,
    };

    private static InvalidDataException NotAUserExport() => new("This file isn't a user export.");
}
