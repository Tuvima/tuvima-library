using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

/// <summary>
/// Setup writes the profile side of access next to the account side (users, profiles and households spec, wave UPH1a):
/// the first administrator's profile is the server and household administrator in the default household, and the sign-in opens it.
/// </summary>
public sealed class ProfileHeldAccessSetupTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_profile_held_setup_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ProfileAccessRepository _access;
    private readonly FirstPartyIdentityService _service;

    public ProfileHeldAccessSetupTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _access = new ProfileAccessRepository(_database);
        _service = new FirstPartyIdentityService(
            new IdentityRepository(_database),
            _accounts,
            new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(),
            new PasswordHasher<ProfileCredential>(),
            TimeProvider.System,
            new FixedPolicy());
    }

    [Fact]
    public async Task Setup_LeavesTheFirstAdministratorsProfileAsServerAndHouseholdAdministratorInTheDefaultHousehold()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "device", "Browser", "Dashboard");

        var account = Assert.Single(await _accounts.GetAllAsync());
        var profile = (await new ProfileRepository(_database).GetByIdAsync(account.ProfileId))!;
        Assert.Equal(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, profile.Id);
        Assert.True(profile.IsServerAdministrator);
        Assert.True(profile.IsHouseholdAdministrator);
        Assert.Equal(AccountFeatureId.All.OrderBy(feature => feature.Value), (await _access.GetAreasAsync(profile.Id)).OrderBy(feature => feature.Value));
        Assert.NotNull(profile.HouseholdId);
        Assert.Equal(profile.HouseholdId, (await new HouseholdRepository(_database).GetForAccountAsync(account.Id))!.Id);
        Assert.Equal(profile.HouseholdId, await _access.GetDefaultHouseholdIdAsync());
        Assert.Equal(account.Id, await _access.FindUserIdOfProfileAsync(profile.Id));
    }

    [Fact]
    public async Task Setup_WithoutAPassword_AlsoAttachesTheSignInToItsProfile()
    {
        await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");

        var account = Assert.Single(await _accounts.GetAllAsync());
        Assert.Equal(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, account.ProfileId);
        var flags = (await _access.GetAdminFlagsAsync(account.ProfileId))!;
        Assert.True(flags.IsServerAdministrator);
        Assert.True(flags.IsHouseholdAdministrator);
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
