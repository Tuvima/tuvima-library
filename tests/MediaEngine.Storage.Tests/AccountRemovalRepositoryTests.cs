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
/// Deleting a user takes their own profiles and everything personal those profiles left behind; household people with no
/// sign-in of their own stay; deleting a profile leaves nothing personal behind either.
/// </summary>
public sealed class AccountRemovalRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ProfileRepository _profiles;
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
    }

    // ---- Which profiles go with a user ----

    [Fact]
    public async Task DeletingAUser_DeletesTheProfileTheySignInAs_EvenWhenAnotherSignInCanAlsoOpenIt()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var sam = await AddMainSignInAsync("sam@example.com", "Sam", owner.HouseholdId);
        var samProfile = await DefaultProfileAsync(sam);
        await _accounts.UpsertGrantAsync(Grant(owner.Id, samProfile));

        var outcome = await _accounts.DeleteAccountWithOwnProfilesAsync(sam.Id);

        Assert.Equal([samProfile], outcome.DeletedProfileIds);
        Assert.Null(await _accounts.GetByIdAsync(sam.Id));
        Assert.Null(await _profiles.GetByIdAsync(samProfile));
        Assert.Null(await _accounts.GetGrantAsync(owner.Id, samProfile));
        Assert.Equal([owner.Id], outcome.AffectedAccountIds);
        Assert.NotNull(await _profiles.GetByIdAsync(await DefaultProfileAsync(owner)));
    }

    [Fact]
    public async Task DeletingAUser_AlsoDeletesProfilesOnlyTheyCanOpen_ButNotOnesAnotherSignInCanOpen()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var sam = await AddMainSignInAsync("sam@example.com", "Sam", owner.HouseholdId);
        var kids = NewProfile("Sam (Kids)");
        await _accounts.CreateManagedProfileAsync(kids, Grant(sam.Id, kids.Id));
        var shared = NewProfile("Games night");
        await _accounts.CreateManagedProfileAsync(shared, Grant(sam.Id, shared.Id));
        await _accounts.UpsertGrantAsync(Grant(owner.Id, shared.Id));

        var plan = await _accounts.PlanAccountRemovalAsync(sam.Id);
        await _accounts.DeleteAccountWithOwnProfilesAsync(sam.Id);

        Assert.Equal(["Sam", "Sam (Kids)"], plan.OwnProfiles.Select(profile => profile.DisplayName).Order().ToArray());
        Assert.Null(await _profiles.GetByIdAsync(kids.Id));
        Assert.NotNull(await _profiles.GetByIdAsync(shared.Id));
        Assert.NotNull(await _accounts.GetGrantAsync(owner.Id, shared.Id));
    }

    [Fact]
    public async Task DeletingAUser_KeepsHouseholdPeopleWithoutASignIn_WhileAnotherMainSignInCanOpenThem()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var partner = await AddMainSignInAsync("pat@example.com", "Pat", owner.HouseholdId);
        var mary = NewProfile("Mary");
        await _accounts.CreateHouseholdPersonAsync(mary, owner.HouseholdId!.Value,
            [Grant(owner.Id, mary.Id), Grant(partner.Id, mary.Id)]);

        var outcome = await _accounts.DeleteAccountWithOwnProfilesAsync(partner.Id);

        Assert.DoesNotContain(mary.Id, outcome.DeletedProfileIds);
        Assert.NotNull(await _profiles.GetByIdAsync(mary.Id));
        Assert.NotNull(await _accounts.GetGrantAsync(owner.Id, mary.Id));
        Assert.Null(await _accounts.GetGrantAsync(partner.Id, mary.Id));
    }

    [Fact]
    public async Task DeletingAPersonsOwnSignIn_ThroughDeleteUser_DeletesThePerson_ButRemovingItKeepsThem()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var mary = NewProfile("Mary");
        await _accounts.CreateHouseholdPersonAsync(mary, owner.HouseholdId!.Value, [Grant(owner.Id, mary.Id)]);
        var own = await AddOwnSignInAsync("mary@example.com", mary.Id, owner);
        var carl = NewProfile("Carl");
        await _accounts.CreateHouseholdPersonAsync(carl, owner.HouseholdId.Value, [Grant(owner.Id, carl.Id)]);
        var carlOwn = await AddOwnSignInAsync("carl@example.com", carl.Id, owner);

        // The sign-in alone goes (what "Remove own sign-in" does): the person stays.
        await _accounts.DeleteAccountAsync(carlOwn.Id);
        Assert.NotNull(await _profiles.GetByIdAsync(carl.Id));

        // Through Delete user the person and their profile go with the sign-in.
        var outcome = await _accounts.DeleteAccountWithOwnProfilesAsync(own.Id);

        Assert.Equal([mary.Id], outcome.DeletedProfileIds);
        Assert.Null(await _profiles.GetByIdAsync(mary.Id));
        Assert.Null(await _accounts.GetByIdAsync(own.Id));
        Assert.NotNull(await _accounts.GetByIdAsync(owner.Id));
    }

    [Fact]
    public async Task DeletingAUser_IsRefused_WhenAnotherSignInCouldOnlyOpenAProfileThatWouldGo_NamingThem()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var sam = await AddMainSignInAsync("sam@example.com", "Sam", owner.HouseholdId);
        var samProfile = await DefaultProfileAsync(sam);
        var mia = await AddMainSignInAsync("mia@example.com", "Mia", owner.HouseholdId);
        var miaProfile = await DefaultProfileAsync(mia);
        // Mia is left able to open only Sam's profile, which is also her default.
        await _accounts.UpsertGrantAsync(Grant(mia.Id, samProfile, isDefault: true));
        Execute("DELETE FROM account_profile_grants WHERE account_id=@mia AND profile_id=@profile",
            new { mia = mia.Id, profile = miaProfile });

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteAccountWithOwnProfilesAsync(sam.Id));
        var planRefused = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.PlanAccountRemovalAsync(sam.Id));

        Assert.Equal("mia@example.com can only open this profile. Give mia@example.com another profile first.", refused.Message);
        Assert.Equal(refused.Message, planRefused.Message);
        Assert.NotNull(await _accounts.GetByIdAsync(sam.Id));
        Assert.NotNull(await _profiles.GetByIdAsync(samProfile));
    }

    [Fact]
    public async Task DeletingAUser_RepairsTheDefaultOfAnotherSignInThatSharedADeletedProfile()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var sam = await AddMainSignInAsync("sam@example.com", "Sam", owner.HouseholdId);
        var samProfile = await DefaultProfileAsync(sam);
        var ownerProfile = await DefaultProfileAsync(owner);
        // Alex also signs in as Sam (default), and keeps his own profile as a second one.
        await _accounts.UpsertGrantAsync(Grant(owner.Id, samProfile, isDefault: true));

        await _accounts.DeleteAccountWithOwnProfilesAsync(sam.Id);

        var grants = await _accounts.GetGrantsAsync(owner.Id);
        var remaining = Assert.Single(grants);
        Assert.Equal(ownerProfile, remaining.ProfileId);
        Assert.True(remaining.IsDefault);
        Assert.True(remaining.IsEnabled);
    }

    [Fact]
    public async Task DeletingAUser_IsRefused_WhenTheirProfileIsTheOwnerProfile()
    {
        var admin = NewAccount("admin@example.com", administrator: true);
        await _accounts.CreateAccountAsync(admin, Grant(admin.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>());
        var other = await AddMainSignInAsync("other@example.com", "Other", admin.HouseholdId);
        Execute("INSERT INTO account_credentials(id,account_id,credential_kind,secret_hash,hash_scheme,hash_version,security_stamp,created_at,updated_at) VALUES(@id,@admin,'Password','h','pbkdf2',1,'s',@now,@now)",
            new { id = Guid.NewGuid(), admin = other.Id, now = _now.ToString("O") });
        Execute("UPDATE accounts SET is_administrator=1 WHERE id=@other", new { other = other.Id });
        Execute("UPDATE account_profile_grants SET admin_enabled=1 WHERE account_id=@other", new { other = other.Id });
        Execute("INSERT INTO account_credentials(id,account_id,credential_kind,secret_hash,hash_scheme,hash_version,security_stamp,created_at,updated_at) VALUES(@id,@admin,'Password','h','pbkdf2',1,'s',@now,@now)",
            new { id = Guid.NewGuid(), admin = admin.Id, now = _now.ToString("O") });

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteAccountWithOwnProfilesAsync(admin.Id));

        Assert.Contains("Owner profile", refused.Message, StringComparison.Ordinal);
        Assert.NotNull(await _profiles.GetByIdAsync(Profile.SeedProfileId));
        Assert.NotNull(await _accounts.GetByIdAsync(admin.Id));
    }

    [Fact]
    public async Task DeletingAUser_WhoIsTheLastAdministrator_StillStopsAtTheDataStoresBackstop()
    {
        var admin = NewAccount("admin@example.com", administrator: true);
        var profile = NewProfile("Admin");
        await _accounts.CreateAccountAsync(admin, Grant(admin.Id, profile.Id, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>(), newProfile: profile);
        Execute("INSERT INTO account_credentials(id,account_id,credential_kind,secret_hash,hash_scheme,hash_version,security_stamp,created_at,updated_at) VALUES(@id,@admin,'Password','h','pbkdf2',1,'s',@now,@now)",
            new { id = Guid.NewGuid(), admin = admin.Id, now = _now.ToString("O") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteAccountWithOwnProfilesAsync(admin.Id));

        Assert.NotNull(await _profiles.GetByIdAsync(profile.Id));
        Assert.NotNull(await _accounts.GetByIdAsync(admin.Id));
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
    public async Task DeletingAUser_RemovesEverythingPersonalTheirProfilesLeftBehind_InOneStep()
    {
        var owner = await AddMainSignInAsync("alex@example.com", "Alex");
        var sam = await AddMainSignInAsync("sam@example.com", "Sam", owner.HouseholdId);
        var samProfile = await DefaultProfileAsync(sam);
        var asset = AddAsset();
        var left = Behind(samProfile, asset);
        var keep = Behind(await DefaultProfileAsync(owner), asset);

        await _accounts.DeleteAccountWithOwnProfilesAsync(sam.Id);

        Assert.Equal(0, PersonalRowsFor(samProfile, left));
        Assert.Equal(PersonalRowCount, PersonalRowsFor(await DefaultProfileAsync(owner), keep));
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

    /// <summary>A person's own sign-in: it follows <paramref name="main"/> and opens exactly that person.</summary>
    private async Task<Account> AddOwnSignInAsync(string email, Guid profileId, Account main)
    {
        var account = NewAccount(email);
        account.HouseholdId = main.HouseholdId;
        account.GrantsInheritFromAccountId = main.Id;
        await _accounts.CreateAccountAsync(account, Grant(account.Id, profileId, isDefault: true),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>());
        return (await _accounts.GetByIdAsync(account.Id))!;
    }

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
