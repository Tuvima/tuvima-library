using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Networking;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

/// <summary>Reads real users from a temp data store: who could sign in from outside, and who stays home only.</summary>
public sealed class RemoteSignInReadinessReaderTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-remote-readiness-{Guid.NewGuid():N}.db");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-remote-readiness-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly AccountRepository _accounts;
    private readonly FirstPartyIdentityService _identity;

    public RemoteSignInReadinessReaderTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _configuration = new ConfigurationDirectoryLoader(_configPath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _identity = new FirstPartyIdentityService(
            new IdentityRepository(_database),
            _accounts,
            new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(),
            new PasswordHasher<ProfileCredential>(),
            TimeProvider.System,
            new ConfigurationAuthenticationPolicyProvider(_configuration));
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            File.Delete(path);
        }

        if (Directory.Exists(_configPath))
        {
            Directory.Delete(_configPath, recursive: true);
        }
    }

    private RemoteSignInReadinessReader Reader() => new(_accounts, new ProfileRepository(_database));

    [Fact]
    public async Task AnAdministratorThatWorksOnlyOnThisComputer_HasNoPassword()
    {
        await _identity.BootstrapThisComputerAdministratorAsync(null, "Owner", "device-1", "Desktop", "Dashboard");

        var readiness = await Reader().ReadAsync(CancellationToken.None);

        Assert.True(readiness.NoAdministratorHasPassword);
        Assert.Empty(readiness.HomeOnlyProfileNames);
    }

    [Fact]
    public async Task AnAdministratorWithAPassword_NeedsNothingMore()
    {
        await _identity.BootstrapAdministratorAsync(
            "owner@example.com", "a-long-unusual-passphrase-1", "Owner", "device-1", "Desktop", "Dashboard");

        var readiness = await Reader().ReadAsync(CancellationToken.None);

        Assert.False(readiness.NoAdministratorHasPassword);
        Assert.Empty(readiness.HomeOnlyProfileNames);
    }

    [Fact]
    public async Task WithNoUsersAtAll_NothingIsReportedMissing()
    {
        var readiness = await Reader().ReadAsync(CancellationToken.None);

        Assert.False(readiness.NoAdministratorHasPassword);
        Assert.Empty(readiness.HomeOnlyProfileNames);
    }
}
