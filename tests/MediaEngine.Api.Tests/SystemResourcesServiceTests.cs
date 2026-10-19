using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Api.Services;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Settings System summary reads the Engine's own process and the drives that hold the library and the
/// data store. These tests use temp folders and hand-written readings so no real machine state is assumed,
/// except one that checks the real drive reader against a real temp folder.
/// </summary>
public sealed class SystemResourcesServiceTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly DriveReading DriveC = new(@"C:\", "System", 1_000, 400);
    private static readonly DriveReading DriveD = new(@"D:\", "Media", 8_000, 6_000);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-resources-{Guid.NewGuid():N}");

    public SystemResourcesServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Reading_ReportsVersionStartTimeAndHowLongTheEngineHasBeenRunning()
    {
        var service = Build(now: Started.AddDays(2).AddHours(3).AddSeconds(5));

        var reading = service.Read("1.4.2");

        Assert.Equal("1.4.2", reading.EngineVersion);
        Assert.Equal(Started, reading.StartedAt);
        Assert.Equal((2 * 24 + 3) * 3600 + 5, reading.UptimeSeconds);
    }

    [Fact]
    public void Reading_TurnsTheMonitorsProcessorShareIntoAPercentOfTheMachine_AndReportsMemoryInMegabytes()
    {
        var service = Build(snapshot: new ResourceSnapshot { CpuPressure = 0.256, EngineRamMb = 640, TotalRamMb = 16_000 });

        var reading = service.Read("1");

        Assert.Equal(25.6, reading.ProcessorPercent);
        Assert.Equal(640, reading.MemoryUsedMb);
        Assert.Equal(16_000, reading.MemoryTotalMb);
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.7, 100.0)]
    public void Reading_KeepsTheProcessorPercentBetweenZeroAndAHundred(double pressure, double expected)
    {
        var service = Build(snapshot: new ResourceSnapshot { CpuPressure = pressure });

        Assert.Equal(expected, service.Read("1").ProcessorPercent);
    }

    [Fact]
    public void LibraryAndDataStoreOnTwoDrives_AreReportedAsTwoDrivesEachNamingWhatItHolds()
    {
        var service = Build(
            libraryRoot: @"D:\Media",
            dataStorePath: @"C:\Tuvima\library.db",
            driveFor: path => path.StartsWith("D:", StringComparison.OrdinalIgnoreCase) ? DriveD : DriveC);

        var drives = service.Read("1").Drives;

        Assert.Equal(2, drives.Count);
        var library = Assert.Single(drives, drive => drive.HoldsLibrary);
        Assert.Equal(@"D:\", library.Root);
        Assert.Equal("Media", library.Label);
        Assert.Equal(8_000, library.TotalBytes);
        Assert.Equal(6_000, library.FreeBytes);
        Assert.False(library.HoldsDataStore);
        var dataStore = Assert.Single(drives, drive => drive.HoldsDataStore);
        Assert.Equal(@"C:\", dataStore.Root);
        Assert.False(dataStore.HoldsLibrary);
    }

    [Fact]
    public void LibraryAndDataStoreOnTheSameDrive_AreReportedOnceWithBothLabels()
    {
        var service = Build(libraryRoot: @"C:\Media", dataStorePath: @"C:\Tuvima\library.db", driveFor: _ => DriveC);

        var drive = Assert.Single(service.Read("1").Drives);

        Assert.True(drive.HoldsLibrary);
        Assert.True(drive.HoldsDataStore);
        Assert.Equal(1_000, drive.TotalBytes);
        Assert.Equal(400, drive.FreeBytes);
    }

    [Fact]
    public void ALocationWhoseDriveCannotBeRead_IsLeftOut_WithoutHidingTheOther()
    {
        var service = Build(
            libraryRoot: @"\\nas\media",
            dataStorePath: @"C:\Tuvima\library.db",
            driveFor: path => path.StartsWith(@"\\", StringComparison.Ordinal) ? null : DriveC);

        var drive = Assert.Single(service.Read("1").Drives);

        Assert.True(drive.HoldsDataStore);
        Assert.False(drive.HoldsLibrary);
    }

    [Fact]
    public void NoLibraryRootConfigured_ReportsOnlyTheDataStoreDrive()
    {
        var service = Build(libraryRoot: "  ", dataStorePath: @"C:\Tuvima\library.db", driveFor: _ => DriveC);

        var drive = Assert.Single(service.Read("1").Drives);

        Assert.True(drive.HoldsDataStore);
        Assert.False(drive.HoldsLibrary);
    }

    [Fact]
    public void DataStoreSize_IsTheFileLength_AndMissingWhenTheFileDoesNotExist()
    {
        var file = Path.Combine(_root, "library.db");
        File.WriteAllBytes(file, new byte[4096]);

        Assert.Equal(4096, Build(dataStorePath: file).Read("1").DataStoreSizeBytes);
        Assert.Null(Build(dataStorePath: Path.Combine(_root, "missing.db")).Read("1").DataStoreSizeBytes);
    }

    [Fact]
    public void TheRealDriveReader_FindsTheDriveThatHoldsRealTempFolders()
    {
        var library = Directory.CreateDirectory(Path.Combine(_root, "library")).FullName;
        var file = Path.Combine(_root, "library.db");
        File.WriteAllBytes(file, new byte[10]);
        var service = new SystemResourcesService(
            new ResourceMonitorService(NullLogger<ResourceMonitorService>.Instance, new AiSettings()),
            Options.Create(new IngestionOptions { LibraryRoot = library }),
            file);

        var reading = service.Read("1");

        var drive = Assert.Single(reading.Drives);
        Assert.True(drive.HoldsLibrary);
        Assert.True(drive.HoldsDataStore);
        Assert.True(drive.TotalBytes > 0);
        Assert.InRange(drive.FreeBytes, 0, drive.TotalBytes);
        Assert.Equal(10, reading.DataStoreSizeBytes);
        Assert.True(reading.UptimeSeconds >= 0);
        Assert.True(reading.MemoryTotalMb > 0);
    }

    private static SystemResourcesService Build(
        ResourceSnapshot? snapshot = null,
        DateTimeOffset? now = null,
        string libraryRoot = @"C:\Media",
        string dataStorePath = @"C:\Tuvima\library.db",
        Func<string, DriveReading?>? driveFor = null) =>
        new(
            () => snapshot ?? new ResourceSnapshot(),
            () => libraryRoot,
            dataStorePath,
            () => Started,
            driveFor ?? (_ => DriveC),
            new FixedClock(now ?? Started));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
