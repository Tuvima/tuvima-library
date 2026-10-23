using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>Passkey registration updates the account through Identity; it must never invent an email for an account that has none.</summary>
public sealed class AccountPasskeyStoreEmailTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_passkey_email_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;

    public AccountPasskeyStoreEmailTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
    }

    [Fact]
    public async Task UpdatingAnAccountWithNoEmail_LeavesTheEmailColumnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var account = new Account { Id = id, IsEnabled = true, AuthorizationVersion = 1, CreatedAt = now, UpdatedAt = now };
        account.MarkThisComputerOnly();
        await _accounts.CreateAccountAsync(account,
            new TestGrant(id, MediaEngine.Domain.Aggregates.Profile.SeedProfileId, IsDefault: true, AdminEnabled: false),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>());
        var store = new AccountPasskeyStore(_accounts, _database);
        var loaded = (await store.FindByIdAsync(id.ToString("D"), CancellationToken.None))!;

        // What UserManager does on every passkey write: normalise the user name, then update.
        await store.SetNormalizedUserNameAsync(loaded, id.ToString("D").ToUpperInvariant(), CancellationToken.None);
        await store.UpdateAsync(loaded, CancellationToken.None);

        var stored = (await _accounts.GetByIdAsync(id))!;
        Assert.Null(stored.Email);
        Assert.Null(stored.NormalizedEmail);
        Assert.False(stored.HasEmail);
        Assert.True(stored.IsThisComputerOnly);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }
}
