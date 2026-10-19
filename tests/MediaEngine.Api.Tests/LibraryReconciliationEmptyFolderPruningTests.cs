using MediaEngine.Api.Services;
using MediaEngine.Domain.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class LibraryReconciliationEmptyFolderPruningTests : IDisposable
{
    private static readonly string[] FreshLibraryFolders = ["Audiobooks", "Books", "Comics", "Movies", "Music", "TV"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-prune-{Guid.NewGuid():N}");

    public LibraryReconciliationEmptyFolderPruningTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void PruneEmptyFolders_NeverRemovesEmptyFoldersInsideTheDataFolder()
    {
        var hlsCache = Directory.CreateDirectory(Path.Combine(_root, ".data", "variants", "hls")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(_root, ".data", "staging")).FullName;

        LibraryReconciliationService.PruneEmptyFolders(_root, LibraryRoots(), NullLogger.Instance, CancellationToken.None);

        Assert.True(Directory.Exists(hlsCache));
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public void PruneEmptyFolders_RemovesEmptyMediaFolders_ButKeepsCategoryRootsAndFoldersWithFiles()
    {
        var emptySeries = Directory.CreateDirectory(Path.Combine(_root, "Books", "Dune", "Empty Edition")).FullName;
        var kept = Directory.CreateDirectory(Path.Combine(_root, "Books", "Dune", "Dune (1965)")).FullName;
        File.WriteAllText(Path.Combine(kept, "dune.epub"), "book");
        var emptyCategory = Directory.CreateDirectory(Path.Combine(_root, "Comics")).FullName;

        var cleaned = LibraryReconciliationService.PruneEmptyFolders(
            _root, LibraryRoots(), NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, cleaned);
        Assert.False(Directory.Exists(emptySeries));
        Assert.True(Directory.Exists(kept));
        Assert.True(Directory.Exists(emptyCategory));
    }

    [Fact]
    public void PruneEmptyFolders_KeepsEveryEmptyConfiguredLibraryFolder_AndRemovesUnconfiguredOnes()
    {
        foreach (var folder in FreshLibraryFolders)
        {
            Directory.CreateDirectory(Path.Combine(_root, folder));
        }
        var leftover = Directory.CreateDirectory(Path.Combine(_root, "Videos")).FullName;

        var cleaned = LibraryReconciliationService.PruneEmptyFolders(
            _root, LibraryRoots(), NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, cleaned);
        Assert.False(Directory.Exists(leftover));
        Assert.All(FreshLibraryFolders, folder => Assert.True(Directory.Exists(Path.Combine(_root, folder)), folder));
    }

    // Mirrors config/libraries.json: one library per folder, each with a source under the library root.
    private IReadOnlySet<string> LibraryRoots() =>
        LibraryReconciliationService.ConfiguredLibraryRoots(new LibrariesConfiguration
        {
            Libraries = FreshLibraryFolders
                .Select(folder => new LibraryFolderConfig
                {
                    Name = folder,
                    Sources = [new LibrarySourceConfig { Path = Path.Combine(_root, folder) + Path.DirectorySeparatorChar }],
                })
                .ToList(),
        });

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
