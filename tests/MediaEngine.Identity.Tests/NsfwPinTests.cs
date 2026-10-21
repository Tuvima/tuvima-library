using Dapper;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

// The NSFW PIN (Videos spec D19, D25): its own hashed record with the profile PIN's rules (4 to 12 digits, five wrong tries pause
// unlocking, a pause that ends on its own), never the same digits as the profile PIN, and never readable, logged or returned.
public sealed class NsfwPinTests : IDisposable
{
    private const string ProfilePin = "2468";
    private const string NsfwPin = "13579";

    /// <summary>How long five wrong tries pause unlocking: the same pause the profile PIN has.</summary>
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_nsfw_pin_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly NsfwAccessRepository _nsfw;
    private readonly FirstPartyIdentityService _identity;
    private readonly NsfwPinService _pins;
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _adult = Guid.NewGuid();

    public NsfwPinTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _nsfw = new NsfwAccessRepository(_database);
        var identities = new IdentityRepository(_database);
        _identity = new FirstPartyIdentityService(
            identities,
            new AccountRepository(_database),
            new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(),
            new PasswordHasher<ProfileCredential>(),
            _clock,
            new FixedPolicy(),
            nsfwAccess: _nsfw);
        _pins = new NsfwPinService(_nsfw, identities, new PasswordHasher<ProfileCredential>(), _clock);
        InsertProfile(_adult, ProfileRole.StandardUser, null);
    }

    [Fact]
    public async Task APinCanOnlyBeSavedForAProfileThatCanUseNsfwMode()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _pins.SetPinAsync(_adult, NsfwPin, null));

        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        Assert.True(await _pins.HasPinAsync(_adult));
    }

    [Fact]
    public async Task AKidsOrContentLimitedProfile_CanNeverHoldAPin()
    {
        var kid = Guid.NewGuid();
        var limited = Guid.NewGuid();
        InsertProfile(kid, ProfileRole.RestrictedProfile, null);
        InsertProfile(limited, ProfileRole.StandardUser, "PG");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _pins.SetPinAsync(kid, NsfwPin, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _pins.SetPinAsync(limited, NsfwPin, null));
        await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(kid, NsfwAccessMode.Pin));
        await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(limited, NsfwAccessMode.Allowed));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234567890123")]
    [InlineData("12a45")]
    [InlineData("")]
    public async Task APinThatIsNotFourToTwelveDigits_IsRefused(string pin)
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);

        await Assert.ThrowsAsync<ArgumentException>(() => _pins.SetPinAsync(_adult, pin, null));
    }

    [Fact]
    public async Task ThePinIsStoredHashedInItsOwnRecord_NeverAsDigits_AndNeverInTheProfilePinRecord()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        using var connection = _database.CreateConnection();
        var stored = connection.QuerySingle<string>("SELECT secret_hash FROM profile_nsfw_pins;");
        Assert.DoesNotContain(NsfwPin, stored);
        Assert.NotEqual(NsfwPin, stored);
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profile_credentials;"));
    }

    [Fact]
    public async Task TheSameDigitsHashDifferentlyAsProfilePinAndNsfwPin_SoOneHashCannotBeReplayedForTheOther()
    {
        // The two records are salted and prefixed differently, so even identical digits (which are refused anyway) never share a hash.
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _identity.SetProfilePinAsync(_adult, ProfilePin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        using var connection = _database.CreateConnection();
        var profileHash = connection.QuerySingle<string>("SELECT secret_hash FROM profile_credentials;");
        var nsfwHash = connection.QuerySingle<string>("SELECT secret_hash FROM profile_nsfw_pins;");
        Assert.NotEqual(profileHash, nsfwHash);
    }

    [Fact]
    public async Task ARightPinPasses_AWrongOneDoesNot()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        await _pins.VerifyPinAsync(_adult, NsfwPin);
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _pins.VerifyPinAsync(_adult, "00000"));
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _pins.VerifyPinAsync(_adult, null));
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _pins.VerifyPinAsync(_adult, ""));
    }

    [Fact]
    public async Task FiveWrongTries_PauseUnlocking_EvenForTheRightPin_UntilThePauseEnds()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _pins.VerifyPinAsync(_adult, "00000"));
        }

        // The fifth wrong try is the one that pauses unlocking.
        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _pins.VerifyPinAsync(_adult, "00000"));
        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _pins.VerifyPinAsync(_adult, NsfwPin));

        _clock.Advance(Lockout - TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _pins.VerifyPinAsync(_adult, NsfwPin));

        _clock.Advance(TimeSpan.FromSeconds(2));
        await _pins.VerifyPinAsync(_adult, NsfwPin);
    }

    [Fact]
    public async Task ARightPinClearsTheWrongTryCount()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        for (var round = 0; round < 3; round++)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _pins.VerifyPinAsync(_adult, "00000"));
            }

            await _pins.VerifyPinAsync(_adult, NsfwPin);
        }
    }

    [Fact]
    public async Task ParallelWrongGuesses_CannotSlipPastTheFiveTryLimit()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ =>
        {
            try
            {
                await _pins.VerifyPinAsync(_adult, "00000");
                return "passed";
            }
            catch (ProfilePinLockedException)
            {
                return "locked";
            }
            catch (ProfilePinRequiredException)
            {
                return "wrong";
            }
        }));

        Assert.DoesNotContain("passed", outcomes);
        Assert.True(outcomes.Count(outcome => outcome == "wrong") <= 4, "Only four guesses may be answered as plain wrong tries.");
        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _pins.VerifyPinAsync(_adult, NsfwPin));
    }

    [Fact]
    public async Task WrongNsfwTries_NeverPauseTheProfilePin_AndTheOtherWayRound()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _identity.SetProfilePinAsync(_adult, ProfilePin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => _pins.VerifyPinAsync(_adult, "00000"));
        }

        await _identity.VerifyProfilePinAsync(_adult, ProfilePin, countsTowardLockout: true);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => _identity.VerifyProfilePinAsync(_adult, "00000", countsTowardLockout: true));
        }

        _clock.Advance(Lockout + TimeSpan.FromSeconds(1));
        await _pins.VerifyPinAsync(_adult, NsfwPin);
    }

    [Fact]
    public async Task AProfileWithNoNsfwPin_CanNeverPass_WhateverItTypes()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);

        foreach (var guess in new[] { null, "", "0000", ProfilePin, NsfwPin })
        {
            await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _pins.VerifyPinAsync(_adult, guess));
        }
    }

    [Fact]
    public async Task TheNsfwPinCannotBeTheSameDigitsAsTheProfilePin()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _identity.SetProfilePinAsync(_adult, ProfilePin);

        await Assert.ThrowsAsync<ArgumentException>(() => _pins.SetPinAsync(_adult, ProfilePin, null));

        Assert.False(await _pins.HasPinAsync(_adult));
    }

    [Fact]
    public async Task TheProfilePinCannotBeTheSameDigitsAsTheNsfwPin()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        await Assert.ThrowsAsync<ArgumentException>(() => _identity.SetProfilePinAsync(_adult, NsfwPin));

        await _identity.SetProfilePinAsync(_adult, ProfilePin);
        await _pins.VerifyPinAsync(_adult, NsfwPin);
    }

    [Fact]
    public async Task SavingMatchingPins_CannotBeUsedToGuessTheOtherPin()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _identity.SetProfilePinAsync(_adult, ProfilePin);

        // Every "is this your profile PIN" answer costs a try, so five of them pause the profile PIN like five wrong sign-ins do.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _pins.SetPinAsync(_adult, ProfilePin, null));
        }

        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _identity.VerifyProfilePinAsync(_adult, ProfilePin, countsTowardLockout: true));
        // And while it is paused the comparison itself is refused, so it answers nothing.
        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _pins.SetPinAsync(_adult, ProfilePin, null));
        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _pins.SetPinAsync(_adult, "99999", null));
    }

    [Fact]
    public async Task SavingTheOtherPinAfterOneWrongSaveAttempt_StillWorks()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _identity.SetProfilePinAsync(_adult, ProfilePin);

        await Assert.ThrowsAsync<ArgumentException>(() => _pins.SetPinAsync(_adult, ProfilePin, null));
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        await _pins.VerifyPinAsync(_adult, NsfwPin);
    }

    [Fact]
    public async Task ChangingAPinNeedsTheCurrentPin_AndCountsAsATry()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _pins.SetPinAsync(_adult, "97531", null));
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _pins.SetPinAsync(_adult, "97531", "00000"));
        await _pins.VerifyPinAsync(_adult, NsfwPin);

        await _pins.SetPinAsync(_adult, "97531", NsfwPin);

        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _pins.VerifyPinAsync(_adult, NsfwPin));
        await _pins.VerifyPinAsync(_adult, "97531");
    }

    [Fact]
    public async Task AnAdministratorCanResetAPinWithoutReadingIt_AndTheProfileThenSetsANewOne()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);

        Assert.True(await _pins.ResetPinAsync(_adult));
        Assert.False(await _pins.ResetPinAsync(_adult));

        Assert.False(await _pins.HasPinAsync(_adult));
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _pins.VerifyPinAsync(_adult, NsfwPin));
        await _pins.SetPinAsync(_adult, "86420", null);
        await _pins.VerifyPinAsync(_adult, "86420");
    }

    [Fact]
    public async Task ResettingAPinAlsoEndsEveryUnlockItBacked()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Pin, 15, _clock.GetUtcNow());
        Assert.True(await _nsfw.IsUnlockedAsync(session, _adult, _clock.GetUtcNow()));

        await _pins.ResetPinAsync(_adult);

        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _clock.GetUtcNow()));
    }

    [Fact]
    public async Task ChangingAPin_EndsEveryUnlock()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);
        var session = InsertSession(_adult);
        await _nsfw.UnlockAsync(session, _adult, NsfwUnlockMethod.Pin, 15, _clock.GetUtcNow());

        await _pins.SetPinAsync(_adult, "97531", NsfwPin);

        Assert.False(await _nsfw.IsUnlockedAsync(session, _adult, _clock.GetUtcNow()));
    }

    [Fact]
    public async Task NoErrorMessage_EverContainsAPin()
    {
        await OpenAsync(_adult, NsfwAccessMode.Pin);
        await _identity.SetProfilePinAsync(_adult, ProfilePin);
        await _pins.SetPinAsync(_adult, NsfwPin, null);
        var messages = new List<string>();

        async Task Capture(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                messages.Add(exception.ToString());
            }
        }

        await Capture(() => _pins.VerifyPinAsync(_adult, "00000"));
        await Capture(() => _pins.SetPinAsync(_adult, ProfilePin, null));
        await Capture(() => _pins.SetPinAsync(_adult, "12", null));
        await Capture(() => _pins.SetPinAsync(_adult, "97531", "11111"));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Capture(() => _pins.VerifyPinAsync(_adult, "00000"));
        }

        await Capture(() => _pins.VerifyPinAsync(_adult, NsfwPin));

        Assert.NotEmpty(messages);
        foreach (var message in messages)
        {
            foreach (var pin in new[] { ProfilePin, NsfwPin, "00000", "97531", "11111" })
            {
                Assert.DoesNotContain(pin, message);
            }
        }
    }

    private async Task OpenAsync(Guid profileId, NsfwAccessMode mode) =>
        await _nsfw.SetAsync(profileId, mode, null, null, _clock.GetUtcNow());

    private void InsertProfile(Guid id, ProfileRole role, string? contentLimit)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@id,@name,'#7C4DFF',@role,@contentLimit,@createdAt);",
            new { id, name = role.ToString(), role = role.ToString(), contentLimit, createdAt = DateTimeOffset.UtcNow.ToString("O") });
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
}
