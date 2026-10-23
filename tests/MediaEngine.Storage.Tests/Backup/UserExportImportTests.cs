using System.IO.Compression;
using System.Text;
using Dapper;
using MediaEngine.Storage.Backup;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests.Backup;

/// <summary>
/// One user moved between data stores (backup and restore spec D19): the export holds the user, the one profile it opens and that profile's
/// own state and nothing of anyone else, and the import makes them a new user who can sign in but has no access rights until an administrator chooses.
/// Each test builds real source and target data stores in temporary files.
/// </summary>
public sealed class UserExportImportTests : IDisposable
{
    private static readonly DateTimeOffset ImportedAt = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Stamp = "2026-10-21T10:00:00Z";

    private readonly List<Store> _stores = [];

    // ---------------------------------------------------------------------------------------------------------
    // Export
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Export_HoldsTheUserItsProfileAndItsState_AndNothingOfAnyoneElse()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya", "Maya (kids)"], administrator: false);
        var other = source.AddUser("other-person@example.com", ["Secret Other Profile"], administrator: false);
        var movie = source.AddMovie("Dune", "tt1160419", "aaaa1111");
        source.AddUserState(maya.Profiles[0], movie.Asset, 42.5);
        source.AddUserState(other.Profiles[0], movie.Asset, 99.0);

        var export = Export(source, maya);

        var accounts = Table(export, "accounts");
        Assert.Equal(["maya@example.com"], accounts.Rows.Select(row => Text(accounts, row, "email")).ToList());
        var profiles = Table(export, "profiles");
        Assert.Equal(["Maya"], profiles.Rows.Select(row => Text(profiles, row, "display_name")).ToList());
        var states = Table(export, "user_states");
        Assert.Single(states.Rows);
        Assert.Single(Table(export, "account_credentials").Rows);

        var bytes = ArchiveText(export);
        Assert.DoesNotContain("other-person@example.com", bytes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret Other Profile", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("Maya (kids)", bytes, StringComparison.Ordinal);
        Assert.Equal("user", ContentsKind(export));
    }

    [Fact]
    public void Export_WithTheTwoStepSecretLeftOut_HoldsNoTwoStepRow_AndNoPartOfTheSecret_ButKeepsTheRestOfTheSignIn()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false, twoStepSecret: "SECRET-MARKER");

        var export = new PersonalStateBackupRepository(source.Database, new FakeCodec()).Export(BackupScope.ForAccount(maya.Account, leaveOutTwoStep: true));

        Assert.DoesNotContain(export.Tables, table => string.Equals(table.Table, "account_two_step", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("SECRET-MARKER", ArchiveText(export), StringComparison.Ordinal);
        Assert.Single(Table(export, "account_credentials").Rows);
        Assert.Single(Table(export, "accounts").Rows);
    }

    [Fact]
    public void Import_OfAFileWithNoTwoStepRow_Succeeds_AndTwoStepIsOffForThePerson()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false, twoStepSecret: "SECRET-MARKER");
        var export = new PersonalStateBackupRepository(source.Database, new FakeCodec()).Export(BackupScope.ForAccount(maya.Account, leaveOutTwoStep: true));

        var result = ImportInto(target, export);

        Assert.Equal(maya.Account, result.AccountId);
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM account_two_step"));
        Assert.Equal(source.Text("SELECT secret_hash FROM account_credentials"), target.Text("SELECT secret_hash FROM account_credentials"));
        Assert.Equal(UserImportRefusal.None, new UserImportRepository(NewStore().Database).Check(UserExportReader.Read(export)));
    }

    [Fact]
    public void Export_NeverHoldsAccessRights_Devices_Sessions_OrInvitations()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: true);
        source.Execute("INSERT INTO account_invitations (id, account_id, token_hash, created_at, expires_at) VALUES (randomblob(16), " + Blob(maya.Account) + ", 'hash-1', '" + Stamp + "', '" + Stamp + "')");

        var export = Export(source, maya);

        var names = export.Tables.Select(table => table.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("profile_feature_grants", names);
        Assert.DoesNotContain("profile_library_grants", names);
        Assert.DoesNotContain("profile_admin_protections", names);
        Assert.DoesNotContain("auth_sessions", names);
        Assert.DoesNotContain("account_invitations", names);
        Assert.DoesNotContain("client_devices", names);
        Assert.DoesNotContain("provider_config", names);
        Assert.DoesNotContain("backup_settings", names);
        Assert.Empty(export.Choices);
        Assert.Empty(export.Corrections);
        Assert.All(export.Tables, table => Assert.NotEqual(BackupDataKind.Catalogue, table.Kind));
    }

    [Fact]
    public void Export_LeavesOutTheOtherPeopleInTheHousehold_AndNeverCarriesTheDefaultHouseholdMark()
    {
        var source = NewStore();
        var household = source.AddHousehold("The Khans");
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false, household: household);
        source.AddUser("lead@example.com", ["Lead"], administrator: false, household: household);
        source.Execute($"UPDATE households SET is_default = 1 WHERE id = {Blob(household)}");

        var export = Export(source, maya);

        var households = Table(export, "households");
        var row = Assert.Single(households.Rows);
        Assert.Equal("The Khans", Text(households, row, "name"));
        Assert.Equal(0L, Assert.IsType<BackupValue.Integer>(Value(households, row, "is_default")).Value);
        Assert.Equal(["Maya"], Table(export, "profiles").Rows.Select(profile => Text(Table(export, "profiles"), profile, "display_name")).ToList());
        Assert.DoesNotContain("lead@example.com", ArchiveText(export), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lead", ArchiveText(export), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_HoldsOnlyTheCollectionsTheirProfilesBuilt_WithTheirItems()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        var other = source.AddUser("other@example.com", ["Other"], administrator: false);
        var movie = source.AddMovie("Dune", "tt1160419", "aaaa1111");
        var mine = source.AddPlaylist(maya.Profiles[0], "Friday night", movie);
        source.AddPlaylist(other.Profiles[0], "Not mine", movie);
        source.Execute("INSERT INTO collections (id, collection_type, display_name, owner_kind) VALUES (randomblob(16), 'Custom', 'Library built', 'Library')");

        var export = Export(source, maya);

        var collections = Table(export, "collections");
        Assert.Equal(["Friday night"], collections.Rows.Select(row => Text(collections, row, "display_name")).ToList());
        var items = Table(export, "collection_items");
        Assert.Single(items.Rows);
        Assert.Contains(items.Rows.Single(), value => value is BackupValue.TitleRef);
        Assert.Contains(Table(export, "collection_placements").Rows, _ => true);
        Assert.Equal(mine, ((BackupValue.Blob)Value(collections, collections.Rows[0], "id")).Value);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Import
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void RoundTrip_TheUserSignsInWithTheSameHash_TheTwoStepSecretSurvives_AndTheirProfileIsNotAnAdministrator()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("Maya@Example.com", ["Maya", "Maya (kids)"], administrator: true, twoStepSecret: "SECRET-MARKER");
        source.Execute($"UPDATE profiles SET role = 'Administrator' WHERE id = {Blob(maya.Profiles[0])}");

        var result = ImportInto(target, Export(source, maya));

        Assert.Equal(maya.Account, result.AccountId);
        Assert.Equal("Maya@Example.com", result.Email);
        Assert.Equal(source.Text("SELECT secret_hash FROM account_credentials"), target.Text("SELECT secret_hash FROM account_credentials"));
        Assert.Equal("MAYA@EXAMPLE.COM", target.Text("SELECT normalized_email FROM accounts"));
        Assert.Equal("sealed:readable:SECRET-MARKER", target.Text("SELECT secret_protected FROM account_two_step"));
        Assert.Equal(1, source.Scalar("SELECT is_server_admin FROM profiles WHERE display_name = 'Maya'"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM profiles WHERE is_server_admin = 1 OR is_household_admin = 1"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM profile_feature_grants"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM profile_library_grants"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM profiles WHERE role = 'Administrator' AND display_name LIKE 'Maya%'"));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM profiles WHERE display_name LIKE 'Maya%'"));
        Assert.Equal(1, target.Scalar($"SELECT COUNT(*) FROM accounts WHERE profile_id = {Blob(maya.Profiles[0])}"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM auth_sessions"));
    }

    [Fact]
    public void Export_WritesAProfilesAdministratorRolesAsZero_AndKeepsTheProfileASignInOpens()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: true);
        source.Execute($"UPDATE profiles SET is_server_admin = 1, is_household_admin = 1 WHERE id = {Blob(maya.Profiles[0])}");

        var export = Export(source, maya);

        var profiles = Table(export, "profiles");
        var profileRow = Assert.Single(profiles.Rows);
        Assert.Equal(0L, Assert.IsType<BackupValue.Integer>(Value(profiles, profileRow, "is_server_admin")).Value);
        Assert.Equal(0L, Assert.IsType<BackupValue.Integer>(Value(profiles, profileRow, "is_household_admin")).Value);
        var accounts = Table(export, "accounts");
        Assert.Equal(maya.Profiles[0], UserExportReader.GuidOf(Value(accounts, Assert.Single(accounts.Rows), "profile_id")));
    }

    [Fact]
    public void Import_NeverCarriesAProfilesAdministratorRoles_EvenWhenTheFileSaysSo_AndKeepsTheSignInAttachedToItsProfile()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        var export = Export(source, maya);
        var profiles = Table(export, "profiles");
        var serverIndex = profiles.Columns.ToList().FindIndex(column => string.Equals(column, "is_server_admin", StringComparison.OrdinalIgnoreCase));
        var householdIndex = profiles.Columns.ToList().FindIndex(column => string.Equals(column, "is_household_admin", StringComparison.OrdinalIgnoreCase));
        var crafted = profiles with
        {
            Rows = profiles.Rows
                .Select(row =>
                {
                    var values = row.ToList();
                    values[serverIndex] = BackupValue.FromInteger(1);
                    values[householdIndex] = BackupValue.FromInteger(1);
                    return (IReadOnlyList<BackupValue>)values;
                })
                .ToList(),
        };
        export = export with { Tables = export.Tables.Select(table => ReferenceEquals(table, profiles) ? crafted : table).ToList() };

        ImportInto(target, export);

        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM profiles WHERE is_server_admin = 1 OR is_household_admin = 1"));
        Assert.Equal(1, target.Scalar($"SELECT COUNT(*) FROM accounts WHERE profile_id = {Blob(maya.Profiles[0])}"));
    }

    [Fact]
    public async Task RoundTrip_AfterTheUserIsDeleted_TheSameFileImportsAgainWithNothingLeftToClash()
    {
        var store = NewStore();
        var maya = store.AddUser("maya@example.com", ["Maya"], administrator: false);
        var movie = store.AddMovie("Dune", "tt1160419", "aaaa1111");
        store.AddUserState(maya.Profiles[0], movie.Asset, 42.5);
        store.Execute($"INSERT INTO user_taste_profiles (user_id, profile_json) VALUES ({Blob(maya.Profiles[0])}, '{{}}')");
        var export = Export(store, maya);

        // Remove the sign-in, then the people with the rows no foreign key would have cleared.
        var accounts = new AccountRepository(store.Database);
        await accounts.DeleteAccountAsync(maya.Account);
        foreach (var profile in maya.Profiles)
        {
            await accounts.DeleteManagedProfileAsync(profile);
        }

        Assert.Equal(0, store.Scalar("SELECT COUNT(*) FROM accounts"));
        Assert.Equal(0, store.Scalar("SELECT COUNT(*) FROM profiles WHERE display_name LIKE 'Maya%'"));
        Assert.Equal(0, store.Scalar("SELECT COUNT(*) FROM user_states"));
        Assert.Equal(0, store.Scalar("SELECT COUNT(*) FROM user_taste_profiles"));

        var result = ImportInto(store, export);

        Assert.Equal(maya.Account, result.AccountId);
        Assert.Equal(1, store.Scalar("SELECT COUNT(*) FROM accounts"));
        Assert.Equal(1, store.Scalar("SELECT COUNT(*) FROM profiles WHERE display_name LIKE 'Maya%'"));
    }

    [Fact]
    public void RoundTrip_TheProfilesKeepTheirPinPreferencesAndSettings_ButVaultAccessIsSwitchedOff()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);

        ImportInto(target, Export(source, maya));

        Assert.Equal(source.Text("SELECT secret_hash FROM profile_credentials"), target.Text("SELECT secret_hash FROM profile_credentials"));
        Assert.Equal("title", target.Text("SELECT sort FROM profile_grid_preferences WHERE grid_key = 'watch'"));
        Assert.Equal("{\"volume\":7}", target.Text("SELECT settings_json FROM user_playback_settings"));
        Assert.Equal("pin", source.Text("SELECT access_mode FROM profile_nsfw_settings"));
        Assert.Equal("off", target.Text("SELECT access_mode FROM profile_nsfw_settings"));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM account_notice_dismissals"));
    }

    [Fact]
    public void RoundTrip_TheirStateWaitsForTheTitle_ThenAttachesWhenTheSameTitleIsAdded()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        var movie = source.AddMovie("Dune", "tt1160419", "aaaa1111");
        source.AddUserState(maya.Profiles[0], movie.Asset, 42.5);
        source.AddPlaylist(maya.Profiles[0], "Friday night", movie);

        var result = ImportInto(target, Export(source, maya));

        Assert.True(result.WaitingItems >= 2);
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM user_states"));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM collections"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM collection_items"));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM backup_restores WHERE backup_file_name = 'tuvima-user-1.zip'"));

        target.AddMovie("Dune", "tt1160419", "bbbb2222");
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(ImportedAt.AddMinutes(5), 100, 90);

        Assert.True(pass.Attached >= 2);
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM user_states"));
        Assert.Equal(42.5, target.Real("SELECT progress_pct FROM user_states"));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM collection_items"));
    }

    [Fact]
    public void Import_JoinsAHouseholdThatExists_WithoutMakingThemItsAdministrator()
    {
        var source = NewStore();
        var target = NewStore();
        var household = source.AddHousehold("The Khans");
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false, household: household, householdAdmin: true);
        target.AddHousehold("The Khans", household);
        target.AddUser("lead@example.com", ["Lead"], administrator: false, household: household, householdAdmin: true);

        ImportInto(target, Export(source, maya));

        Assert.Equal(Blob(household), "X'" + target.Text("SELECT hex(household_id) FROM profiles WHERE display_name = 'Maya'") + "'");
        Assert.Equal(0, target.Scalar("SELECT is_household_admin FROM profiles WHERE display_name = 'Maya'"));
        Assert.Equal(1, target.Scalar("SELECT is_household_admin FROM profiles WHERE display_name = 'Lead'"));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM households"));
    }

    [Fact]
    public void Import_CreatesTheHousehold_ButNeverAsTheDefaultOne_AndNeverMakesThemAHouseholdAdministrator()
    {
        var source = NewStore();
        var target = NewStore();
        var household = source.AddHousehold("The Khans");
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false, household: household, householdAdmin: true);
        source.Execute($"UPDATE households SET is_default = 1 WHERE id = {Blob(household)}");

        ImportInto(target, Export(source, maya));

        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM households WHERE name = 'The Khans'"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM households WHERE is_default = 1"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM profiles WHERE is_household_admin = 1"));
        Assert.Equal(1, target.Scalar($"SELECT COUNT(*) FROM profiles WHERE household_id = {Blob(household)}"));
    }

    [Fact]
    public void Import_IsRefused_WhenSomeoneHereAlreadySignsInWithThatEmail_AndNothingIsWritten()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        target.AddUser("MAYA@example.com", ["Someone else"], administrator: false);
        var before = Counts(target);

        var ex = Assert.Throws<UserImportRefusedException>(() => ImportInto(target, Export(source, maya)));

        Assert.Equal(UserImportRefusal.EmailInUse, ex.Refusal);
        Assert.Equal(before, Counts(target));
    }

    [Fact]
    public void Import_IsRefused_WhenTheAccountOrAProfileIsAlreadyHere_AndNothingIsWritten()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        var export = Export(source, maya);

        var sameAccount = NewStore();
        sameAccount.Execute($"INSERT INTO profiles (id, display_name, created_at) VALUES (randomblob(16), 'Renamed', '{Stamp}')");
        sameAccount.Execute($"INSERT INTO accounts (id, email, normalized_email, created_at, updated_at, profile_id) VALUES ({Blob(maya.Account)}, 'renamed@example.com', 'RENAMED@EXAMPLE.COM', '{Stamp}', '{Stamp}', (SELECT id FROM profiles WHERE display_name = 'Renamed'))");
        Assert.Equal(UserImportRefusal.AlreadyHere, Assert.Throws<UserImportRefusedException>(() => ImportInto(sameAccount, export)).Refusal);
        Assert.Equal(0, sameAccount.Scalar("SELECT COUNT(*) FROM profiles WHERE display_name LIKE 'Maya%'"));

        var sameProfile = NewStore();
        sameProfile.Execute($"INSERT INTO profiles (id, display_name, created_at) VALUES ({Blob(maya.Profiles[0])}, 'Taken', '{Stamp}')");
        Assert.Equal(UserImportRefusal.AlreadyHere, Assert.Throws<UserImportRefusedException>(() => ImportInto(sameProfile, export)).Refusal);
        Assert.Equal(0, sameProfile.Scalar("SELECT COUNT(*) FROM profiles WHERE display_name LIKE 'Maya%'"));
        Assert.Equal(0, sameProfile.Scalar("SELECT COUNT(*) FROM accounts"));
    }

    [Fact]
    public void Import_IsRefused_WhenTheOutsideSignInIsAlreadyLinkedToSomeoneHere_AndNothingIsWritten()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false, outsideSubject: "sub-123");
        var someone = target.AddUser("someone@example.com", ["Someone"], administrator: false, outsideSubject: "sub-123");
        Assert.NotEqual(maya.Account, someone.Account);
        var before = Counts(target);

        var ex = Assert.Throws<UserImportRefusedException>(() => ImportInto(target, Export(source, maya)));

        Assert.Equal(UserImportRefusal.OutsideSignInInUse, ex.Refusal);
        Assert.Equal(before, Counts(target));
    }

    [Fact]
    public void Import_IsRefused_WhenTheTwoStepSecretWasNotMadeReadableForAnyComputer()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false, twoStepSecret: "SECRET-MARKER");
        var export = new PersonalStateBackupRepository(source.Database, null).Export(BackupScope.ForAccount(maya.Account));

        var ex = Assert.Throws<UserImportRefusedException>(() => ImportInto(target, export));

        Assert.Equal(UserImportRefusal.TwoStepCannotMove, ex.Refusal);
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM accounts"));
    }

    [Fact]
    public void Check_ReportsTheSameRefusalsAsTheImport_AndWritesNothing()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        var summary = UserExportReader.Read(Export(source, maya));
        var repository = new UserImportRepository(target.Database);

        Assert.Equal(UserImportRefusal.None, repository.Check(summary));
        target.AddUser("maya@example.com", ["Other"], administrator: false);
        Assert.Equal(UserImportRefusal.EmailInUse, repository.Check(summary));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM accounts"));
    }

    [Fact]
    public void Import_EmptiesAnAvatarPathAndCollectionArtworkPathsACraftedFileNames_SoNoFileOnThisComputerCanBeServedOrDeleted()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        var movie = source.AddMovie("Dune", "tt1160419", "aaaa1111");
        source.AddPlaylist(maya.Profiles[0], "Friday night", movie);
        var crafted = Export(source, maya);
        crafted = WithValue(crafted, "profiles", "avatar_image_path", BackupValue.FromText("/etc/tuvima/secrets/key-ring.xml"));
        foreach (var column in new[] { "cover_artwork_path", "background_artwork_path", "banner_artwork_path", "logo_artwork_path" })
        {
            crafted = WithValue(crafted, "collections", column, BackupValue.FromText("/var/lib/tuvima/tuvima_master.db"));
        }

        ImportInto(target, crafted);

        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM profiles WHERE display_name = 'Maya' AND avatar_image_path IS NULL"));
        Assert.Equal(
            1,
            target.Scalar(
                "SELECT COUNT(*) FROM collections WHERE display_name = 'Friday night' AND cover_artwork_path IS NULL AND background_artwork_path IS NULL " +
                "AND banner_artwork_path IS NULL AND logo_artwork_path IS NULL"));
    }

    [Fact]
    public void Import_EmptiesWhoChangedTheVaultSetting_AndAParentCollectionThatIsNotInTheFile()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        var movie = source.AddMovie("Dune", "tt1160419", "aaaa1111");
        var first = source.AddPlaylist(maya.Profiles[0], "First", movie);
        var second = source.AddPlaylist(maya.Profiles[0], "Second", movie);
        var crafted = Export(source, maya);
        crafted = WithValue(crafted, "profile_nsfw_settings", "changed_by_profile_id", BackupValue.FromBlob(GuidSql.ToBlob(maya.Profiles[0])));
        var elsewhere = BackupValue.FromBlob(GuidSql.ToBlob(Guid.NewGuid()));
        crafted = WithValue(crafted, "collections", "parent_collection_id", elsewhere, rowWhere: (table, row) => Text(table, row, "display_name") == "First");
        crafted = WithValue(crafted, "collections", "parent_collection_id", BackupValue.FromBlob(first), rowWhere: (table, row) => Text(table, row, "display_name") == "Second");

        ImportInto(target, crafted);

        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM profile_nsfw_settings WHERE changed_by_profile_id IS NOT NULL"));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM collections WHERE display_name = 'First' AND parent_collection_id IS NULL"));
        Assert.Equal(1, target.Scalar($"SELECT COUNT(*) FROM collections WHERE display_name = 'Second' AND parent_collection_id = {Blob(first)}"));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Import_BringsCollectionsInPrivateAndNotFeatured_AndKeepsAnAudienceLimitedToTheirOwnProfiles()
    {
        var source = NewStore();
        var target = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya", "Maya (kids)"], administrator: false);
        var movie = source.AddMovie("Dune", "tt1160419", "aaaa1111");
        var everyone = source.AddPlaylist(maya.Profiles[0], "For everyone", movie);
        var selected = source.AddPlaylist(maya.Profiles[0], "For the kids", movie);
        source.Execute($"UPDATE collections SET is_featured = 1, audience = 'Everyone' WHERE id = {Blob(everyone)}");
        source.Execute($"UPDATE collections SET is_featured = 1, audience = 'SelectedProfiles' WHERE id = {Blob(selected)}");
        source.Execute($"INSERT INTO collection_profile_audience (collection_id, profile_id) VALUES ({Blob(selected)}, {Blob(maya.Profiles[0])})");
        source.Execute($"INSERT INTO collection_profile_audience (collection_id, profile_id) VALUES ({Blob(selected)}, {Blob(maya.Profiles[1])})");

        ImportInto(target, Export(source, maya));

        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM collections WHERE is_featured <> 0"));
        Assert.Equal("Private", target.Text("SELECT audience FROM collections WHERE display_name = 'For everyone'"));
        Assert.Equal("SelectedProfiles", target.Text("SELECT audience FROM collections WHERE display_name = 'For the kids'"));
        Assert.Equal(1, target.Scalar($"SELECT COUNT(*) FROM collection_profile_audience WHERE profile_id = {Blob(maya.Profiles[0])}"));
        Assert.Equal(0, target.Scalar($"SELECT COUNT(*) FROM collection_profile_audience WHERE profile_id = {Blob(maya.Profiles[1])}"));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM collection_profile_audience"));
    }

    [Fact]
    public void Preview_ListsTheProfilesRoleAndContentLimitAsTheFileStatesThem()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya (kids)"], administrator: false);
        source.Execute($"UPDATE profiles SET role = 'RestrictedProfile', content_limit = 'PG', content_limit_allow_unrated = 1 WHERE id = {Blob(maya.Profiles[0])}");

        var summary = UserExportReader.Read(Export(source, maya));

        Assert.Equal(new UserExportProfile("Maya (kids)", "RestrictedProfile", "PG", true), Assert.Single(summary.Profiles));
        Assert.Equal(maya.Profiles[0], Assert.Single(summary.ProfileIds));
    }

    [Fact]
    public void FindHouseholdName_NamesAHouseholdThatExistsHere_AndIsNullForOneThatDoesNot()
    {
        var target = NewStore();
        var household = target.AddHousehold("The Khans here");

        var repository = new UserImportRepository(target.Database);

        Assert.Equal("The Khans here", repository.FindHouseholdName(household));
        Assert.Null(repository.FindHouseholdName(Guid.NewGuid()));
    }

    [Fact]
    public void AFileThatFillsAColumnAnExportAlwaysLeavesEmpty_IsRefusedByTheReaderOfAUserExportAndOfAWholeLibraryBackup()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);

        var user = WithValue(Export(source, maya), "profiles", "avatar_image_path", BackupValue.FromText("/etc/passwd"));
        using (var stream = Zip(user))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.ReadUserExport(archive));
        }

        var whole = WithValue(
            new PersonalStateBackupRepository(source.Database).Export(BackupScope.WholeLibrary),
            "profiles",
            "avatar_image_path",
            BackupValue.FromText("/etc/passwd"));
        using (var stream = Zip(whole))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.ReadBackupFile(archive));
        }

        // The same file with the column left empty, as every export writes it, is read.
        using var honest = Zip(new PersonalStateBackupRepository(source.Database).Export(BackupScope.WholeLibrary));
        using var honestArchive = new ZipArchive(honest, ZipArchiveMode.Read);
        BackupArchiveFormat.ReadBackupFile(honestArchive);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reading a file
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void AUserExportFile_ReadsBackAsAUserExport_ButNotAsAWholeLibraryBackup()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        using var stream = Zip(Export(source, maya));

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Equal(maya.Account, UserExportReader.Read(BackupArchiveFormat.ReadUserExport(archive)).AccountId);
        }

        stream.Position = 0;
        using var again = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.ReadBackupFile(again));
    }

    [Fact]
    public void AWholeLibraryBackupFile_IsNotAUserExport()
    {
        var source = NewStore();
        source.AddUser("maya@example.com", ["Maya"], administrator: false);
        using var stream = Zip(new PersonalStateBackupRepository(source.Database).Export(BackupScope.WholeLibrary));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.ReadUserExport(archive));
    }

    [Fact]
    public void AFileWithARowOfSomeoneElse_IsNotAUserExport()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        var other = source.AddUser("other@example.com", ["Other"], administrator: false);
        var export = Export(source, maya);

        var dismissals = Table(export, "account_notice_dismissals");
        var crafted = dismissals with
        {
            Rows = [.. dismissals.Rows, [BackupValue.FromBlob(GuidSql.ToBlob(other.Account)), BackupValue.FromText("secure-account"), BackupValue.FromText(Stamp)]],
        };
        var tamperedAccounts = export with { Tables = [.. export.Tables.Where(table => table.Table != "account_notice_dismissals"), crafted] };
        Assert.Throws<InvalidDataException>(() => UserExportReader.Read(tamperedAccounts));

        var states = export.Tables.First(table => table.Table == "profiles");
        var extraProfile = states with { Rows = [.. states.Rows, .. Table(Export(source, other), "profiles").Rows] };
        var tamperedProfiles = export with { Tables = [.. export.Tables.Where(table => table.Table != "profiles"), extraProfile] };
        Assert.Throws<InvalidDataException>(() => UserExportReader.Read(tamperedProfiles));
    }

    [Fact]
    public void AFileWithAProviderBuiltCollectionOwnedByTheirProfile_IsNotAUserExport()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        source.Execute($"INSERT INTO collections (id, collection_type, display_name, profile_id, owner_kind) VALUES (randomblob(16), 'Smart', 'Mine', {Blob(maya.Profiles[0])}, 'Profile')");
        source.Execute($"INSERT INTO collections (id, collection_type, display_name, profile_id, owner_kind) VALUES (randomblob(16), 'Universe', 'Not built by a person', {Blob(maya.Profiles[0])}, 'Profile')");
        var export = Export(source, maya);

        // The export holds only what a person built.
        var collections = Table(export, "collections");
        Assert.Equal(["Mine"], collections.Rows.Select(row => Text(collections, row, "display_name")).ToList());
        Assert.Equal(maya.Account, UserExportReader.Read(export).AccountId);

        var typeIndex = collections.Columns.ToList().IndexOf("collection_type");
        var crafted = collections with { Rows = [collections.Rows[0].Select((value, index) => index == typeIndex ? BackupValue.FromText("Universe") : value).ToList()] };
        var tampered = export with { Tables = [.. export.Tables.Where(table => table.Table != "collections"), crafted] };
        Assert.Throws<InvalidDataException>(() => UserExportReader.Read(tampered));
    }

    [Fact]
    public void AFileWithATableAUserExportNeverHolds_IsRefusedWhenRead()
    {
        var source = NewStore();
        var maya = source.AddUser("maya@example.com", ["Maya"], administrator: false);
        var export = Export(source, maya);
        var grants = new BackupExportTable(
            "profile_feature_grants",
            BackupDataKind.Configuration,
            ["profile_id", "feature_id", "granted_at"],
            [[BackupValue.FromBlob(GuidSql.ToBlob(maya.Profiles[0])), BackupValue.FromText("read"), BackupValue.FromText(Stamp)]]);
        using var stream = Zip(export with { Tables = [.. export.Tables, grants] });
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.ReadUserExport(archive));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private Store NewStore()
    {
        var store = new Store();
        _stores.Add(store);
        return store;
    }

    private static BackupExport Export(Store source, User user) =>
        new PersonalStateBackupRepository(source.Database, new FakeCodec()).Export(BackupScope.ForAccount(user.Account));

    private static UserImportResult ImportInto(Store target, BackupExport export) =>
        new UserImportRepository(target.Database).Import(export, "tuvima-user-1.zip", ImportedAt.AddDays(-1), new FakeCodec(), ImportedAt);

    /// <summary>The export with one column of a table set to a value in every row (or in the rows that pass <paramref name="rowWhere"/>), as a crafted file would hold it.</summary>
    private static BackupExport WithValue(
        BackupExport export,
        string tableName,
        string column,
        BackupValue value,
        Func<BackupExportTable, IReadOnlyList<BackupValue>, bool>? rowWhere = null)
    {
        var table = Table(export, tableName);
        var index = table.Columns.ToList().IndexOf(column);
        Assert.True(index >= 0, $"{tableName} has no column {column} in the export.");
        var rows = table.Rows
            .Select(row => rowWhere is null || rowWhere(table, row)
                ? (IReadOnlyList<BackupValue>)row.Select((existing, position) => position == index ? value : existing).ToList()
                : row)
            .ToList();
        return export with { Tables = [.. export.Tables.Select(candidate => candidate.Table == tableName ? table with { Rows = rows } : candidate)] };
    }

    private static BackupExportTable Table(BackupExport export, string name) => export.Tables.Single(table => table.Table == name);

    private static BackupValue Value(BackupExportTable table, IReadOnlyList<BackupValue> row, string column) =>
        row[table.Columns.ToList().FindIndex(candidate => candidate == column)];

    private static string Text(BackupExportTable table, IReadOnlyList<BackupValue> row, string column) =>
        ((BackupValue.Text)Value(table, row, column)).Value;

    private static string ContentsKind(BackupExport export)
    {
        using var stream = Zip(export);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry(BackupArchiveFormat.ContentsEntry)!.Open(), Encoding.UTF8);
        using var json = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
        return json.RootElement.GetProperty("kind").GetString()!;
    }

    private static MemoryStream Zip(BackupExport export)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            BackupArchiveFormat.Write(archive, export);
        }

        stream.Position = 0;
        return stream;
    }

    private static string ArchiveText(BackupExport export)
    {
        using var stream = Zip(export);
        using var readable = new ZipArchive(stream, ZipArchiveMode.Read);
        var text = new StringBuilder();
        foreach (var entry in readable.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            text.AppendLine(reader.ReadToEnd());
        }

        return text.ToString();
    }

    private static string Blob(Guid id) => "X'" + Convert.ToHexString(GuidSql.ToBlob(id)) + "'";

    private static string Blob(byte[] id) => "X'" + Convert.ToHexString(id) + "'";

    private static string Counts(Store store) =>
        string.Join(
            ",",
            new[] { "accounts", "profiles", "households", "profile_credentials", "backup_restores", "waiting_personal_state", "account_external_logins" }
                .Select(table => store.Scalar($"SELECT COUNT(*) FROM {table}")));

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }
    }

    private sealed class FakeCodec : IBackupPortableValueCodec
    {
        public string? Export(string table, string column, string protectedValue, IReadOnlyDictionary<string, BackupValue> row) => "readable:" + protectedValue;

        public string Import(string table, string column, string portableValue, IReadOnlyDictionary<string, BackupValue> row) => "sealed:" + portableValue;
    }

    private sealed record User(Guid Account, IReadOnlyList<Guid> Profiles);

    private sealed record Movie(byte[] Work, byte[] Edition, byte[] Asset);

    /// <summary>One real data store in a temporary file, with the helpers that fill it.</summary>
    private sealed class Store : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_user_export_{Guid.NewGuid():N}.db");

        public Store()
        {
            DapperConfiguration.Configure();
            Database = new DatabaseConnection(_path);
            Database.InitializeSchema();
            Database.RunStartupChecks();
        }

        public DatabaseConnection Database { get; }

        public Guid AddHousehold(string name, Guid? id = null)
        {
            var household = id ?? Guid.NewGuid();
            Run(
                "INSERT INTO households (id, name, created_at) VALUES (@id, @name, @now)",
                new { id = GuidSql.ToBlob(household), name, now = Stamp });
            return household;
        }

        /// <summary>
        /// A user with its sign-in, two-step, notices and a session, and the profile it opens (the first name) with its settings. Any further
        /// names are other people of the same household: they exist, but the user does not open them and an export never holds them.
        /// </summary>
        public User AddUser(string email, string[] profileNames, bool administrator, Guid? household = null, bool householdAdmin = false, string? twoStepSecret = null, string? outsideSubject = null)
        {
            var account = Guid.NewGuid();
            var accountBlob = GuidSql.ToBlob(account);
            var profiles = new List<Guid>();
            for (var i = 0; i < profileNames.Length; i++)
            {
                var profile = Guid.NewGuid();
                var profileBlob = GuidSql.ToBlob(profile);
                profiles.Add(profile);
                Run(
                    "INSERT INTO profiles (id, display_name, role, created_at, household_id, is_server_admin, is_household_admin) VALUES (@id, @name, 'StandardUser', @now, @household, @admin, @householdAdmin)",
                    new
                    {
                        id = profileBlob,
                        name = profileNames[i],
                        now = Stamp,
                        household = household is { } h2 ? GuidSql.ToBlob(h2) : null,
                        admin = i == 0 && administrator ? 1 : 0,
                        householdAdmin = i == 0 && householdAdmin ? 1 : 0,
                    });
                Run(
                    """
                    INSERT INTO profile_credentials (id, profile_id, credential_kind, secret_hash, hash_scheme, hash_version, security_stamp, created_at, updated_at)
                    VALUES (@id, @profile, 'ProfilePin', @hash, 'pbkdf2', 1, 'stamp', @now, @now)
                    """,
                    new { id = GuidSql.ToBlob(Guid.NewGuid()), profile = profileBlob, hash = "PIN-" + Guid.NewGuid().ToString("N"), now = Stamp });
                Run("INSERT INTO profile_grid_preferences (profile_id, grid_key, sort, updated_at) VALUES (@profile, 'watch', 'title', @now)", new { profile = profileBlob, now = Stamp });
                Run("INSERT INTO user_playback_settings (profile_id, settings_json, updated_at) VALUES (@profile, '{\"volume\":7}', @now)", new { profile = profileBlob, now = Stamp });
                Run("INSERT INTO profile_nsfw_settings (profile_id, access_mode, updated_at) VALUES (@profile, 'pin', @now)", new { profile = profileBlob, now = Stamp });
            }

            Run(
                """
                INSERT INTO accounts (id, email, normalized_email, created_at, updated_at, this_computer_only, profile_id)
                VALUES (@account, @email, @normalized, @now, @now, 1, @profile)
                """,
                new
                {
                    account = accountBlob,
                    email,
                    normalized = email.ToUpperInvariant(),
                    now = Stamp,
                    profile = GuidSql.ToBlob(profiles[0]),
                });
            Run(
                """
                INSERT INTO account_credentials (id, account_id, credential_kind, secret_hash, hash_scheme, hash_version, security_stamp, created_at, updated_at)
                VALUES (@id, @account, 'Password', @hash, 'pbkdf2', 1, 'stamp', @now, @now)
                """,
                new { id = GuidSql.ToBlob(Guid.NewGuid()), account = accountBlob, hash = "HASH-" + Guid.NewGuid().ToString("N"), now = Stamp });
            Run("INSERT INTO account_notice_dismissals (account_id, notice_key, dismissed_at) VALUES (@account, 'secure-account', @now)", new { account = accountBlob, now = Stamp });
            Run("INSERT INTO profile_feature_grants (profile_id, feature_id, granted_at) VALUES (@profile, 'read', @now)", new { profile = GuidSql.ToBlob(profiles[0]), now = Stamp });
            Run("INSERT INTO profile_library_grants (profile_id, library_id, granted_at) VALUES (@profile, @library, @now)", new { profile = GuidSql.ToBlob(profiles[0]), library = GuidSql.ToBlob(Guid.NewGuid()), now = Stamp });
            if (twoStepSecret is not null)
            {
                Run("INSERT INTO account_two_step (account_id, secret_protected, created_at) VALUES (@account, @secret, @now)", new { account = accountBlob, secret = twoStepSecret, now = Stamp });
            }

            if (outsideSubject is not null)
            {
                Run(
                    "INSERT INTO account_external_logins (id, account_id, provider, issuer, subject, linked_at) VALUES (@id, @account, 'oidc', 'https://issuer.example', @subject, @now)",
                    new { id = GuidSql.ToBlob(Guid.NewGuid()), account = accountBlob, subject = outsideSubject, now = Stamp });
            }

            Run(
                """
                INSERT INTO auth_sessions (id, account_id, active_profile_id, token_hash, device_id, device_name, client, authentication_method, security_stamp, created_at, last_seen_at, expires_at)
                VALUES (@id, @account, @profile, @token, 'd', 'd', 'web', 'password', 'stamp', @now, @now, @now)
                """,
                new { id = GuidSql.ToBlob(Guid.NewGuid()), account = accountBlob, profile = GuidSql.ToBlob(profiles[0]), token = "token-" + Guid.NewGuid().ToString("N"), now = Stamp });
            return new User(account, profiles);
        }

        public Movie AddMovie(string title, string imdb, string hash)
        {
            var work = GuidSql.ToBlob(Guid.NewGuid());
            var editionId = GuidSql.ToBlob(Guid.NewGuid());
            var asset = GuidSql.ToBlob(Guid.NewGuid());
            using var connection = Database.CreateConnection();
            connection.Execute("INSERT INTO works (id, media_type) VALUES (@work, 'Movies')", new { work });
            connection.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @work, '4K Remux')", new { editionId, work });
            connection.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@asset, @editionId, @hash, @path)",
                new { asset, editionId, hash, path = $"/media/{hash}.mkv" });
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, 'title', @title, @now)",
                new { work, title, now = Stamp });
            connection.Execute(
                "INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES (@id, @work, 'imdb_id', @imdb)",
                new { id = GuidSql.ToBlob(Guid.NewGuid()), work, imdb });
            return new Movie(work, editionId, asset);
        }

        public void AddUserState(Guid profile, byte[] asset, double progress) =>
            Run(
                "INSERT INTO user_states (user_id, asset_id, content_hash, progress_pct, last_accessed, extended_properties) VALUES (@profile, @asset, (SELECT content_hash FROM media_assets WHERE id = @asset), @progress, @now, '{}')",
                new { profile = GuidSql.ToBlob(profile), asset, progress, now = Stamp });

        /// <summary>A playlist a profile built, holding one title and shown on the home page. Returns the playlist's id.</summary>
        public byte[] AddPlaylist(Guid profile, string name, Movie movie)
        {
            var id = GuidSql.ToBlob(Guid.NewGuid());
            Run(
                "INSERT INTO collections (id, collection_type, display_name, profile_id, owner_kind) VALUES (@id, 'Playlist', @name, @profile, 'Profile')",
                new { id, name, profile = GuidSql.ToBlob(profile) });
            Run(
                "INSERT INTO collection_items (id, collection_id, work_id) VALUES (@item, @id, @work)",
                new { item = GuidSql.ToBlob(Guid.NewGuid()), id, work = movie.Work });
            Run(
                "INSERT INTO collection_placements (id, collection_id, location) VALUES (@placement, @id, 'home')",
                new { placement = GuidSql.ToBlob(Guid.NewGuid()), id });
            return id;
        }

        public void Execute(string sql) => Run(sql, new { });

        public int Scalar(string sql)
        {
            using var connection = Database.CreateConnection();
            return connection.ExecuteScalar<int>(sql);
        }

        public double Real(string sql)
        {
            using var connection = Database.CreateConnection();
            return connection.ExecuteScalar<double>(sql);
        }

        public string Text(string sql)
        {
            using var connection = Database.CreateConnection();
            return connection.ExecuteScalar<string>(sql) ?? string.Empty;
        }

        public void Dispose()
        {
            Database.Dispose();
            using (var pool = new SqliteConnection($"Data Source={_path}"))
            {
                SqliteConnection.ClearPool(pool);
            }

            // Best-effort cleanup of the temporary data store; a locked file must not fail the test run.
            foreach (var leftover in new[] { _path, _path + "-wal", _path + "-shm" })
            {
                try
                {
                    File.Delete(leftover);
                }
                catch (IOException)
                {
                }
            }
        }

        private void Run(string sql, object parameters)
        {
            using var connection = Database.CreateConnection();
            connection.Execute(sql, parameters);
        }
    }
}
