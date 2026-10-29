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
/// The sign-in that first-run setup starts right after creating the administrator goes straight on to the next setup step: the household
/// has one profile, so there is nobody to choose between. After setup a household with one profile and no PIN also goes straight in; the
/// picker appears only when there is more than one profile to choose from or a PIN to enter (users, profiles and households spec D3).
/// </summary>
public sealed class SetupSignInHandOffTests : IDisposable
{
    private const string Email = "owner@example.com";
    private const string Password = "correct horse battery staple";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_handoff_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly SetupState _setup = new() { IsInProgress = true };
    private readonly FirstPartyIdentityService _service;

    public SetupSignInHandOffTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _service = new FirstPartyIdentityService(
            new IdentityRepository(_database), _accounts, new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            TimeProvider.System, new FixedPolicy(), firstRunSetup: _setup);
    }

    [Fact]
    public async Task PasswordSignIn_DuringSetup_WithOneProfile_DoesNotAskWhoIsUsingLibrary_EvenWithAPin()
    {
        await _service.BootstrapAdministratorAsync(Email, Password, "Dad", "setup-device", "Setup", "Dashboard", pin: "4821");

        var result = await _service.AuthenticatePasswordAsync(Email, Password, "laptop", "laptop", "Dashboard");

        Assert.True(result.Succeeded);
        Assert.False(result.IssuedSession!.ChooseProfile);
        Assert.False(result.IssuedSession.Session.ProfilePending);
    }

    [Fact]
    public async Task ThisComputerSignIn_DuringSetup_WithOneProfile_DoesNotAskWhoIsUsingLibrary()
    {
        await _service.BootstrapThisComputerAdministratorAsync(null, "Dad", "setup-device", "Setup", "Dashboard");

        var signedIn = await _service.SignInThisComputerAccountAsync("laptop", "Laptop", "Dashboard", ClientIngress.ThisComputer);

        Assert.NotNull(signedIn);
        Assert.False(signedIn!.ChooseProfile);
        Assert.False(signedIn.Session.ProfilePending);
    }

    [Fact]
    public async Task SignInAfterSetupIsDone_WithOneProfileAndNoPin_StillGoesStraightIn()
    {
        await _service.BootstrapAdministratorAsync(Email, Password, "Dad", "setup-device", "Setup", "Dashboard");
        _setup.IsInProgress = false;

        var result = await _service.AuthenticatePasswordAsync(Email, Password, "laptop", "laptop", "Dashboard");

        Assert.False(result.IssuedSession!.ChooseProfile);
        Assert.False(result.IssuedSession.Session.ProfilePending);
    }

    [Fact]
    public async Task SignInAfterSetupIsDone_WithOneProfileThatHasAPin_AsksForThePin()
    {
        await _service.BootstrapAdministratorAsync(Email, Password, "Dad", "setup-device", "Setup", "Dashboard", pin: "4821");
        _setup.IsInProgress = false;

        var result = await _service.AuthenticatePasswordAsync(Email, Password, "laptop", "laptop", "Dashboard");

        Assert.True(result.IssuedSession!.ChooseProfile);
        Assert.True(result.IssuedSession.Session.ProfilePending);
    }

    [Fact]
    public async Task SignInDuringSetup_WithSeveralProfiles_StillAsksWhoIsUsingLibrary()
    {
        var owner = await _service.BootstrapAdministratorAsync(Email, Password, "Dad", "setup-device", "Setup", "Dashboard");
        var mary = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO profiles(id,display_name,avatar_color,avatar_image_path,role,created_at,navigation_config)
                VALUES(@mary,'Mary','#7C4DFF',NULL,'StandardUser',@now,NULL);
                UPDATE profiles SET household_id = (SELECT p.household_id FROM profiles p JOIN accounts a ON a.profile_id = p.id WHERE a.id = @accountId) WHERE id = @mary;
                """, new { mary, now = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"), accountId = owner.Account.Id });
        }

        var result = await _service.AuthenticatePasswordAsync(Email, Password, "laptop", "laptop", "Dashboard");

        Assert.True(result.IssuedSession!.ChooseProfile);
        Assert.True(result.IssuedSession.Session.ProfilePending);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }

    private sealed class SetupState : IFirstRunSetupState
    {
        public bool IsInProgress { get; set; }
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
