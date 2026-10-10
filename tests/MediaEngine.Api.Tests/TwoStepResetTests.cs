using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

/// <summary>An administrator switching off someone's two-step codes: who may do it, and that it is recorded.</summary>
public sealed class TwoStepResetTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-two-step-reset-{Guid.NewGuid():N}.db");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-two-step-reset-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly FirstPartyIdentityService _firstParty;
    private readonly AccountAccessMutationService _mutations;
    private readonly RecordingAudit _audit = new();

    public TwoStepResetTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _configuration = new ConfigurationDirectoryLoader(_configPath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        var accounts = new AccountRepository(_database);
        var identities = new IdentityRepository(_database);
        var profiles = new ProfileRepository(_database);
        _firstParty = new FirstPartyIdentityService(
            identities, accounts, profiles,
            new PasswordHasher<AccountCredential>(),
            new PasswordHasher<ProfileCredential>(),
            TimeProvider.System,
            new ConfigurationAuthenticationPolicyProvider(_configuration),
            twoStepSecrets: new PlainProtector());
        _mutations = new AccountAccessMutationService(
            accounts, identities, profiles, _configuration,
            new AllowDecisions(), new AllowEvaluator(),
            new PasswordHasher<GrantAdminProtection>(),
            new NoOpInvalidation(), _audit, _firstParty, new NoProfilePhotos(), TimeProvider.System);
    }

    [Fact]
    public async Task AnAdministrator_CanTurnItOffForSomeoneElse_AndItIsRecorded()
    {
        var target = await TargetWithTwoStepAsync();

        await _mutations.ResetTwoStepAsync(Actor(administrator: true), target);

        Assert.False(await _firstParty.IsTwoStepEnabledAsync(target));
        var recorded = Assert.Single(_audit.Events);
        Assert.Equal("account.two_step_reset", recorded.EventType);
        Assert.Equal(target.ToString("D"), recorded.SubjectId);
    }

    [Fact]
    public async Task ResettingTwice_SaysItWasNotOn_AndRecordsNothingExtra()
    {
        var target = await TargetWithTwoStepAsync();
        await _mutations.ResetTwoStepAsync(Actor(administrator: true), target);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _mutations.ResetTwoStepAsync(Actor(administrator: true), target));
        Assert.Single(_audit.Events);
    }

    [Fact]
    public async Task NoOneResetsTheirOwn_ThroughTheAdministratorAction()
    {
        var target = await TargetWithTwoStepAsync();
        var self = Actor(administrator: true) with { AccountId = target };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _mutations.ResetTwoStepAsync(self, target));
        Assert.True(await _firstParty.IsTwoStepEnabledAsync(target));
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task AdministratorTargets_NeedAnUnlockedAdministrator()
    {
        var target = await TargetWithTwoStepAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _mutations.ResetTwoStepAsync(Actor(administrator: false), target));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _mutations.ResetTwoStepAsync(Actor(administrator: true) with { GrantAdminEnabled = false }, target));
        Assert.True(await _firstParty.IsTwoStepEnabledAsync(target));
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task UnknownAccount_IsNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _mutations.ResetTwoStepAsync(Actor(administrator: true), Guid.NewGuid()));
    }

    private async Task<Guid> TargetWithTwoStepAsync()
    {
        var owner = await _firstParty.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");
        var setup = await _firstParty.BeginTwoStepSetupAsync(owner.Account.Id);
        var code = TotpGenerator.Compute(TotpGenerator.FromBase32(setup.Secret), TotpGenerator.StepAt(DateTimeOffset.UtcNow));
        await _firstParty.EnableTwoStepAsync(owner.Account.Id, code);
        return owner.Account.Id;
    }

    private static RequestAuthority Actor(bool administrator) => new(
        PrincipalKind.Human,
        true,
        AccountId: Guid.NewGuid(),
        ActiveProfileId: Guid.NewGuid(),
        SessionId: Guid.NewGuid(),
        AccountEnabled: true,
        GrantEnabled: true,
        AccountIsAdministrator: administrator,
        GrantAdminEnabled: administrator);

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
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
    }

    private sealed class PlainProtector : ITwoStepSecretProtector
    {
        public string Protect(string base32Secret) => "p:" + base32Secret;
        public string? Unprotect(string protectedSecret) => protectedSecret.StartsWith("p:", StringComparison.Ordinal) ? protectedSecret[2..] : null;
    }

    private sealed class RecordingAudit : IAuthorizationAuditWriter
    {
        public List<AuthorizationAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AllowDecisions : IAccountAccessDecisionService
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
}
