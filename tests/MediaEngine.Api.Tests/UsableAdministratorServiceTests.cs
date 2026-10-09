using MediaEngine.Api.Services.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class UsableAdministratorServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T12:00:00Z");

    [Fact]
    public async Task DisabledAdministratorFails()
    {
        await WithServiceAsync(async (service, accounts, identities) =>
        {
            var admin = await CreateAdministratorAsync(accounts, isEnabled: false);
            await AddPasswordAsync(identities, admin);
            await AddRecoveryCodeAsync(identities, admin);

            var status = await service.EvaluateForRemoteAsync(new AuthSettings(), CancellationToken.None);

            Assert.False(status.HasUsableAdministrator);
        });
    }

    [Fact]
    public async Task AdministratorWithPasswordSignInTurnedOffAndNoPasskeyFails()
    {
        await WithServiceAsync(async (service, accounts, identities) =>
        {
            var admin = await CreateAdministratorAsync(accounts);
            await AddPasswordAsync(identities, admin);
            var policy = new AuthSettings { PasswordSignInEnabled = false, PasskeySignInEnabled = false, ExternalSignInEnabled = false };

            var status = await service.EvaluateForRemoteAsync(policy, CancellationToken.None);

            Assert.False(status.HasUsableAdministrator);
        });
    }

    [Fact]
    public async Task UsableAdministratorWithoutRecoveryCodesIsReportedWithoutCodes()
    {
        await WithServiceAsync(async (service, accounts, identities) =>
        {
            var admin = await CreateAdministratorAsync(accounts);
            await AddPasswordAsync(identities, admin);

            var status = await service.EvaluateForRemoteAsync(new AuthSettings(), CancellationToken.None);

            Assert.True(status.HasUsableAdministrator);
            Assert.False(status.HasRecoveryCodes);
        });
    }

    [Fact]
    public async Task UsableAdministratorWithRecoveryCodesPasses()
    {
        await WithServiceAsync(async (service, accounts, identities) =>
        {
            var admin = await CreateAdministratorAsync(accounts);
            await AddPasswordAsync(identities, admin);
            await AddRecoveryCodeAsync(identities, admin);

            var status = await service.EvaluateForRemoteAsync(new AuthSettings(), CancellationToken.None);

            Assert.True(status.HasUsableAdministrator);
            Assert.True(status.HasRecoveryCodes);
        });
    }

    [Fact]
    public async Task ExpiredRecoveryCodesDoNotCount()
    {
        await WithServiceAsync(async (service, accounts, identities) =>
        {
            var admin = await CreateAdministratorAsync(accounts);
            await AddPasswordAsync(identities, admin);
            await AddRecoveryCodeAsync(identities, admin, expiresAt: Now.AddDays(-1));

            var status = await service.EvaluateForRemoteAsync(new AuthSettings(), CancellationToken.None);

            Assert.False(status.HasRecoveryCodes);
        });
    }

    [Fact]
    public async Task LocalOnlyAdministratorDoesNotCountForRemoteAccess()
    {
        await WithServiceAsync(async (service, accounts, identities) =>
        {
            var admin = await CreateAdministratorAsync(accounts, isLocalOnly: true);
            await AddRecoveryCodeAsync(identities, admin);

            var status = await service.EvaluateForRemoteAsync(new AuthSettings(), CancellationToken.None);

            Assert.False(status.HasUsableAdministrator);
        });
    }

    private static async Task WithServiceAsync(
        Func<UsableAdministratorService, AccountRepository, IdentityRepository, Task> test)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-usable-admin-{Guid.NewGuid():N}.db");
        try
        {
            using var database = new DatabaseConnection(databasePath);
            database.InitializeSchema();
            var accounts = new AccountRepository(database);
            var identities = new IdentityRepository(database);
            var external = new AccountExternalLoginService(new AccountExternalLoginRepository(database), accounts);
            var service = new UsableAdministratorService(
                accounts, identities, external, null!, null!, new FixedTime(Now));
            await test(service, accounts, identities);
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
        }
    }

    private static async Task<Account> CreateAdministratorAsync(
        AccountRepository accounts, bool isEnabled = true, bool isLocalOnly = false)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@example.com",
            IsEnabled = isEnabled,
            IsAdministrator = true,
            IsLocalOnly = isLocalOnly,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        account.NormalizedEmail = account.Email.ToUpperInvariant();
        var profileId = Guid.NewGuid();
        await accounts.CreateAccountAsync(account, new AccountProfileGrant
        {
            AccountId = account.Id,
            ProfileId = profileId,
            IsDefault = true,
            IsEnabled = true,
            AdminEnabled = true,
            GrantedAt = Now,
        }, new HashSet<AccountFeatureId>(), new HashSet<Guid>(), CancellationToken.None, new Profile
        {
            Id = profileId,
            DisplayName = "Administrator",
            CreatedAt = Now,
        });
        return account;
    }

    private static Task AddPasswordAsync(IdentityRepository identities, Account account) =>
        identities.UpsertAccountCredentialAsync(new AccountCredential
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = AccountCredentialKind.Password,
            SecretHash = "test",
            SecurityStamp = "stamp",
            CreatedAt = Now,
            UpdatedAt = Now,
        });

    private static Task AddRecoveryCodeAsync(IdentityRepository identities, Account account, DateTimeOffset? expiresAt = null) =>
        identities.InsertRecoveryCodesAsync([new PasswordRecoveryCode
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            CodeHash = Guid.NewGuid().ToString("N"),
            CreatedAt = Now,
            ExpiresAt = expiresAt ?? Now.AddDays(30),
        }]);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
