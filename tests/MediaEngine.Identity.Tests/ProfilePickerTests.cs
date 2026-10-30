using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

/// <summary>
/// Signing in and switching people (users, profiles and households spec D3, D12, D13): every interactive sign-in leaves the session
/// "choose a profile" (pending) with the user's own profile as the one to pre-select; nothing is in use until the person picks a
/// profile of the sign-in's own household (its PIN when set; Kids included). There is no per-device "always open as" any more.
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
    public async Task ThisComputerSignIn_WithSeveralProfiles_LeavesTheSessionPending_WithTheUsersOwnProfileToPreSelect_AndRecordsTheHomeNetwork()
    {
        var owner = await _service.BootstrapThisComputerAdministratorAsync(Email, "Dad", "setup-device", "Setup", "Dashboard");
        var mary = new Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Mary",
            AvatarColor = "#7C4DFF",
            Role = ProfileRole.StandardUser,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        await InsertProfileAsync(mary);
        await JoinHouseholdOfAsync(owner.Account.Id, mary.Id);

        var signedIn = await _service.SignInThisComputerAccountAsync("laptop", "Laptop", "Dashboard", ClientIngress.HomeNetwork);

        Assert.NotNull(signedIn);
        Assert.True(signedIn!.ChooseProfile);
        Assert.True(signedIn.Session.ProfilePending);
        Assert.Equal(owner.Profile.Id, signedIn.ActiveProfile.Id);
        Assert.Equal(ClientIngress.HomeNetwork, signedIn.Session.IssuedIngress);
    }

    [Fact]
    public async Task PasswordSignIn_WithSeveralProfilesInTheHousehold_LeavesTheSessionPending_WithTheUsersOwnProfileToPreSelect()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 2);

        var signedIn = await SignInAsync("laptop");

        Assert.True(signedIn.ChooseProfile);
        Assert.True(signedIn.Session.ProfilePending);
        Assert.Equal(owner.Profile.Id, signedIn.ActiveProfile.Id);
        Assert.True((await _service.ValidateSessionAsync(signedIn.PlaintextToken))!.Session.ProfilePending);
    }

    [Fact]
    public async Task PasswordSignIn_WithOneProfileAndNoPin_GoesStraightIn_WithoutAPicker()
    {
        await BootstrapWithProfilesAsync(extraProfiles: 0);

        var signedIn = await SignInAsync("laptop");

        Assert.False(signedIn.ChooseProfile);
        Assert.False(signedIn.Session.ProfilePending);
    }

    [Fact]
    public async Task PasswordSignIn_WithOneProfileThatHasAPin_StillAsksForThePin()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 0);
        await _service.SetProfilePinAsync(owner.Profile.Id, "2468");

        var signedIn = await SignInAsync("laptop");

        Assert.True(signedIn.ChooseProfile);
        Assert.True(signedIn.Session.ProfilePending);
    }

    [Fact]
    public async Task EveryUsersSignIn_PreSelectsThatUsersOwnProfile_WhateverDeviceOrWayItSignedInBy()
    {
        var dad = await BootstrapWithProfilesAsync(extraProfiles: 1);
        var mom = await AddUserInHouseholdOfAsync(dad.Account.Id, "Mom");

        var dadPasskey = await _service.CreatePasskeySessionAsync(dad.Account.Id, "tv", "TV", "Dashboard");
        var momPasskey = await _service.CreatePasskeySessionAsync(mom.Id, "tv", "TV", "Dashboard");
        var momOutside = await _service.CreateExternalSessionAsync(mom.Id, "google", "phone", "Phone", "Dashboard");

        Assert.All(new[] { dadPasskey, momPasskey, momOutside }, issued =>
        {
            Assert.True(issued.ChooseProfile);
            Assert.True(issued.Session.ProfilePending);
        });
        Assert.Equal(dad.Profile.Id, dadPasskey.ActiveProfile.Id);
        Assert.Equal(mom.ProfileId, momPasskey.ActiveProfile.Id);
        Assert.Equal(mom.ProfileId, momOutside.ActiveProfile.Id);
    }

    [Fact]
    public async Task ChoosingAProfile_EndsThePendingState_AndOnlyTheHouseholdsProfilesCanBeChosen()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 1);
        var mary = (await ExtraProfilesAsync(owner.Account.Id))[0];
        var signedIn = await SignInAsync("laptop");
        var outsider = new Profile { Id = Guid.NewGuid(), DisplayName = "Outsider", AvatarColor = "#111111", Role = ProfileRole.StandardUser, CreatedAt = DateTimeOffset.UtcNow };
        await InsertProfileAsync(outsider);

        // Another household's profile is refused exactly like an id that does not exist, and the session stays pending.
        var other = await Assert.ThrowsAnyAsync<Exception>(() => _service.SwitchActiveProfileAsync(signedIn.PlaintextToken, outsider.Id, null));
        var unknown = await Assert.ThrowsAnyAsync<Exception>(() => _service.SwitchActiveProfileAsync(signedIn.PlaintextToken, Guid.NewGuid(), null));
        Assert.IsType<UnauthorizedAccessException>(other);
        Assert.IsType<UnauthorizedAccessException>(unknown);
        Assert.True((await _service.ValidateSessionAsync(signedIn.PlaintextToken))!.Session.ProfilePending);

        var chosen = await _service.SwitchActiveProfileAsync(signedIn.PlaintextToken, mary.Id, null);

        Assert.Equal(mary.Id, chosen.ActiveProfile.Id);
        Assert.False(chosen.Session.ProfilePending);
        Assert.False((await _service.ValidateSessionAsync(signedIn.PlaintextToken))!.Session.ProfilePending);
    }

    [Fact]
    public async Task ChoosingTheUsersOwnPreSelectedProfile_WhenItHasAPin_AsksForThePinFirst()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 0);
        await _service.SetProfilePinAsync(owner.Profile.Id, "2468");
        var signedIn = await SignInAsync("laptop");

        await Assert.ThrowsAsync<ProfilePinRequiredException>(() =>
            _service.SwitchActiveProfileAsync(signedIn.PlaintextToken, owner.Profile.Id, null));
        Assert.True((await _service.ValidateSessionAsync(signedIn.PlaintextToken))!.Session.ProfilePending);

        await _service.SwitchActiveProfileAsync(signedIn.PlaintextToken, owner.Profile.Id, "2468");

        Assert.False((await _service.ValidateSessionAsync(signedIn.PlaintextToken))!.Session.ProfilePending);
    }

    [Fact]
    public async Task SwitchingToAProfileWithAPin_StillAsksForThePin_AndAProfileWithoutOneOpensFreely()
    {
        var owner = await BootstrapWithProfilesAsync(extraProfiles: 2);
        var extras = await ExtraProfilesAsync(owner.Account.Id);
        var mary = extras[0];
        var sue = extras[1];
        await _service.SetProfilePinAsync(mary.Id, "2468");
        var signedIn = await SignInAsync("laptop");

        await Assert.ThrowsAsync<ProfilePinRequiredException>(() =>
            _service.SwitchActiveProfileAsync(signedIn.PlaintextToken, mary.Id, null));
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() =>
            _service.SwitchActiveProfileAsync(signedIn.PlaintextToken, mary.Id, "0000"));
        Assert.Equal(owner.Profile.Id, (await _service.ValidateSessionAsync(signedIn.PlaintextToken))!.Session.ActiveProfileId);

        var opened = await _service.SwitchActiveProfileAsync(signedIn.PlaintextToken, mary.Id, "2468");
        Assert.Equal(mary.Id, opened.ActiveProfile.Id);
        Assert.False((await _service.ValidateSessionAsync(signedIn.PlaintextToken))!.Session.ProfilePending);

        // A Kids profile switches into an adult profile that has no PIN without being asked for anything.
        Assert.Equal(ProfileRole.RestrictedProfile, sue.Role);
        await _service.SwitchActiveProfileAsync(signedIn.PlaintextToken, sue.Id, null);
        var adult = await _service.SwitchActiveProfileAsync(signedIn.PlaintextToken, owner.Profile.Id, null);
        Assert.Equal(owner.Profile.Id, adult.ActiveProfile.Id);
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
            await JoinHouseholdOfAsync(owner.Account.Id, profile.Id);
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

    /// <summary>A second sign-in whose own profile is in the household of the profile the given user opens (Mom beside Dad).</summary>
    private async Task<Account> AddUserInHouseholdOfAsync(Guid accountId, string name)
    {
        var profile = new Profile { Id = Guid.NewGuid(), DisplayName = name, AvatarColor = "#222222", Role = ProfileRole.StandardUser, CreatedAt = DateTimeOffset.UtcNow };
        await InsertProfileAsync(profile);
        await JoinHouseholdOfAsync(accountId, profile.Id);
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = $"{name.ToLowerInvariant()}@example.com",
            NormalizedEmail = $"{name.ToUpperInvariant()}@EXAMPLE.COM",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ProfileId = profile.Id,
        };
        await _accounts.InsertAsync(account);
        return account;
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
            profile.Id,
            profile.DisplayName,
            profile.AvatarColor,
            profile.AvatarImagePath,
            Role = profile.Role.ToString(),
            CreatedAt = profile.CreatedAt.ToString("O"),
            profile.NavigationConfig,
        });
        return Task.CompletedTask;
    }

    /// <summary>Puts a profile into the household of the profile the user opens, so the user may act as it.</summary>
    private Task JoinHouseholdOfAsync(Guid accountId, Guid profileId)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "UPDATE profiles SET household_id = (SELECT p.household_id FROM profiles p JOIN accounts a ON a.profile_id = p.id WHERE a.id = @accountId) WHERE id = @profileId;",
            new { accountId, profileId });
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
