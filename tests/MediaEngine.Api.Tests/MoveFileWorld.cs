using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Services;
using MediaEngine.Intelligence.Models;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The disk side of a library move over real temp folders: a library with a Managed folder, an As-is folder and a read-only Managed
/// folder; the real organiser (naming policy, shared mutation policy with the real merge ledger, file mover, companion files and
/// stored path); and the real durable file queue drained by the real dispatcher. Only the tag writer is a spy.
/// </summary>
internal sealed class MoveFileWorld
{
    public MoveFileWorld(DatabaseConnection database, string root)
    {
        Database = database;
        Managed = Directory.CreateDirectory(Path.Combine(root, "managed")).FullName;
        AsIs = Directory.CreateDirectory(Path.Combine(root, "as-is")).FullName;
        ReadOnly = Directory.CreateDirectory(Path.Combine(root, "read-only")).FullName;
        Options = new IngestionOptions
        {
            LibraryFolders = BuildFolders(),
            // The naming templates the Engine ships in config/core.json.
            OrganizationTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = "{Category}/{Title} ({Year})/{Title}{Ext}",
                ["Audiobooks"] = "Audiobooks/{Author}/{BookTitle} ({Year})/{TrackNumber?} {TrackTitle}{Ext}",
                ["TV"] = "TV/{Series} ({Year})/Season {Season}/{Series} - s{Season}e{Episode} - {EpisodeTitle}{Ext}",
                ["Music"] = "Music/{AlbumArtist}/{Album} ({Year})/{TrackNumber} - {Title}{Ext}",
            },
        };
        MergeLedger = new FolderOrganiseLogRepository(database);
        Activity = new SystemActivityRepository(database);
        HashCache = new FileHashCacheRepository(database);
        Organizer = CreateOrganizer(new FileOrganizer(NullLogger<FileOrganizer>.Instance, HashCache));
        Intents = new MediaFileWriteIntentRepository(database);
        WriteBack = new SpyWriteBack();
        Dispatcher = new MediaFileWriteIntentDispatcher(Intents, WriteBack, Organizer, NullLogger<MediaFileWriteIntentDispatcher>.Instance);
    }

    public DatabaseConnection Database { get; }
    public string Managed { get; }
    public string AsIs { get; }
    public string ReadOnly { get; }
    public IngestionOptions Options { get; }
    public FolderOrganiseLogRepository MergeLedger { get; }
    public SystemActivityRepository Activity { get; }
    public FileHashCacheRepository HashCache { get; }
    public AutoOrganizeService Organizer { get; }
    public MediaFileWriteIntentRepository Intents { get; }
    public SpyWriteBack WriteBack { get; }
    public MediaFileWriteIntentDispatcher Dispatcher { get; }

    /// <summary>The real organiser over a file organiser of the test's choosing (one that fails a move, say).</summary>
    public AutoOrganizeService CreateOrganizer(IFileOrganizer fileOrganizer) => new(
        new MediaAssetRepository(Database),
        new CanonicalValueRepository(Database),
        fileOrganizer,
        Activity,
        new ReviewQueueRepository(Database),
        new NoEvents(),
        new OrganizationGate(new ScoringConfiguration()),
        Microsoft.Extensions.Options.Options.Create(Options),
        NullLogger<AutoOrganizeService>.Instance,
        workRepo: new WorkRepository(Database),
        libraryResolver: new LibraryFolderResolver(new StaticMonitor(Options)),
        sourceMutationGate: new SourceMutationPolicyGate(MergeLedger),
        sidecars: new SidecarMover(new SystemOrganiseFileMover(), NullLogger<SidecarMover>.Instance));

    /// <summary>Writes a real file (and its folders) and returns its full path.</summary>
    public static string WriteFile(string folder, string relativePath, string content = "audio bytes")
    {
        var path = Path.GetFullPath(Path.Combine(folder, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Lists the file as the original of a folder merge, so the shared policy protects it.</summary>
    public async Task MarkMergeOriginalAsync(string path) =>
        await MergeLedger.UpsertAsync(new FolderOrganiseLogEntry(
            Guid.NewGuid(), Guid.NewGuid(), path, path + ".copy", FolderOrganiseOutcomes.Copied, DateTimeOffset.UtcNow));

    /// <summary>Runs the durable file queue until it has nothing left to claim.</summary>
    public async Task<int> DrainQueueAsync()
    {
        var handled = 0;
        while (await Dispatcher.DispatchOnceAsync())
        {
            handled++;
        }

        return handled;
    }

    public string StoredPath(Guid assetId)
    {
        using var connection = Database.CreateConnection();
        return connection.QuerySingle<string>("SELECT file_path_root FROM media_assets WHERE id = @assetId;", new { assetId });
    }

    public void Execute(string sql, object? args = null)
    {
        using var connection = Database.CreateConnection();
        connection.Execute(sql, args);
    }

    public T Scalar<T>(string sql, object? args = null)
    {
        using var connection = Database.CreateConnection();
        return connection.ExecuteScalar<T>(sql, args)!;
    }

    public long AssetCount()
    {
        using var connection = Database.CreateConnection();
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM media_assets;");
    }

    public (string Status, string Trigger, string? Error) Intent(Guid assetId)
    {
        using var connection = Database.CreateConnection();
        return connection.QuerySingle<(string, string, string?)>(
            "SELECT status AS Status, trigger AS Trigger, last_error AS Error FROM media_file_write_intents WHERE asset_id = @assetId;",
            new { assetId });
    }

    public IReadOnlyList<string> ActivityDetails(Guid assetId)
    {
        using var connection = Database.CreateConnection();
        return [.. connection.Query<string>(
            "SELECT detail FROM system_activity WHERE entity_id = @assetId AND action_type = 'PathUpdated' ORDER BY id;",
            new { assetId })];
    }

    private IReadOnlyList<LibraryFolderEntry> BuildFolders() =>
    [
        new LibraryFolderEntry
        {
            Id = "music",
            Name = "Music",
            PrimaryDestinationSourceId = "managed",
            Sources =
            [
                Source("managed", Managed, LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.Writable),
                Source("as-is", AsIs, LibrarySourceManagementModes.ExistingLibrary, LibrarySourceAccessModes.ReadOnly),
                Source("read-only", ReadOnly, LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.ReadOnly),
            ],
        },
    ];

    private static LibrarySourceEntry Source(string id, string path, string management, string access) =>
        new() { Id = id, Path = path, ManagementMode = management, AccessMode = access };

    /// <summary>What the preview says in a world that only records files: nothing happens to any file.</summary>
    internal sealed class NoFilesOrganizer : IAutoOrganizeService
    {
        public Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AssetRefilePlan> PlanRefileAsync(Guid assetId, AssetRefilePlacement? placement, CancellationToken ct = default) =>
            Task.FromResult(AssetRefilePlan.Of(AssetRefileDisposition.AlreadyFits, string.Empty));

        public Task<AssetRefileOutcome> RefileAsync(Guid assetId, CancellationToken ct = default) =>
            Task.FromResult(new AssetRefileOutcome(AssetRefileOutcomeKind.AlreadyFits, AssetRefileDisposition.AlreadyFits, string.Empty, null));
    }

    /// <summary>Records every tag write the queue asks for and says it was verified.</summary>
    internal sealed class SpyWriteBack : IWriteBackOutcomeService
    {
        public List<(Guid AssetId, string Trigger, string PathAtTheTime)> Calls { get; } = [];

        public Func<Guid, string>? PathOf { get; set; }

        public Task WriteMetadataAsync(Guid assetId, string trigger, CancellationToken ct = default) =>
            WriteMetadataWithOutcomeAsync(assetId, trigger, ct);

        public Task<WriteBackOutcome> WriteMetadataWithOutcomeAsync(Guid assetId, string trigger, CancellationToken ct = default)
        {
            Calls.Add((assetId, trigger, PathOf?.Invoke(assetId) ?? string.Empty));
            return Task.FromResult(WriteBackOutcome.Verified());
        }
    }

    /// <summary>A file organiser whose first moves fail (a file in use), then behaves like the real one.</summary>
    internal sealed class FlakyFileOrganizer(IFileOrganizer inner, int failures) : IFileOrganizer
    {
        public int Attempts { get; private set; }

        public string CalculatePath(IngestionCandidate candidate, string template) => inner.CalculatePath(candidate, template);

        public string? ValidateTemplate(string template, out string? error) => inner.ValidateTemplate(template, out error);

        public Task<string?> ExecuteMoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
        {
            Attempts++;
            return Attempts <= failures ? Task.FromResult<string?>(null) : inner.ExecuteMoveAsync(sourcePath, destinationPath, ct);
        }
    }

    private sealed class NoEvents : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }

    private sealed class StaticMonitor(IngestionOptions value) : IOptionsMonitor<IngestionOptions>
    {
        public IngestionOptions CurrentValue => value;

        public IngestionOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<IngestionOptions, string?> listener) => null;
    }
}
