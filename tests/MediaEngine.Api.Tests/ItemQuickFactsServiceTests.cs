using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>"Read-only library" in the editor's Quick Facts: the item's files live in a library folder marked read-only.</summary>
public sealed class ItemQuickFactsServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_quick_facts_service_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public ItemQuickFactsServiceTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A temp file another process still holds is cleaned up by the system temp sweep.
        }
    }

    private Guid Film(params string[] paths)
    {
        var workId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, 'Movies', 'standalone', 'Owned');", new { workId });
        foreach (var path in paths)
        {
            var editionId = Guid.NewGuid();
            connection.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
            connection.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@id, @editionId, @hash, @path);",
                new { id = Guid.NewGuid(), editionId, hash = Guid.NewGuid().ToString("N"), path });
        }

        return workId;
    }

    private ItemQuickFactsService Service(params (string Folder, string Access)[] sources) =>
        new(new ItemQuickFactsRepository(_database), new FolderResolverSpy(sources));

    [Fact]
    public async Task AFilmInAReadOnlyFolder_IsFlaggedReadOnly()
    {
        var film = Film("/media/archive/Dune.mkv");

        var facts = await Service(("/media/archive", LibrarySourceAccessModes.ReadOnly)).GetAsync(film);

        Assert.True(facts!.ReadOnlyLibrary);
    }

    [Fact]
    public async Task AFilmInAWritableFolder_IsNotFlagged()
    {
        var film = Film("/media/library/Dune.mkv");

        var facts = await Service(("/media/library", LibrarySourceAccessModes.Writable)).GetAsync(film);

        Assert.False(facts!.ReadOnlyLibrary);
    }

    [Fact]
    public async Task OneFileInAReadOnlyFolder_IsEnoughToFlagTheItem()
    {
        var film = Film("/media/library/Dune.mkv", "/media/archive/Dune.4k.mkv");

        var facts = await Service(("/media/library", LibrarySourceAccessModes.Writable), ("/media/archive", LibrarySourceAccessModes.ReadOnly)).GetAsync(film);

        Assert.True(facts!.ReadOnlyLibrary);
    }

    [Fact]
    public async Task AFileOutsideEveryConfiguredFolder_IsNotFlagged()
    {
        var film = Film("/elsewhere/Dune.mkv");

        var facts = await Service(("/media/archive", LibrarySourceAccessModes.ReadOnly)).GetAsync(film);

        Assert.False(facts!.ReadOnlyLibrary);
    }

    [Fact]
    public async Task AnUnknownId_HasNoQuickFacts()
    {
        Assert.Null(await Service().GetAsync(Guid.NewGuid()));
    }

    private sealed class FolderResolverSpy((string Folder, string Access)[] sources) : ILibraryFolderResolver
    {
        public LibraryFolderEntry? ResolveById(string libraryId) => null;

        public LibraryFolderEntry? ResolveForPath(string absolutePath) => ResolveSourceForPath(absolutePath)?.Library;

        public string? ResolveSourcePath(string absolutePath) => ResolveSourceForPath(absolutePath)?.Source.Path;

        public ResolvedLibrarySource? ResolveSourceForPath(string absolutePath)
        {
            foreach (var (folder, access) in sources)
            {
                if (absolutePath.StartsWith(folder + "/", StringComparison.Ordinal))
                {
                    return new ResolvedLibrarySource(
                        new LibraryFolderEntry { Id = "movies" },
                        new LibrarySourceEntry { Id = "source", Path = folder, AccessMode = access });
                }
            }

            return null;
        }
    }
}
