using Dapper;
using MediaEngine.Api.DependencyInjection;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.DependencyInjection;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The tools page counts waiting files through <see cref="IToolWaitingWorkCounter"/>, and waiting files resume through an
/// <see cref="IMediaToolStatusListener"/>. Both must be the real ones in the running Engine, whichever module registers first.
/// </summary>
public sealed class MediaToolWaitingCompositionTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_di_waiting_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public MediaToolWaitingCompositionTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    public void Dispose()
    {
        _database.Dispose();
        foreach (var leftover in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(leftover);
            }
            catch (IOException)
            {
                // A temporary data store that is still locked is left for the operating system's temp clean-up.
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheRealWaitingFileCounterWins_WhicheverModuleIsRegisteredFirst(bool toolsModuleFirst)
    {
        using var provider = Compose(toolsModuleFirst);
        var asset = SeedHeldAsset();

        var counter = provider.GetRequiredService<IToolWaitingWorkCounter>();

        Assert.IsType<MediaInspectionWaitingWorkCounter>(counter);
        Assert.Equal(1, await counter.CountWaitingAsync("ffmpeg", CancellationToken.None));
        Assert.Equal(0, await counter.CountWaitingAsync("tesseract", CancellationToken.None));
        Assert.NotEqual(Guid.Empty, asset);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WaitingFilesResumeThroughTheToolsCheck_WhicheverModuleIsRegisteredFirst(bool toolsModuleFirst)
    {
        using var provider = Compose(toolsModuleFirst);

        var listeners = provider.GetServices<IMediaToolStatusListener>().ToList();

        Assert.Single(listeners.OfType<PendingMediaInspectionResumer>());
    }

    private ServiceProvider Compose(bool toolsModuleFirst)
    {
        var configuration = new ConfigurationManager();
        var configLoader = new ConfigurationDirectoryLoader(Path.Combine(FindRepoRoot(), "config"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IConfigurationLoader>(configLoader);
        services.AddSingleton<IDatabaseConnection>(_database);
        services.AddSingleton<IFFmpegService, MissingFfmpeg>();
        services.AddTuvimaStorage();
        if (toolsModuleFirst)
        {
            services.AddTuvimaToolRequirements();
            services.AddMediaEngineIngestion(configuration, configLoader);
        }
        else
        {
            services.AddMediaEngineIngestion(configuration, configLoader);
            services.AddTuvimaToolRequirements();
        }

        return services.BuildServiceProvider();
    }

    private Guid SeedHeldAsset()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _database.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@assetId, @editionId, @hash, @path);
            INSERT INTO media_inspection_holds (asset_id, tool_id, held_at) VALUES (@assetId, 'ffmpeg', @now);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{assetId:N}.mkv",
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        return assetId;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private sealed class MissingFfmpeg : IFFmpegService
    {
        public string? FfmpegPath => null;
        public string? FfprobePath => null;
        public bool IsAvailable => false;
        public HardwareCapabilities HardwareCapabilities => new();
        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) => Task.FromResult<MediaProbeResult?>(null);

        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
