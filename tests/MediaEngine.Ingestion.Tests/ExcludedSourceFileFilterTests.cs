using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// A title deleted from an as-is folder leaves its file on disk and a row in the excluded files. Scans and the folder watcher ask this
/// filter whether to leave a file alone: an unchanged file is skipped without being read, a touched copy stays skipped, and a different
/// file at the same place is ingested. Real temp folders and a real SQLite data store.
/// </summary>
public sealed class ExcludedSourceFileFilterTests : IDisposable
{
    private const string LibraryId = "bbbbbbbb-0000-4000-8000-000000000001";
    private const string SourceId = "books-asis";

    private readonly ImportFolderHarness _h = new();
    private readonly ExcludedSourceFileRepository _excluded;
    private readonly CountingHasher _hasher = new();
    private readonly ExcludedSourceFileFilter _filter;

    public ExcludedSourceFileFilterTests()
    {
        _excluded = new ExcludedSourceFileRepository(_h.Db.Connection);
        var options = new IngestionOptions
        {
            LibraryFolders =
            [
                LibraryFolderEntryFactory.Create(
                    ImportFolderHarness.NewLibrary(
                        LibraryId, "Books", "Books", destinationId: null, ImportFolderHarness.AsIs(SourceId, _h.AsIsDir))),
            ],
        };
        _filter = new ExcludedSourceFileFilter(
            new LibraryFolderResolver(new ReloadableOptionsMonitor<IngestionOptions>(options)),
            _excluded,
            _hasher,
            NullLogger<ExcludedSourceFileFilter>.Instance);
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task An_excluded_file_that_has_not_changed_is_skipped_without_being_read()
    {
        var path = WriteFile("Dune.epub", "dune book");
        await ExcludeAsync(path);

        Assert.True(await _filter.ShouldSkipAsync(path));
        Assert.True(await _filter.ShouldSkipAsync(path));

        Assert.Equal(0, _hasher.Count);
    }

    [Fact]
    public async Task A_file_that_is_not_excluded_is_not_skipped()
    {
        var excludedPath = WriteFile("Dune.epub", "dune book");
        var otherPath = WriteFile("Emma.epub", "emma book");
        await ExcludeAsync(excludedPath);

        Assert.False(await _filter.ShouldSkipAsync(otherPath));
    }

    [Fact]
    public async Task A_file_outside_every_library_folder_is_not_skipped()
    {
        await ExcludeAsync(WriteFile("Dune.epub", "dune book"));
        var outside = Path.Combine(_h.Root, "elsewhere", "Dune.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "dune book");

        Assert.False(await _filter.ShouldSkipAsync(outside));
    }

    [Fact]
    public async Task A_different_file_at_the_excluded_place_is_ingested_and_the_exclusion_is_lifted()
    {
        var path = WriteFile("Dune.epub", "dune book");
        await ExcludeAsync(path);
        File.WriteAllText(path, "a completely different book, longer than before");

        Assert.False(await _filter.ShouldSkipAsync(path));

        Assert.Equal(1, _hasher.Count);
        Assert.Null(await _excluded.GetAsync(SourceId, "Dune.epub"));
        Assert.False(await _filter.ShouldSkipAsync(path));
    }

    [Fact]
    public async Task A_different_file_of_the_same_size_at_the_excluded_place_is_still_noticed_by_its_content()
    {
        var path = WriteFile("Dune.epub", "dune book");
        await ExcludeAsync(path);
        File.WriteAllText(path, "dune BOOK");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        Assert.False(await _filter.ShouldSkipAsync(path));
        Assert.Null(await _excluded.GetAsync(SourceId, "Dune.epub"));
    }

    [Fact]
    public async Task The_same_content_with_a_touched_time_stays_skipped_and_the_row_is_refreshed()
    {
        var path = WriteFile("Dune.epub", "dune book");
        await ExcludeAsync(path);
        var touched = DateTime.UtcNow.AddHours(-3);
        File.SetLastWriteTimeUtc(path, touched);

        Assert.True(await _filter.ShouldSkipAsync(path));

        var row = await _excluded.GetAsync(SourceId, "Dune.epub");
        Assert.NotNull(row);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), row.LastWriteUtc);
        Assert.Equal(1, _hasher.Count);

        // The refreshed facts mean the next scan does not read the file again.
        Assert.True(await _filter.ShouldSkipAsync(path));
        Assert.Equal(1, _hasher.Count);
    }

    [Fact]
    public async Task A_file_that_is_gone_is_not_skipped_and_its_row_is_left_for_Settings()
    {
        var path = WriteFile("Dune.epub", "dune book");
        await ExcludeAsync(path);
        File.Delete(path);

        Assert.False(await _filter.ShouldSkipAsync(path));
        Assert.NotNull(await _excluded.GetAsync(SourceId, "Dune.epub"));
    }

    [Fact]
    public async Task An_exclusion_added_after_the_filter_loaded_is_picked_up_once_it_is_invalidated()
    {
        var first = WriteFile("Dune.epub", "dune book");
        var second = WriteFile("Emma.epub", "emma book");
        await ExcludeAsync(first);
        Assert.False(await _filter.ShouldSkipAsync(second));

        await ExcludeAsync(second);
        Assert.False(await _filter.ShouldSkipAsync(second));

        _filter.Invalidate();
        Assert.True(await _filter.ShouldSkipAsync(second));
    }

    [Fact]
    public async Task An_excluded_file_in_a_sub_folder_is_matched_by_its_place_inside_the_source_folder()
    {
        var path = WriteFile(Path.Combine("Frank Herbert", "Dune.epub"), "dune book");
        await ExcludeAsync(path);

        Assert.True(await _filter.ShouldSkipAsync(path));
        Assert.False(await _filter.ShouldSkipAsync(WriteFile(Path.Combine("Someone Else", "Dune.epub"), "dune book")));
    }

    private string WriteFile(string relative, string content)
    {
        var path = Path.Combine(_h.AsIsDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task ExcludeAsync(string path)
    {
        var info = new FileInfo(path);
        var hash = (await new TestAssetHasher().ComputeAsync(path)).Hex;
        await _excluded.AddOrReplaceAsync(new ExcludedSourceFile(
            Guid.NewGuid(),
            LibraryId,
            SourceId,
            ExcludedSourceFilePaths.RelativePathOf(_h.AsIsDir, path)!,
            hash,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            Path.GetFileNameWithoutExtension(path),
            DateTimeOffset.UtcNow,
            null,
            "Test",
            ExcludedSourceFileReasons.AsIs));
    }

    /// <summary>Hashes like the real thing and counts how many files it was asked to read.</summary>
    private sealed class CountingHasher : IAssetHasher
    {
        private readonly TestAssetHasher _inner = new();
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task<HashResult> ComputeAsync(string filePath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            return _inner.ComputeAsync(filePath, ct);
        }
    }
}
