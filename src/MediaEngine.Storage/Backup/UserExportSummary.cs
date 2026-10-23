using System.Globalization;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Storage.Backup;

/// <summary>An outside sign-in link a user export carries.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Issuer">The issuer.</param>
/// <param name="Subject">The person's id at the provider.</param>
public sealed record UserExportExternalLogin(string Provider, string Issuer, string Subject);

/// <summary>A profile as the file states it, so an administrator can see the limits it carries before importing.</summary>
/// <param name="Id">The profile's id in the file.</param>
/// <param name="Name">The profile's name.</param>
/// <param name="Role">The role stored in the file (Administrator, StandardUser or RestrictedProfile). An import never carries an administrator role.</param>
/// <param name="ContentLimit">The highest rating the profile may see (G, PG, PG-13 or R); null when there is none.</param>
/// <param name="AllowsUnrated">Whether unrated items stay visible although a content limit is set.</param>
/// <param name="SignInId">The id of the sign-in (user) that opens this profile in the file; null when none does.</param>
/// <param name="Access">The features the profile could use in the file (read, watch, listen, view); an import applies them only when asked.</param>
public sealed record UserExportProfile(
    Guid Id,
    string Name,
    string Role,
    string? ContentLimit,
    bool AllowsUnrated,
    Guid? SignInId,
    IReadOnlyList<string> Access);

/// <summary>A sign-in (user) a file carries.</summary>
/// <param name="Id">The user's id in the file.</param>
/// <param name="Email">The email the person signs in with.</param>
/// <param name="ProfileId">The profile it opens.</param>
/// <param name="PasskeyIds">The passkeys' credential ids.</param>
/// <param name="ExternalLogins">The outside sign-in links.</param>
/// <param name="HasTwoStep">True when the file holds a two-step row for it.</param>
/// <param name="TwoStepSecretIsPortable">True when there is no two-step secret, or it was made readable for any computer.</param>
/// <param name="HasPassword">True when the file holds a password for it.</param>
public sealed record UserExportSignIn(
    Guid Id,
    string Email,
    Guid ProfileId,
    IReadOnlyList<byte[]> PasskeyIds,
    IReadOnlyList<UserExportExternalLogin> ExternalLogins,
    bool HasTwoStep,
    bool TwoStepSecretIsPortable,
    bool HasPassword)
{
    /// <summary>True when the person could sign in without the computer's trust: a password or a passkey travels with it.</summary>
    public bool CanSignInAnywhere => HasPassword || PasskeyIds.Count > 0;
}

/// <summary>
/// What a user file says about itself, read from its data (never from its manifest, which a person could have edited). Built only by
/// <see cref="UserExportReader.Read"/>, which refuses a file that holds anything but one profile with its sign-in, or one household's
/// profiles with their sign-ins, and nothing of anyone else.
/// </summary>
/// <param name="Scope"><c>profile</c> (one profile and its sign-in) or <c>household</c> (every profile of a household).</param>
/// <param name="HouseholdId">The household the profiles belonged to, when they belonged to one.</param>
/// <param name="HouseholdName">The household's name, when the file holds the household.</param>
/// <param name="Profiles">The profiles, in the order the file lists them.</param>
/// <param name="SignIns">The sign-ins that open them (a profile may have none).</param>
/// <param name="CollectionIds">The collections the profiles built.</param>
/// <param name="PersonalItems">Rows of the people's own state and what they built (progress, bookmarks, favourites, collection items).</param>
public sealed record UserExportSummary(
    string Scope,
    Guid? HouseholdId,
    string? HouseholdName,
    IReadOnlyList<UserExportProfile> Profiles,
    IReadOnlyList<UserExportSignIn> SignIns,
    IReadOnlyList<Guid> CollectionIds,
    int PersonalItems)
{
    /// <summary>The scope word of a file that holds one profile.</summary>
    public const string ProfileScope = "profile";

    /// <summary>The scope word of a file that holds a household.</summary>
    public const string HouseholdScope = "household";

    /// <summary>The profiles' ids, in the file's order.</summary>
    public IReadOnlyList<Guid> ProfileIds => Profiles.Select(profile => profile.Id).ToList();

    /// <summary>The profiles' names, in the file's order.</summary>
    public IReadOnlyList<string> ProfileNames => Profiles.Select(profile => profile.Name).ToList();

    /// <summary>True when the file holds a two-step row for any sign-in.</summary>
    public bool HasTwoStep => SignIns.Any(signIn => signIn.HasTwoStep);
}

/// <summary>
/// Checks that a <see cref="BackupExport"/> read from a file is a user file (one profile with its sign-in, or one household) and
/// summarises it. A crafted file cannot smuggle in another sign-in, another profile, or rows that belong to nobody in the file: every row
/// of every table must prove, by the checks in <see cref="UserExportScope"/>, that it belongs to one of the file's sign-ins or profiles.
/// </summary>
public static class UserExportReader
{
    /// <summary>
    /// The summary of a user file. Throws <see cref="InvalidDataException"/> when the export is not one: wrong scope, a table a user
    /// file never holds, a sign-in with no email or no profile, more profiles than a household may hold, or a row that belongs to
    /// someone else.
    /// </summary>
    public static UserExportSummary Read(BackupExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        var scope = export.Contents.Scope;
        if (!scope.IsUserFile)
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

        // The profiles: at most a household's worth, each once.
        var profilesTable = tables.GetValueOrDefault("profiles") ?? throw NotAUserExport();
        if (profilesTable.Rows.Count == 0 || profilesTable.Rows.Count > Household.MaximumProfiles)
        {
            throw NotAUserExport();
        }

        var profileRows = new List<(Guid Id, string Name, string Role, string? Limit, bool AllowsUnrated, Guid? Household)>();
        var profileIds = new HashSet<Guid>();
        foreach (var row in profilesTable.Rows)
        {
            var id = GuidOf(Value(profilesTable, row, "id")) ?? throw NotAUserExport();
            var name = Value(profilesTable, row, "display_name") is BackupValue.Text { Value: var displayName } ? displayName : throw NotAUserExport();
            if (id == Guid.Empty || !profileIds.Add(id))
            {
                throw NotAUserExport();
            }

            Guid? household = Value(profilesTable, row, "household_id") is { } value and not BackupValue.NullValue
                ? GuidOf(value) ?? throw NotAUserExport()
                : null;
            profileRows.Add((
                id,
                name,
                Value(profilesTable, row, "role") is BackupValue.Text { Value: var role } ? role : "RestrictedProfile",
                Value(profilesTable, row, "content_limit") is BackupValue.Text { Value: { Length: > 0 } limit } ? limit : null,
                Value(profilesTable, row, "content_limit_allow_unrated") is BackupValue.Integer { Value: 1 },
                household));
        }

        // The sign-ins: each opens one of the profiles, and no two open the same one.
        var accountsTable = tables.GetValueOrDefault("accounts");
        var accountRows = new List<(Guid Id, string Email, Guid Profile)>();
        var accountIds = new HashSet<Guid>();
        var openedProfiles = new HashSet<Guid>();
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in accountsTable?.Rows ?? [])
        {
            var id = GuidOf(Value(accountsTable!, row, "id")) ?? throw NotAUserExport();
            var email = Value(accountsTable!, row, "email") is BackupValue.Text { Value: var rawEmail } && !string.IsNullOrWhiteSpace(rawEmail)
                ? rawEmail.Trim()
                : throw NotAUserExport();
            var opened = GuidOf(Value(accountsTable!, row, "profile_id")) ?? throw NotAUserExport();
            if (id == Guid.Empty || !accountIds.Add(id) || !profileIds.Contains(opened) || !openedProfiles.Add(opened) || !emails.Add(email))
            {
                throw NotAUserExport();
            }

            accountRows.Add((id, email, opened));
        }

        // The scope the file claims must match what it holds.
        Guid? householdId;
        string scopeWord;
        if (scope.AccountId is { } scopeAccount)
        {
            if (accountRows.Count != 1 || accountRows[0].Id != scopeAccount || profileRows.Count != 1)
            {
                throw NotAUserExport();
            }

            scopeWord = UserExportSummary.ProfileScope;
            householdId = profileRows[0].Household;
        }
        else
        {
            householdId = scope.HouseholdId;
            if (householdId is null || profileRows.Any(profile => profile.Household != householdId))
            {
                throw NotAUserExport();
            }

            scopeWord = UserExportSummary.HouseholdScope;
        }

        CheckRows(tables, accountIds, profileIds);

        string? householdName = null;
        if (tables.TryGetValue("households", out var householdsTable) && householdsTable.Rows.Count > 0)
        {
            if (householdId is null || householdsTable.Rows.Count != 1 || GuidOf(Value(householdsTable, householdsTable.Rows[0], "id")) != householdId)
            {
                throw NotAUserExport();
            }

            householdName = Value(householdsTable, householdsTable.Rows[0], "name") is BackupValue.Text { Value: var nameText } ? nameText : throw NotAUserExport();
        }

        var access = new Dictionary<Guid, List<string>>();
        if (tables.TryGetValue("profile_feature_grants", out var grantTable))
        {
            foreach (var row in grantTable.Rows)
            {
                var profile = GuidOf(Value(grantTable, row, "profile_id")) ?? throw NotAUserExport();
                var feature = TextOf(grantTable, row, "feature_id");
                if (!access.TryGetValue(profile, out var features))
                {
                    access[profile] = features = [];
                }

                if (!features.Contains(feature, StringComparer.Ordinal))
                {
                    features.Add(feature);
                }
            }
        }

        var signIns = new List<UserExportSignIn>();
        foreach (var (id, email, opened) in accountRows)
        {
            var hasTwoStep = false;
            var twoStepPortable = true;
            if (tables.TryGetValue(UserExportScope.TwoStepTable, out var twoStep))
            {
                var mine = twoStep.Rows.Where(row => GuidOf(Value(twoStep, row, "account_id")) == id).ToList();
                hasTwoStep = mine.Count > 0;
                twoStepPortable = mine.All(row => Value(twoStep, row, "secret_protected") is BackupValue.Portable);
            }

            var hasPassword = tables.TryGetValue("account_credentials", out var credentialTable)
                && credentialTable.Rows.Any(row => GuidOf(Value(credentialTable, row, "account_id")) == id);

            var passkeys = new List<byte[]>();
            if (tables.TryGetValue("account_passkeys", out var passkeyTable))
            {
                foreach (var row in passkeyTable.Rows.Where(row => GuidOf(Value(passkeyTable, row, "account_id")) == id))
                {
                    passkeys.Add(Value(passkeyTable, row, "credential_id") is BackupValue.Blob blob ? blob.Value : throw NotAUserExport());
                }
            }

            var logins = new List<UserExportExternalLogin>();
            if (tables.TryGetValue("account_external_logins", out var loginTable))
            {
                foreach (var row in loginTable.Rows.Where(row => GuidOf(Value(loginTable, row, "account_id")) == id))
                {
                    logins.Add(new UserExportExternalLogin(
                        TextOf(loginTable, row, "provider"),
                        TextOf(loginTable, row, "issuer"),
                        TextOf(loginTable, row, "subject")));
                }
            }

            signIns.Add(new UserExportSignIn(id, email, opened, passkeys, logins, hasTwoStep, twoStepPortable, hasPassword));
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

        var profiles = profileRows
            .Select(profile => new UserExportProfile(
                profile.Id,
                profile.Name,
                profile.Role,
                profile.Limit,
                profile.AllowsUnrated,
                accountRows.Where(account => account.Profile == profile.Id).Select(account => (Guid?)account.Id).FirstOrDefault(),
                access.TryGetValue(profile.Id, out var granted) ? granted : []))
            .ToList();
        return new UserExportSummary(scopeWord, householdId, householdName, profiles, signIns, collections, personalItems);
    }

    /// <summary>Every row of every table proves it belongs to one of the file's sign-ins or profiles (see <see cref="UserRowCheck"/>).</summary>
    private static void CheckRows(Dictionary<string, BackupExportTable> tables, HashSet<Guid> accounts, HashSet<Guid> profiles)
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
                        UserRowCheck.AccountColumn => GuidOf(value) is { } account && accounts.Contains(account),
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
