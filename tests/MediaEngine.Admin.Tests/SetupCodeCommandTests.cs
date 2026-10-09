using System.Text.RegularExpressions;
using MediaEngine.Storage;

namespace MediaEngine.Admin.Tests;

public sealed class SetupCodeCommandTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima-admin-setup-code-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly SetupCodeRepository _codes;

    public SetupCodeCommandTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _codes = new SetupCodeRepository(_database);
    }

    [Fact]
    public async Task PrintsACodeAndStoresOnlyItsHash()
    {
        var console = new CapturingConsole();

        var exitCode = await Command(console, administratorExists: false).ExecuteAsync();

        Assert.Equal(0, exitCode);
        var printed = Assert.Single(console.Output, line => line.StartsWith("Setup code: ", StringComparison.Ordinal));
        var code = printed["Setup code: ".Length..];
        Assert.Matches(new Regex("^[A-HJKMNP-Z2-9]{4}-[A-HJKMNP-Z2-9]{4}$"), code);
        Assert.Contains(console.Output, line => line.Contains("30 minutes", StringComparison.Ordinal));
        var stored = _codes.NewestStoredHash();
        Assert.Equal(SetupCodeRepository.Hash(SetupCodeRepository.Normalize(code)), stored);
        Assert.DoesNotContain(code.Replace("-", string.Empty), stored, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SetupCodeCheck.Accepted, await _codes.VerifyAndConsumeAsync(code, DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task IsRefusedOnceAnAdministratorExists()
    {
        var console = new CapturingConsole();

        var exitCode = await Command(console, administratorExists: true).ExecuteAsync();

        Assert.Equal(SetupCodeCommand.AdministratorExistsExitCode, exitCode);
        Assert.Equal(5, exitCode);
        Assert.Contains(console.Errors, line => line.Contains("already has an administrator", StringComparison.Ordinal));
        Assert.Null(_codes.NewestStoredHash());
    }

    [Fact]
    public async Task NewCodeInvalidatesTheOldOne()
    {
        var first = new CapturingConsole();
        var second = new CapturingConsole();

        await Command(first, administratorExists: false).ExecuteAsync();
        await Command(second, administratorExists: false).ExecuteAsync();

        var oldCode = first.Output.Single(line => line.StartsWith("Setup code: ", StringComparison.Ordinal))["Setup code: ".Length..];
        var newCode = second.Output.Single(line => line.StartsWith("Setup code: ", StringComparison.Ordinal))["Setup code: ".Length..];
        Assert.NotEqual(oldCode, newCode);
        Assert.Equal(SetupCodeCheck.Wrong, await _codes.VerifyAndConsumeAsync(oldCode, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(SetupCodeCheck.Accepted, await _codes.VerifyAndConsumeAsync(newCode, DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task UnelevatedHost_GetsNoCode()
    {
        var console = new CapturingConsole();
        var command = new SetupCodeCommand(
            new SystemHostRecoveryAuthorizer(new DeniedProbe()),
            _ => Task.FromResult(false),
            _codes,
            console,
            TimeProvider.System);

        var exitCode = await command.ExecuteAsync();

        Assert.Equal(3, exitCode);
        Assert.Null(_codes.NewestStoredHash());
    }

    private SetupCodeCommand Command(CapturingConsole console, bool administratorExists) =>
        new(new SystemHostRecoveryAuthorizer(new AllowedProbe()),
            _ => Task.FromResult(administratorExists),
            _codes,
            console,
            TimeProvider.System);

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class AllowedProbe : IHostPrivilegeProbe
    {
        public bool HasAdministrativeHostAccess() => true;
    }

    private sealed class DeniedProbe : IHostPrivilegeProbe
    {
        public bool HasAdministrativeHostAccess() => false;
    }

    private sealed class CapturingConsole : IAdminConsole
    {
        public List<string> Output { get; } = [];
        public List<string> Errors { get; } = [];
        public string? ReadLine(string prompt) => null;
        public string ReadSecret(string prompt) => string.Empty;
        public void WriteLine(string message) => Output.Add(message);
        public void WriteError(string message) => Errors.Add(message);
    }
}
