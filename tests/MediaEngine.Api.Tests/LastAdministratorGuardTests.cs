using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The server must always keep one administrator who can sign in: switching one off, taking administrator away, removing
/// their administrator access or deleting them (or the person they administer as) is refused when nobody else could still
/// sign in as an administrator, and allowed once somebody else can.
/// </summary>
public sealed class LastAdministratorGuardTests : IAsyncDisposable
{
    private const string Password = "a long temporary password";
    private const string Refusal = "This is the last administrator who can sign in. Make someone else an administrator first.";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-last-admin-{Guid.NewGuid():N}.db");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-last-admin-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly AccountRepository _accounts;
    private readonly AccountAccessMutationService _mutations;
    private readonly RequestAuthority _actor = new(PrincipalKind.Human, true,
        AccountId: Guid.NewGuid(),
        ActiveProfileId: Guid.NewGuid(),
        SessionId: Guid.NewGuid(),
        AccountEnabled: true,
        ActiveProfileInUserHousehold: true,
        ActiveProfileIsServerAdministrator: true);
    private readonly SlowAfterCheck _slowCheck;
    private int _counter;

    public LastAdministratorGuardTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _configuration = new ConfigurationDirectoryLoader(_configPath);
        _database.InitializeSchema();
        _accounts = new AccountRepository(_database);
        var identities = new IdentityRepository(_database);
        var profiles = new ProfileRepository(_database);
        var identity = new FirstPartyIdentityService(
            identities, _accounts, profiles,
            new PasswordHasher<AccountCredential>(),
            new PasswordHasher<ProfileCredential>(),
            TimeProvider.System,
            new ConfigurationAuthenticationPolicyProvider(_configuration));
        var usable = new UsableAdministratorService(
            _accounts, identities,
            new AccountExternalLoginService(new AccountExternalLoginRepository(_database), _accounts),
            null!, new AuthenticationProviderConfigurationService(_configuration), _configuration, TimeProvider.System);
        _slowCheck = new SlowAfterCheck(usable);
        _mutations = new AccountAccessMutationService(
            _accounts,
                        new ProfileAccessRepository(_database), identities, profiles, _configuration,
            new AllowAdministratorDecisions(), new AllowEvaluator(),
            new NoOpInvalidation(), new NoOpAudit(),
            identity, new NoProfilePhotos(), TimeProvider.System, _slowCheck, new AuthenticationPolicyMutationGate());
    }

    // ---- Disabling and demoting ----

    [Fact]
    public async Task DisablingTheOnlyAdministrator_IsRefused_AndTheAccountStaysOn()
    {
        var only = await CreateAdministratorAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.UpdateAsync(_actor, only.Id, new UpdateAccountAccessCommand(only.Email, false, true)));

        Assert.Equal(Refusal, refused.Message);
        Assert.True((await _accounts.GetByIdAsync(only.Id))!.IsEnabled);
    }

    [Fact]
    public async Task TakingAdministratorAwayFromTheOnlyAdministrator_IsRefused_AndTheyStayAnAdministrator()
    {
        var only = await CreateAdministratorAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.UpdateAsync(_actor, only.Id, new UpdateAccountAccessCommand(only.Email, true, false)));

        Assert.Equal(Refusal, refused.Message);
        Assert.True((await _accounts.GetByIdAsync(only.Id))!.IsAdministrator);
    }

    [Fact]
    public async Task AnAdministratorCanBeDisabledOrDemoted_OnceAnotherCanSignIn_ButNotTheLastOneLeft()
    {
        var first = await CreateAdministratorAsync();
        var second = await CreateAdministratorAsync();

        await _mutations.UpdateAsync(_actor, first.Id, new UpdateAccountAccessCommand(first.Email, false, true));
        Assert.False((await _accounts.GetByIdAsync(first.Id))!.IsEnabled);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.UpdateAsync(_actor, second.Id, new UpdateAccountAccessCommand(second.Email, true, false)));
        Assert.Equal(Refusal, refused.Message);
    }

    [Fact]
    public async Task ChangingAnAdministratorsEmail_IsNotBlockedByTheRule_EvenForTheOnlyAdministrator()
    {
        var only = await CreateAdministratorAsync();

        // Same account, still enabled and still an administrator: nothing is lost, so nothing is refused here.
        await _mutations.UpdateAsync(_actor, only.Id, new UpdateAccountAccessCommand(only.Email, true, true));

        Assert.True((await _accounts.GetByIdAsync(only.Id))!.IsAdministrator);
    }

    [Fact]
    public async Task AnotherAdministratorWhoCannotSignInUnderTheCurrentPolicy_DoesNotCountAsSomeoneElse()
    {
        var first = await CreateAdministratorAsync();
        await CreateAdministratorAsync();
        var core = _configuration.LoadCore();
        core.Auth.PasswordSignInEnabled = false;
        core.Auth.PasskeySignInEnabled = false;
        core.Auth.ExternalSignInEnabled = false;
        _configuration.SaveCore(core);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.UpdateAsync(_actor, first.Id, new UpdateAccountAccessCommand(first.Email, false, true)));

        Assert.Equal(Refusal, refused.Message);
    }

    // ---- Deleting ----

    [Fact]
    public async Task DeletingTheOnlyAdministrator_IsRefused_AndAllowedOnceAnotherCanSignIn()
    {
        var first = await CreateAdministratorAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _mutations.DeleteAsync(_actor, first.Id));
        Assert.Equal(Refusal, refused.Message);
        Assert.NotNull(await _accounts.GetByIdAsync(first.Id));

        var second = await CreateAdministratorAsync();
        // Each administrator here starts a household of their own and is its owner; a household's last owner has its own rule
        // (HouseholdOwnerDeletionTests), so the first one stops being an owner before they are deleted.
        await _mutations.SetHouseholdAdminAsync(_actor, first.Id, false);
        await _mutations.DeleteAsync(_actor, first.Id);

        Assert.Null(await _accounts.GetByIdAsync(first.Id));
        var last = await Assert.ThrowsAsync<InvalidOperationException>(() => _mutations.DeleteAsync(_actor, second.Id));
        Assert.Equal(Refusal, last.Message);
    }

    [Fact]
    public async Task DeletingAnOrdinaryAccount_IsNeverBlockedByTheRule()
    {
        await CreateAdministratorAsync();
        var member = await _mutations.CreateAsync(_actor, new CreateAccountAccessCommand(
            "member@example.com", IsAdministrator: false, ProfileId: null,
            NewProfile: new NewAccountProfileCommand("Member", "#112233"),
            Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            Libraries: new HashSet<Guid>(), TemporaryPassword: Password));
        // The member starts a household of their own and is its owner; keeping a household's last owner is a separate rule.
        await _mutations.SetHouseholdAdminAsync(_actor, member.Id, false);

        await _mutations.DeleteAsync(_actor, member.Id);

        Assert.Null(await _accounts.GetByIdAsync(member.Id));
    }

    // ---- Administrator access on a grant ----

    [Fact]
    public async Task SwitchingOffAdministratorAccessOnTheOnlyAdministratorsGrant_IsRefused_AndAllowedOnceAnotherCanSignIn()
    {
        var first = await CreateAdministratorAsync();
        var grant = await OwnGrantAsync(first);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.UpsertGrantAsync(_actor, WithoutAdministrator(grant)));
        Assert.Equal(Refusal, refused.Message);
        Assert.True((await _accounts.GetGrantAsync(first.Id, grant.ProfileId))!.AdminEnabled);
        Assert.True((await _accounts.GetByIdAsync(first.Id))!.IsAdministrator);

        await CreateAdministratorAsync();
        await _mutations.UpsertGrantAsync(_actor, WithoutAdministrator(grant));

        // The role is the profile's: switching it off on the grant takes the administrator role off the profile.
        Assert.False((await _accounts.GetGrantAsync(first.Id, grant.ProfileId))!.AdminEnabled);
        Assert.False((await _accounts.GetByIdAsync(first.Id))!.IsAdministrator);
    }

    [Fact]
    public async Task DisablingTheOnlyAdministratorsGrant_IsRefused()
    {
        var first = await CreateAdministratorAsync();
        var grant = await OwnGrantAsync(first);
        // The administrator opens a second person so that its first grant could be switched off on its own.
        await _mutations.CreateProfileAsync(_actor, new CreateManagedProfileCommand(first.Id, "Other", "#445566", false));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.UpsertGrantAsync(_actor, new AccountProfileGrant
            {
                AccountId = first.Id,
                ProfileId = grant.ProfileId,
                IsDefault = false,
                IsEnabled = false,
                AdminEnabled = true,
                AuthorizationVersion = grant.AuthorizationVersion,
            }));

        Assert.Equal(Refusal, refused.Message);
    }

    [Fact]
    public async Task AdministratorAccessOnAPersonTheSignInOnlyOpens_IsRefused_BecauseTheRoleBelongsToTheProfileASignInOpens()
    {
        var first = await CreateAdministratorAsync();
        var other = await _mutations.CreateProfileAsync(_actor, new CreateManagedProfileCommand(first.Id, "Other", "#445566", false));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.UpsertGrantAsync(_actor, new AccountProfileGrant
            {
                AccountId = first.Id,
                ProfileId = other.Id,
                IsEnabled = true,
                AdminEnabled = true,
                AuthorizationVersion = 1,
            }));

        Assert.Equal("Administrator access belongs to the profile a sign-in opens.", refused.Message);
        Assert.False((await new ProfileRepository(_database).GetByIdAsync(other.Id))!.IsServerAdministrator);
    }

    [Fact]
    public async Task RemovingTheOnlyAdministratorsAdministratorGrant_IsRefused_AndAllowedOnceAnotherCanSignIn()
    {
        var first = await CreateAdministratorAsync();
        var second = await CreateAdministratorAsync();
        var firstAdminProfile = (await OwnGrantAsync(first)).ProfileId;
        // Give the first administrator a second, ordinary person so removing the administrator grant is otherwise possible.
        await _mutations.CreateProfileAsync(_actor, new CreateManagedProfileCommand(first.Id, "Other", "#445566", false));
        // The second administrator is no longer one, so the first is the last.
        await _mutations.UpdateAsync(_actor, second.Id, new UpdateAccountAccessCommand(second.Email, true, false));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.RevokeGrantAsync(_actor, first.Id, firstAdminProfile));
        Assert.Equal(Refusal, refused.Message);
        Assert.True((await _accounts.GetByIdAsync(first.Id))!.IsAdministrator);

        // Handing administrator back to the second makes the removal allowed.
        await _mutations.UpdateAsync(_actor, second.Id, new UpdateAccountAccessCommand(second.Email, true, true));
        await _mutations.RevokeGrantAsync(_actor, first.Id, firstAdminProfile);

        // Taking the administrator's own profile grant away takes the role off the profile too.
        Assert.False((await _accounts.GetByIdAsync(first.Id))!.IsAdministrator);
        Assert.True((await _accounts.GetByIdAsync(second.Id))!.IsAdministrator);
    }

    // ---- Deleting the person an administrator administers as ----

    [Fact]
    public async Task DeletingThePersonTheOnlyAdministratorAdministersAs_IsRefused()
    {
        var first = await CreateAdministratorAsync();
        var adminProfile = (await OwnGrantAsync(first)).ProfileId;
        // The administrator also opens a second person, so deleting the first is not stopped by the "only profile" rule.
        await _mutations.CreateProfileAsync(_actor, new CreateManagedProfileCommand(first.Id, "Other", "#778899", false));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.DeleteProfileAsync(_actor, adminProfile));

        Assert.Equal(Refusal, refused.Message);
    }

    [Fact]
    public async Task DeletingThePersonAnAdministratorSignsInAs_IsRefusedUntilTheirSignInIsDeleted()
    {
        var first = await CreateAdministratorAsync();
        await CreateAdministratorAsync();
        var adminProfile = (await OwnGrantAsync(first)).ProfileId;
        await _mutations.CreateProfileAsync(_actor, new CreateManagedProfileCommand(first.Id, "Other", "#778899", false));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.DeleteProfileAsync(_actor, adminProfile));

        Assert.Equal("This person has their own sign-in. Delete the sign-in first.", refused.Message);
        Assert.NotNull(await _accounts.GetByIdAsync(first.Id));
        Assert.True(await _accounts.HasProfileAccessAsync(first.Id, adminProfile));
    }

    // ---- Two changes at once ----

    [Fact]
    public async Task TwoAdministratorsDemotedAtTheSameTime_OnlyOneChangeGoesThrough_AndTheOtherIsToldWhy()
    {
        var first = await CreateAdministratorAsync();
        var second = await CreateAdministratorAsync();
        // Each check takes a while, so without the shared gate both would see the other as "still an administrator".
        _slowCheck.Delay = TimeSpan.FromMilliseconds(300);

        var attempts = await Task.WhenAll(
            TryAsync(() => _mutations.UpdateAsync(_actor, first.Id, new UpdateAccountAccessCommand(first.Email, true, false))),
            TryAsync(() => _mutations.UpdateAsync(_actor, second.Id, new UpdateAccountAccessCommand(second.Email, true, false))));

        Assert.Equal(1, attempts.Count(error => error is null));
        var refused = Assert.Single(attempts, error => error is not null);
        Assert.Equal(Refusal, refused!.Message);
        var remaining = (await _accounts.GetAllAsync()).Count(account => account.IsAdministrator);
        Assert.Equal(1, remaining);
    }

    private static async Task<InvalidOperationException?> TryAsync(Func<Task> change)
    {
        try
        {
            await change();
            return null;
        }
        catch (InvalidOperationException refused)
        {
            return refused;
        }
    }

    // ---- Helpers ----

    /// <summary>The profile a sign-in opens (its default entry in the household view of what it may act as).</summary>
    private async Task<AccountProfileGrant> OwnGrantAsync(Account account) =>
        (await _accounts.GetGrantsAsync(account.Id)).Single(grant => grant.IsDefault);

    private static AccountProfileGrant WithoutAdministrator(AccountProfileGrant grant) => new()
    {
        AccountId = grant.AccountId,
        ProfileId = grant.ProfileId,
        IsDefault = grant.IsDefault,
        IsEnabled = grant.IsEnabled,
        AdminEnabled = false,
        AuthorizationVersion = grant.AuthorizationVersion,
    };

    /// <summary>An enabled administrator with a password, so under the default policy they can sign in.</summary>
    private async Task<Account> CreateAdministratorAsync()
    {
        var number = ++_counter;
        return await _mutations.CreateAsync(_actor, new CreateAccountAccessCommand(
            $"admin{number}@example.com", IsAdministrator: true, ProfileId: null,
            NewProfile: new NewAccountProfileCommand($"Admin {number}", "#7C4DFF"),
            Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            Libraries: new HashSet<Guid>(), TemporaryPassword: Password));
    }

    public ValueTask DisposeAsync()
    {
        _configuration.Dispose();
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(_configPath))
        {
            Directory.Delete(_configPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The real check, with a pause after it so tests can make two changes overlap.</summary>
    private sealed class SlowAfterCheck(IUsableAdministratorService inner) : IUsableAdministratorService
    {
        public TimeSpan Delay { get; set; }

        public async Task<bool> HasUsableAdministratorSignInAfterAsync(AdministratorSignInLoss loss, CancellationToken ct)
        {
            var answer = await inner.HasUsableAdministratorSignInAfterAsync(loss, ct);
            await Task.Delay(Delay, ct);
            return answer;
        }

        public Task<bool> HasUsableAdministratorSignInAsync(MediaEngine.Domain.Configuration.AuthSettings policy, CancellationToken ct) =>
            inner.HasUsableAdministratorSignInAsync(policy, ct);

        public Task<UsableAdministratorStatus> EvaluateForRemoteAsync(CancellationToken ct) => inner.EvaluateForRemoteAsync(ct);

        public Task<bool> OnlyThisComputerAdministratorsAsync(CancellationToken ct) => inner.OnlyThisComputerAdministratorsAsync(ct);
    }

    private sealed class AllowAdministratorDecisions : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class AllowEvaluator : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class NoOpInvalidation : IAuthorizationInvalidationService
    {
        public ValueTask InvalidateAccountAsync(Guid accountId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateGrantAsync(Guid accountId, Guid profileId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateApplicationAsync(Guid applicationId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class NoOpAudit : IAuthorizationAuditWriter
    {
        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
