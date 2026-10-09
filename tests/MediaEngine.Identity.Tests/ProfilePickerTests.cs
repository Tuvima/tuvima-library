using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

/// <summary>
/// "Who's using Tuvima?": after sign-in a household with several people is asked who is watching, unless this device was
/// set to always open as one person. That choice belongs to one account on one device and never skips a PIN.
/// </summary>
public sealed class ProfilePickerTests : IDisposable
{
    private const string Email = "owner@example.com";
    private const string Password = "correct horse battery staple";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_picker_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly FirstPartyIdentityService _service;

    public ProfilePickerTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _service = new FirstPartyIdentityService(
            new IdentityRepository(_database), _accounts, new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            TimeProvider.System, new FixedPolicy());
    }

    [Fact]
    public async Task SignIn_WithSeveralProfiles_AsksWhoIsUsingTuvima()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 2);

        var signedIn = await SignInAsync("laptop");

        Assert.True(signedIn.ChooseProfile);
        Assert.Equal(owner.Profile.Id, signedIn.ActiveProfile.Id);
    }

    [Fact]
    public async Task SignIn_WithOneProfile_GoesStraightIn()
    {
        await BootstrapWithProfilesAsync(extraProfiles: 0);

        Assert.False((await SignInAsync("laptop")).ChooseProfile);
    }

    [Fact]
    public async Task AlwaysOpenAs_StartsTheNextSignInInThatProfile_OnlyOnThatDevice()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 2);
        var mary = (await ExtraProfilesAsync(owner.Account.Id))[0];
        var laptop = await SignInAsync("laptop");
        await _service.SetDeviceProfilePreferenceAsync(owner.Account.Id, laptop.Session.Id, mary.Id);

        var again = await SignInAsync("laptop");
        var tv = await SignInAsync("living-room-tv");

        Assert.False(again.ChooseProfile);
        Assert.Equal(mary.Id, again.ActiveProfile.Id);
        Assert.True(tv.ChooseProfile);
        Assert.Equal(owner.Profile.Id, tv.ActiveProfile.Id);
        Assert.Null(await _service.GetDeviceProfilePreferenceAsync(owner.Account.Id, tv.Session.Id));
    }

    [Fact]
    public async Task AlwaysOpenAs_ForAProfileWithAPin_AsksForThePinInsteadOfSkippingIt()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 2);
        var mary = (await ExtraProfilesAsync(owner.Account.Id))[0];
        await _service.SetProfilePinAsync(mary.Id, "2468");
        var laptop = await SignInAsync("laptop");
        await _service.SetDeviceProfilePreferenceAsync(owner.Account.Id, laptop.Session.Id, mary.Id);

        var again = await SignInAsync("laptop");

        Assert.True(again.ChooseProfile);
        Assert.Equal(owner.Profile.Id, again.ActiveProfile.Id);
        Assert.Equal(mary.Id, await _service.GetDeviceProfilePreferenceAsync(owner.Account.Id, again.Session.Id));

        // The session stays "not yet chosen" until the person is switched to someone, with their PIN.
        Assert.True((await _service.ValidateSessionAsync(again.PlaintextToken))!.Session.ProfilePending);
        await _service.SwitchActiveProfileAsync(again.PlaintextToken, mary.Id, "2468");
        Assert.False((await _service.ValidateSessionAsync(again.PlaintextToken))!.Session.ProfilePending);
    }

    [Fact]
    public async Task StopAlwaysOpeningAs_AsksAgainNextTime()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 2);
        var mary = (await ExtraProfilesAsync(owner.Account.Id))[0];
        var laptop = await SignInAsync("laptop");
        await _service.SetDeviceProfilePreferenceAsync(owner.Account.Id, laptop.Session.Id, mary.Id);

        Assert.True(await _service.ClearDeviceProfilePreferenceAsync(owner.Account.Id, laptop.Session.Id));

        Assert.True((await SignInAsync("laptop")).ChooseProfile);
    }

    [Fact]
    public async Task APreferenceForAProfileNoLongerGranted_IsIgnored()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 2);
        var mary = (await ExtraProfilesAsync(owner.Account.Id))[0];
        var laptop = await SignInAsync("laptop");
        await _service.SetDeviceProfilePreferenceAsync(owner.Account.Id, laptop.Session.Id, mary.Id);
        await _accounts.RevokeProfileAsync(owner.Account.Id, mary.Id);

        var again = await SignInAsync("laptop");

        Assert.True(again.ChooseProfile);
        Assert.Null(await _service.GetDeviceProfilePreferenceAsync(owner.Account.Id, again.Session.Id));
    }

    [Fact]
    public async Task PreferenceActions_CannotTouchAnotherAccountsSessionOrAnUngrantedProfile()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 2);
        var mary = (await ExtraProfilesAsync(owner.Account.Id))[0];
        var laptop = await SignInAsync("laptop");
        await _service.SetDeviceProfilePreferenceAsync(owner.Account.Id, laptop.Session.Id, mary.Id);
        var stranger = new Account
        {
            Id = Guid.NewGuid(), Email = "stranger@example.com", NormalizedEmail = "STRANGER@EXAMPLE.COM",
            IsEnabled = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _accounts.InsertAsync(stranger);
        var outsider = new Profile { Id = Guid.NewGuid(), DisplayName = "Outsider", AvatarColor = "#111111", Role = ProfileRole.StandardUser, CreatedAt = DateTimeOffset.UtcNow };
        await InsertProfileAsync(outsider);

        // Another account naming the owner's session reads nothing and changes nothing.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetDeviceProfilePreferenceAsync(stranger.Id, laptop.Session.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SetDeviceProfilePreferenceAsync(stranger.Id, laptop.Session.Id, mary.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ClearDeviceProfilePreferenceAsync(stranger.Id, laptop.Session.Id));
        // The owner cannot point a device at a profile the account does not have.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SetDeviceProfilePreferenceAsync(owner.Account.Id, laptop.Session.Id, outsider.Id));
        Assert.Equal(mary.Id, await _service.GetDeviceProfilePreferenceAsync(owner.Account.Id, laptop.Session.Id));
    }

    [Fact]
    public async Task ProfilesWithAPin_AreReportedSoThePickerCanShowALock()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 2);
        var extras = await ExtraProfilesAsync(owner.Account.Id);
        await _service.SetProfilePinAsync(extras[1].Id, "2468");

        var withPin = await _service.GetProfileIdsWithPinAsync([owner.Profile.Id, extras[0].Id, extras[1].Id]);

        Assert.Equal(new[] { extras[1].Id }, withPin.ToArray());
        Assert.Empty(await _service.GetProfileIdsWithPinAsync([]));
    }

    private async Task<SessionIssueResult> BootstrapWithProfilesAsync(int extraProfiles)
    {
        var owner = await _service.BootstrapAdministratorAsync(Email, Password, "Dad", "setup-device", "Setup", "Dashboard");
        for (var index = 0; index < extraProfiles; index++)
        {
            var profile = new Profile
            {
                Id = Guid.NewGuid(),
                DisplayName = index == 0 ? "Mary" : "Sue",
                AvatarColor = "#7C4DFF",
                Role = index == 0 ? ProfileRole.StandardUser : ProfileRole.RestrictedProfile,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(index + 1),
            };
            await InsertProfileAsync(profile);
            await _accounts.GrantProfileAsync(new AccountProfileGrant
            {
                AccountId = owner.Account.Id, ProfileId = profile.Id, GrantedAt = DateTimeOffset.UtcNow.AddMinutes(index + 1),
            });
        }

        return owner;
    }

    private async Task<IReadOnlyList<Profile>> ExtraProfilesAsync(Guid accountId)
    {
        var profiles = new ProfileRepository(_database);
        var result = new List<Profile>();
        foreach (var id in await _accounts.GetProfileIdsAsync(accountId))
        {
            if (await profiles.GetByIdAsync(id) is { DisplayName: "Mary" or "Sue" } profile)
            {
                result.Add(profile);
            }
        }

        return [.. result.OrderBy(profile => profile.DisplayName == "Mary" ? 0 : 1)];
    }

    private async Task<SessionIssueResult> SignInAsync(string deviceId)
    {
        var result = await _service.AuthenticatePasswordAsync(Email, Password, deviceId, deviceId, "Dashboard");
        Assert.True(result.Succeeded);
        return result.IssuedSession!;
    }

    private Task InsertProfileAsync(Profile profile)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles(id,display_name,avatar_color,avatar_image_path,role,created_at,navigation_config)
            VALUES(@Id,@DisplayName,@AvatarColor,@AvatarImagePath,@Role,@CreatedAt,@NavigationConfig);
            """, new
        {
            profile.Id, profile.DisplayName, profile.AvatarColor, profile.AvatarImagePath,
            Role = profile.Role.ToString(), CreatedAt = profile.CreatedAt.ToString("O"), profile.NavigationConfig,
        });
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _database.Dispose();
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
