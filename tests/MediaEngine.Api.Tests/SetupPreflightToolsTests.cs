using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Contracts.Settings;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>A missing tool is reported in the server check but never changes whether setup may continue.</summary>
public sealed class SetupPreflightToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-preflight-tools", Guid.NewGuid().ToString("N"));

    public SetupPreflightToolsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task AMissingRequiredTool_DoesNotChangePassed_AndTheToolsAreIncluded()
    {
        var withTools = await RunAsync(new FixedTools(Tools(ready: true)));
        var withoutFfmpeg = await RunAsync(new FixedTools(Tools(ready: false)));

        Assert.True(withTools.Passed);
        Assert.True(withoutFfmpeg.Passed);
        Assert.True(withoutFfmpeg.Tools.NeedsAttention);
        Assert.Equal(ToolRequirementStates.Missing, Assert.Single(withoutFfmpeg.Tools.Tools).State);
        Assert.False(withTools.Tools.NeedsAttention);
    }

    private async Task<MediaEngine.Contracts.Setup.SetupPreflightDto> RunAsync(IToolRequirementsService tools)
    {
        var configRoot = Path.Combine(_root, "config");
        var configuration = new ConfigurationDirectoryLoader(configRoot);
        var core = configuration.LoadCore();
        core.LibraryRoot = Path.Combine(_root, "library");
        core.DataRoot = Path.Combine(_root, "data");
        configuration.SaveCore(core);

        // The data store file must exist for the database check to pass; keep it inside this test's folder.
        var databasePath = Path.GetFullPath(TuvimaDataPathResolver.ResolveDatabasePath(
            Path.GetFullPath(configuration.ConfigDirectoryPath),
            Environment.GetEnvironmentVariable("TUVIMA_DB_PATH"),
            Environment.GetEnvironmentVariable("TUVIMA_LIBRARY_ROOT")));
        Assert.StartsWith(_root, databasePath, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await File.WriteAllBytesAsync(databasePath, []);

        var service = new SetupPreflightService(configuration, new HealthyDatabase(), tools, NullLogger<SetupPreflightService>.Instance);
        return await service.RunAsync(CancellationToken.None);
    }

    private static ToolRequirementsDto Tools(bool ready) => new(
        ToolInstallKinds.Source,
        DateTimeOffset.UnixEpoch,
        !ready,
        [
            new ToolRequirementDto(
                ToolRequirementIds.Ffmpeg, "FFmpeg", "tool", ToolRequirementLevels.Required,
                ready ? ToolRequirementStates.Ready : ToolRequirementStates.Missing,
                "Videos, music and audiobooks wait until FFmpeg is available.", null, ready ? string.Empty : "Install it.",
                null, 0, !ready, false, false, null),
        ]);

    private sealed class FixedTools(ToolRequirementsDto result) : IToolRequirementsService
    {
        public Task<ToolRequirementsDto> GetAsync(CancellationToken ct) => Task.FromResult(result);
        public Task<ToolRequirementsDto> CheckAsync(CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class HealthyDatabase : IDatabaseConnection
    {
        public SqliteConnection Open() => throw new NotSupportedException();

        public SqliteConnection CreateConnection()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            return connection;
        }

        public Task<T> ExecuteReadAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default) => throw new NotSupportedException();
        public void InitializeSchema() => throw new NotSupportedException();
        public void RunStartupChecks() => throw new NotSupportedException();
        public Task AcquireWriteLockAsync(CancellationToken ct = default, string holderFile = "", string holderMember = "") => throw new NotSupportedException();
        public void ReleaseWriteLock() => throw new NotSupportedException();
        public string? DescribeWriteLockHolder() => null;
        public Task<T> ExecuteWriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default, string holderFile = "", string holderMember = "") => throw new NotSupportedException();
        public Task ExecuteWriteAsync(Action<SqliteConnection, SqliteTransaction, CancellationToken> body, CancellationToken ct = default, string holderFile = "", string holderMember = "") => throw new NotSupportedException();
        public void Dispose() { }
    }
}
