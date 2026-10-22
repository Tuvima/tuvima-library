using Dapper;
using MediaEngine.Api.DevSupport;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The default layout keeps every managed source folder inside the library folder, next to the photo space and the
/// Library's own data folder, so a development reset must remove only the files it created.
/// </summary>
public sealed partial class DevHarnessResetServiceTests
{
    [Fact]
    public async Task Reset_OnTheDefaultLayout_DeletesOnlyLibraryMadeFiles_WhereverTheyWereFiled()
    {
        using var database = StartedDatabase();
        var layout = DefaultLayout();
        var options = Options.Create(new IngestionOptions { LibraryRoot = layout.LibraryRoot });

        // A fixture ingestion has filed (moved and renamed) inside its managed folder, with a subtitle file it wrote.
        var seededDune = Path.Combine(layout.Books, "Dune.epub");
        var filedDune = Write(Path.Combine(layout.Books, "Books", "Frank Herbert", "Dune (1965)", "Dune (1965).epub"));
        var duneSubtitles = Write(Path.ChangeExtension(filedDune, ".en.vtt"));
        TrackAsset(database, seededDune, filedDune, generatedTrack: duneSubtitles, userTrack: null);

        // A fixture not ingested yet, still where it was seeded.
        var seededArrival = Write(Path.Combine(layout.Books, "Arrival.epub"));

        // A listed fixture that now sits in an as-is folder, which the Library never changes.
        var asIsFixture = Write(Path.Combine(layout.Shelf, "Old Fixture.epub"));

        DevFixtureManifestStore.Record(options, [seededDune, seededArrival, asIsFixture], NullLogger.Instance);

        // Files the Library did not create.
        var personsBook = Write(Path.Combine(layout.Books, "My Book.epub"));
        var photo = Write(Path.Combine(layout.View, "Profiles", "owner", "beach.jpg"));
        var waitingImport = Write(Path.Combine(layout.Import, "Books", "Waiting.epub"));
        var artwork = Write(Path.Combine(layout.LibraryRoot, ".data", "assets", "artwork", "cover.jpg"));

        var watcher = new WatcherSpy();
        var service = NewDefaultLayoutService(database, options, watcher);

        var result = await service.ResetLibraryDataAsync(resumeWatcher: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(File.Exists(filedDune));
        Assert.False(File.Exists(duneSubtitles));
        Assert.False(File.Exists(seededArrival));
        Assert.False(Directory.Exists(Path.Combine(layout.Books, "Books")));
        Assert.True(Directory.Exists(layout.Books));
        Assert.False(File.Exists(artwork));

        Assert.True(File.Exists(personsBook));
        Assert.True(File.Exists(photo));
        Assert.True(File.Exists(waitingImport));
        Assert.True(File.Exists(asIsFixture));

        Assert.Contains(result.Details, line => line.StartsWith("Seed fixtures: 2 test file(s) deleted (1 after being filed), 1 Library-made", StringComparison.Ordinal));
        Assert.Contains(result.Details, line => line.StartsWith("Kept files: ", StringComparison.Ordinal) && line.Contains(personsBook, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Details, line => line.StartsWith("Kept files: ", StringComparison.Ordinal) && line.Contains(photo, StringComparison.OrdinalIgnoreCase));
        Assert.Equal([Path.GetFullPath(asIsFixture)], DevFixtureManifestStore.Read(options, NullLogger.Instance));
        Assert.Equal(1, watcher.Resumes);
    }

    [Fact]
    public async Task Reset_KeepsSubtitlesSomeoneSupplied_EvenBesideAFiledFixture()
    {
        using var database = StartedDatabase();
        var layout = DefaultLayout();
        var options = Options.Create(new IngestionOptions { LibraryRoot = layout.LibraryRoot });
        var seeded = Path.Combine(layout.Books, "Dune.epub");
        var filed = Write(Path.Combine(layout.Books, "Books", "Dune (1965).epub"));
        var supplied = Write(Path.ChangeExtension(filed, ".fr.vtt"));
        TrackAsset(database, seeded, filed, generatedTrack: null, userTrack: supplied);
        DevFixtureManifestStore.Record(options, [seeded], NullLogger.Instance);

        await NewDefaultLayoutService(database, options, new WatcherSpy())
            .ResetLibraryDataAsync(resumeWatcher: true)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(File.Exists(filed));
        Assert.True(File.Exists(supplied));
    }

    [Fact]
    public async Task FullWipe_WhenASourceFolderHoldsTheLibrarysOwnDataFolder_IsRefusedBeforeAnythingIsDeleted()
    {
        using var database = StartedDatabase();
        var layout = DefaultLayout(sourceIsLibraryRoot: true);
        var options = Options.Create(new IngestionOptions { LibraryRoot = layout.LibraryRoot });
        var media = Write(Path.Combine(layout.LibraryRoot, "Movies", "Dune (2021).mkv"));
        var watcher = new WatcherSpy();

        var error = await Assert.ThrowsAsync<DevHarnessResetBlockedException>(
            () => NewDefaultLayoutService(database, options, watcher)
                .WipeAsync(DevHarnessWipeScope.Full, resumeWatcher: false)
                .WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains("before anything was deleted", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(media));
        Assert.Equal(1, watcher.Resumes);
    }

    [Fact]
    public async Task Reset_WhenRealMediaProtectionIsOn_IsRefusedWithoutPausingAnything()
    {
        using var database = StartedDatabase();
        var layout = DefaultLayout();
        var options = Options.Create(new IngestionOptions { LibraryRoot = layout.LibraryRoot });
        var watcher = new WatcherSpy();
        var run = new RealMediaRun(
            layout.Books, layout.LibraryRoot, layout.LibraryRoot, _path, Guid.NewGuid(), "baseline", "hash", DateTimeOffset.UtcNow);
        var service = new DevHarnessResetService(
            database, options, _configuration, watcher, new EnrichmentPipelineExecutionGate(),
            NullLogger<DevHarnessResetService>.Instance, ShortTimeouts, realMediaRun: run);

        var error = await Assert.ThrowsAsync<DevHarnessResetBlockedException>(
            () => service.ResetLibraryDataAsync(resumeWatcher: true).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("Real-media protection is on", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, watcher.Pauses);
    }

    [Fact]
    public async Task Reset_WhenAFixtureWasSkippedAsACopyOfSomeonesFile_KeepsTheirFile()
    {
        using var database = StartedDatabase();
        var layout = DefaultLayout();
        var options = Options.Create(new IngestionOptions { LibraryRoot = layout.LibraryRoot });
        var seeded = Path.Combine(layout.Books, "Dune.epub");
        var someonesCopy = Write(Path.Combine(layout.Books, "My Dune.epub"));
        TrackAsset(database, seeded, someonesCopy, generatedTrack: null, userTrack: null, status: "duplicate");
        DevFixtureManifestStore.Record(options, [seeded], NullLogger.Instance);

        await NewDefaultLayoutService(database, options, new WatcherSpy())
            .ResetLibraryDataAsync(resumeWatcher: true)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(File.Exists(someonesCopy));
    }

    [Fact]
    public async Task Reset_AfterAReingest_StillDeletesFixturesFiledBeforeIt()
    {
        using var database = StartedDatabase();
        var layout = DefaultLayout();
        var options = Options.Create(new IngestionOptions { LibraryRoot = layout.LibraryRoot });
        var seeded = Path.Combine(layout.Books, "Dune.epub");
        var filed = Write(Path.Combine(layout.Books, "Books", "Dune (1965).epub"));
        TrackAsset(database, seeded, filed, generatedTrack: null, userTrack: null);
        DevFixtureManifestStore.Record(options, [seeded], NullLogger.Instance);
        var service = NewDefaultLayoutService(database, options, new WatcherSpy());

        // Reingest clears the data store but keeps the files, so the fixture list must now name the filed copy.
        await service.PrepareForReingestAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(filed));
        Assert.Equal([Path.GetFullPath(filed)], DevFixtureManifestStore.Read(options, NullLogger.Instance));

        await service.ResetLibraryDataAsync(resumeWatcher: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(File.Exists(filed));
    }

    [Fact]
    public async Task Reset_KeepsListedPathsOutsideEveryLibraryFolder_AndAsIsFilesAreNotReportedAsKept()
    {
        using var database = StartedDatabase();
        var layout = DefaultLayout();
        var options = Options.Create(new IngestionOptions { LibraryRoot = layout.LibraryRoot });
        var outside = Write(Path.Combine(_configRoot, "media", "Elsewhere", "notes.txt"));
        var climbsOut = Path.Combine(layout.Books, "..", "..", "Elsewhere", "climbs.txt");
        Write(climbsOut);
        var asIsFile = Write(Path.Combine(layout.Shelf, "Shelf Book.epub"));
        DevFixtureManifestStore.Record(options, [outside, climbsOut], NullLogger.Instance);

        var result = await NewDefaultLayoutService(database, options, new WatcherSpy())
            .ResetLibraryDataAsync(resumeWatcher: true)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(climbsOut));
        Assert.True(File.Exists(asIsFile));
        Assert.Contains(result.Details, line => line.EndsWith("2 kept because they are outside the folders the Library may change", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Details, line => line.Contains(asIsFile, StringComparison.OrdinalIgnoreCase));
    }

    private DevHarnessResetService NewDefaultLayoutService(
        DatabaseConnection database,
        IOptions<IngestionOptions> options,
        WatcherSpy watcher) =>
        new(database, options, _configuration, watcher, new EnrichmentPipelineExecutionGate(),
            NullLogger<DevHarnessResetService>.Instance, ShortTimeouts);

    private sealed record Layout(string LibraryRoot, string Books, string Shelf, string View, string Import);

    /// <summary>The shipped layout: managed Books, an as-is Shelf and View inside the library folder; Import beside it.</summary>
    private Layout DefaultLayout(bool sourceIsLibraryRoot = false)
    {
        var libraryRoot = Path.Combine(_configRoot, "media", "library");
        var layout = new Layout(
            libraryRoot,
            Path.Combine(libraryRoot, "Books"),
            Path.Combine(libraryRoot, "Shelf"),
            Path.Combine(libraryRoot, "View"),
            Path.Combine(_configRoot, "media", "Incoming"));
        foreach (var folder in new[] { layout.Books, layout.Shelf, layout.View, layout.Import })
        {
            Directory.CreateDirectory(folder);
        }

        _configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations =
            [
                new ServerStorageLocationConfig { Id = "media", Label = "Library data", Path = libraryRoot, AllowWrite = true },
                new ServerStorageLocationConfig { Id = ImportFolderRules.LocationId, Label = "Import", Path = layout.Import, AllowWrite = true },
            ],
            ViewStorage = new ViewStorageConfig { StorageLocationId = "media", RelativeRoot = "View" },
            // Source folders may not overlap, so a source that is the whole library folder stands alone.
            Libraries = sourceIsLibraryRoot
                ? [Library("Books", libraryRoot, LibrarySourceManagementModes.ManagedByTuvima)]
                :
                [
                    Library("Books", layout.Books, LibrarySourceManagementModes.ManagedByTuvima),
                    Library("Shelf", layout.Shelf, LibrarySourceManagementModes.ExistingLibrary),
                ],
        });

        return layout;
    }

    private static LibraryFolderConfig Library(string name, string path, string managementMode)
    {
        var sourceId = Guid.NewGuid().ToString("D");
        return new LibraryFolderConfig
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = name,
            Category = "Books",
            Kind = LibraryKinds.Catalogued,
            KindKey = "books",
            MediaTypes = ["Books"],
            Area = LibraryAreas.Read,
            Presentation = LibraryPresentations.Catalogue,
            MetadataPolicy = LibraryMetadataPolicies.Enriched,
            OrganizationPolicy = new LibraryOrganizationPolicyConfig(),
            PrimaryDestinationSourceId = managementMode == LibrarySourceManagementModes.ManagedByTuvima ? sourceId : null,
            Sources =
            [
                new LibrarySourceConfig
                {
                    Id = sourceId,
                    Path = path,
                    ManagementMode = managementMode,
                    SourceType = LibrarySourceTypes.LocalFolder,
                    AccessMode = LibrarySourceAccessModes.Writable,
                },
            ],
        };
    }

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture bytes");
        return path;
    }

    /// <summary>Records what ingestion leaves behind for a file found at <paramref name="detected"/> and filed to <paramref name="current"/>.</summary>
    private static void TrackAsset(DatabaseConnection database, string detected, string current, string? generatedTrack, string? userTrack, string status = "complete")
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@workId, 'Books');", new { workId });
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        connection.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @hash, @current, 'Normal');
            """, new { assetId, editionId, hash = Guid.NewGuid().ToString("N"), current });
        connection.Execute(
            "INSERT INTO ingestion_log (id, file_path, media_asset_id, status) VALUES (@id, @detected, @assetId, @status);",
            new { id = Guid.NewGuid(), detected, assetId, status });
        foreach (var (path, userOwned) in new[] { (generatedTrack, 0), (userTrack, 1) })
        {
            if (path is null)
            {
                continue;
            }

            connection.Execute("""
                INSERT INTO text_tracks (id, asset_id, kind, provider, source_format, normalized_format, local_path, sidecar_path, is_user_owned)
                VALUES (@id, @assetId, 'Subtitles', 'test', 'vtt', 'vtt', @path, @path, @userOwned);
                """, new { id = Guid.NewGuid(), assetId, path, userOwned });
        }
    }
}
