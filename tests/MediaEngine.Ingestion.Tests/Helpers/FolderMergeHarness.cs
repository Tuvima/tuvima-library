using System.Reflection;
using Dapper;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Services;
using MediaEngine.Intelligence.Models;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests.Helpers;

/// <summary>
/// A real folder merge setup on temp folders and a real data store: a Movies library with a source folder (As-is or Managed)
/// and a managed target folder, the real naming rules, transfer, organiser, ledger, runner and previewer. Only the hasher and
/// the asset repository go through seams so a copy that does not match, or a process that stops part way, can be staged on any
/// operating system.
/// </summary>
internal sealed class FolderMergeHarness : IDisposable
{
    public const string LibraryId = "cccccccc-0000-4000-8000-000000000001";

    public string Root { get; }
    public string SourceDir { get; }
    public string TargetDir { get; }
    public Guid SourceId { get; } = Guid.NewGuid();
    public Guid TargetId { get; } = Guid.NewGuid();
    public Guid OperationId { get; private set; } = Guid.NewGuid();

    public StubConfigurationLoader Config { get; } = new();
    public TestDatabaseFactory Db { get; } = new();
    public IMediaAssetRepository Assets { get; }
    public IMediaAssetRepository RealAssets { get; }
    public ICanonicalValueRepository Canonicals { get; }
    public IReviewQueueRepository Reviews { get; }
    public ISystemActivityRepository Activity { get; }
    public IFolderOrganiseLogRepository Ledger { get; }
    public ScriptedHasher Hasher { get; } = new();
    public RecordingIntake Intake { get; } = new();
    public IngestionOptions Options { get; }
    public FolderMergeOrganiser Organiser { get; }
    public FolderMergeRunner Runner { get; }
    public FolderMergePreviewer Previewer { get; }

    public FolderMergeHarness(
        string duplicatePolicy = LibraryDuplicatePolicies.KeepOne,
        bool sourceManaged = false,
        bool move = false,
        Action<CrashProxy<IMediaAssetRepository>>? scriptAssets = null,
        TimeSpan? heartbeat = null)
    {
        Root = Path.Combine(Path.GetTempPath(), $"tuvima_merge_{Guid.NewGuid():N}");
        SourceDir = Path.Combine(Root, "Plex", "Movies");
        TargetDir = Path.Combine(Root, "Tuvima", "Movies");
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(TargetDir);

        Config.Libraries = new LibrariesConfiguration
        {
            FileHandling = new FileHandlingConfig { DuplicatePolicy = duplicatePolicy },
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = LibraryId,
                    Name = "Movies",
                    Kind = LibraryKinds.Catalogued,
                    KindKey = "movies",
                    MediaTypes = ["Movies"],
                    Sources =
                    [
                        new LibrarySourceConfig
                        {
                            Id = SourceId.ToString("D"),
                            Path = SourceDir,
                            ManagementMode = sourceManaged ? LibrarySourceManagementModes.ManagedByTuvima : LibrarySourceManagementModes.ExistingLibrary,
                            AccessMode = sourceManaged ? LibrarySourceAccessModes.Writable : LibrarySourceAccessModes.ReadOnly,
                            MergeState = LibrarySourceMergeStates.Merging,
                            MergeOperationId = OperationId.ToString("D"),
                            MergeTargetSourceId = TargetId.ToString("D"),
                            MergeMode = move ? LibraryMergeModes.Move : LibraryMergeModes.Copy,
                        },
                        new LibrarySourceConfig
                        {
                            Id = TargetId.ToString("D"),
                            Path = TargetDir,
                            ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                            AccessMode = LibrarySourceAccessModes.Writable,
                        },
                    ],
                    PrimaryDestinationSourceId = TargetId.ToString("D"),
                },
            ],
        };

        var db = Db.Connection;
        RealAssets = new MediaAssetRepository(db);
        Canonicals = new CanonicalValueRepository(db);
        Reviews = new ReviewQueueRepository(db);
        Activity = new SystemActivityRepository(db);
        Ledger = new FolderOrganiseLogRepository(db);
        Assets = BuildAssets(scriptAssets);

        Options = new IngestionOptions
        {
            LibraryRoot = TargetDir,
            AutoOrganize = true,
            DuplicatePolicy = duplicatePolicy,
            WatchDirectories = [SourceDir, TargetDir],
            LibraryFolders = Config.Libraries.Libraries.Select(LibraryFolderEntryFactory.Create).ToList(),
            PollIntervalSeconds = 0,
            IncludeSubdirectories = true,
        };
        var monitor = new OptionsMonitorStub<IngestionOptions>(Options);

        var mover = new ScriptedMover();
        var organizer = new FileOrganizer(NullLogger<FileOrganizer>.Instance);
        var sidecars = new SidecarMover(mover, NullLogger<SidecarMover>.Instance);
        var auto = new AutoOrganizeService(
            Assets,
            Canonicals,
            organizer,
            Activity,
            Reviews,
            new StubEventPublisher(),
            new OrganizationGate(new ScoringConfiguration()),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<AutoOrganizeService>.Instance,
            workRepo: new WorkRepository(db),
            libraryResolver: new LibraryFolderResolver(monitor),
            sourceMutationGate: new SourceMutationPolicyGate(Ledger),
            sidecars: sidecars);
        var naming = new FolderConversionOrganiser(
            auto,
            Assets,
            Canonicals,
            organizer,
            new OrganizationGate(new ScoringConfiguration()),
            Reviews,
            Activity,
            Ledger,
            Hasher,
            new SourceMutationPolicyGate(Ledger),
            sidecars,
            mover,
            monitor,
            NullLogger<FolderConversionOrganiser>.Instance);
        Organiser = new FolderMergeOrganiser(
            naming,
            Assets,
            Reviews,
            Activity,
            Ledger,
            Hasher,
            new SourceMutationPolicyGate(Ledger),
            new MergeFileTransfer(Hasher, NullLogger<MergeFileTransfer>.Instance),
            sidecars,
            NullLogger<FolderMergeOrganiser>.Instance);
        Runner = new FolderMergeRunner(
            Config,
            Assets,
            Ledger,
            Organiser,
            NullLogger<FolderMergeRunner>.Instance,
            intake: Intake,
            timing: new FolderOrganiseTiming
            {
                RetryDelay = TimeSpan.FromMilliseconds(5),
                RetryRounds = 3,
                ProgressInterval = TimeSpan.Zero,
                SettingsRefresh = TimeSpan.Zero,
                HeartbeatInterval = heartbeat ?? TimeSpan.Zero,
            });
        Previewer = new FolderMergePreviewer(Config, Assets, Organiser, NullLogger<FolderMergePreviewer>.Instance);
    }

    private IMediaAssetRepository BuildAssets(Action<CrashProxy<IMediaAssetRepository>>? script)
    {
        if (script is null)
        {
            return RealAssets;
        }

        var proxy = DispatchProxy.Create<IMediaAssetRepository, CrashProxy<IMediaAssetRepository>>();
        var crash = (CrashProxy<IMediaAssetRepository>)(object)proxy;
        crash.Target = RealAssets;
        script(crash);
        return proxy;
    }

    public FolderMergeJob Job => new(OperationId, SourceId);

    /// <summary>The person stops the merge: the folder is a plain folder again, exactly as it was before the merge was started.</summary>
    public void SettleCancelledMerge()
    {
        Source.MergeState = LibrarySourceMergeStates.None;
        Source.MergeOperationId = null;
        Source.MergeTargetSourceId = null;
        Source.MergeMode = null;
        Source.MergedAt = null;
    }

    /// <summary>The person starts the merge again: a new operation, the same folders, and a ledger that still holds the earlier one.</summary>
    public void StartMergeAgain(bool move = false)
    {
        OperationId = Guid.NewGuid();
        Source.MergeState = LibrarySourceMergeStates.Merging;
        Source.MergeOperationId = OperationId.ToString("D");
        Source.MergeTargetSourceId = TargetId.ToString("D");
        Source.MergeMode = move ? LibraryMergeModes.Move : LibraryMergeModes.Copy;
    }

    public LibrarySourceConfig Source => Config.Libraries.Libraries[0].Sources[0];

    public LibrarySourceConfig Target => Config.Libraries.Libraries[0].Sources[1];

    public string WriteSource(string relativePath, string content = "media bytes") => Write(SourceDir, relativePath, content);

    public string WriteTarget(string relativePath, string content = "media bytes") => Write(TargetDir, relativePath, content);

    private static string Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public IEnumerable<string> SourceFiles() => List(SourceDir);

    /// <summary>Every file in the target, relative and with forward slashes, without the temporary copies a stopped merge leaves.</summary>
    public IEnumerable<string> TargetFiles() => List(TargetDir);

    private static IEnumerable<string> List(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    /// <summary>A fingerprint of everything under a folder (names, sizes, times, bytes), to prove a folder was never written.</summary>
    public static string Fingerprint(string root)
    {
        var lines = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path);
            if (Directory.Exists(path))
            {
                lines.Add($"D {relative}");
                continue;
            }

            var info = new FileInfo(path);
            using var stream = File.OpenRead(path);
            lines.Add($"F {relative} {info.Length} {info.LastWriteTimeUtc.Ticks} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>Registers a file the way ingestion would have (work, edition, asset, title, type), without moving it.</summary>
    public async Task<Guid> RegisterAsync(string path, string title, string? year = null, MediaType type = MediaType.Movies)
    {
        var hash = (await Hasher.ComputeAsync(path)).Hex;
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var connection = Db.Connection.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, @mediaType, 'standalone', 'Owned');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id)
                VALUES (@assetId, @editionId, @contentHash, @path, 'Normal', @libraryId);
                """,
                new { workId, mediaType = type.ToString(), editionId, assetId, contentHash = hash, path, libraryId = LibraryId });
        }

        var values = new List<CanonicalValue>
        {
            new() { EntityId = assetId, Key = "title", Value = title, LastScoredAt = DateTimeOffset.UtcNow },
            new() { EntityId = assetId, Key = "media_type", Value = type.ToString(), LastScoredAt = DateTimeOffset.UtcNow },
        };
        if (year is not null)
        {
            values.Add(new CanonicalValue { EntityId = assetId, Key = "year", Value = year, LastScoredAt = DateTimeOffset.UtcNow });
        }

        await Canonicals.UpsertBatchAsync(values);
        return assetId;
    }

    public async Task AddPendingReviewAsync(Guid assetId)
    {
        await Reviews.InsertAsync(new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = assetId,
            EntityType = "MediaAsset",
            Trigger = MediaEngine.Domain.Constants.ReviewTrigger.LowConfidence,
            ConfidenceScore = 0.2,
            Detail = "Needs a person to confirm the match.",
            ReviewReadyAt = DateTimeOffset.UtcNow,
            AutomationCompletedAt = DateTimeOffset.UtcNow,
        });
    }

    public Task<FolderOrganiseRunResult> RunAsync(TestHost? host = null, CancellationToken ct = default) =>
        Runner.RunAsync(Job, host ?? new TestHost(), ct);

    /// <summary>A second Engine start: the same data store, folders and settings, with fresh in-memory services.</summary>
    public FolderMergeRunner NewRunner(IMediaAssetRepository assets, ScriptedHasher? hasher = null)
    {
        var useHasher = hasher ?? Hasher;
        var monitor = new OptionsMonitorStub<IngestionOptions>(Options);
        var mover = new ScriptedMover();
        var organizer = new FileOrganizer(NullLogger<FileOrganizer>.Instance);
        var sidecars = new SidecarMover(mover, NullLogger<SidecarMover>.Instance);
        var auto = new AutoOrganizeService(
            assets, Canonicals, organizer, Activity, Reviews, new StubEventPublisher(),
            new OrganizationGate(new ScoringConfiguration()),
            Microsoft.Extensions.Options.Options.Create(Options), NullLogger<AutoOrganizeService>.Instance,
            workRepo: new WorkRepository(Db.Connection), libraryResolver: new LibraryFolderResolver(monitor),
            sourceMutationGate: new SourceMutationPolicyGate(Ledger), sidecars: sidecars);
        var naming = new FolderConversionOrganiser(
            auto, assets, Canonicals, organizer, new OrganizationGate(new ScoringConfiguration()), Reviews, Activity, Ledger,
            useHasher, new SourceMutationPolicyGate(Ledger), sidecars, mover, monitor, NullLogger<FolderConversionOrganiser>.Instance);
        var organiser = new FolderMergeOrganiser(
            naming, assets, Reviews, Activity, Ledger, useHasher, new SourceMutationPolicyGate(Ledger),
            new MergeFileTransfer(useHasher, NullLogger<MergeFileTransfer>.Instance), sidecars,
            NullLogger<FolderMergeOrganiser>.Instance);
        return new FolderMergeRunner(
            Config, assets, Ledger, organiser, NullLogger<FolderMergeRunner>.Instance, intake: Intake,
            timing: new FolderOrganiseTiming
            {
                RetryDelay = TimeSpan.FromMilliseconds(5),
                RetryRounds = 3,
                ProgressInterval = TimeSpan.Zero,
                SettingsRefresh = TimeSpan.Zero,
            });
    }

    public void Dispose()
    {
        Db.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked temp folder is removed by the next run's cleanup of tuvima_merge_* folders.
            _ = ex;
        }
    }
}

/// <summary>A hasher that can be told to report a different fingerprint for some files, like a disk that returns wrong bytes.</summary>
internal sealed class ScriptedHasher : IAssetHasher
{
    private readonly TestAssetHasher _real = new();

    /// <summary>Returns a fingerprint to report instead of the real one, or null to use the real one.</summary>
    public Func<string, string?>? Override { get; set; }

    public Task<HashResult> ComputeAsync(string filePath, CancellationToken ct = default)
    {
        var forced = Override?.Invoke(filePath);
        if (forced is null)
        {
            return _real.ComputeAsync(filePath, ct);
        }

        return Task.FromResult(new HashResult
        {
            FilePath = filePath,
            Hex = forced,
            FileSize = new FileInfo(filePath).Length,
            Elapsed = TimeSpan.FromMilliseconds(1),
        });
    }
}

/// <summary>Forwards every call to a real object, but can throw <see cref="SimulatedCrashException"/> on a named method once.</summary>
public class CrashProxy<T> : DispatchProxy
    where T : class
{
    private readonly HashSet<string> _crashOnce = new(StringComparer.Ordinal);

    public T? Target { get; set; }

    /// <summary>The next call to this method throws, as if the Engine had been killed at that point.</summary>
    public void CrashOnNext(string methodName) => _crashOnce.Add(methodName);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (_crashOnce.Remove(targetMethod.Name))
        {
            throw new SimulatedCrashException();
        }

        try
        {
            return targetMethod.Invoke(Target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
