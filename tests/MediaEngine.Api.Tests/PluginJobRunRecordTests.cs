using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugin.MediaSegments;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Plugin job history is a durable run record, not in-memory state: it survives an Engine restart, and the run action
/// still answers with the unchanged <see cref="PluginJobSnapshot"/> shape.
/// </summary>
public sealed class PluginJobRunRecordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-plugin-job-runs-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;

    public PluginJobRunRecordTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "tuvima.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Fact]
    public async Task ScheduledPass_WritesARunRecordPerEnabledPlugin_AndReturnsItAsTheUnchangedSnapshotShape()
    {
        var (service, pluginId) = CreateService(new ScheduledTaskRunRepository(_database));

        var snapshots = await service.RunScheduledPassAsync();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(pluginId, snapshot.PluginId);
        Assert.Equal("playback-segment-detection", snapshot.JobType);
        Assert.Equal("completed", snapshot.Status);
        Assert.NotNull(snapshot.CompletedAt);
        Assert.Equal(0, snapshot.AssetsScanned);
        Assert.Null(snapshot.Error);

        var stored = Assert.Single(await new ScheduledTaskRunRepository(_database)
            .ListRecentAsync(ScheduledTaskKeys.PluginSegmentDetection, 10, pluginId: pluginId));
        Assert.Equal(snapshot.Id, stored.Id);
        Assert.Equal(ScheduledTaskOutcomes.Succeeded, stored.Outcome);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot)).RootElement;
        Assert.Equal(
            ["id", "pluginId", "jobType", "status", "startedAt", "completedAt", "assetsScanned", "segmentsWritten", "error"],
            json.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task ScheduledPass_WithNoSegmentPluginSwitchedOn_RecordsASkippedRunWithTheReason()
    {
        var (service, _) = CreateService(new ScheduledTaskRunRepository(_database), enable: false);

        var snapshots = await service.RunScheduledPassAsync();

        Assert.Empty(snapshots);
        var stored = Assert.Single(await new ScheduledTaskRunRepository(_database)
            .ListRecentAsync(ScheduledTaskKeys.PluginSegmentDetection, 10));
        Assert.Equal(ScheduledTaskOutcomes.Skipped, stored.Outcome);
        Assert.Equal("No playback segment plugin is switched on.", stored.Message);
    }

    [Fact]
    public async Task JobHistory_SurvivesARestart_BecauseANewRepositoryReadsTheSameRecords()
    {
        var (service, pluginId) = CreateService(new ScheduledTaskRunRepository(_database));
        await service.RunScheduledPassAsync();
        await service.RunScheduledPassAsync();
        var firstId = Assert.Single(await new ScheduledTaskRunRepository(_database)
            .ListRecentAsync(ScheduledTaskKeys.PluginSegmentDetection, 1, skip: 1, pluginId: pluginId)).Id;

        _database.Dispose();
        using var restarted = new DatabaseConnection(_databasePath);
        restarted.InitializeSchema();
        restarted.RunStartupChecks();
        var history = await new ScheduledTaskRunRepository(restarted)
            .ListRecentAsync(ScheduledTaskKeys.PluginSegmentDetection, 10, pluginId: pluginId);

        Assert.Equal(2, history.Count);
        Assert.Contains(history, run => run.Id == firstId);
        Assert.All(history, run => Assert.Equal(ScheduledTaskOutcomes.Succeeded, run.Outcome));
    }

    [Fact]
    public void Snapshot_ForAFailedRun_CarriesTheFailureMessageAsItsError()
    {
        var started = new DateTimeOffset(2026, 10, 19, 2, 0, 0, TimeSpan.Zero);
        var run = new ScheduledTaskRun(
            Guid.NewGuid(), ScheduledTaskKeys.PluginSegmentDetection, "tuvima.intro-skip", started,
            started.AddMinutes(2), ScheduledTaskOutcomes.Failed, 4, "ffmpeg could not open the file.");

        var snapshot = PluginScheduledSegmentService.ToSnapshot(run, segmentsWritten: 1);

        Assert.Equal(run.Id, snapshot.Id);
        Assert.Equal("tuvima.intro-skip", snapshot.PluginId);
        Assert.Equal("failed", snapshot.Status);
        Assert.Equal(started, snapshot.StartedAt);
        Assert.Equal(started.AddMinutes(2), snapshot.CompletedAt);
        Assert.Equal(4, snapshot.AssetsScanned);
        Assert.Equal(1, snapshot.SegmentsWritten);
        Assert.Equal("ffmpeg could not open the file.", snapshot.Error);
    }

    private (PluginScheduledSegmentService Service, string PluginId) CreateService(IScheduledTaskRunRepository runs, bool enable = true)
    {
        var configDirectory = Path.Combine(_root, "config");
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(libraryRoot);
        var loader = new ConfigurationDirectoryLoader(configDirectory);
        loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        var catalog = new PluginCatalog(
            [new IntroSkipPlugin()], new PluginSettingsService(loader), loader, NullLogger<PluginCatalog>.Instance);
        var pluginId = Assert.Single(catalog.List()).Manifest.Id;
        catalog.SetEnabled(pluginId, enable);

        var services = new ServiceCollection();
        services.AddSingleton<IMediaAssetRepository>(new MediaAssetRepository(_database));
        services.AddSingleton(new PluginSegmentDetectionService(
            catalog, null!, null!, null!, NullLogger<PluginSegmentDetectionService>.Instance));
        var provider = services.BuildServiceProvider();
        var service = new PluginScheduledSegmentService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            loader,
            catalog,
            new ScheduledTaskRunner(runs, NullLogger<ScheduledTaskRunner>.Instance),
            NullLogger<PluginScheduledSegmentService>.Instance);
        return (service, pluginId);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
