using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

public sealed class LocalOnlyAccountEntryTests
{
    [Fact]
    public async Task ManagedAccountCreation_ProducesUsablePasswordlessLocalEntry()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-local-entry-{Guid.NewGuid():N}.db");
        var configPath = Path.Combine(Path.GetTempPath(), $"tuvima-local-entry-{Guid.NewGuid():N}");
        try
        {
            using (var database = new DatabaseConnection(databasePath))
            {
                using (var configuration = new ConfigurationDirectoryLoader(configPath))
                {
                    database.InitializeSchema();
                    var accounts = new AccountRepository(database);
                    var identities = new IdentityRepository(database);
                    var profiles = new ProfileRepository(database);
                    var mutations = new AccountAccessMutationService(
                        accounts,
                        identities,
                        profiles,
                        configuration,
                        new AllowAdministratorDecisions(),
                        new AllowEvaluator(),
                        new PasswordHasher<GrantAdminProtection>(),
                        new NoOpInvalidation(),
                        new NoOpAudit(),
                        TimeProvider.System);
                    var actor = new RequestAuthority(
                        PrincipalKind.Human,
                        true,
                        AccountId: Guid.NewGuid(),
                        ActiveProfileId: Guid.NewGuid(),
                        SessionId: Guid.NewGuid(),
                        AccountEnabled: true,
                        GrantEnabled: true,
                        AccountIsAdministrator: true,
                        GrantAdminEnabled: true);

                    var account = await mutations.CreateAsync(actor, new CreateAccountAccessCommand(
                        null,
                        IsLocalOnly: true,
                        IsAdministrator: false,
                        ProfileId: null,
                        NewProfile: new NewAccountProfileCommand("Household", "#7C4DFF"),
                        Features: new HashSet<AccountFeatureId>(),
                        Libraries: new HashSet<Guid>()));
                    var profileId = Assert.Single(await accounts.GetGrantsAsync(account.Id)).ProfileId;
                    var identity = new FirstPartyIdentityService(
                        identities,
                        accounts,
                        profiles,
                        new PasswordHasher<AccountCredential>(),
                        new PasswordHasher<ProfileCredential>(),
                        TimeProvider.System,
                        new ConfigurationAuthenticationPolicyProvider(configuration));

                    var result = await identity.AuthenticatePinAsync(
                        profileId, string.Empty, "living-room", "Living room", "Dashboard");

                    Assert.True(result.Succeeded);
                    Assert.Equal(account.Id, result.IssuedSession?.Account.Id);
                    Assert.Equal("ProfileEntry", result.IssuedSession?.Session.AuthenticationMethod);

                    var core = configuration.LoadCore();
                    core.Auth.InvitationLifetimeHours = 3;
                    configuration.SaveCore(core);
                    var invitation = await mutations.IssueInvitationAsync(actor, new IssueAccountInvitationCommand(
                        "invited@example.com", [profileId], profileId));
                    Assert.InRange(invitation.ExpiresAt - DateTimeOffset.UtcNow,
                        TimeSpan.FromHours(2.9), TimeSpan.FromHours(3.1));
                    var invited = await identity.AcceptInvitationAsync(
                        invitation.PlaintextToken, "invited password", "browser", "Browser", "Dashboard");
                    Assert.Equal(invitation.AccountId, invited.Account.Id);
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => identity.AcceptInvitationAsync(
                        invitation.PlaintextToken, "other password", "other", "Other", "Dashboard"));
                }
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            if (Directory.Exists(configPath))
            {
                Directory.Delete(configPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ChildProfilesCannotBeAdministrators_ButAnAdministratorsOwnProfileCan()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-child-admin-{Guid.NewGuid():N}.db");
        var configPath = Path.Combine(Path.GetTempPath(), $"tuvima-child-admin-{Guid.NewGuid():N}");
        try
        {
            using (var database = new DatabaseConnection(databasePath))
            {
                using (var configuration = new ConfigurationDirectoryLoader(configPath))
                {
                    database.InitializeSchema();
                    var accounts = new AccountRepository(database);
                    var profiles = new ProfileRepository(database);
                    var mutations = new AccountAccessMutationService(
                        accounts,
                        new IdentityRepository(database),
                        profiles,
                        configuration,
                        new AllowAdministratorDecisions(),
                        new AllowEvaluator(),
                        new PasswordHasher<GrantAdminProtection>(),
                        new NoOpInvalidation(),
                        new NoOpAudit(),
                        TimeProvider.System);
                    var actor = new RequestAuthority(
                        PrincipalKind.Human, true, AccountId: Guid.NewGuid(), ActiveProfileId: Guid.NewGuid(),
                        SessionId: Guid.NewGuid(), AccountEnabled: true, GrantEnabled: true,
                        AccountIsAdministrator: true, GrantAdminEnabled: true);

                    var child = await mutations.CreateAsync(actor, new CreateAccountAccessCommand(
                        "child@example.com", IsLocalOnly: false, IsAdministrator: false, ProfileId: null,
                        NewProfile: new NewAccountProfileCommand("Child", "#7C4DFF"),
                        Features: new HashSet<AccountFeatureId>(), Libraries: new HashSet<Guid>()));
                    var childProfileId = Assert.Single(await accounts.GetGrantsAsync(child.Id)).ProfileId;
                    Assert.Equal(MediaEngine.Domain.Enums.ProfileRole.RestrictedProfile,
                        (await profiles.GetByIdAsync(childProfileId))!.Role);

                    var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.UpsertGrantAsync(
                        actor, new AccountProfileGrant
                        {
                            AccountId = child.Id,
                            ProfileId = childProfileId,
                            IsDefault = true,
                            IsEnabled = true,
                            AdminEnabled = true,
                            AuthorizationVersion = 1,
                        }));
                    Assert.Equal("Child profiles can't be administrators.", refused.Message);

                    var promoted = await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.UpdateAsync(
                        actor, child.Id, new UpdateAccountAccessCommand(
                            "child@example.com", IsLocalOnly: false, IsEnabled: true, IsAdministrator: true)));
                    Assert.Equal("Child profiles can't be administrators.", promoted.Message);

                    var admin = await mutations.CreateAsync(actor, new CreateAccountAccessCommand(
                        "admin@example.com", IsLocalOnly: false, IsAdministrator: true, ProfileId: null,
                        NewProfile: new NewAccountProfileCommand("Parent", "#7C4DFF"),
                        Features: new HashSet<AccountFeatureId>(), Libraries: new HashSet<Guid>()));
                    var adminGrant = Assert.Single(await accounts.GetGrantsAsync(admin.Id));
                    Assert.True(adminGrant.AdminEnabled);
                    Assert.Equal(MediaEngine.Domain.Enums.ProfileRole.StandardUser,
                        (await profiles.GetByIdAsync(adminGrant.ProfileId))!.Role);
                }
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            if (Directory.Exists(configPath))
            {
                Directory.Delete(configPath, recursive: true);
            }
        }
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
