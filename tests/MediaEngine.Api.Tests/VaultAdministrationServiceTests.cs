using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

// Creating a Vault library or opening NSFW access to a profile needs the administrator's own profile PIN, set and typed again at
// that moment (Videos spec D25); NSFW mode itself follows the profile's mode, its own PIN and the five-wrong-tries pause (D18, D19).
// No PIN is ever returned or logged.
public sealed class VaultAdministrationServiceTests : IDisposable
{
    private const string AdminPin = "2468";
    private const string AdultNsfwPin = "13579";
    private const string WrongPin = "00000";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_vault_admin_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly VaultLibraryRepository _libraries;
    private readonly NsfwAccessRepository _nsfw;
    private readonly FirstPartyIdentityService _identity;
    private readonly NsfwPinService _pins;
    private readonly VaultAdministrationService _service;
    private readonly CapturingLogger _log = new();
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _adult = Guid.NewGuid();
    private readonly Guid _kid = Guid.NewGuid();
    private readonly Guid _limited = Guid.NewGuid();

    public VaultAdministrationServiceTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _libraries = new VaultLibraryRepository(_database);
        _nsfw = new NsfwAccessRepository(_database);
        var identities = new IdentityRepository(_database);
        var profiles = new ProfileRepository(_database);
        _identity = new FirstPartyIdentityService(
            identities, new AccountRepository(_database), profiles,
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(), _clock, new FixedPolicy(),
            nsfwAccess: _nsfw);
        _pins = new NsfwPinService(_nsfw, identities, new PasswordHasher<ProfileCredential>(), _clock);
        _service = new VaultAdministrationService(_libraries, _nsfw, _pins, profiles, identities, _identity, _clock, _log);
        InsertProfile(_admin, "Administrator", null);
        InsertProfile(_adult, "StandardUser", null);
        InsertProfile(_kid, "RestrictedProfile", null);
        InsertProfile(_limited, "StandardUser", "PG");
    }

    // ── The administrator's own PIN ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ALibraryCannotBeCreated_WhenTheAdministratorHasNoProfilePin()
    {
        await Assert.ThrowsAsync<VaultAdminPinNotSetException>(() => Create(AdminPin));
        await Assert.ThrowsAsync<VaultAdminPinNotSetException>(() => Create(null));

        Assert.False(await _libraries.AnyAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(WrongPin)]
    public async Task ALibraryCannotBeCreated_WithoutTheRightAdministratorPin(string? typed)
    {
        await _identity.SetProfilePinAsync(_admin, AdminPin);

        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => Create(typed));

        Assert.False(await _libraries.AnyAsync());
    }

    [Fact]
    public async Task ALibraryCanBeCreated_WithTheRightAdministratorPin_AndKeepsItsOwnSettings()
    {
        await _identity.SetProfilePinAsync(_admin, AdminPin);

        var created = await _service.CreateLibraryAsync(_admin, VaultLookupSources.StashBox, "server-1", true, AdminPin, default);

        Assert.True(await _libraries.AnyAsync());
        Assert.Equal(created.LibraryId, (await _libraries.GetAllAsync()).Single().LibraryId);
        Assert.Equal(VaultLookupSources.StashBox, created.EffectiveLookupSource);
        Assert.Equal(_admin, created.CreatedByProfileId);
    }

    [Fact]
    public async Task ALibraryCannotBeCreated_WithoutConfirmingEveryoneWhoCanOpenItIsAnAdult()
    {
        await _identity.SetProfilePinAsync(_admin, AdminPin);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CreateLibraryAsync(_admin, VaultLookupSources.None, null, false, AdminPin, default));

        Assert.False(await _libraries.AnyAsync());
    }

    [Theory]
    [InlineData("tmdb", null)]
    [InlineData("none", "server-1")]
    public async Task ALibraryCannotBeCreated_WithALookupThatIsNotAVaultSource(string source, string? server)
    {
        await _identity.SetProfilePinAsync(_admin, AdminPin);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CreateLibraryAsync(_admin, source, server, true, AdminPin, default));
    }

    [Fact]
    public async Task FiveWrongAdministratorPins_PauseTheGate_EvenForTheRightPin()
    {
        await _identity.SetProfilePinAsync(_admin, AdminPin);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Assert.ThrowsAsync<ProfilePinRequiredException>(() => Create(WrongPin));
        }

        await Assert.ThrowsAsync<ProfilePinLockedException>(() => Create(WrongPin));
        await Assert.ThrowsAsync<ProfilePinLockedException>(() => Create(AdminPin));
        Assert.False(await _libraries.AnyAsync());

        _clock.Advance(TimeSpan.FromMinutes(16));
        await Create(AdminPin);
        Assert.True(await _libraries.AnyAsync());
    }

    [Theory]
    [InlineData(NsfwAccessMode.Pin)]
    [InlineData(NsfwAccessMode.Allowed)]
    public async Task OpeningNsfwAccess_NeedsTheAdministratorPin_SetAndTypedAgain(NsfwAccessMode mode)
    {
        await Assert.ThrowsAsync<VaultAdminPinNotSetException>(() => _service.SetAccessAsync(_admin, _adult, mode, null, AdminPin, default));
        await _identity.SetProfilePinAsync(_admin, AdminPin);
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _service.SetAccessAsync(_admin, _adult, mode, null, null, default));
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _service.SetAccessAsync(_admin, _adult, mode, null, WrongPin, default));
        Assert.False((await _nsfw.GetAsync(_adult)).CanUnlock);

        var access = await _service.SetAccessAsync(_admin, _adult, mode, null, AdminPin, default);

        Assert.Equal(mode, access.EffectiveMode);
    }

    [Fact]
    public async Task ClosingNsfwAccess_NeedsNoPin_AndLocksEveryDeviceTheProfileHad()
    {
        await _identity.SetProfilePinAsync(_admin, AdminPin);
        await _service.SetAccessAsync(_admin, _adult, NsfwAccessMode.Allowed, null, AdminPin, default);
        var session = InsertSession(_adult);
        await _service.UnlockAsync(session, _adult, null, default);
        Assert.True(await _nsfw.IsUnlockedAsync(session, _adult, _clock.GetUtcNow()));
        await _identity.SetProfilePinAsync(_admin, null);

        await _service.SetAccessAsync(_admin, _adult, NsfwAccessMode.Off, null, null, default);

        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _clock.GetUtcNow()));
        Assert.False((await _nsfw.GetAsync(_adult)).CanUnlock);
    }

    [Theory]
    [InlineData(NsfwAccessMode.Pin)]
    [InlineData(NsfwAccessMode.Allowed)]
    public async Task AKidsProfile_CanNeverBeGivenNsfwAccess_EvenWithTheRightAdministratorPin(NsfwAccessMode mode)
    {
        await _identity.SetProfilePinAsync(_admin, AdminPin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetAccessAsync(_admin, _kid, mode, null, AdminPin, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetAccessAsync(_admin, _limited, mode, null, AdminPin, default));

        Assert.False((await _nsfw.GetAsync(_kid)).CanUnlock);
        Assert.False((await _nsfw.GetAsync(_limited)).CanUnlock);
    }

    [Fact]
    public async Task AKidsProfile_CanNeverUnlock_WhateverItTypes()
    {
        var kidSession = InsertSession(_kid);
        var limitedSession = InsertSession(_limited);

        await Assert.ThrowsAsync<NsfwNotAvailableException>(() => _service.UnlockAsync(kidSession, _kid, null, default));
        await Assert.ThrowsAsync<NsfwNotAvailableException>(() => _service.UnlockAsync(kidSession, _kid, AdultNsfwPin, default));
        await Assert.ThrowsAsync<NsfwNotAvailableException>(() => _service.UnlockAsync(limitedSession, _limited, null, default));
        Assert.Null(await _service.GetStateAsync(kidSession, _kid, default));
        Assert.Null(await _service.GetStateAsync(limitedSession, _limited, default));
        await Assert.ThrowsAsync<NsfwNotAvailableException>(() => _service.SetOwnPinAsync(_kid, AdultNsfwPin, null, default));
    }

    [Fact]
    public async Task ABecomeKidsProfile_CanNoLongerUnlock_EvenWithAPinItSetBefore()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _service.SetOwnPinAsync(_adult, AdultNsfwPin, null, default);
        var session = InsertSession(_adult);
        await _service.UnlockAsync(session, _adult, AdultNsfwPin, default);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET role='RestrictedProfile' WHERE id=@id;", new { id = _adult });
        }

        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _clock.GetUtcNow()));
        await Assert.ThrowsAsync<NsfwNotAvailableException>(() => _service.UnlockAsync(session, _adult, AdultNsfwPin, default));
    }

    [Theory]
    [InlineData(NsfwAccessMode.Pin, 0)]
    [InlineData(NsfwAccessMode.Pin, 1441)]
    [InlineData(NsfwAccessMode.Pin, -1)]
    [InlineData(NsfwAccessMode.Allowed, 1441)]
    public async Task AnIdleTimeOutsideTheAllowedRange_IsRefused_AndNeverMeansForeverForAPinProfile(NsfwAccessMode mode, int minutes)
    {
        await _identity.SetProfilePinAsync(_admin, AdminPin);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetAccessAsync(_admin, _adult, mode, minutes, AdminPin, default));
    }

    [Fact]
    public async Task AnAllowedProfile_MayChooseNeverToLockByIdle()
    {
        await _identity.SetProfilePinAsync(_admin, AdminPin);

        await _service.SetAccessAsync(_admin, _adult, NsfwAccessMode.Allowed, 0, AdminPin, default);
        var session = InsertSession(_adult);
        await _service.UnlockAsync(session, _adult, null, default);

        Assert.True(await _nsfw.IsUnlockedAsync(session, _adult, _clock.GetUtcNow().AddDays(100)));
    }

    // ── Unlocking ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AProfileThatIsOff_CannotUnlock_AndSeesNoStatus()
    {
        var session = InsertSession(_adult);

        await Assert.ThrowsAsync<NsfwNotAvailableException>(() => _service.UnlockAsync(session, _adult, AdultNsfwPin, default));
        Assert.Null(await _service.GetStateAsync(session, _adult, default));
        Assert.False(await _service.KeepAliveAsync(session, _adult, default));
    }

    [Fact]
    public async Task APinProfile_UnlocksWithItsNsfwPin_OnlyForThatDeviceSession()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _service.SetOwnPinAsync(_adult, AdultNsfwPin, null, default);
        var phone = InsertSession(_adult);
        var browser = InsertSession(_adult);

        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _service.UnlockAsync(phone, _adult, null, default));
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _service.UnlockAsync(phone, _adult, WrongPin, default));
        Assert.False((await _service.GetStateAsync(phone, _adult, default))!.Unlocked);

        var state = await _service.UnlockAsync(phone, _adult, AdultNsfwPin, default);

        Assert.True(state.Unlocked);
        Assert.True((await _service.GetStateAsync(phone, _adult, default))!.Unlocked);
        Assert.False((await _service.GetStateAsync(browser, _adult, default))!.Unlocked);
    }

    [Fact]
    public async Task APinProfileWithNoPinSaved_CannotUnlock_AndTheProfilePinDoesNotWork()
    {
        await _identity.SetProfilePinAsync(_adult, AdminPin);
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        var session = InsertSession(_adult);

        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _service.UnlockAsync(session, _adult, AdminPin, default));
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _service.UnlockAsync(session, _adult, null, default));
        Assert.False((await _service.GetStateAsync(session, _adult, default))!.Unlocked);
    }

    [Fact]
    public async Task FiveWrongNsfwPins_PauseUnlocking_EvenForTheRightPin_UntilThePauseEnds()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _service.SetOwnPinAsync(_adult, AdultNsfwPin, null, default);
        var session = InsertSession(_adult);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _service.UnlockAsync(session, _adult, WrongPin, default));
        }

        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _service.UnlockAsync(session, _adult, WrongPin, default));
        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _service.UnlockAsync(session, _adult, AdultNsfwPin, default));
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _clock.GetUtcNow()));

        // Another device session of the same profile is paused too: the count belongs to the PIN, not to the device.
        var other = InsertSession(_adult);
        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _service.UnlockAsync(other, _adult, AdultNsfwPin, default));

        _clock.Advance(TimeSpan.FromMinutes(16));
        Assert.True((await _service.UnlockAsync(session, _adult, AdultNsfwPin, default)).Unlocked);
    }

    [Fact]
    public async Task AnAllowedProfile_UnlocksWithNoPin_AndLockingEndsIt()
    {
        await OpenAsync(_adult, NsfwAccessMode.Allowed);
        var session = InsertSession(_adult);

        Assert.True((await _service.UnlockAsync(session, _adult, null, default)).Unlocked);
        Assert.True(await _service.KeepAliveAsync(session, _adult, default));

        await _service.LockAsync(session, default);

        Assert.False((await _service.GetStateAsync(session, _adult, default))!.Unlocked);
        Assert.False(await _service.KeepAliveAsync(session, _adult, default));
    }

    [Fact]
    public async Task AnUnlockOnADeviceThatSwitchedProfile_IsRefused()
    {
        await OpenAsync(_adult, NsfwAccessMode.Allowed);
        var session = InsertSession(_admin);

        // The session belongs to another profile: unlocking this profile on it is refused, and nothing is written.
        await Assert.ThrowsAsync<NsfwNotAvailableException>(() => _service.UnlockAsync(session, _adult, null, default));
        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _clock.GetUtcNow()));
    }

    [Fact]
    public async Task TheIdleTimerRunsFromActivity_AndAnExpiredSessionCannotBeKeptAlive()
    {
        await OpenAsync(_adult, NsfwAccessMode.Allowed);
        var session = InsertSession(_adult);
        await _service.UnlockAsync(session, _adult, null, default);

        _clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True(await _service.KeepAliveAsync(session, _adult, default));
        _clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True((await _service.GetStateAsync(session, _adult, default))!.Unlocked);

        _clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False((await _service.GetStateAsync(session, _adult, default))!.Unlocked);
        Assert.False(await _service.KeepAliveAsync(session, _adult, default));
    }

    // ── The NSFW PIN ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APinCanBeSavedOnlyByAProfileThatCanUseNsfwMode()
    {
        await Assert.ThrowsAsync<NsfwNotAvailableException>(() => _service.SetOwnPinAsync(_adult, AdultNsfwPin, null, default));

        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _service.SetOwnPinAsync(_adult, AdultNsfwPin, null, default);

        Assert.True((await _service.GetStateAsync(InsertSession(_adult), _adult, default))!.HasPin);
    }

    [Fact]
    public async Task TheSameDigitsCannotBeBothPins_InEitherOrder()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _identity.SetProfilePinAsync(_adult, AdminPin);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetOwnPinAsync(_adult, AdminPin, null, default));

        await _service.SetOwnPinAsync(_adult, AdultNsfwPin, null, default);
        await Assert.ThrowsAsync<ArgumentException>(() => _identity.SetProfilePinAsync(_adult, AdultNsfwPin));
    }

    [Fact]
    public async Task AnAdministratorResetsAPinWithoutReadingIt_AndTheProfileMustSetANewOneBeforeUnlockingAgain()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _service.SetOwnPinAsync(_adult, AdultNsfwPin, null, default);
        var session = InsertSession(_adult);
        await _service.UnlockAsync(session, _adult, AdultNsfwPin, default);

        Assert.True(await _service.ResetPinAsync(_adult, default));

        Assert.False((await _service.GetStateAsync(session, _adult, default))!.Unlocked);
        Assert.False((await _service.GetStateAsync(session, _adult, default))!.HasPin);
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _service.UnlockAsync(session, _adult, AdultNsfwPin, default));
        await _service.SetOwnPinAsync(_adult, "97531", null, default);
        Assert.True((await _service.UnlockAsync(session, _adult, "97531", default)).Unlocked);
    }

    // ── What is returned and what is logged ─────────────────────────────────────────────────────

    [Fact]
    public async Task NoResponseShape_CarriesAPin_AndNoLogLineEverContainsOne()
    {
        const string loggedAdminPin = "1357924";
        const string loggedNsfwPin = "8642097";
        const string loggedWrong = "5550123";
        await _identity.SetProfilePinAsync(_admin, loggedAdminPin);
        await _service.SetAccessAsync(_admin, _adult, NsfwAccessMode.Pin, null, loggedAdminPin, default);
        await _service.SetOwnPinAsync(_adult, loggedNsfwPin, null, default);
        var session = InsertSession(_adult);
        var library = await _service.CreateLibraryAsync(_admin, VaultLookupSources.None, null, true, loggedAdminPin, default);
        var outputs = new List<string>();
        foreach (var attempt in new Func<Task>[]
        {
            () => _service.CreateLibraryAsync(_admin, VaultLookupSources.None, null, true, loggedWrong, default),
            () => _service.SetAccessAsync(_admin, _adult, NsfwAccessMode.Allowed, null, loggedWrong, default),
            () => _service.UnlockAsync(session, _adult, loggedWrong, default),
            () => _service.SetOwnPinAsync(_adult, "9988776", loggedWrong, default),
        })
        {
            try
            {
                await attempt();
            }
            catch (Exception exception)
            {
                outputs.Add(exception.ToString());
            }
        }

        var state = await _service.UnlockAsync(session, _adult, loggedNsfwPin, default);
        outputs.Add(JsonSerializer.Serialize(new NsfwStatusDto
        {
            Mode = NsfwAccessRules.ToStored(state.Mode),
            Unlocked = state.Unlocked,
            HasPin = state.HasPin,
            IdleLockMinutes = state.IdleLockMinutes,
        }));
        outputs.Add(JsonSerializer.Serialize(new VaultLibraryDto { Id = library.LibraryId, LookupSource = library.EffectiveLookupSource }));
        outputs.AddRange(_log.Lines);

        Assert.NotEmpty(_log.Lines);
        foreach (var output in outputs)
        {
            foreach (var pin in new[] { loggedAdminPin, loggedNsfwPin, loggedWrong })
            {
                Assert.DoesNotContain(pin, output);
            }
        }
    }

    [Fact]
    public void ResponseTypes_HaveNoPropertyThatCouldCarryAPin()
    {
        foreach (var type in new[] { typeof(NsfwStatusDto), typeof(VaultLibraryDto) })
        {
            var names = type.GetProperties().Select(property => property.Name.ToLowerInvariant()).ToArray();
            Assert.DoesNotContain(names, name => name.Contains("pin") && name != "haspin");
            Assert.DoesNotContain(names, name => name.Contains("secret") || name.Contains("hash"));
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private Task<VaultLibrary> Create(string? adminPin) =>
        _service.CreateLibraryAsync(_admin, VaultLookupSources.None, null, true, adminPin, default);

    private async Task OpenAsync(Guid profile, NsfwAccessMode mode) =>
        await _nsfw.SetAsync(profile, mode, null, _admin, _clock.GetUtcNow());

    private void InsertProfile(Guid id, string role, string? contentLimit)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@id,@name,'#7C4DFF',@role,@contentLimit,@createdAt);",
            new { id, name = role, role, contentLimit, createdAt = _clock.GetUtcNow().ToString("O") });
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
                now = _clock.GetUtcNow().ToString("O"),
                later = _clock.GetUtcNow().AddDays(30).ToString("O"),
            });
        return sessionId;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }

    private sealed class CapturingLogger : ILogger<VaultAdministrationService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null)
            {
                Lines.Add(exception.ToString());
            }
        }
    }
}
