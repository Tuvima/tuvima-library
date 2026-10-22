using MediaEngine.Api.Services.Libraries;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A real temp data store and real temp folders behind the real excluded-file service: a managed Movies library and an as-is Old Rips
/// library. The scan intake is a hand-written spy that remembers what it was asked to take in, and the real filter is wrapped so the
/// tests can count how often the scans were told to forget their remembered exclusions.
/// </summary>
internal sealed class ExcludedFileTestWorld : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_excluded_files_{Guid.NewGuid():N}.db");

    public ExcludedFileTestWorld()
    {
        DapperConfiguration.Configure();
        Root = Path.Combine(Path.GetTempPath(), $"tuvima_excluded_files_{Guid.NewGuid():N}");
        Managed = Directory.CreateDirectory(Path.Combine(Root, "managed")).FullName;
        AsIs = Directory.CreateDirectory(Path.Combine(Root, "as-is")).FullName;

        Database = new DatabaseConnection(_databasePath);
        Database.InitializeSchema();
        Database.RunStartupChecks();

        var options = new IngestionOptions { LibraryFolders = BuildFolders() };
        Resolver = new LibraryFolderResolver(new StaticMonitor(options));
        Excluded = new ExcludedSourceFileRepository(Database);
        RealFilter = new ExcludedSourceFileFilter(Resolver, Excluded, new AssetHasher(), NullLogger<ExcludedSourceFileFilter>.Instance);
        Filter = new CountingFilter(RealFilter);
        Intake = new SpyIntake();
        Service = new ExcludedFileService(Excluded, Resolver, Filter, Intake, NullLogger<ExcludedFileService>.Instance, new FixedVaultLibraries());
    }

    public string Root { get; }
    public string Managed { get; }
    public string AsIs { get; }
    public Guid ManagedSourceId { get; } = Guid.NewGuid();
    public Guid AsIsSourceId { get; } = Guid.NewGuid();
    public DatabaseConnection Database { get; }
    public ILibraryFolderResolver Resolver { get; }
    public ExcludedSourceFileRepository Excluded { get; }
    public ExcludedSourceFileFilter RealFilter { get; }
    public CountingFilter Filter { get; }
    public SpyIntake Intake { get; }
    public ExcludedFileService Service { get; }

    /// <summary>Writes a real file (and its folder) and returns its full path.</summary>
    public string WriteFile(string folder, string relativePath, int bytes = 100)
    {
        var path = Path.Combine(folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>Remembers a file as excluded, as the deletion does, and returns its row.</summary>
    public async Task<ExcludedSourceFile> ExcludeAsync(
        string libraryId,
        string relativePath,
        DateTimeOffset excludedAt,
        string? title = "Dune",
        string reason = ExcludedSourceFileReasons.AsIs)
    {
        var asIs = libraryId == "old-rips";
        var sourceRoot = asIs ? AsIs : Managed;
        var info = new FileInfo(Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var row = new ExcludedSourceFile(
            Guid.NewGuid(),
            libraryId,
            (asIs ? AsIsSourceId : ManagedSourceId).ToString("D"),
            relativePath,
            ContentHash: null,
            FileSize: info.Exists ? info.Length : null,
            LastWriteUtc: info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null,
            title,
            excludedAt,
            ExcludedByAccountId: Guid.NewGuid(),
            ExcludedByName: "Shy",
            reason);
        await Excluded.AddOrReplaceAsync(row);
        Filter.Invalidate();
        Filter.Invalidations = 0;
        return row;
    }

    public void Dispose()
    {
        Database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
        TestTemp.DeleteDirectory(Root);
    }

    private IReadOnlyList<LibraryFolderEntry> BuildFolders() =>
    [
        new LibraryFolderEntry
        {
            Id = "movies",
            Name = "Movies",
            PrimaryDestinationSourceId = ManagedSourceId.ToString("D"),
            Sources =
            [
                new LibrarySourceEntry
                {
                    Id = ManagedSourceId.ToString("D"),
                    Path = Managed,
                    ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                    AccessMode = LibrarySourceAccessModes.Writable,
                },
            ],
        },
        new LibraryFolderEntry
        {
            Id = "old-rips",
            Name = "Old Rips",
            Sources =
            [
                new LibrarySourceEntry
                {
                    Id = AsIsSourceId.ToString("D"),
                    Path = AsIs,
                    ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
                    AccessMode = LibrarySourceAccessModes.ReadOnly,
                },
            ],
        },
    ];

    /// <summary>The real filter, with a count of how many times the scans were told to forget what they remembered.</summary>
    internal sealed class CountingFilter(IExcludedSourceFileFilter inner) : IExcludedSourceFileFilter
    {
        public int Invalidations { get; set; }

        public Task<bool> ShouldSkipAsync(string fullPath, CancellationToken ct = default) => inner.ShouldSkipAsync(fullPath, ct);

        public void Invalidate()
        {
            Invalidations++;
            inner.Invalidate();
        }
    }

    /// <summary>Remembers every file the service asked the ingestion engine to take in; can be made to refuse.</summary>
    internal sealed class SpyIntake : IFileIntake
    {
        public List<(string Path, string LibraryId, string SourceId)> Requests { get; } = [];

        public Exception? Failure { get; set; }

        public Task EnqueueAsync(string path, string libraryId, string sourceId, CancellationToken ct = default)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Requests.Add((path, libraryId, sourceId));
            return Task.CompletedTask;
        }
    }

    private sealed class StaticMonitor(IngestionOptions value) : IOptionsMonitor<IngestionOptions>
    {
        public IngestionOptions CurrentValue => value;

        public IngestionOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<IngestionOptions, string?> listener) => null;
    }
}
