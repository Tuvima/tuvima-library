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
/// The server must always keep one administrator who can sign in: taking administrator away or deleting them (or the person
/// they administer as) is refused when nobody else could still sign in as an administrator, and allowed once somebody else can.
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
    private readonly ProfileAccessRepository _access;
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
        _access = new ProfileAccessRepository(_database);
        _mutations = AccountSetupKit.Attach(new AccountAccessMutationService(
            _accounts,
            _access, identities, profiles, _configuration,
            new AllowAdministratorDecisions(), new AllowEvaluator(),
            new NoOpInvalidation(), new NoOpAudit(),
            identity, new NoProfilePhotos(), TimeProvider.System, _slowCheck, new AuthenticationPolicyMutationGate(), new FixedVaultLibraries()),
            _accounts, _access, identity);
    }

    // ---- Demoting ----

    [Fact]
    public async Task TakingAdministratorAwayFromTheOnlyAdministrator_IsRefused_AndTheyStayAnAdministrator()
    {
        var only = await CreateAdministratorAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoteAsync(only));

        Assert.Equal(Refusal, refused.Message);
        Assert.True((await _access.GetAdminFlagsAsync(only.ProfileId))!.IsServerAdministrator);
    }

    [Fact]
    public async Task AnAdministratorCanBeDemoted_OnceAnotherCanSignIn_ButNotTheLastOneLeft()
    {
        var first = await CreateAdministratorAsync();
        var second = await CreateAdministratorAsync();
        // The Library's own household keeps a household administrator too (D7), so the second one looks after it as well.
        await _access.SetHouseholdAdministratorAsync(second.ProfileId, true, DateTimeOffset.UtcNow);

        await DemoteAsync(first);
        Assert.False((await _access.GetAdminFlagsAsync(first.ProfileId))!.IsServerAdministrator);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoteAsync(second));
        Assert.Equal(Refusal, refused.Message);
    }

    [Fact]
    public async Task ChangingAnAdministratorsEmail_IsNotBlockedByTheRule_EvenForTheOnlyAdministrator()
    {
        var only = await CreateAdministratorAsync();

        // Same user, still enabled and still an administrator: nothing is lost, so nothing is refused here.
        await _mutations.ChangeEmailAsync(_actor, only.Id, "renamed@example.com");

        Assert.True((await _access.GetAdminFlagsAsync(only.ProfileId))!.IsServerAdministrator);
        Assert.Equal("renamed@example.com", (await _accounts.GetByIdAsync(only.Id))!.Email);
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

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoteAsync(first));

        Assert.Equal(Refusal, refused.Message);
    }

    // ---- Deleting ----

    [Fact]
    public async Task DeletingTheOnlyAdministrator_IsRefused_AndAllowedOnceAnotherCanSignIn()
    {
        var first = await CreateAdministratorAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _mutations.DeleteUserAsync(_actor, first.Id));
        Assert.Equal(Refusal, refused.Message);
        Assert.NotNull(await _accounts.GetByIdAsync(first.Id));

        var second = await CreateAdministratorAsync();
        // Deleting a user removes only the sign-in; the second administrator can still sign in, so the first may go.
        await _mutations.DeleteUserAsync(_actor, first.Id);

        Assert.Null(await _accounts.GetByIdAsync(first.Id));
        var last = await Assert.ThrowsAsync<InvalidOperationException>(() => _mutations.DeleteUserAsync(_actor, second.Id));
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
            HiddenLibraries: new HashSet<Guid>(), TemporaryPassword: Password));
        // The member starts a household of their own and is its last administrator: deleting them removes that household too.
        var preview = await _mutations.PreviewDeleteUserAsync(_actor, member.Id);
        await _mutations.DeleteUserAsync(_actor, member.Id,
            confirmedProfileIds: preview.Plan.HouseholdProfiles.Select(profile => profile.Id).ToArray());

        Assert.Null(await _accounts.GetByIdAsync(member.Id));
    }

    // ---- Deleting the person an administrator administers as ----

    [Fact]
    public async Task DeletingThePersonTheOnlyAdministratorAdministersAs_IsRefused()
    {
        var first = await CreateAdministratorAsync();
        var adminProfile = first.ProfileId;
        // The household also has a second person, so deleting the first is not stopped by the "only profile" rule.
        await AddPersonAsync(first, "Other");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.DeleteProfileAsync(_actor, adminProfile));

        Assert.Equal(Refusal, refused.Message);
    }

    [Fact]
    public async Task DeletingTheOnlyHouseholdAdministratorsProfile_IsRefused_AndKeepsTheirSignIn()
    {
        var first = await CreateAdministratorAsync();
        await CreateAdministratorAsync();
        var adminProfile = first.ProfileId;
        await AddPersonAsync(first, "Other");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _mutations.DeleteProfileAsync(_actor, adminProfile));

        Assert.Equal(MediaEngine.Domain.Contracts.UserRemovalMessages.LastHouseholdAdministrator, refused.Message);
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
            TryAsync(() => DemoteAsync(first)),
            TryAsync(() => DemoteAsync(second)));

        Assert.Equal(1, attempts.Count(error => error is null));
        var refused = Assert.Single(attempts, error => error is not null);
        Assert.Equal(Refusal, refused!.Message);
        var remaining = (await _accounts.GetEnabledServerAdministratorsAsync()).Count;
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

    /// <summary>Takes the server administrator role off the profile the user opens (its household administrator role stays as it is).</summary>
    private async Task DemoteAsync(Account user) =>
        await _mutations.SetProfileRolesAsync(_actor, user.ProfileId,
            (await _access.GetAdminFlagsAsync(user.ProfileId))!.IsHouseholdAdministrator, isServerAdministrator: false);

    /// <summary>Adds an ordinary person to the household of the user's profile.</summary>
    private async Task AddPersonAsync(Account user, string name)
    {
        var household = (await new HouseholdRepository(_database).GetForAccountAsync(user.Id))!.Id;
        await _mutations.AddHouseholdPersonAsync(_actor, new AddHouseholdPersonCommand(household, name, "#445566", false, null));
    }

    /// <summary>An enabled administrator with a password, so under the default policy they can sign in.</summary>
    private async Task<Account> CreateAdministratorAsync()
    {
        var number = ++_counter;
        return await _mutations.CreateAsync(_actor, new CreateAccountAccessCommand(
            $"admin{number}@example.com", IsAdministrator: true, ProfileId: null,
            NewProfile: new NewAccountProfileCommand($"Admin {number}", "#7C4DFF"),
            Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            HiddenLibraries: new HashSet<Guid>(), TemporaryPassword: Password));
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
