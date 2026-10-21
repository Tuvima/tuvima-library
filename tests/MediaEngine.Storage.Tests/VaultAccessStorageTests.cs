using Dapper;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

// The Vault's data structure (Videos spec WP9): which libraries are the Vault, who may open it, how long a device session stays
// unlocked, and the Vault's own names and artwork. Rules that must never be missed live in the data structure itself.
public sealed class VaultAccessStorageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_vault_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly VaultLibraryRepository _libraries;
    private readonly NsfwAccessRepository _nsfw;
    private readonly VaultNameRepository _names;
    private readonly VaultArtworkRepository _artwork;
    private readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _adult = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    public VaultAccessStorageTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _libraries = new VaultLibraryRepository(_database);
        _nsfw = new NsfwAccessRepository(_database);
        _names = new VaultNameRepository(_database);
        _artwork = new VaultArtworkRepository(_database);
        InsertProfile(_adult, "StandardUser", null);
        InsertProfile(_admin, "Administrator", null);
    }

    // ── The list of Vault libraries ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ALibraryIsOnlyVaultWhenItIsListed_AndTheListIsTheSingleSourceOfTruth()
    {
        var vault = NewLibrary();
        Assert.False(await _libraries.AnyAsync());
        Assert.Empty(await _libraries.GetIdsAsync());

        await _libraries.AddAsync(vault);

        Assert.True(await _libraries.AnyAsync());
        Assert.Contains(vault.LibraryId, await _libraries.GetIdsAsync());
        Assert.Equal(vault.LibraryId, (await _libraries.GetAsync(vault.LibraryId))!.LibraryId);
        Assert.Null(await _libraries.GetAsync(Guid.NewGuid().ToString("D")));
    }

    [Fact]
    public async Task ALibraryIdMatchesWhateverTheCaseItIsWritten()
    {
        var vault = NewLibrary();
        await _libraries.AddAsync(vault);

        Assert.NotNull(await _libraries.GetAsync(vault.LibraryId.ToUpperInvariant()));
        Assert.Contains(vault.LibraryId, await _libraries.GetIdsAsync());
    }

    [Fact]
    public async Task ALibraryCannotBeMarkedTwice_OrWithAnUnknownLookupSource()
    {
        var vault = NewLibrary();
        await _libraries.AddAsync(vault);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _libraries.AddAsync(vault));
        await Assert.ThrowsAsync<ArgumentException>(() => _libraries.AddAsync(vault with { LibraryId = Guid.NewGuid().ToString("D"), LookupSource = "tmdb" }));
    }

    [Fact]
    public async Task ALibraryThatAlreadyHoldsOrdinaryItems_CannotBecomeAVaultLibrary()
    {
        var library = Guid.NewGuid().ToString("D");
        InsertAsset(library);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _libraries.AddAsync(NewLibrary(library)));

        Assert.False(await _libraries.AnyAsync());
    }

    [Fact]
    public async Task TheLookupSettingsAreStoredPerLibrary_AndAStashBoxLibraryWithNoServerUsesNone()
    {
        var first = NewLibrary();
        var second = NewLibrary();
        await _libraries.AddAsync(first);
        await _libraries.AddAsync(second);

        Assert.True(await _libraries.UpdateLookupAsync(first.LibraryId, VaultLookupSources.StashBox, "server-1", _now));

        var stored = (await _libraries.GetAsync(first.LibraryId))!;
        Assert.Equal(VaultLookupSources.StashBox, stored.EffectiveLookupSource);
        Assert.Equal("server-1", stored.LookupServerId);
        Assert.Equal(VaultLookupSources.None, (await _libraries.GetAsync(second.LibraryId))!.EffectiveLookupSource);

        await _libraries.UpdateLookupAsync(first.LibraryId, VaultLookupSources.StashBox, null, _now);
        Assert.Equal(VaultLookupSources.None, (await _libraries.GetAsync(first.LibraryId))!.EffectiveLookupSource);
        Assert.False(await _libraries.UpdateLookupAsync(Guid.NewGuid().ToString("D"), VaultLookupSources.None, null, _now));
    }

    [Fact]
    public async Task AVaultLibraryWithItemsCannotBeUnmarked_SoItsItemsCanNeverTurnIntoNormalOnes()
    {
        var vault = NewLibrary();
        await _libraries.AddAsync(vault);
        InsertAsset(vault.LibraryId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _libraries.RemoveAsync(vault.LibraryId));
        // Even a statement that skips the repository is refused by the data structure.
        using var connection = _database.CreateConnection();
        Assert.Throws<SqliteException>(() => connection.Execute("DELETE FROM vault_libraries;"));
        Assert.True(await _libraries.AnyAsync());
    }

    [Fact]
    public async Task AnEmptyVaultLibraryCanBeRemoved()
    {
        var vault = NewLibrary();
        await _libraries.AddAsync(vault);

        Assert.True(await _libraries.RemoveAsync(vault.LibraryId));
        Assert.False(await _libraries.RemoveAsync(vault.LibraryId));
        Assert.False(await _libraries.AnyAsync());
    }

    [Fact]
    public async Task IsInVault_IsTrueForAFileAndForItsWork_AndFalseForEverythingElse()
    {
        var vault = NewLibrary();
        var normal = Guid.NewGuid().ToString("D");
        await _libraries.AddAsync(vault);
        var (vaultWork, vaultAsset) = InsertAsset(vault.LibraryId);
        var (normalWork, normalAsset) = InsertAsset(normal);

        Assert.True(await _libraries.IsInVaultAsync(vaultAsset));
        Assert.True(await _libraries.IsInVaultAsync(vaultWork));
        Assert.False(await _libraries.IsInVaultAsync(normalAsset));
        Assert.False(await _libraries.IsInVaultAsync(normalWork));
        Assert.False(await _libraries.IsInVaultAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task AFileCannotMoveIntoOrOutOfAVaultLibrary()
    {
        var vault = NewLibrary();
        var normal = Guid.NewGuid().ToString("D");
        await _libraries.AddAsync(vault);
        var (_, vaultAsset) = InsertAsset(vault.LibraryId);
        var (_, normalAsset) = InsertAsset(normal);
        using var connection = _database.CreateConnection();

        Assert.Throws<SqliteException>(() =>
            connection.Execute("UPDATE media_assets SET library_id=@to WHERE id=@id;", new { to = normal, id = vaultAsset }));
        Assert.Throws<SqliteException>(() =>
            connection.Execute("UPDATE media_assets SET library_id=@to WHERE id=@id;", new { to = vault.LibraryId, id = normalAsset }));
        // Moving between two ordinary libraries is untouched.
        connection.Execute("UPDATE media_assets SET library_id=@to WHERE id=@id;", new { to = Guid.NewGuid().ToString("D"), id = normalAsset });
    }

    // ── Who may open the Vault ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AProfileWithNoSettingIsOff()
    {
        var access = await _nsfw.GetAsync(_adult);

        Assert.Equal(NsfwAccessMode.Off, access.ConfiguredMode);
        Assert.False(access.CanUnlock);
        Assert.False(access.HasPin);
    }

    [Theory]
    [InlineData(NsfwAccessMode.Pin)]
    [InlineData(NsfwAccessMode.Allowed)]
    public async Task AnAdultProfileCanBeOpenedUp(NsfwAccessMode mode)
    {
        await _nsfw.SetAsync(_adult, mode, 30, _admin, _now);

        var access = await _nsfw.GetAsync(_adult);
        Assert.Equal(mode, access.EffectiveMode);
        Assert.Equal(30, access.IdleLockMinutes);
        Assert.True(access.CanUnlock);
    }

    [Theory]
    [InlineData("RestrictedProfile", null)]
    [InlineData("StandardUser", "PG")]
    [InlineData("Administrator", "R")]
    [InlineData("RestrictedProfile", "G")]
    public async Task AKidsOrContentLimitedProfile_CanNeverBeOpenedUp_EvenByAStatementThatSkipsTheEngineRules(string role, string? limit)
    {
        var profile = Guid.NewGuid();
        InsertProfile(profile, role, limit);

        await Assert.ThrowsAnyAsync<Exception>(() => _nsfw.SetAsync(profile, NsfwAccessMode.Pin, null, _admin, _now));
        await Assert.ThrowsAnyAsync<Exception>(() => _nsfw.SetAsync(profile, NsfwAccessMode.Allowed, null, _admin, _now));
        using var connection = _database.CreateConnection();
        Assert.Throws<SqliteException>(() => connection.Execute(
            "INSERT INTO profile_nsfw_settings(profile_id,access_mode,updated_at) VALUES(@profile,'allowed',@now);",
            new { profile, now = _now.ToString("O") }));
        Assert.False((await _nsfw.GetAsync(profile)).CanUnlock);
    }

    [Fact]
    public async Task AKidsProfileStoredAsPinOrAllowedReadsAsOff()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Allowed, null, _admin, _now);
        using (var connection = _database.CreateConnection())
        {
            // Not through the withdraw trigger: simulates an old row, with the guard triggers off for the statement.
            connection.Execute("DROP TRIGGER trg_profiles_nsfw_access_withdrawn;");
            connection.Execute("UPDATE profiles SET role='RestrictedProfile' WHERE id=@id;", new { id = _adult });
        }

        var access = await _nsfw.GetAsync(_adult);
        Assert.Equal(NsfwAccessMode.Allowed, access.ConfiguredMode);
        Assert.Equal(NsfwAccessMode.Off, access.EffectiveMode);
        Assert.False(access.CanUnlock);

        // And even then the unlock check refuses it.
        var session = InsertSession(_adult);
        using var insert = _database.CreateConnection();
        insert.Execute(
            "INSERT INTO nsfw_session_unlocks(session_id,profile_id,method,unlocked_at,last_activity_at) VALUES(@session,@profile,'allowed',@now,@now);",
            new { session, profile = _adult, now = _now.ToString("O") });
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
    }

    [Fact]
    public async Task ABecomingKidsOrContentLimitedProfileLosesAccessAndEveryUnlockAtOnce()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Allowed, null, _admin, _now);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Allowed, 15, _now);
        Assert.True(await _nsfw.IsUnlockedAsync(session, _adult, _now));

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET content_limit='PG-13' WHERE id=@id;", new { id = _adult });
        }

        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
        Assert.Equal(NsfwAccessMode.Off, (await _nsfw.GetAsync(_adult)).ConfiguredMode);

        // Clearing the limit again does not bring access back; an administrator has to grant it again.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET content_limit=NULL WHERE id=@id;", new { id = _adult });
        }

        Assert.False((await _nsfw.GetAsync(_adult)).CanUnlock);
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
    }

    [Fact]
    public async Task AProfileTurnedIntoAKidsProfileLosesAccess()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Pin, null, _admin, _now);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Pin, 15, _now);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET role='RestrictedProfile' WHERE id=@id;", new { id = _adult });
        }

        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
        Assert.False((await _nsfw.GetAsync(_adult)).CanUnlock);
    }

    // ── A device session's unlock ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnlockBelongsToOneSession_NotToTheProfileOrTheDevicesAround()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Allowed, null, _admin, _now);
        var phone = InsertSession(_adult);
        var browser = InsertSession(_adult);

        await _nsfw.UnlockAsync(phone, _adult, NsfwUnlockMethod.Allowed, 15, _now);

        Assert.True(await _nsfw.IsUnlockedAsync(phone, _adult, _now));
        Assert.False(await _nsfw.IsUnlockedAsync(browser, _adult, _now));
        Assert.False(await _nsfw.IsUnlockedAsync(Guid.NewGuid(), _adult, _now));
        Assert.False(await _nsfw.IsUnlockedAsync(phone, _admin, _now));
        Assert.False(await _nsfw.IsUnlockedAsync(Guid.Empty, _adult, _now));
        Assert.False(await _nsfw.IsUnlockedAsync(phone, Guid.Empty, _now));
    }

    [Fact]
    public async Task AnUnlockLocksItselfAfterTheIdleTime_AndActivityMovesTheTimeForward()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Allowed, null, _admin, _now);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Allowed, 15, _now);

        Assert.True(await _nsfw.IsUnlockedAsync(session, _adult, _now.AddMinutes(14)));
        Assert.True(await _nsfw.TouchAsync(session, _adult, 15, _now.AddMinutes(14)));
        Assert.True(await _nsfw.IsUnlockedAsync(session, _adult, _now.AddMinutes(28)));
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now.AddMinutes(30)));
    }

    [Fact]
    public async Task AnUnlockThatHasRunOut_CannotBeKeptAlive()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Allowed, null, _admin, _now);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Allowed, 15, _now);

        Assert.False(await _nsfw.TouchAsync(session, _adult, 15, _now.AddMinutes(16)));
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now.AddMinutes(17)));
    }

    [Fact]
    public async Task ZeroIdleMinutesNeverRunsOut()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Allowed, 0, _admin, _now);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Allowed, 0, _now);

        Assert.True(await _nsfw.IsUnlockedAsync(session, _adult, _now.AddDays(300)));
    }

    [Fact]
    public async Task ASessionLocksOnRequest_AndAProfileLocksEveryDeviceItHad()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Allowed, null, _admin, _now);
        var phone = InsertSession(_adult);
        var browser = InsertSession(_adult);
        await _nsfw.UnlockAsync(phone, _adult, NsfwUnlockMethod.Allowed, 15, _now);
        await _nsfw.UnlockAsync(browser, _adult, NsfwUnlockMethod.Allowed, 15, _now);

        await _nsfw.LockSessionAsync(phone);
        Assert.False(await _nsfw.IsUnlockedAsync(phone, _adult, _now));
        Assert.True(await _nsfw.IsUnlockedAsync(browser, _adult, _now));

        await _nsfw.LockProfileAsync(_adult);
        Assert.False(await _nsfw.IsUnlockedAsync(browser, _adult, _now));
    }

    [Fact]
    public async Task SwitchingProfile_LocksTheSession_AndSwitchingBackDoesNotBringTheUnlockBack()
    {
        var other = Guid.NewGuid();
        InsertProfile(other, "StandardUser", null);
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Allowed, null, _admin, _now);
        await _nsfw.SetAsync(other, NsfwAccessMode.Allowed, null, _admin, _now);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Allowed, 15, _now);

        SwitchSession(session, other);
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
        Assert.False(await _nsfw.IsUnlockedAsync(session, other, _now));

        SwitchSession(session, _adult);
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
    }

    [Fact]
    public async Task AnUnlockOnASessionTheOtherProfileOwns_IsNeverValid()
    {
        var other = Guid.NewGuid();
        InsertProfile(other, "StandardUser", null);
        await _nsfw.SetAsync(other, NsfwAccessMode.Allowed, null, _admin, _now);
        var session = InsertSession(_adult);

        // The session belongs to the adult profile; an unlock written for another profile never counts.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO nsfw_session_unlocks(session_id,profile_id,method,unlocked_at,last_activity_at) VALUES(@session,@profile,'allowed',@now,@now);",
                new { session, profile = other, now = _now.ToString("O") });
        }

        Assert.False(await _nsfw.IsUnlockedAsync(session, other, _now));
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
    }

    [Fact]
    public async Task ARevokedSession_IsNeverUnlocked()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Allowed, null, _admin, _now);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Allowed, 15, _now);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE auth_sessions SET revoked_at=@now WHERE id=@id;", new { id = session, now = _now.ToString("O") });
        }

        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
    }

    [Fact]
    public async Task ChangingTheMode_LocksEveryDeviceOfThatProfile_AndSettingOffClosesTheDoor()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Allowed, null, _admin, _now);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Allowed, 15, _now);

        await _nsfw.SetAsync(_adult, NsfwAccessMode.Pin, null, _admin, _now);
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));

        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Pin, 15, _now);
        Assert.True(await _nsfw.IsUnlockedAsync(session, _adult, _now));
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Off, null, _admin, _now);
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
        Assert.False((await _nsfw.GetAsync(_adult)).CanUnlock);
    }

    [Fact]
    public async Task APinUnlock_DoesNotSurviveTheModeBecomingAllowed_NorAnAllowedUnlockSurviveItBecomingPin()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Pin, null, _admin, _now);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Pin, 15, _now);
        // Writing the stored mode back to the same value does not matter; a real change does.
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Pin, 20, _admin, _now);
        Assert.True(await _nsfw.IsUnlockedAsync(session, _adult, _now));

        // An unlock made the Allowed way is not valid once the profile needs a PIN, even if a row slipped through.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE nsfw_session_unlocks SET method='allowed';");
        }

        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
    }

    [Fact]
    public async Task UnlockingAProfileThatCannotUseNsfwMode_IsRefused()
    {
        var session = InsertSession(_adult);

        await Assert.ThrowsAnyAsync<Exception>(() => _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Allowed, 15, _now));

        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
    }

    // ── The PIN record ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePinIsAHashInItsOwnTable_AndTheProfilePinTableNeverHoldsIt()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Pin, null, _admin, _now);
        var credential = new MediaEngine.Domain.Entities.ProfileCredential
        {
            Id = Guid.NewGuid(),
            ProfileId = _adult,
            Kind = MediaEngine.Domain.Entities.ProfileCredentialKind.NsfwPin,
            SecretHash = "hashed-value",
            SecurityStamp = "stamp",
            CreatedAt = _now,
            UpdatedAt = _now,
        };

        await _nsfw.UpsertPinAsync(credential);

        Assert.NotNull(await _nsfw.GetPinAsync(_adult));
        Assert.True((await _nsfw.GetAsync(_adult)).HasPin);
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profile_credentials;"));
    }

    [Fact]
    public async Task ChangingOrRemovingThePin_LocksEveryDevice()
    {
        await _nsfw.SetAsync(_adult, NsfwAccessMode.Pin, null, _admin, _now);
        var credential = new MediaEngine.Domain.Entities.ProfileCredential
        {
            Id = Guid.NewGuid(),
            ProfileId = _adult,
            Kind = MediaEngine.Domain.Entities.ProfileCredentialKind.NsfwPin,
            SecretHash = "one",
            SecurityStamp = "stamp",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        await _nsfw.UpsertPinAsync(credential);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Pin, 15, _now);

        credential.SecretHash = "two";
        await _nsfw.UpsertPinAsync(credential);
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));

        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Pin, 15, _now);
        Assert.True(await _nsfw.DeletePinAsync(_adult));
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _now));
        Assert.False(await _nsfw.DeletePinAsync(_adult));
    }

    // ── The Vault's own names and artwork ───────────────────────────────────────────────────────

    [Fact]
    public async Task VaultNamesLiveInTheirOwnTables_AndNeverCreateSharedPeopleTagsOrSeries()
    {
        var vault = NewLibrary();
        await _libraries.AddAsync(vault);
        var (work, asset) = InsertAsset(vault.LibraryId);
        _ = work;

        var performer = await _names.EnsurePerformerAsync(vault.LibraryId, "Some Performer", "Performer, Some", "stash", _now);
        var studio = await _names.EnsureStudioAsync(vault.LibraryId, "Some Studio", "stash", _now);
        var tag = await _names.EnsureTagAsync(vault.LibraryId, "some tag", _now);
        await _names.LinkPerformerAsync(asset, performer);
        await _names.LinkStudioAsync(asset, studio);
        await _names.LinkTagAsync(asset, tag);

        Assert.Single(await _names.GetPerformersOfAsync(asset));
        Assert.Single(await _names.GetStudiosOfAsync(asset));
        Assert.Single(await _names.GetTagsOfAsync(asset));
        Assert.Equal(asset, Assert.Single(await _names.GetAssetIdsOfPerformerAsync(performer)));
        using var connection = _database.CreateConnection();
        foreach (var shared in new[] { "persons", "person_media_links", "person_aliases", "tags", "local_item_tag_links", "collections", "collection_items", "artwork_assets", "entity_artwork_links" })
        {
            Assert.Equal(0, connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM {shared};"));
        }
    }

    [Fact]
    public async Task ANameIsFoundAgainWhateverTheCase_AndStaysOneRecord()
    {
        var vault = NewLibrary();
        await _libraries.AddAsync(vault);

        var first = await _names.EnsurePerformerAsync(vault.LibraryId, "Name One", null, null, _now);
        var again = await _names.EnsurePerformerAsync(vault.LibraryId, "NAME one", null, null, _now);

        Assert.Equal(first, again);
        Assert.Single(await _names.GetPerformersAsync(vault.LibraryId));
    }

    [Fact]
    public async Task NamesBelongToOneVaultLibrary_AndASceneOnlyLinksToNamesOfItsOwnLibrary()
    {
        var one = NewLibrary();
        var two = NewLibrary();
        await _libraries.AddAsync(one);
        await _libraries.AddAsync(two);
        var (_, sceneInOne) = InsertAsset(one.LibraryId);
        var inTwo = await _names.EnsurePerformerAsync(two.LibraryId, "Same Name", null, null, _now);
        var inOne = await _names.EnsurePerformerAsync(one.LibraryId, "Same Name", null, null, _now);

        Assert.NotEqual(inOne, inTwo);
        await Assert.ThrowsAnyAsync<Exception>(() => _names.LinkPerformerAsync(sceneInOne, inTwo));
        await _names.LinkPerformerAsync(sceneInOne, inOne);
    }

    [Fact]
    public async Task ANameCanOnlyBeCreatedInAVaultLibrary()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _names.EnsurePerformerAsync(Guid.NewGuid().ToString("D"), "Nobody", null, null, _now));
        await Assert.ThrowsAnyAsync<Exception>(() => _names.EnsureTagAsync(Guid.NewGuid().ToString("D"), "nothing", _now));
    }

    [Fact]
    public async Task ANameLeftWithNoSceneGoesWithItsLastLink()
    {
        var vault = NewLibrary();
        await _libraries.AddAsync(vault);
        var (_, one) = InsertAsset(vault.LibraryId);
        var (_, two) = InsertAsset(vault.LibraryId);
        var performer = await _names.EnsurePerformerAsync(vault.LibraryId, "Shared Performer", null, null, _now);
        var tag = await _names.EnsureTagAsync(vault.LibraryId, "shared tag", _now);
        await _names.LinkPerformerAsync(one, performer);
        await _names.LinkPerformerAsync(two, performer);
        await _names.LinkTagAsync(one, tag);

        await _names.UnlinkAllAsync(one);
        Assert.Single(await _names.GetPerformersAsync(vault.LibraryId));
        Assert.Empty(await _names.GetTagsAsync(vault.LibraryId));

        await _names.UnlinkAllAsync(two);
        Assert.Empty(await _names.GetPerformersAsync(vault.LibraryId));
    }

    [Fact]
    public async Task VaultArtworkIsStoredApart_AndGoesWithItsOwner()
    {
        var vault = NewLibrary();
        await _libraries.AddAsync(vault);
        var (_, asset) = InsertAsset(vault.LibraryId);
        var performer = await _names.EnsurePerformerAsync(vault.LibraryId, "Face", null, null, _now);
        await _names.LinkPerformerAsync(asset, performer);

        var cover = await _artwork.AddAsync(VaultArtworkOwnerKinds.Asset, asset, VaultArtworkKinds.Cover, "/vault/cover.jpg", "hash", 600, 900, "stash", _now);
        var portrait = await _artwork.AddAsync(VaultArtworkOwnerKinds.Performer, performer, VaultArtworkKinds.Portrait, "/vault/face.jpg", null, null, null, "stash", _now);

        Assert.Equal("/vault/cover.jpg", (await _artwork.GetAsync(cover))!.FilePath);
        Assert.Single(await _artwork.GetForOwnerAsync(VaultArtworkOwnerKinds.Asset, asset));
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM artwork_assets;"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM entity_assets;"));

        await _names.UnlinkAllAsync(asset);
        Assert.Null(await _artwork.GetAsync(portrait));
        Assert.NotNull(await _artwork.GetAsync(cover));
        Assert.True(await _artwork.DeleteAsync(cover));
        Assert.False(await _artwork.DeleteAsync(cover));
    }

    [Fact]
    public async Task VaultArtworkWithAnUnknownOwnerKindOrKind_IsRefused()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _artwork.AddAsync("person", Guid.NewGuid(), VaultArtworkKinds.Cover, "/x.jpg", null, null, null, null, _now));
        await Assert.ThrowsAnyAsync<Exception>(() => _artwork.AddAsync(VaultArtworkOwnerKinds.Asset, Guid.NewGuid(), "banner", "/x.jpg", null, null, null, null, _now));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private VaultLibrary NewLibrary(string? id = null) =>
        new(id ?? Guid.NewGuid().ToString("D"), VaultLookupSources.None, null, _now, _admin, _now, _now);

    private (Guid WorkId, Guid AssetId) InsertAsset(string libraryId)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Videos', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
            """,
            new { workId, editionId, assetId, hash = Guid.NewGuid().ToString("N"), path = $"C:/vault/{assetId:N}.mkv", libraryId });
        return (workId, assetId);
    }

    private void InsertProfile(Guid id, string role, string? contentLimit)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@id,@name,'#7C4DFF',@role,@contentLimit,@createdAt);",
            new { id, name = role, role, contentLimit, createdAt = _now.ToString("O") });
    }

    private Guid InsertSession(Guid profileId)
    {
        var sessionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO accounts(id,email,normalized_email,is_enabled,is_administrator,authorization_version,created_at,updated_at)
            VALUES(@accountId,@email,@normalized,1,0,1,@now,@now);
            INSERT INTO auth_sessions(id,account_id,active_profile_id,token_hash,device_id,device_name,client,authentication_method,security_stamp,created_at,last_seen_at,expires_at)
            VALUES(@sessionId,@accountId,@profileId,@hash,'device','Device','Dashboard','password','stamp',@now,@now,@later);
            """,
            new
            {
                sessionId,
                accountId,
                profileId,
                email = $"{accountId:N}@example.com",
                normalized = $"{accountId:N}@EXAMPLE.COM",
                hash = Guid.NewGuid().ToString("N"),
                now = _now.ToString("O"),
                later = _now.AddDays(30).ToString("O"),
            });
        return sessionId;
    }

    private void SwitchSession(Guid sessionId, Guid profileId)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE auth_sessions SET active_profile_id=@profileId WHERE id=@sessionId;", new { sessionId, profileId });
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }
}
