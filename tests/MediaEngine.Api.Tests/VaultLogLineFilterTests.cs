using MediaEngine.Api.Services.Vault;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>
/// No Engine log line may spell the name or place of a file in a Vault library (Videos spec D17, D33). These cases run a real
/// Serilog pipeline with the same filter the Engine installs. The Vault folder list is process wide, so they share one class.
/// </summary>
public sealed class VaultLogLineFilterTests : IDisposable
{
    private static readonly string Folder = "/mnt/private-" + Guid.NewGuid().ToString("N") + "/Clips";
    private readonly CollectingSink _sink = new();
    private readonly Serilog.Core.Logger _serilog;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    public VaultLogLineFilterTests()
    {
        VaultLocationGuard.Replace([Folder]);
        _serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Filter.ByExcluding(VaultLogLineFilter.NamesVaultLocation)
            .WriteTo.Sink(_sink)
            .CreateLogger();
        _logger = new SerilogLoggerFactory(_serilog).CreateLogger("test");
    }

    public void Dispose()
    {
        _serilog.Dispose();
        VaultLocationGuard.Replace([]);
    }

    [Fact]
    public void AnOrdinaryLogLine_IsWritten()
    {
        _logger.LogInformation("Settled {Path} after {Attempts} attempts", "/mnt/movies/Dune.mkv", 2);

        Assert.Single(_sink.Events);
    }

    [Fact]
    public void ALogLineThatNamesAFileInAVaultFolder_IsNeverWritten()
    {
        _logger.LogInformation("Settled {Path}", Folder + "/summer/one.mkv");
        _logger.LogWarning("Could not read {Folder}", Folder);

        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void ALogLineWhoseErrorTextNamesAVaultFile_IsNeverWritten()
    {
        _logger.LogError(new IOException("Access denied to " + Folder + "/summer/one.mkv"), "Could not hash a file");

        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void EveryLogLineInsideTheVaultScope_IsNeverWritten_EvenWhenItOnlyNamesABareFileName()
    {
        using (_logger.BeginScope(new Dictionary<string, object> { [VaultLocationGuard.LogScopeProperty] = true }))
        {
            _logger.LogInformation("Identified {File} as a scene", "one.mkv");
        }

        _logger.LogInformation("Next file");

        Assert.Equal(["Next file"], _sink.Events.Select(e => e.RenderMessage()));
    }

    [Fact]
    public void TheEnginesLogSetup_InstallsTheVaultFilter_AndTheIngestionPipelineOpensTheVaultScope()
    {
        var root = RepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "Program.cs"));
        var pipeline = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Ingestion", "IngestionEngine.Pipeline.cs"));

        Assert.Contains("Filter.ByExcluding(VaultLogLineFilter.NamesVaultLocation)", program, StringComparison.Ordinal);
        Assert.Contains("VaultLocationGuard.LogScopeProperty", pipeline, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
