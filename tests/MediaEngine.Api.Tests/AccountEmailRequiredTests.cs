using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

public sealed class AccountEmailRequiredTests
{
    [Fact]
    public async Task ManagedAccountCreation_RequiresAnEmail_AndCannotSignInWithoutACredential()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-email-required-{Guid.NewGuid():N}.db");
        var configPath = Path.Combine(Path.GetTempPath(), $"tuvima-email-required-{Guid.NewGuid():N}");
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
                    var firstParty = new FirstPartyIdentityService(
                        new IdentityRepository(database),
                        accounts,
                        new ProfileRepository(database),
                        new PasswordHasher<AccountCredential>(),
                        new PasswordHasher<ProfileCredential>(),
                        TimeProvider.System,
                        new ConfigurationAuthenticationPolicyProvider(configuration));
                    var mutations = AccountSetupKit.Attach(new AccountAccessMutationService(
                        accounts,
                        new ProfileAccessRepository(database),
                        identities,
                        profiles,
                        configuration,
                        new AllowAdministratorDecisions(),
                        new AllowEvaluator(),
                        new NoOpInvalidation(),
                        new NoOpAudit(),
                        firstParty,
                        new NoProfilePhotos(), TimeProvider.System, new AlwaysUsableAdministrators(), new AuthenticationPolicyMutationGate()), accounts, new ProfileAccessRepository(database), firstParty);
                    var actor = new RequestAuthority(
                        PrincipalKind.Human,
                        true,
                        AccountId: Guid.NewGuid(),
                        ActiveProfileId: Guid.NewGuid(),
                        SessionId: Guid.NewGuid(),
                        AccountEnabled: true,
                        ActiveProfileInUserHousehold: true,
                        ActiveProfileIsServerAdministrator: true);

                    foreach (var missing in new string?[] { null, "", "   " })
                    {
                        await Assert.ThrowsAsync<ArgumentException>(() => mutations.CreateAsync(actor, new CreateAccountAccessCommand(
                            missing,
                            IsAdministrator: false,
                            ProfileId: null,
                            NewProfile: new NewAccountProfileCommand("Nameless", "#7C4DFF")
                            )));
                    }

                    var account = await mutations.CreateAsync(actor, new CreateAccountAccessCommand(
                        "household@example.com",
                        IsAdministrator: false,
                        ProfileId: null,
                        NewProfile: new NewAccountProfileCommand("Household", "#7C4DFF")
                        ));
                    var profileId = account.ProfileId;
                    var identity = new FirstPartyIdentityService(
                        identities,
                        accounts,
                        profiles,
                        new PasswordHasher<AccountCredential>(),
                        new PasswordHasher<ProfileCredential>(),
                        TimeProvider.System,
                        new ConfigurationAuthenticationPolicyProvider(configuration));

                    // A new account has no credential yet, so nothing can sign in as it.
                    var attempt = await identity.AuthenticatePasswordAsync(
                        "household@example.com", string.Empty, "living-room", "Living room", "Dashboard");
                    Assert.False(attempt.Succeeded);
                    Assert.Null(attempt.IssuedSession);
                    Assert.Empty(await identity.GetSessionsAsync(account.Id));

                    var core = configuration.LoadCore();
                    core.Auth.InvitationLifetimeHours = 3;
                    configuration.SaveCore(core);
                    var createdInvited = await mutations.CreateUserAsync(actor, new CreateUserCommand(
                        "invited@example.com", null,
                        new NewUserProfileCommand("Invited", null, null, "Invited household"), NewUserSignInMethod.Invitation));
                    var invitation = createdInvited.Invitation!;
                    Assert.InRange(invitation.ExpiresAt - DateTimeOffset.UtcNow,
                        TimeSpan.FromHours(2.9), TimeSpan.FromHours(3.1));
                    var invited = await identity.AcceptInvitationAsync(
                        invitation.Code, "invited password", "browser", "Browser", "Dashboard");
                    Assert.Equal(createdInvited.User.Id, invited.Account.Id);
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => identity.AcceptInvitationAsync(
                        invitation.Code, "other password", "other", "Other", "Dashboard"));
                }
            }
        }
        finally
        {
            TestTemp.ReleaseSqlitePool(databasePath);
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
    public async Task Households_AManagedAccountStartsOne_AndAnOutsideInvitationStartsAnother()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-households-service-{Guid.NewGuid():N}.db");
        var configPath = Path.Combine(Path.GetTempPath(), $"tuvima-households-service-{Guid.NewGuid():N}");
        try
        {
            using (var database = new DatabaseConnection(databasePath))
            {
                using (var configuration = new ConfigurationDirectoryLoader(configPath))
                {
                    database.InitializeSchema();
                    var accounts = new AccountRepository(database);
                    var households = new HouseholdRepository(database);
                    var firstParty = new FirstPartyIdentityService(
                        new IdentityRepository(database),
                        accounts,
                        new ProfileRepository(database),
                        new PasswordHasher<AccountCredential>(),
                        new PasswordHasher<ProfileCredential>(),
                        TimeProvider.System,
                        new ConfigurationAuthenticationPolicyProvider(configuration));
                    var mutations = AccountSetupKit.Attach(new AccountAccessMutationService(
                        accounts,
                        new ProfileAccessRepository(database),
                        new IdentityRepository(database),
                        new ProfileRepository(database),
                        configuration,
                        new AllowAdministratorDecisions(),
                        new AllowEvaluator(),
                        new NoOpInvalidation(),
                        new NoOpAudit(),
                        firstParty,
                        new NoProfilePhotos(), TimeProvider.System, new AlwaysUsableAdministrators(), new AuthenticationPolicyMutationGate()), accounts, new ProfileAccessRepository(database), firstParty);
                    var actor = new RequestAuthority(
                        PrincipalKind.Human,
                        true,
                        AccountId: Guid.NewGuid(),
                        ActiveProfileId: Guid.NewGuid(),
                        SessionId: Guid.NewGuid(),
                        AccountEnabled: true,
                        ActiveProfileInUserHousehold: true,
                        ActiveProfileIsServerAdministrator: true);

                    var owner = await mutations.CreateAsync(actor, new CreateAccountAccessCommand(
                        "owner@example.com",
                        IsAdministrator: false,
                        ProfileId: null,
                        NewProfile: new NewAccountProfileCommand("Owner", "#7C4DFF")
                        ));
                    var ownerHousehold = await households.GetForAccountAsync(owner.Id);
                    Assert.NotNull(ownerHousehold);

                    var guest = await mutations.CreateUserAsync(actor, new CreateUserCommand(
                        "guest@example.com", null,
                        new NewUserProfileCommand("Guest", null, null, "Guest household"), NewUserSignInMethod.Invitation));
                    var guestHousehold = await households.GetForAccountAsync(guest.User.Id);
                    Assert.NotNull(guestHousehold);
                    Assert.NotEqual(ownerHousehold.Id, guestHousehold.Id);
                    var guestPerson = Assert.Single(await households.ListProfilesAsync(guestHousehold.Id));
                    Assert.Equal("Guest", guestPerson.DisplayName);
                    Assert.Equal(guestPerson.Id, guest.User.ProfileId);

                    // A new household needs a name for itself, and a person that is made needs a name too.
                    await Assert.ThrowsAsync<ArgumentException>(() => mutations.CreateUserAsync(actor, new CreateUserCommand(
                        "other@example.com", null, new NewUserProfileCommand("Other", null, Guid.NewGuid(), "Both"), NewUserSignInMethod.Invitation)));
                    await Assert.ThrowsAsync<ArgumentException>(() => mutations.CreateUserAsync(actor, new CreateUserCommand(
                        "blank@example.com", null, new NewUserProfileCommand("   ", null, null, "Blank"), NewUserSignInMethod.Invitation)));

                    // An email that already has a sign-in cannot start a second household.
                    await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.CreateUserAsync(actor,
                        new CreateUserCommand("guest@example.com", null,
                            new NewUserProfileCommand("Guest again", null, null, "Again"), NewUserSignInMethod.Invitation)));
                }
            }
        }
        finally
        {
            TestTemp.ReleaseSqlitePool(databasePath);
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
                    var firstParty = new FirstPartyIdentityService(
                        new IdentityRepository(database),
                        accounts,
                        new ProfileRepository(database),
                        new PasswordHasher<AccountCredential>(),
                        new PasswordHasher<ProfileCredential>(),
                        TimeProvider.System,
                        new ConfigurationAuthenticationPolicyProvider(configuration));
                    var mutations = AccountSetupKit.Attach(new AccountAccessMutationService(
                        accounts,
                        new ProfileAccessRepository(database),
                        new IdentityRepository(database),
                        profiles,
                        configuration,
                        new AllowAdministratorDecisions(),
                        new AllowEvaluator(),
                        new NoOpInvalidation(),
                        new NoOpAudit(),
                        firstParty,
                        new NoProfilePhotos(), TimeProvider.System, new AlwaysUsableAdministrators(), new AuthenticationPolicyMutationGate()), accounts, new ProfileAccessRepository(database), firstParty);
                    var actor = new RequestAuthority(
                        PrincipalKind.Human, true, AccountId: Guid.NewGuid(), ActiveProfileId: Guid.NewGuid(),
                        SessionId: Guid.NewGuid(), AccountEnabled: true, ActiveProfileInUserHousehold: true,
                        ActiveProfileIsServerAdministrator: true);

                    var child = await mutations.CreateAsync(actor, new CreateAccountAccessCommand(
                        "child@example.com", IsAdministrator: false, ProfileId: null,
                        NewProfile: new NewAccountProfileCommand("Child", "#7C4DFF")
                        ));
                    var childProfileId = child.ProfileId;
                    // A new profile is an ordinary member; this one is then made a Kids profile.
                    Assert.Equal(MediaEngine.Domain.Enums.ProfileRole.StandardUser,
                        (await profiles.GetByIdAsync(childProfileId))!.Role);
                    using (var connection = database.CreateConnection())
                    {
                        Dapper.SqlMapper.Execute(connection, "UPDATE profiles SET role='RestrictedProfile' WHERE id=@childProfileId;",
                            new { childProfileId });
                    }

                    Assert.Equal(MediaEngine.Domain.Enums.ProfileRole.RestrictedProfile,
                        (await profiles.GetByIdAsync(childProfileId))!.Role);

                    var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.SetProfileRolesAsync(
                        actor, childProfileId, false, isServerAdministrator: true));
                    Assert.Equal("Child profiles can't be administrators.", refused.Message);

                    var admin = await mutations.CreateAsync(actor, new CreateAccountAccessCommand(
                        "admin@example.com", IsAdministrator: true, ProfileId: null,
                        NewProfile: new NewAccountProfileCommand("Parent", "#7C4DFF")
                        ));
                    Assert.True((await new ProfileAccessRepository(database).GetAdminFlagsAsync(admin.ProfileId))!.IsServerAdministrator);
                    Assert.Equal(MediaEngine.Domain.Enums.ProfileRole.StandardUser,
                        (await profiles.GetByIdAsync(admin.ProfileId))!.Role);
                }
            }
        }
        finally
        {
            TestTemp.ReleaseSqlitePool(databasePath);
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
    public async Task TemporaryPassword_RefusesSelfAndAdministratorTargetsForApplications_AndLastsSevenDays()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-temp-password-{Guid.NewGuid():N}.db");
        var configPath = Path.Combine(Path.GetTempPath(), $"tuvima-temp-password-{Guid.NewGuid():N}");
        try
        {
            using (var database = new DatabaseConnection(databasePath))
            {
                using (var configuration = new ConfigurationDirectoryLoader(configPath))
                {
                    database.InitializeSchema();
                    var accounts = new AccountRepository(database);
                    var firstParty = new FirstPartyIdentityService(
                        new IdentityRepository(database), accounts, new ProfileRepository(database),
                        new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
                        TimeProvider.System, new ConfigurationAuthenticationPolicyProvider(configuration));
                    var mutations = AccountSetupKit.Attach(new AccountAccessMutationService(
                        accounts,
                        new ProfileAccessRepository(database), new IdentityRepository(database), new ProfileRepository(database), configuration,
                        new AllowAdministratorDecisions(), new AllowEvaluator(),
                        new NoOpInvalidation(), new NoOpAudit(),
                        firstParty, new NoProfilePhotos(), TimeProvider.System, new AlwaysUsableAdministrators(), new AuthenticationPolicyMutationGate()), accounts, new ProfileAccessRepository(database), firstParty);
                    var human = new RequestAuthority(
                        PrincipalKind.Human, true, AccountId: Guid.NewGuid(), ActiveProfileId: Guid.NewGuid(),
                        SessionId: Guid.NewGuid(), AccountEnabled: true, ActiveProfileInUserHousehold: true,
                        ActiveProfileIsServerAdministrator: true);

                    CreateAccountAccessCommand Command(string email, bool administrator) => new(
                        email, administrator, null, new NewAccountProfileCommand(email, "#7C4DFF"));
                    var member = await mutations.CreateAsync(human, Command("member@example.com", false));
                    var admin = await mutations.CreateAsync(human, Command("admin@example.com", true));

                    // Never your own account.
                    await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.SetTemporaryPasswordAsync(
                        human with { AccountId = member.Id }, member.Id, "temporary pass 123"));

                    // An application cannot take over an administrator, but a person who is one can.
                    var application = human with { PrincipalKind = PrincipalKind.ServiceApplication, AccountId = null };
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.SetTemporaryPasswordAsync(
                        application, admin.Id, "temporary pass 123"));
                    await mutations.SetTemporaryPasswordAsync(human, admin.Id, "temporary pass 123");

                    var stored = await accounts.GetByIdAsync(admin.Id);
                    Assert.True(stored!.MustChangePassword);
                    Assert.InRange(stored.TemporaryPasswordExpiresAt!.Value - DateTimeOffset.UtcNow,
                        TimeSpan.FromDays(6.99), TimeSpan.FromDays(7.01));
                }
            }
        }
        finally
        {
            TestTemp.ReleaseSqlitePool(databasePath);
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

    internal sealed class AllowAdministratorDecisions : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    internal sealed class AllowEvaluator : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    internal sealed class NoOpInvalidation : IAuthorizationInvalidationService
    {
        public ValueTask InvalidateAccountAsync(Guid accountId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateGrantAsync(Guid accountId, Guid profileId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateApplicationAsync(Guid applicationId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    internal sealed class NoOpAudit : IAuthorizationAuditWriter
    {
        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
