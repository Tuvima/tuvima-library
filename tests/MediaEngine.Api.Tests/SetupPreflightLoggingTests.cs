using System.Reflection;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>The server check shows one plain message; every technical reason goes to the Engine log instead.</summary>
public sealed class SetupPreflightLoggingTests : IDisposable
{
    private const string SecretReason = "database is locked by /secret/library.db";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-preflight-tests", Guid.NewGuid().ToString("N"));

    public SetupPreflightLoggingTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ADatabaseThatCannotBeCheckedIsLoggedAtWarningAndBlocksTheDatabaseCheck()
    {
        var logger = new CapturingLogger();
        var configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        var core = configuration.LoadCore();
        core.DataRoot = Path.Combine(_root, "data");
        configuration.SaveCore(core);
        var service = new SetupPreflightService(configuration, new ThrowingDatabase(), new UnavailableFfmpeg(), logger);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("blocked", result.Checks[0].Status);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception?.Message == SecretReason);
    }

    [Fact]
    public void AFolderThatCannotBeUsedIsLoggedAtWarning()
    {
        var logger = new CapturingLogger();
        var service = new SetupPreflightService(null!, null!, null!, logger);
        var missing = Path.Combine(_root, "does-not-exist");

        var check = (MediaEngine.Contracts.Setup.SetupPathCheckDto)typeof(SetupPreflightService)
            .GetMethod("Probe", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, ["test", "Test", missing, "test", false])!;

        Assert.Equal("blocked", check.Status);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is not null);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Test cleanup is best effort; the folder is under the temp directory.
        }
    }

    private sealed class ThrowingDatabase : IDatabaseConnection
    {
        public SqliteConnection Open() => throw new NotSupportedException();
        public SqliteConnection CreateConnection() => throw new InvalidOperationException(SecretReason);
        public Task<T> ExecuteReadAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default) => throw new NotSupportedException();
        public void InitializeSchema() => throw new NotSupportedException();
        public void RunStartupChecks() => throw new NotSupportedException();
        public Task AcquireWriteLockAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void ReleaseWriteLock() => throw new NotSupportedException();
        public Task<T> ExecuteWriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ExecuteWriteAsync(Action<SqliteConnection, SqliteTransaction, CancellationToken> body, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class UnavailableFfmpeg : IFFmpegService
    {
        public string? FfmpegPath => null;
        public string? FfprobePath => null;
        public bool IsAvailable => false;
        public HardwareCapabilities HardwareCapabilities { get; } = new();
        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) => Task.FromResult<MediaProbeResult?>(null);
        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<SetupPreflightService>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }
}
