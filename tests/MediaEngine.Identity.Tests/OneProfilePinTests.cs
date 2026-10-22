using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

/// <summary>
/// One PIN per profile (owner decision, 2026-10-21): the profile PIN is the only PIN. It protects switching to the profile, and,
/// when the person turns the matching switch on, their admin pages and Vault mode. Every wrong try counts toward the same pause.
/// </summary>
public sealed class OneProfilePinTests : IDisposable
{
    private const string Pin = "2468";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_onepin_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly NsfwAccessRepository _vault;
    private readonly FirstPartyIdentityService _service;

    public OneProfilePinTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _vault = new NsfwAccessRepository(_database);
        _service = new FirstPartyIdentityService(
            new IdentityRepository(_database), _accounts, new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            TimeProvider.System, new FixedPolicy(), nsfwAccess: _vault);
    }

    [Fact]
    public async Task HasProfilePin_FollowsTheProfilePin_SoACallerCanFailClosedWhenThereIsNone()
    {
        var owner = await BootstrapAsync();

        Assert.False(await _service.HasProfilePinAsync(owner.Profile.Id));

        await _service.SetProfilePinAsync(owner.Profile.Id, Pin);
        Assert.True(await _service.HasProfilePinAsync(owner.Profile.Id));

        await _service.SetProfilePinAsync(owner.Profile.Id, null);
        Assert.False(await _service.HasProfilePinAsync(owner.Profile.Id));
    }

    [Fact]
    public async Task ProfilePin_CanBeRemoved_WhileBothSwitchesAreOff_AndWhileVaultModeNeedsNoPin()
    {
        var owner = await BootstrapAsync();
        await _service.SetProfilePinAsync(owner.Profile.Id, Pin);

        // Both switches off: nothing depends on the PIN.
        await _service.SetProfilePinAsync(owner.Profile.Id, null);
        Assert.False(await _service.HasProfilePinAsync(owner.Profile.Id));

        // Vault mode open to anyone using the profile also needs no PIN, so the PIN can still go.
        await _service.SetProfilePinAsync(owner.Profile.Id, Pin);
        await _vault.SetAsync(owner.Profile.Id, NsfwAccessMode.Allowed, 60, owner.Profile.Id, DateTimeOffset.UtcNow);
        await _service.SetProfilePinAsync(owner.Profile.Id, null);
        Assert.False(await _service.HasProfilePinAsync(owner.Profile.Id));
    }

    [Fact]
    public async Task ProfilePin_CannotBeRemoved_WhileVaultModeAsksForIt()
    {
        var owner = await BootstrapAsync();
        await _service.SetProfilePinAsync(owner.Profile.Id, Pin);
        await _vault.SetAsync(owner.Profile.Id, NsfwAccessMode.Pin, 60, owner.Profile.Id, DateTimeOffset.UtcNow);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetProfilePinAsync(owner.Profile.Id, null));

        Assert.Contains("Ask for my PIN to open Vault mode", refused.Message, StringComparison.Ordinal);
        Assert.True(await _service.HasProfilePinAsync(owner.Profile.Id));
    }

    [Fact]
    public async Task ProfilePin_CannotBeRemoved_WhileAdminPagesAskForIt()
    {
        var owner = await BootstrapAsync();
        await _service.SetProfilePinAsync(owner.Profile.Id, Pin);
        await _accounts.SetAdminProtectionAsync(new GrantAdminProtection
        {
            AccountId = owner.Account.Id,
            ProfileId = owner.Profile.Id,
            IsEnabled = true,
            UnlockMode = "FixedDuration",
            UnlockMinutes = 30,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetProfilePinAsync(owner.Profile.Id, null));

        Assert.Contains("Ask for my PIN on admin pages", refused.Message, StringComparison.Ordinal);
        Assert.True(await _service.HasProfilePinAsync(owner.Profile.Id));
    }

    [Fact]
    public async Task WrongPinTries_AtAnyDoor_CountTogether_AndPauseTheSamePin()
    {
        var owner = await BootstrapAsync();
        await _service.SetProfilePinAsync(owner.Profile.Id, Pin);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            // The admin-page door and the Vault door both ask the same verifier and both count toward the pause.
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => _service.VerifyProfilePinAsync(owner.Profile.Id, "0000", countsTowardLockout: true));
        }

        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _service.VerifyProfilePinAsync(owner.Profile.Id, Pin, countsTowardLockout: true));
    }

    [Fact]
    public async Task RightPin_Passes_AndClearsEarlierWrongTries()
    {
        var owner = await BootstrapAsync();
        await _service.SetProfilePinAsync(owner.Profile.Id, Pin);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => _service.VerifyProfilePinAsync(owner.Profile.Id, "0000", countsTowardLockout: true));
        }

        await _service.VerifyProfilePinAsync(owner.Profile.Id, Pin, countsTowardLockout: true);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => _service.VerifyProfilePinAsync(owner.Profile.Id, "0000", countsTowardLockout: true));
        await _service.VerifyProfilePinAsync(owner.Profile.Id, Pin, countsTowardLockout: true);
    }

    private Task<SignedInOwner> BootstrapAsync() => BootstrapCoreAsync();

    private async Task<SignedInOwner> BootstrapCoreAsync()
    {
        var owner = await _service.BootstrapThisComputerAdministratorAsync("owner@example.com", "Dad", "setup-device", "Setup", "Dashboard");
        return new SignedInOwner(owner.Account, owner.Profile);
    }

    private sealed record SignedInOwner(Account Account, Profile Profile);

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
