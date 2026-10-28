using System.Globalization;

namespace MediaEngine.Storage.Backup;

/// <summary>A check an imported row must pass: it belongs to one of the sign-ins in the file, or to one of the file's profiles.</summary>
public abstract record UserRowCheck
{
    private UserRowCheck()
    {
    }

    /// <summary>The column holds the id of one of the sign-ins (users) the file holds.</summary>
    public sealed record AccountColumn(string Column) : UserRowCheck;

    /// <summary>The column holds the id of one of the file's profiles (BLOB, or the TEXT form some tables keep).</summary>
    public sealed record ProfileColumn(string Column, bool Nullable = false) : UserRowCheck;

    /// <summary>The column holds the key of a row of another table that the file also holds.</summary>
    public sealed record ParentRow(string Column, string ParentTable, string ParentColumn) : UserRowCheck;

    /// <summary>The column holds exactly this text.</summary>
    public sealed record ColumnIs(string Column, string Value) : UserRowCheck;

    /// <summary>The column holds one of these texts.</summary>
    public sealed record ColumnIn(string Column, IReadOnlyList<string> Values) : UserRowCheck;
}

/// <summary>How a user export treats one table: which rows it holds, which columns it leaves out, and what every row must prove.</summary>
/// <param name="Table">The table name.</param>
/// <param name="Filter">
/// SQL predicate over the table's own columns. <c>{accounts}</c> stands for a comma-separated list of the file's user ids as BLOB
/// literals, <c>{profiles}</c> for the file's profile ids as BLOB literals and <c>{profileTexts}</c> for the same ids as lower-case text
/// literals. The literals are built from <see cref="Guid"/> values only.
/// </param>
/// <param name="ExcludedColumns">Nullable columns written as NULL because they point at someone else (another account or profile).</param>
/// <param name="Checks">What every row of the table must satisfy when a file is read back, so a crafted file cannot write another person's rows.</param>
/// <param name="ZeroedColumns">NOT NULL columns whose real value is never read: every row is written with 0 (an administrator role, for example).</param>
public sealed record UserExportTable(
    string Table,
    string Filter,
    IReadOnlyList<string> ExcludedColumns,
    IReadOnlyList<UserRowCheck> Checks,
    IReadOnlyList<string>? ZeroedColumns = null);

/// <summary>
/// What a user file holds, as one explicit map (backup and restore spec D19, users spec D9 and D16). A user file holds one profile or a whole
/// household: each profile, the sign-in that opens it (when there is one), the profile's own state, the playlists and collections it
/// built, and the Read/Watch/Listen/View access it had (an import applies that only when the administrator chooses to keep it). It never
/// holds roles, library access, administrator settings, devices, server settings or the catalogue.
/// A guardrail test fails when a Configuration or PersonalState table is neither in <see cref="Tables"/> nor in
/// <see cref="NotInAUserExport"/>, so a new table forces a decision.
/// </summary>
public static class UserExportScope
{
    /// <summary>The manifest and contents kind of a user export.</summary>
    public const string Kind = "user";

    /// <summary>The table holding an account's two-step secret, which an administrator's export of someone else leaves out.</summary>
    public const string TwoStepTable = "account_two_step";

    private const string InProfiles = "{profiles}";

    private const string BuiltByTheseProfiles =
        "SELECT id FROM collections WHERE profile_id IN ({profiles}) AND owner_kind = 'Profile' AND " + BackupTableClassification.PersonBuiltCollections;

    private static readonly IReadOnlyList<UserExportTable> AllTables = BuildTables();

    /// <summary>Every table a user export holds, in the order they are listed.</summary>
    public static IReadOnlyList<UserExportTable> Tables => AllTables;

    /// <summary>The table's entry, or null when a user export never holds it.</summary>
    public static UserExportTable? Find(string table) =>
        AllTables.FirstOrDefault(entry => string.Equals(entry.Table, table, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Configuration tables a user export leaves out, each with a one-line reason. PersonalState tables are all in
    /// <see cref="Tables"/>, because a person's own state is the point of the file.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NotInAUserExport { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["profile_library_hidden"] = "Library ids differ between Libraries, so hidden libraries are never carried by a user file (users spec D9, simpler access D3).",
        ["library_profile_grants"] = "Library ids differ between Libraries, so the profiles chosen for a Selected-profiles library are never carried by a user file (library pages D9).",
        ["profile_admin_protections"] = "Administrator settings are never carried by a user file (users spec D9).",
        ["client_devices"] = "Paired devices sign in again on the new Library.",
        ["applications"] = "Server settings: outside applications belong to the Library.",
        ["application_client_bindings"] = "Server settings: outside applications belong to the Library.",
        ["application_credentials"] = "Server settings: outside application credentials belong to the Library.",
        ["application_permission_grants"] = "Server settings: outside application permissions belong to the Library.",
        ["application_webhooks"] = "Server settings: webhooks belong to the Library and hold a signing secret.",
        ["approval_records"] = "Approvals an administrator gave on this Library; they say nothing about the new one.",
        ["plugin_lore_sources"] = "Server settings: lore sources belong to the Library.",
        ["plugin_library_limits"] = "Server settings: which libraries a plugin works on belong to the Library.",
        ["metadata_providers"] = "Server settings: providers belong to the Library.",
        ["provider_config"] = "Server settings: provider settings belong to the Library.",
        ["notification_settings"] = "Server settings: notification channels belong to the Library.",
        ["backup_settings"] = "Server settings: backup settings belong to the Library.",
        ["doc_types"] = "Library-wide document settings, not one person's.",
        ["doc_field_definitions"] = "Library-wide document settings, not one person's.",
        ["doc_field_choices"] = "Library-wide document settings, not one person's.",
        ["doc_correspondents"] = "Library-wide document settings, not one person's.",
        ["doc_rules"] = "Library-wide document filing rules, not one person's.",
        ["doc_rule_conditions"] = "Library-wide document filing rules, not one person's.",
        ["doc_rule_actions"] = "Library-wide document filing rules, not one person's.",
        ["tags"] = "View tags belong to View items and the folders they were found in, which a user export does not carry.",
        ["view_personal_spaces"] = "A View space is tied to a library and folders configured on this Library, so it cannot be moved with the person.",
        ["view_sources"] = "Folders and devices a View space reads from belong to this Library.",
        ["view_source_policies"] = "Folders and devices a View space reads from belong to this Library.",
        ["view_devices"] = "Paired devices are not carried.",
        ["view_storage_labels"] = "Names of View storage locations belong to this Library.",
        ["view_shared_library"] = "The household's shared View library belongs to the household and this Library.",
        ["view_galleries"] = "View galleries hang off a View space and View items, which a user export does not carry.",
        ["view_gallery_items"] = "View galleries hang off a View space and View items, which a user export does not carry.",
        ["view_gallery_shares"] = "View galleries hang off a View space and View items, which a user export does not carry.",
        ["collection_view_sources"] = "The gallery or rule feeding a collection depends on View galleries, which a user export does not carry.",
        ["doc_field_values"] = "Values on View documents, which a user export does not carry.",
        ["local_item_tag_links"] = "Tags on View items, which a user export does not carry.",
        ["doc_item_details"] = "Details on View documents, which a user export does not carry.",
        ["excluded_source_files"] = "Files kept out of this Library's own folders.",
        ["fictional_entity_user_overrides"] = "Typed catalogue corrections belong to the Library, not to one person.",
        ["narrative_root_user_overrides"] = "Typed catalogue corrections belong to the Library, not to one person.",
        ["folder_first_checks"] = "The first check of a folder belongs to the Library's own folders, not to one person.",
        ["backup_restores"] = "The Library's own restore history.",
        ["waiting_personal_state"] = "Items still waiting on the old Library are not moved; a file could otherwise name any table to write into.",
        ["vault_libraries"] = "Server settings: which libraries are Vault libraries belongs to the Library.",
    };

    /// <summary>
    /// The predicate that picks this table's rows for the file's users and profiles, with the tokens filled in. When there are none,
    /// the token becomes <c>NULL</c>, which matches no row.
    /// </summary>
    public static string FilterFor(UserExportTable table, IReadOnlyCollection<Guid> accountIds, IReadOnlyCollection<Guid> profileIds)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(accountIds);
        ArgumentNullException.ThrowIfNull(profileIds);
        var accounts = accountIds.Count == 0 ? "NULL" : string.Join(", ", accountIds.Select(Literal));
        var blobs = profileIds.Count == 0 ? "NULL" : string.Join(", ", profileIds.Select(Literal));
        var texts = profileIds.Count == 0 ? "NULL" : string.Join(", ", profileIds.Select(id => "'" + id.ToString("D", CultureInfo.InvariantCulture) + "'"));
        return table.Filter
            .Replace("{accounts}", accounts, StringComparison.Ordinal)
            .Replace("{profiles}", blobs, StringComparison.Ordinal)
            .Replace("{profileTexts}", texts, StringComparison.Ordinal);
    }

    private static string Literal(Guid id) => "X'" + Convert.ToHexString(GuidSql.ToBlob(id)) + "'";

    private static List<UserExportTable> BuildTables()
    {
        var tables = new List<UserExportTable>
        {
            // The users (sign-ins) that open the file's profiles, and how they sign in. A user opens exactly one profile.
            new("accounts", "id IN ({accounts})", [],
                [new UserRowCheck.AccountColumn("id"), new UserRowCheck.ProfileColumn("profile_id")]),
            new("account_credentials", "account_id IN ({accounts})", [], [new UserRowCheck.AccountColumn("account_id")]),
            new(TwoStepTable, "account_id IN ({accounts})", [], [new UserRowCheck.AccountColumn("account_id")]),
            new("account_passkeys", "account_id IN ({accounts})", [], [new UserRowCheck.AccountColumn("account_id")]),
            new("account_external_logins", "account_id IN ({accounts})", [], [new UserRowCheck.AccountColumn("account_id")]),
            new("account_notice_dismissals", "account_id IN ({accounts})", [], [new UserRowCheck.AccountColumn("account_id")]),

            // The household of the file's profiles. An import puts the profiles in the household the administrator chooses, so this row is
            // only read for its name. Whether it is the Library's default household is never carried (written as 0).
            new("households", "id IN (SELECT household_id FROM profiles WHERE id IN (" + InProfiles + "))", [],
                [new UserRowCheck.ParentRow("id", "profiles", "household_id")], ["is_default"]),

            // The file's profiles, and nothing of anyone else.
            // The administrator roles are NOT NULL, so they are written as 0 rather than left out; an import forces them to 0 as well.
            new("profiles", "id IN (" + InProfiles + ")", [], [new UserRowCheck.ProfileColumn("id")], ["is_server_admin", "is_household_admin"]),

            // Read/Watch/Listen/View as the profile had it. An import applies these only when the administrator ticks "Keep their access".
            new("profile_feature_grants", "profile_id IN (" + InProfiles + ")", [],
                [new UserRowCheck.ProfileColumn("profile_id"), new UserRowCheck.ColumnIn("feature_id", ["read", "watch", "listen", "view"])]),
            new("profile_credentials", "profile_id IN (" + InProfiles + ")", [], [new UserRowCheck.ProfileColumn("profile_id")]),
            new("profile_grid_preferences", "profile_id IN (" + InProfiles + ")", [], [new UserRowCheck.ProfileColumn("profile_id")]),
            new("profile_notice_dismissals", "profile_id IN (" + InProfiles + ")", [], [new UserRowCheck.ProfileColumn("profile_id")]),
            new("profile_sequence_preferences", "profile_id IN (" + InProfiles + ")", [], [new UserRowCheck.ProfileColumn("profile_id")]),
            new("profile_view_policies", "profile_id IN (" + InProfiles + ")", [], [new UserRowCheck.ProfileColumn("profile_id")]),

            // The View scope a profile last looked at may be another person's; such a row is left out rather than point at them.
            new("profile_view_preferences",
                "profile_id IN (" + InProfiles + ") AND (last_scope_profile_id IS NULL OR last_scope_profile_id IN (" + InProfiles + "))",
                [],
                [new UserRowCheck.ProfileColumn("profile_id"), new UserRowCheck.ProfileColumn("last_scope_profile_id", Nullable: true)]),
            new("profile_nsfw_settings", "profile_id IN (" + InProfiles + ")", ["changed_by_profile_id"], [new UserRowCheck.ProfileColumn("profile_id")]),
            new("user_playback_settings", "profile_id IN (" + InProfiles + ")", [], [new UserRowCheck.ProfileColumn("profile_id")]),

            // Playlists and collections the profiles built, with the parts of them that belong to those profiles.
            new("collections", "profile_id IN (" + InProfiles + ") AND owner_kind = 'Profile'", [],
                [
                    new UserRowCheck.ProfileColumn("profile_id"),
                    new UserRowCheck.ColumnIs("owner_kind", "Profile"),
                    new UserRowCheck.ColumnIn("collection_type", ["Custom", "Playlist", "PlaylistFolder", "Smart"]),
                ]),
            new("collection_items", "collection_id IN (" + BuiltByTheseProfiles + ")", [],
                [new UserRowCheck.ParentRow("collection_id", "collections", "id")]),
            new("collection_placements", "collection_id IN (" + BuiltByTheseProfiles + ")", [],
                [new UserRowCheck.ParentRow("collection_id", "collections", "id")]),
            new("collection_profile_audience", "collection_id IN (" + BuiltByTheseProfiles + ") AND profile_id IN (" + InProfiles + ")", [],
                [new UserRowCheck.ParentRow("collection_id", "collections", "id"), new UserRowCheck.ProfileColumn("profile_id")]),
        };

        // Each profile's own state: every PersonalState table that names an owner. Owners are BLOB ids in most tables and lower-case
        // TEXT in the reader tables.
        foreach (var rule in BackupTableClassification.OfKind(BackupDataKind.PersonalState).Where(rule => rule.OwnerColumn is not null))
        {
            var owner = rule.OwnerColumn!;
            tables.Add(new UserExportTable(
                rule.Table,
                $"((typeof({owner}) = 'blob' AND {owner} IN ({InProfiles})) OR (typeof({owner}) = 'text' AND lower({owner}) IN ({{profileTexts}})))",
                [],
                [new UserRowCheck.ProfileColumn(owner)]));
        }

        // The items of a bookmark pack belong to whoever the pack belongs to.
        tables.Add(new UserExportTable(
            "bookmark_pack_items",
            "pack_id IN (SELECT id FROM bookmark_packs WHERE profile_id IN (" + InProfiles + "))",
            [],
            [new UserRowCheck.ParentRow("pack_id", "bookmark_packs", "id")]));
        return tables;
    }
}
