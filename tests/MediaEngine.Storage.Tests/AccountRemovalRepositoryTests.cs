using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Deleting a user removes only the sign-in (the profile and everything in it stay), except the last household administrator's
/// user of a non-default household, which takes the household and every profile in it; deleting a profile takes its user and
/// leaves nothing personal behind; a second household needs every server administrator secured first.
/// </summary>
public sealed class AccountRemovalRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ProfileRepository _profiles;
    private readonly ProfileAccessRepository _profileAccess;
    private readonly DateTimeOffset _now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private int _counter;

    public AccountRemovalRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_account_removal_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _profiles = new ProfileRepository(_database);
        _profileAccess = new ProfileAccessRepository(_database);
    }

    // ---- Deleting a user removes only the sign-in (D5) ----

    [Fact]
    public async Task DeletingAUser_RemovesOnlyTheSignIn_AndTheirProfileKeepsEverythingPersonal()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var sam = await AddMainSignInAsync("sam@example.com", "Sam", owner.HouseholdId);
        var samProfile = await DefaultProfileAsync(sam);
        var left = Behind(samProfile, AddAsset());

        var outcome = await _accounts.DeleteUserAsync(sam.Id);

        Assert.Equal([sam.Id], outcome.DeletedUserIds);
        Assert.Empty(outcome.DeletedProfileIds);
        Assert.Null(outcome.RemovedHouseholdId);
        Assert.Null(await _accounts.GetByIdAsync(sam.Id));
        Assert.NotNull(await _profiles.GetByIdAsync(samProfile));
        Assert.Equal(PersonalRowCount, PersonalRowsFor(samProfile, left));
        Assert.NotNull(await _accounts.GetByIdAsync(owner.Id));
    }

    [Fact]
    public async Task AProfileWhoseUserWasDeleted_CanGetANewSignInAttachedLater()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var sam = await AddMainSignInAsync("sam@example.com", "Sam", owner.HouseholdId);
        var samProfile = await DefaultProfileAsync(sam);
        await _accounts.DeleteUserAsync(sam.Id);

        var again = NewAccount("sam@new.example.com");
        again.HouseholdId = owner.HouseholdId;
        await _accounts.CreateAccountAsync(again, Grant(again.Id, samProfile, isDefault: true),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>(), preserveProfileAccess: true);

        Assert.Equal(samProfile, (await _accounts.GetByIdAsync(again.Id))!.ProfileId);
    }

    [Fact]
    public async Task DeletingAHouseholdAdministratorsUser_WhoIsNotTheLast_LeavesTheirProfileAnOrdinaryMember()
    {
        var alex = await AddMainSignInAsync("alex@example.com", "Alex");
        var pat = await AddMainSignInAsync("pat@example.com", "Pat", alex.HouseholdId);
        var alexProfile = await DefaultProfileAsync(alex);
        await _profileAccess.SetHouseholdAdministratorAsync(await DefaultProfileAsync(pat), true, _now);
        Assert.True((await _profiles.GetByIdAsync(alexProfile))!.IsHouseholdAdministrator);

        var plan = await _accounts.PlanUserRemovalAsync(alex.Id);
        var outcome = await _accounts.DeleteUserAsync(alex.Id);

        Assert.False(plan.RemovesHousehold);
        Assert.Null(outcome.RemovedHouseholdId);
        var kept = (await _profiles.GetByIdAsync(alexProfile))!;
        Assert.False(kept.IsHouseholdAdministrator);
        Assert.True((await _profiles.GetByIdAsync(await DefaultProfileAsync(pat)))!.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task DeletingTheLastHouseholdAdministratorsUserOfTheDefaultHousehold_NeverRemovesTheHousehold()
    {
        var admin = await AddServerAdministratorAsync("shy@example.com", "Shy");
        var mom = await AddMainSignInAsync("mom@example.com", "Mom", admin.HouseholdId);
        await _profileAccess.SetHouseholdAdministratorAsync(await DefaultProfileAsync(mom), true, _now);
        await _profileAccess.SetHouseholdAdministratorAsync(await DefaultProfileAsync(admin), false, _now);

        var momProfile = await DefaultProfileAsync(mom);

        var plan = await _accounts.PlanUserRemovalAsync(mom.Id);
        var outcome = await _accounts.DeleteUserAsync(mom.Id);

        Assert.True(plan.HouseholdIsDefault);
        Assert.True(plan.ProfileIsHouseholdAdministrator);
        Assert.False(plan.RemovesHousehold);
        Assert.Null(outcome.RemovedHouseholdId);
        Assert.NotNull(await _profiles.GetByIdAsync(momProfile));
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM profiles WHERE household_id=@household", new { household = admin.HouseholdId }));
    }

    // ---- The last household administrator's user takes the household (D6) ----

    [Fact]
    public async Task DeletingTheLastHouseholdAdministratorsUser_OfANonDefaultHousehold_RemovesTheHouseholdAndEveryProfileInIt()
    {
        var admin = await AddServerAdministratorAsync("shy@example.com", "Shy");
        var pat = await AddMainSignInAsync("pat@example.com", "Pat");
        var sam = await AddMainSignInAsync("sam@example.com", "Sam", pat.HouseholdId);
        var mary = NewProfile("Mary");
        await _accounts.CreateHouseholdPersonAsync(mary, pat.HouseholdId!.Value, [Grant(pat.Id, mary.Id)]);
        var patProfile = await DefaultProfileAsync(pat);
        var samProfile = await DefaultProfileAsync(sam);
        var asset = AddAsset();
        var gone = new[] { Behind(patProfile, asset), Behind(samProfile, asset), Behind(mary.Id, asset) };
        var adminProfile = await DefaultProfileAsync(admin);
        var kept = Behind(adminProfile, asset);

        var plan = await _accounts.PlanUserRemovalAsync(pat.Id);
        var outcome = await _accounts.DeleteUserAsync(pat.Id, plan.HouseholdProfiles.Select(profile => profile.Id).ToArray());

        Assert.True(plan.RemovesHousehold);
        Assert.False(plan.HouseholdIsDefault);
        Assert.Equal(["Mary", "Pat", "Sam"], plan.HouseholdProfiles.Select(profile => profile.DisplayName).Order().ToArray());
        Assert.Equal(pat.HouseholdId, outcome.RemovedHouseholdId);
        Assert.Equal(3, outcome.DeletedProfileIds.Count);
        Assert.Equal(2, outcome.DeletedUserIds.Count);
        Assert.Null(await _accounts.GetByIdAsync(pat.Id));
        Assert.Null(await _accounts.GetByIdAsync(sam.Id));
        foreach (var id in new[] { patProfile, samProfile, mary.Id })
        {
            Assert.Null(await _profiles.GetByIdAsync(id));
        }

        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM households WHERE id=@household", new { household = pat.HouseholdId }));
        Assert.Equal(0, PersonalRowsFor(patProfile, gone[0]) + PersonalRowsFor(samProfile, gone[1]) + PersonalRowsFor(mary.Id, gone[2]));
        // The Library's own household is untouched.
        Assert.NotNull(await _accounts.GetByIdAsync(admin.Id));
        Assert.Equal(PersonalRowCount, PersonalRowsFor(adminProfile, kept));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM households WHERE id=@household", new { household = admin.HouseholdId }));
    }

    [Fact]
    public async Task ARemovedHouseholdsEmptySharedLibraryGoesWithIt()
    {
        await AddServerAdministratorAsync("shy@example.com", "Shy");
        var pat = await AddMainSignInAsync("pat@example.com", "Pat");
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(pat.HouseholdId!.Value);

        await _accounts.DeleteUserAsync(pat.Id, await ConfirmedAsync(pat.Id));

        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM view_shared_library WHERE library_id=@library", new { library = shared.LibraryId }));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM households WHERE id=@household", new { household = pat.HouseholdId }));
    }

    [Fact]
    public async Task ARemovedHouseholdThatStillHoldsSharedPhotos_IsRefusedAndNothingIsDeleted()
    {
        await AddServerAdministratorAsync("shy@example.com", "Shy");
        var pat = await AddMainSignInAsync("pat@example.com", "Pat");
        var patProfile = await DefaultProfileAsync(pat);
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(pat.HouseholdId!.Value);
        Execute("""
            INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
            VALUES (@id, 'shared', @library, 'image', 'beach.jpg', 'image/jpeg', @now, @now);
            """, new { id = Guid.NewGuid(), library = shared.LibraryId, now = _now.ToString("O") });

        var plan = await _accounts.PlanUserRemovalAsync(pat.Id);
        var confirmed = await ConfirmedAsync(pat.Id);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteUserAsync(pat.Id, confirmed));

        Assert.True(plan.HouseholdHoldsSharedItems);
        Assert.Equal(UserRemovalMessages.HouseholdHoldsSharedItems, refused.Message);
        Assert.NotNull(await _accounts.GetByIdAsync(pat.Id));
        Assert.NotNull(await _profiles.GetByIdAsync(patProfile));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM households WHERE id=@household", new { household = pat.HouseholdId }));
    }

    [Fact]
    public async Task DeletingAUser_IsRefusedAndChangesNothing_WhenWhatWouldGoDiffersFromTheConfirmedList()
    {
        await AddServerAdministratorAsync("shy@example.com", "Shy");
        var pat = await AddMainSignInAsync("pat@example.com", "Pat");
        var sam = await AddMainSignInAsync("sam@example.com", "Sam", pat.HouseholdId);
        var patProfile = await DefaultProfileAsync(pat);
        var samProfile = await DefaultProfileAsync(sam);

        // Confirmed without Sam (added after the list was shown), with someone who is not in the household, and as "nothing".
        var missing = await Assert.ThrowsAsync<AccountRemovalChangedException>(() => _accounts.DeleteUserAsync(pat.Id, [patProfile]));
        var extra = await Assert.ThrowsAsync<AccountRemovalChangedException>(
            () => _accounts.DeleteUserAsync(pat.Id, [patProfile, samProfile, Guid.NewGuid()]));
        var none = await Assert.ThrowsAsync<AccountRemovalChangedException>(() => _accounts.DeleteUserAsync(pat.Id, []));

        Assert.Equal("Something changed since you looked. Check the list and try again.", missing.Message);
        Assert.Equal(missing.Message, extra.Message);
        Assert.Equal(missing.Message, none.Message);
        Assert.NotNull(await _accounts.GetByIdAsync(pat.Id));
        Assert.NotNull(await _profiles.GetByIdAsync(patProfile));
        Assert.NotNull(await _profiles.GetByIdAsync(samProfile));

        // The exact list, in any order, deletes the household.
        var outcome = await _accounts.DeleteUserAsync(pat.Id, [samProfile, patProfile]);

        Assert.Equal(2, outcome.DeletedProfileIds.Count);
        Assert.Null(await _profiles.GetByIdAsync(samProfile));
    }

    [Fact]
    public async Task DeletingAUserWhoseDeleteOnlyRemovesTheSignIn_ConfirmedWithAProfileList_IsRefused()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var sam = await AddMainSignInAsync("sam@example.com", "Sam", owner.HouseholdId);
        var samProfile = await DefaultProfileAsync(sam);

        await Assert.ThrowsAsync<AccountRemovalChangedException>(() => _accounts.DeleteUserAsync(sam.Id, [samProfile]));
        Assert.NotNull(await _accounts.GetByIdAsync(sam.Id));

        // Sam's profile is not part of a sign-in-only delete, so the confirmed list is empty.
        await _accounts.DeleteUserAsync(sam.Id, []);
        Assert.Null(await _accounts.GetByIdAsync(sam.Id));
        Assert.NotNull(await _profiles.GetByIdAsync(samProfile));
    }

    // ---- Never ----

    [Fact]
    public async Task DeletingTheUserOfTheOwnerProfile_KeepsTheOwnerProfile_AndItLosesTheAdministratorRole()
    {
        var admin = NewAccount("admin@example.com", administrator: true);
        await _accounts.CreateAccountAsync(admin, Grant(admin.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>());
        var other = await AddMainSignInAsync("other@example.com", "Other", admin.HouseholdId);
        AddPassword(admin.Id);
        AddPassword(other.Id);
        await _profileAccess.SetServerAdministratorAsync(await DefaultProfileAsync(other), true, _now);

        await _accounts.DeleteUserAsync(admin.Id);

        Assert.Null(await _accounts.GetByIdAsync(admin.Id));
        var owner = (await _profiles.GetByIdAsync(Profile.SeedProfileId))!;
        Assert.False(owner.IsServerAdministrator);
    }

    [Fact]
    public async Task DeletingTheLastServerAdministratorsUser_StopsAtTheDataStoresBackstop()
    {
        var admin = NewAccount("admin@example.com", administrator: true);
        var profile = NewProfile("Admin");
        await _accounts.CreateAccountAsync(admin, Grant(admin.Id, profile.Id, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>(), newProfile: profile);
        AddPassword(admin.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteUserAsync(admin.Id));

        Assert.NotNull(await _profiles.GetByIdAsync(profile.Id));
        Assert.NotNull(await _accounts.GetByIdAsync(admin.Id));
    }

    [Fact]
    public async Task DeletingAUserThatDoesNotExist_SaysSo()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _accounts.DeleteUserAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _accounts.PlanUserRemovalAsync(Guid.NewGuid()));
    }

    // ---- Deleting a profile takes its user too (D7) ----

    [Fact]
    public async Task DeletingAProfile_AlsoDeletesItsUser_AndEverythingPersonal()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var sam = await AddMainSignInAsync("sam@example.com", "Sam", owner.HouseholdId);
        var samProfile = await DefaultProfileAsync(sam);
        var asset = AddAsset();
        var left = Behind(samProfile, asset);
        var keep = Behind(await DefaultProfileAsync(owner), asset);

        var outcome = await _accounts.DeleteManagedProfileAsync(samProfile);

        Assert.Equal(sam.Id, outcome.DeletedUserId);
        Assert.Null(await _accounts.GetByIdAsync(sam.Id));
        Assert.Null(await _profiles.GetByIdAsync(samProfile));
        Assert.Equal(0, PersonalRowsFor(samProfile, left));
        Assert.Equal(PersonalRowCount, PersonalRowsFor(await DefaultProfileAsync(owner), keep));
    }

    [Fact]
    public async Task DeletingAProfileWithoutAUser_ReportsNoUserRemoved()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var mary = NewProfile("Mary");
        await _accounts.CreateHouseholdPersonAsync(mary, owner.HouseholdId!.Value, [Grant(owner.Id, mary.Id)]);

        var outcome = await _accounts.DeleteManagedProfileAsync(mary.Id);

        Assert.Null(outcome.DeletedUserId);
        Assert.Null(await _profiles.GetByIdAsync(mary.Id));
    }

    [Fact]
    public async Task TheLastHouseholdAdministratorProfile_CannotBeDeleted_AndAnotherOneCan()
    {
        var alex = await AddMainSignInAsync("alex@example.com", "Alex");
        var pat = await AddMainSignInAsync("pat@example.com", "Pat", alex.HouseholdId);
        var alexProfile = await DefaultProfileAsync(alex);
        var patProfile = await DefaultProfileAsync(pat);

        var validation = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.ValidateManagedProfileRemovalAsync(alexProfile));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteManagedProfileAsync(alexProfile));
        Assert.Equal(UserRemovalMessages.LastHouseholdAdministrator, refused.Message);
        Assert.Equal(refused.Message, validation.Message);
        Assert.NotNull(await _profiles.GetByIdAsync(alexProfile));
        Assert.NotNull(await _accounts.GetByIdAsync(alex.Id));

        // With a second household administrator, either one may go (they delete each other's profiles).
        await _profileAccess.SetHouseholdAdministratorAsync(patProfile, true, _now);
        await _accounts.DeleteManagedProfileAsync(alexProfile);
        Assert.Null(await _profiles.GetByIdAsync(alexProfile));
        Assert.Null(await _accounts.GetByIdAsync(alex.Id));
        Assert.NotNull(await _profiles.GetByIdAsync(patProfile));
    }

    // ---- Passwords before a second household (D10) ----

    [Fact]
    public async Task ServerAdministratorsAreSecured_OnlyOnceEachHasAnEmailAndAPasswordOrPasskey()
    {
        Assert.True(await _accounts.AreServerAdministratorsSecuredAsync());

        var admin = NewAccount("admin@example.com", administrator: true);
        var profile = NewProfile("Admin");
        await _accounts.CreateAccountAsync(admin, Grant(admin.Id, profile.Id, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>(), newProfile: profile);
        Assert.False(await _accounts.AreServerAdministratorsSecuredAsync());
        Assert.False(await _accounts.IsUserSecuredAsync(admin.Id));

        AddPassword(admin.Id);
        Assert.True(await _accounts.AreServerAdministratorsSecuredAsync());
        Assert.True(await _accounts.IsUserSecuredAsync(admin.Id));

        // A second server administrator without a password makes the Library unsecured again.
        var second = await AddMainSignInAsync("second@example.com", "Second", admin.HouseholdId);
        await _profileAccess.SetServerAdministratorAsync(await DefaultProfileAsync(second), true, _now);
        Assert.False(await _accounts.AreServerAdministratorsSecuredAsync());
        AddPassword(second.Id);
        Assert.True(await _accounts.AreServerAdministratorsSecuredAsync());
    }

    [Fact]
    public async Task AServerAdministratorWithoutAnEmail_IsNotSecured_EvenWithAPassword()
    {
        var admin = NewAccount("admin@example.com", administrator: true);
        admin.Email = null;
        admin.NormalizedEmail = null;
        var profile = NewProfile("Admin");
        await _accounts.CreateAccountAsync(admin, Grant(admin.Id, profile.Id, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>(), newProfile: profile);
        AddPassword(admin.Id);

        Assert.False(await _accounts.AreServerAdministratorsSecuredAsync());
        Assert.False(await _accounts.IsUserSecuredAsync(admin.Id));
    }

    // ---- Nothing personal is left behind ----

    [Fact]
    public async Task DeletingAProfile_RemovesItsPersonalRows_ButKeepsAuditHistoryAndTheStorageLabel()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var kids = NewProfile("Kids");
        await _accounts.CreateHouseholdPersonAsync(kids, owner.HouseholdId!.Value, [Grant(owner.Id, kids.Id)]);
        var bystander = await DefaultProfileAsync(owner);
        var asset = AddAsset();
        var left = Behind(kids.Id, asset);
        var other = Behind(bystander, asset);
        Execute("INSERT INTO application_events(event_id,event_type,version,occurred_at,server_id,subject_type,subject_id,profile_id,payload_json) VALUES(@id,'x',1,@now,'s','profile','p',@profile,'{}')",
            new { id = Guid.NewGuid(), now = _now.ToString("O"), profile = kids.Id });
        Execute("INSERT INTO approval_records(id,item_kind,item_id,statement_hash,statement_json,approved_by_profile_id,approved_by_name,approved_at) VALUES(@id,'plugin','p','h','{}',@profile,'Kids',@now)",
            new { id = Guid.NewGuid(), now = _now.ToString("O"), profile = kids.Id });
        Execute("INSERT INTO identity_audit_events(profile_id,event_type,succeeded,occurred_at) VALUES(@profile,'sign_in',1,@now)",
            new { now = _now.ToString("O"), profile = kids.Id });
        await _accounts.WriteAuthorizationAuditAsync(new AuthorizationAuditEvent("profile.created", _now, owner.Id, kids.Id, null,
            "profile", kids.Id.ToString("D"), new Dictionary<string, string?> { ["changed"] = "true" }));
        var spaceId = Guid.NewGuid();
        Execute("INSERT INTO view_storage_labels(personal_space_id,label) VALUES(@space,'From Kids')", new { space = spaceId });
        Execute("INSERT INTO system_activity(action_type,profile_id,detail) VALUES('MetadataManualOverride',@profile,'edited a title')", new { profile = kids.Id });

        await _accounts.DeleteManagedProfileAsync(kids.Id);

        Assert.Equal(0, PersonalRowsFor(kids.Id, left));
        Assert.Equal(PersonalRowCount, PersonalRowsFor(bystander, other));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM application_events"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM approval_records"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM identity_audit_events"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM authorization_audit_events WHERE event_type='profile.created'"));
        Assert.Equal("From Kids", Text("SELECT label FROM view_storage_labels"));
        // The library activity log keeps who did what; only the person's own Trash entries go.
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM system_activity WHERE action_type='MetadataManualOverride'"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM system_activity WHERE action_type=@type", new { type = SystemActionType.PersonalTrashEmptied }));
    }

    [Fact]
    public async Task DeletingAProfile_KeepsEncodesAndDownloadsNobodyOwns_AndOtherPeoplesRows()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var kids = NewProfile("Kids");
        await _accounts.CreateHouseholdPersonAsync(kids, owner.HouseholdId!.Value, [Grant(owner.Id, kids.Id)]);
        var asset = AddAsset();
        Execute("INSERT INTO encode_jobs(id,asset_id,owner_profile_id,profile_key,source_hash,status,created_at) VALUES(@id,@asset,NULL,'k','h','complete',@now)",
            new { id = Guid.NewGuid(), asset, now = _now.ToString("O") });
        Execute("INSERT INTO offline_variants(id,asset_id,owner_profile_id,profile_key,source_hash,display_name,status,created_at) VALUES(@id,@asset,NULL,'k','h','Shared','ready',@now)",
            new { id = Guid.NewGuid(), asset, now = _now.ToString("O") });
        Behind(kids.Id, asset);

        await _accounts.DeleteManagedProfileAsync(kids.Id);

        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM encode_jobs"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM offline_variants"));
    }

    [Fact]
    public async Task EveryTableWithAProfileColumnAndNoCascade_IsEitherClearedWithTheProfileOrKeptOnPurpose()
    {
        // Cleared by hand in AccountRepository.DeleteProfileRows.
        var cleared = new HashSet<string>(StringComparer.Ordinal)
        {
            "consumption_history.profile_id", "personal_status_commands.profile_id", "waiting_personal_state.profile_id",
            "system_activity.profile_id", "encode_jobs.owner_profile_id", "offline_variants.owner_profile_id",
            "user_states.user_id", "user_taste_profiles.user_id", "reader_bookmarks.user_id", "reader_highlights.user_id",
            "reader_statistics.user_id",
        };
        // Kept on purpose: audit and approval history.
        var kept = new HashSet<string>(StringComparer.Ordinal)
        {
            "application_events.profile_id", "approval_records.approved_by_profile_id",
        };
        var unhandled = new List<string>();
        using var connection = _database.CreateConnection();
        foreach (var table in connection.Query<string>("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").ToList())
        {
            var cascading = connection.Query("SELECT \"from\" AS FromColumn, on_delete AS OnDelete FROM pragma_foreign_key_list(@table)", new { table })
                .Where(row => row.OnDelete is "CASCADE" or "SET NULL")
                .Select(row => (string)row.FromColumn)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var column in connection.Query<string>("SELECT name FROM pragma_table_info(@table)", new { table }))
            {
                var isProfileColumn = column.EndsWith("profile_id", StringComparison.Ordinal) || column == "user_id";
                var key = $"{table}.{column}";
                if (isProfileColumn && !cascading.Contains(column) && !cleared.Contains(key) && !kept.Contains(key))
                {
                    unhandled.Add(key);
                }
            }
        }

        // A new table that points at a profile without a cascade must be decided in DeleteProfileRows, then listed here.
        Assert.Empty(unhandled);
    }

    // ---- Helpers ----

    private const int PersonalRowCount = 11;

    /// <summary>One row of every kind of personal row a profile can leave behind, for the given profile. Returns the reader key.</summary>
    private string Behind(Guid profile, Guid asset)
    {
        var now = _now.ToString("O");
        var text = profile.ToString("D");
        Execute("INSERT INTO consumption_history(id,profile_id,asset_id,session_key,experience,started_at,updated_at,progress_pct) VALUES(@id,@profile,@asset,'s','watch',@now,@now,10)",
            new { id = Guid.NewGuid(), profile, asset, now });
        Execute("INSERT INTO personal_status_commands(id,profile_id,target_id,media_type,command,changed_at,snapshot_json) VALUES(@id,@profile,@asset,'Movies','watched',@now,'{}')",
            new { id = Guid.NewGuid(), profile, asset, now });
        var restore = Guid.NewGuid();
        Execute("INSERT INTO backup_restores(id,restored_at,backup_file_name,applied_rows,waiting_rows) VALUES(@restore,@now,'b.zip',0,1)", new { restore, now });
        Execute("INSERT INTO waiting_personal_state(id,restore_id,group_kind,table_name,profile_id,row_json,titles_json,created_at) VALUES(@id,@restore,'personal','user_states',@profile,'{}','{}',@now)",
            new { id = Guid.NewGuid(), restore, profile, now });
        Execute("INSERT INTO system_activity(action_type,profile_id,detail) VALUES(@type,@profile,'3 items')", new { type = SystemActionType.PersonalTrashEmptied, profile });
        Execute("INSERT INTO encode_jobs(id,asset_id,owner_profile_id,profile_key,source_hash,status,created_at) VALUES(@id,@asset,@profile,'k','h','complete',@now)",
            new { id = Guid.NewGuid(), asset, profile, now });
        Execute("INSERT INTO offline_variants(id,asset_id,owner_profile_id,profile_key,source_hash,display_name,status,created_at) VALUES(@id,@asset,@profile,'k','h','Mine','ready',@now)",
            new { id = Guid.NewGuid(), asset, profile, now });
        Execute("INSERT INTO user_states(user_id,asset_id,progress_pct) VALUES(@profile,@asset,5)", new { profile, asset });
        Execute("INSERT INTO user_taste_profiles(user_id,profile_json) VALUES(@profile,'{}')", new { profile });
        Execute("INSERT INTO reader_bookmarks(id,user_id,asset_id,chapter_index) VALUES(@id,@text,@asset,1)", new { id = Guid.NewGuid(), text, asset });
        Execute("INSERT INTO reader_highlights(id,user_id,asset_id,chapter_index,start_offset,end_offset,selected_text) VALUES(@id,@text,@asset,1,0,1,'x')", new { id = Guid.NewGuid(), text, asset });
        Execute("INSERT INTO reader_statistics(id,user_id,asset_id) VALUES(@id,@text,@asset)", new { id = Guid.NewGuid(), text, asset });
        return text;
    }

    /// <summary>How many of the rows <see cref="Behind"/> wrote still exist for this profile (a stand-in count of the 12 inserts, with reader rows counted once).</summary>
    private int PersonalRowsFor(Guid profile, string text)
    {
        var p = new { profile, text, type = SystemActionType.PersonalTrashEmptied };
        return Scalar("SELECT COUNT(*) FROM consumption_history WHERE profile_id=@profile", p)
            + Scalar("SELECT COUNT(*) FROM personal_status_commands WHERE profile_id=@profile", p)
            + Scalar("SELECT COUNT(*) FROM waiting_personal_state WHERE profile_id=@profile", p)
            + Scalar("SELECT COUNT(*) FROM system_activity WHERE profile_id=@profile AND action_type=@type", p)
            + Scalar("SELECT COUNT(*) FROM encode_jobs WHERE owner_profile_id=@profile", p)
            + Scalar("SELECT COUNT(*) FROM offline_variants WHERE owner_profile_id=@profile", p)
            + Scalar("SELECT COUNT(*) FROM user_states WHERE user_id=@profile", p)
            + Scalar("SELECT COUNT(*) FROM user_taste_profiles WHERE user_id=@profile", p)
            + Scalar("SELECT COUNT(*) FROM reader_bookmarks WHERE user_id=@text", p)
            + Scalar("SELECT COUNT(*) FROM reader_highlights WHERE user_id=@text", p)
            + Scalar("SELECT COUNT(*) FROM reader_statistics WHERE user_id=@text", p);
    }

    private Guid AddAsset()
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        Execute("INSERT INTO works(id,media_type) VALUES(@work,'Movies')", new { work });
        Execute("INSERT INTO editions(id,work_id,format_label) VALUES(@edition,@work,'4K')", new { edition, work });
        Execute("INSERT INTO media_assets(id,edition_id,content_hash,file_path_root) VALUES(@asset,@edition,@hash,@path)",
            new { asset, edition, hash = Guid.NewGuid().ToString("N"), path = "/media/x.mkv" });
        return asset;
    }

    /// <summary>A main sign-in with its own new default profile. The first one in a household owns it.</summary>
    private async Task<Account> AddMainSignInAsync(string email, string profileName, Guid? householdId = null)
    {
        var account = NewAccount(email);
        account.HouseholdId = householdId;
        var profile = NewProfile(profileName);
        await _accounts.CreateAccountAsync(account, Grant(account.Id, profile.Id, isDefault: true),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>(), newProfile: profile);
        return (await _accounts.GetByIdAsync(account.Id))!;
    }

    /// <summary>A server administrator with a password: its own profile in the Library's own (default) household.</summary>
    private async Task<Account> AddServerAdministratorAsync(string email, string profileName)
    {
        var account = NewAccount(email, administrator: true);
        var profile = NewProfile(profileName);
        await _accounts.CreateAccountAsync(account, Grant(account.Id, profile.Id, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>(), newProfile: profile);
        AddPassword(account.Id);
        return (await _accounts.GetByIdAsync(account.Id))!;
    }

    private void AddPassword(Guid accountId) => Execute(
        "INSERT INTO account_credentials(id,account_id,credential_kind,secret_hash,hash_scheme,hash_version,security_stamp,created_at,updated_at) VALUES(@id,@accountId,'Password','h','pbkdf2',1,'s',@now,@now)",
        new { id = Guid.NewGuid(), accountId, now = _now.ToString("O") });

    /// <summary>The profiles a household removal would take, as the confirmation lists them.</summary>
    private async Task<Guid[]> ConfirmedAsync(Guid userId) =>
        (await _accounts.PlanUserRemovalAsync(userId)).HouseholdProfiles.Select(profile => profile.Id).ToArray();

    private async Task<Guid> DefaultProfileAsync(Account account) => (await _accounts.GetDefaultProfileIdAsync(account.Id))!.Value;

    private Account NewAccount(string email, bool administrator = false) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        IsEnabled = true,
        IsAdministrator = administrator,
        AuthorizationVersion = 1,
        CreatedAt = _now.AddSeconds(++_counter),
        UpdatedAt = _now,
    };

    private Profile NewProfile(string name) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = name,
        AvatarColor = "#7C4DFF",
        Role = ProfileRole.StandardUser,
        CreatedAt = _now,
    };

    private AccountProfileGrant Grant(Guid accountId, Guid profileId, bool isDefault = false, bool administrator = false) => new()
    {
        AccountId = accountId,
        ProfileId = profileId,
        IsDefault = isDefault,
        IsEnabled = true,
        AdminEnabled = administrator,
        AuthorizationVersion = 1,
        GrantedAt = _now.AddSeconds(++_counter),
    };

    private void Execute(string sql, object parameters)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(sql, parameters);
    }

    private int Scalar(string sql, object? parameters = null)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>(sql, parameters);
    }

    private string Text(string sql)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<string>(sql) ?? string.Empty;
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            // Best-effort cleanup of the temporary data store; a locked file must not fail the test run.
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
