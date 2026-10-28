using System.Text;
using System.Text.Json;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Add files (Libraries spec D8, D26) against real folders and the real configuration loader: the copy never touches the originals, a selection
/// that does not fit refuses before any byte moves, exact duplicates follow the resolved rule, and a path outside what the Engine may read
/// refuses the whole request.
/// </summary>
public sealed class LibraryAddFilesServiceTests : IDisposable
{
    private const string LibraryId = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string DestinationId = "aaaaaaaa-1111-4111-8111-000000000001";
    private const string ExistingId = "aaaaaaaa-1111-4111-8111-000000000002";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-add-files-{Guid.NewGuid():N}");
    private readonly string _shared;
    private readonly string _incoming;
    private readonly string _destination;
    private readonly string _existing;
    private readonly string _configDir;
    private ConfigurationDirectoryLoader _config = null!;

    public LibraryAddFilesServiceTests()
    {
        _shared = Path.Combine(_root, "shared");
        _incoming = Directory.CreateDirectory(Path.Combine(_shared, "incoming")).FullName;
        _destination = Directory.CreateDirectory(Path.Combine(_shared, "library", "Movies")).FullName;
        _existing = Directory.CreateDirectory(Path.Combine(_shared, "library", "Old")).FullName;
        _configDir = Directory.CreateDirectory(Path.Combine(_root, "config")).FullName;
        WriteLibraries(withDestination: true, duplicatePolicy: LibraryDuplicatePolicies.KeepOne);
    }

    public void Dispose()
    {
        _config.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    [Fact]
    public async Task Copying_PutsTheFilesInTheImportDestination_AndLeavesTheOriginalsUntouched()
    {
        var film = Write(_incoming, "Dune.mkv", "dune-bytes");
        var other = Write(_incoming, "Arrival.mkv", "arrival-bytes");
        var before = File.GetLastWriteTimeUtc(film);

        var outcome = await Service().AddAsync(LibraryId, [film, other], [], CancellationToken.None);

        Assert.Null(outcome.RefusalCode);
        Assert.Equal(2, outcome.Result!.Copied);
        Assert.Equal("dune-bytes", File.ReadAllText(Path.Combine(_destination, "Dune.mkv")));
        Assert.Equal("arrival-bytes", File.ReadAllText(Path.Combine(_destination, "Arrival.mkv")));
        // The originals are only read: still there, same content and same time stamp. No working file is left behind either.
        Assert.Equal("dune-bytes", File.ReadAllText(film));
        Assert.Equal("arrival-bytes", File.ReadAllText(other));
        Assert.Equal(before, File.GetLastWriteTimeUtc(film));
        Assert.DoesNotContain(Directory.EnumerateFiles(_destination), path => path.EndsWith(".part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASelectionThatDoesNotFit_RefusesWithAPlainSentence_BeforeAnythingIsCopied()
    {
        var film = Write(_incoming, "Dune.mkv", new string('x', 4096));
        var service = Service(freeBytes: _ => 100);

        var outcome = await service.AddAsync(LibraryId, [film], [], CancellationToken.None);

        Assert.Null(outcome.Result);
        Assert.Equal(LibraryAddFilesRefusalCodes.NotEnoughSpace, outcome.RefusalCode);
        Assert.Equal(StatusCodes.Status507InsufficientStorage, outcome.RefusalStatus);
        Assert.StartsWith("Not enough space", outcome.RefusalMessage, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_destination));
    }

    [Fact]
    public async Task AFileAlreadyInTheLibrary_IsSkippedAsADuplicate_WhenTheRuleIsKeepOneCopy()
    {
        Write(_existing, "Dune (old).mkv", "same-content");
        var duplicate = Write(_incoming, "Dune.mkv", "same-content");
        var fresh = Write(_incoming, "Arrival.mkv", "different-content");

        var outcome = await Service().AddAsync(LibraryId, [duplicate, fresh], [], CancellationToken.None);

        Assert.Equal(1, outcome.Result!.Copied);
        Assert.Equal(1, outcome.Result.SkippedDuplicates);
        Assert.False(File.Exists(Path.Combine(_destination, "Dune.mkv")));
        Assert.True(File.Exists(Path.Combine(_destination, "Arrival.mkv")));
        Assert.True(File.Exists(duplicate));
    }

    [Fact]
    public async Task AFileAlreadyInTheLibrary_IsCopiedUnderANumberedName_WhenTheRuleIsKeepBoth()
    {
        WriteLibraries(withDestination: true, duplicatePolicy: LibraryDuplicatePolicies.KeepBoth);
        Write(_destination, "Dune.mkv", "same-content");
        var duplicate = Write(_incoming, "Dune.mkv", "same-content");

        var outcome = await Service().AddAsync(LibraryId, [duplicate], [], CancellationToken.None);

        Assert.Equal(1, outcome.Result!.Copied);
        Assert.Equal(0, outcome.Result.SkippedDuplicates);
        Assert.True(File.Exists(Path.Combine(_destination, "Dune.mkv")));
        Assert.True(File.Exists(Path.Combine(_destination, "Dune (1).mkv")));
    }

    [Fact]
    public async Task FilesTheLibraryCannotTake_AreCountedAsNotSupported_AndNotCopied()
    {
        var notes = Write(_incoming, "notes.txt", "hello");
        var film = Write(_incoming, "Dune.mkv", "dune");

        var outcome = await Service().AddAsync(LibraryId, [notes, film], [], CancellationToken.None);

        Assert.Equal(1, outcome.Result!.Copied);
        Assert.Equal(1, outcome.Result.Unsupported);
        Assert.False(File.Exists(Path.Combine(_destination, "notes.txt")));
    }

    [Theory]
    [InlineData("relative/Dune.mkv")]
    [InlineData("..\\Dune.mkv")]
    [InlineData("")]
    public async Task APathThatIsNotAFullPathOnTheServer_RefusesTheWholeRequest_AndCopiesNothing(string path)
    {
        var fine = Write(_incoming, "Arrival.mkv", "arrival");

        var outcome = await Service().AddAsync(LibraryId, [fine, path], [], CancellationToken.None);

        Assert.Equal(LibraryAddFilesRefusalCodes.PathNotApproved, outcome.RefusalCode);
        Assert.Equal(StatusCodes.Status400BadRequest, outcome.RefusalStatus);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_destination));
    }

    [Fact]
    public async Task AFileInsideTheEnginesOwnDataFolder_IsRefused_AndCopiesNothing()
    {
        var data = Directory.CreateDirectory(Path.Combine(_shared, "library", "tuvima-data")).FullName;
        var core = _config.LoadCore();
        core.DataRoot = data;
        _config.SaveCore(core);
        var own = Write(data, "Secret.mkv", "private");

        var outcome = await Service().AddAsync(LibraryId, [own], [], CancellationToken.None);

        Assert.Equal(LibraryAddFilesRefusalCodes.PathNotApproved, outcome.RefusalCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_destination));
    }

    [Fact]
    public async Task AFileThatIsNoLongerThere_RefusesTheRequest()
    {
        var gone = Path.Combine(_incoming, "Gone.mkv");

        var outcome = await Service().AddAsync(LibraryId, [gone], [], CancellationToken.None);

        Assert.Equal(LibraryAddFilesRefusalCodes.PathNotApproved, outcome.RefusalCode);
    }

    [Fact]
    public async Task ALibraryWithNoImportDestination_RefusesAndCopiesNothing()
    {
        WriteLibraries(withDestination: false, duplicatePolicy: LibraryDuplicatePolicies.KeepOne);
        var film = Write(_incoming, "Dune.mkv", "dune");

        var outcome = await Service().AddAsync(LibraryId, [film], [], CancellationToken.None);

        Assert.Equal(LibraryAddFilesRefusalCodes.NoImportDestination, outcome.RefusalCode);
        Assert.Equal(StatusCodes.Status409Conflict, outcome.RefusalStatus);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_destination));
    }

    [Fact]
    public async Task NothingSelected_IsRefusedWithAPlainSentence()
    {
        var outcome = await Service().AddAsync(LibraryId, [], [], CancellationToken.None);

        Assert.Equal(LibraryAddFilesRefusalCodes.NothingSelected, outcome.RefusalCode);
    }

    [Fact]
    public void Browsing_WithFiles_MarksFilesTheLibraryKindCannotTakeAsNotSupported_AndHidesDotFiles()
    {
        Write(_incoming, "Dune.mkv", "dune");
        Write(_incoming, "notes.txt", "hello");
        Write(_incoming, ".hidden.mkv", "secret");
        var browser = Browser();

        var result = browser.Browse(new MediaEngine.Contracts.Settings.BrowseServerFoldersRequest
        {
            StorageLocationId = "shared",
            RelativePath = "incoming",
            IncludeFiles = true,
            KindKey = "movies",
        });

        Assert.Equal(["Dune.mkv", "notes.txt"], result.Files.Select(file => file.Name).Order(StringComparer.Ordinal));
        Assert.True(result.Files.Single(file => file.Name == "Dune.mkv").Supported);
        Assert.False(result.Files.Single(file => file.Name == "notes.txt").Supported);
    }

    private ServerFolderBrowserService Browser() => new(_config, NullLogger<ServerFolderBrowserService>.Instance);

    private LibraryAddFilesService Service(Func<string, long?>? freeBytes = null) => new(
        _config,
        Browser(),
        new MediaTypeExtensionCatalog(_config),
        NullLogger<LibraryAddFilesService>.Instance,
        kindSource: null,
        freeBytesAt: freeBytes ?? (_ => long.MaxValue / 2));

    private static string Write(string folder, string name, string content)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private void WriteLibraries(bool withDestination, string duplicatePolicy)
    {
        var movies = new LibraryFolderConfig
        {
            Id = LibraryId,
            Name = "Movies",
            Category = "Movies",
            Kind = LibraryKinds.Catalogued,
            KindKey = "movies",
            Area = LibraryAreas.Watch,
            MediaTypes = ["Movies"],
            Sources =
            [
                new LibrarySourceConfig
                {
                    Id = DestinationId,
                    Path = _destination,
                    ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                    AccessMode = LibrarySourceAccessModes.Writable,
                },
                new LibrarySourceConfig
                {
                    Id = ExistingId,
                    Path = _existing,
                    ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
                    AccessMode = LibrarySourceAccessModes.ReadOnly,
                },
            ],
            PrimaryDestinationSourceId = withDestination ? DestinationId : null,
        };
        var libraries = new LibrariesConfiguration
        {
            Libraries = [movies],
            StorageLocations =
            [
                new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = Path.Combine(_root, "own-media"), AllowWrite = true },
                new ServerStorageLocationConfig { Id = "shared", Label = "Shared", Path = _shared, AllowWrite = true },
            ],
            FileHandling = new FileHandlingConfig { DuplicatePolicy = duplicatePolicy },
        };
        File.WriteAllText(Path.Combine(_configDir, "libraries.json"), JsonSerializer.Serialize(libraries));
        // A fresh loader so the rewritten file is what the service reads.
        _config?.Dispose();
        _config = new ConfigurationDirectoryLoader(_configDir);
    }
}
