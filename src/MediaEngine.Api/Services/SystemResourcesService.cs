using System.ComponentModel;
using System.Diagnostics;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Contracts.Settings;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Services;

/// <summary>A drive as the operating system reports it: its root, volume label and space in bytes.</summary>
public sealed record DriveReading(string Root, string Label, long TotalBytes, long FreeBytes);

/// <summary>
/// Reads what the Settings System summary shows: how long the Engine has been running, how much processor and
/// memory it uses, and the space on the drives that hold the library and the data store. Everything is read from
/// this machine; nothing is sent anywhere.
/// </summary>
public sealed class SystemResourcesService
{
    // Used only when the operating system will not say when this process started.
    private static readonly DateTimeOffset TypeLoadedAt = DateTimeOffset.UtcNow;

    private readonly Func<ResourceSnapshot> _snapshot;
    private readonly Func<string?> _libraryRoot;
    private readonly string _dataStorePath;
    private readonly Func<DateTimeOffset> _processStart;
    private readonly Func<string, DriveReading?> _driveFor;
    private readonly TimeProvider _clock;

    public SystemResourcesService(
        ResourceMonitorService monitor,
        IOptions<IngestionOptions> ingestion,
        string dataStorePath)
        : this(monitor.GetSnapshot, () => ingestion.Value.LibraryRoot, dataStorePath, ReadProcessStart, FindDrive, TimeProvider.System)
    {
    }

    public SystemResourcesService(
        Func<ResourceSnapshot> snapshot,
        Func<string?> libraryRoot,
        string dataStorePath,
        Func<DateTimeOffset> processStart,
        Func<string, DriveReading?> driveFor,
        TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataStorePath);
        _snapshot = snapshot;
        _libraryRoot = libraryRoot;
        _dataStorePath = Path.GetFullPath(dataStorePath);
        _processStart = processStart;
        _driveFor = driveFor;
        _clock = clock;
    }

    public SystemResourcesDto Read(string engineVersion)
    {
        var snapshot = _snapshot();
        var now = _clock.GetUtcNow();
        var started = _processStart();

        return new SystemResourcesDto
        {
            EngineVersion = engineVersion,
            StartedAt = started,
            UptimeSeconds = Math.Max(0, (long)(now - started).TotalSeconds),
            ProcessorPercent = Math.Round(Math.Clamp(snapshot.CpuPressure, 0.0, 1.0) * 100.0, 1),
            MemoryUsedMb = snapshot.EngineRamMb,
            MemoryTotalMb = snapshot.TotalRamMb,
            Drives = ReadDrives(),
            DataStoreSizeBytes = ReadDataStoreSize(),
        };
    }

    private List<SystemDriveDto> ReadDrives()
    {
        var drives = new List<SystemDriveDto>();
        var libraryRoot = _libraryRoot();
        if (!string.IsNullOrWhiteSpace(libraryRoot))
        {
            AddLocation(drives, libraryRoot, library: true);
        }

        var dataStoreFolder = Path.GetDirectoryName(_dataStorePath);
        if (!string.IsNullOrEmpty(dataStoreFolder))
        {
            AddLocation(drives, dataStoreFolder, library: false);
        }

        return drives;
    }

    private void AddLocation(List<SystemDriveDto> drives, string path, bool library)
    {
        var reading = _driveFor(path);
        if (reading is null)
        {
            return;
        }

        var existing = drives.FirstOrDefault(drive => string.Equals(drive.Root, reading.Root, PathComparison));
        if (existing is null)
        {
            existing = new SystemDriveDto
            {
                Root = reading.Root,
                Label = reading.Label,
                TotalBytes = reading.TotalBytes,
                FreeBytes = reading.FreeBytes,
            };
            drives.Add(existing);
        }

        if (library)
        {
            existing.HoldsLibrary = true;
        }
        else
        {
            existing.HoldsDataStore = true;
        }
    }

    private long? ReadDataStoreSize()
    {
        try
        {
            var file = new FileInfo(_dataStorePath);
            return file.Exists ? file.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The summary shows no size rather than failing the whole reading when the file cannot be inspected.
            _ = ex;
            return null;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static DateTimeOffset ReadProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The operating system would not say; the time this service type loaded is within moments of startup.
            _ = ex;
            return TypeLoadedAt;
        }
    }

    /// <summary>The drive or mount with the longest root that contains <paramref name="path"/>, or null when none is known.</summary>
    private static DriveReading? FindDrive(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path the system cannot interpret has no drive to report.
            _ = ex;
            return null;
        }

        DriveInfo? best = null;
        var bestLength = -1;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                if (root.Length > bestLength && full.StartsWith(root, PathComparison))
                {
                    best = drive;
                    bestLength = root.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that cannot answer (for example an unplugged removable drive) cannot hold this path.
                _ = ex;
            }
        }

        if (best is null)
        {
            return null;
        }

        try
        {
            return new DriveReading(
                best.RootDirectory.FullName,
                SafeLabel(best),
                best.TotalSize,
                best.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The drive went away between finding it and reading its space.
            _ = ex;
            return null;
        }
    }

    private static string SafeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some mounts have no readable label; the root is shown instead.
            _ = ex;
            return string.Empty;
        }
    }
}
