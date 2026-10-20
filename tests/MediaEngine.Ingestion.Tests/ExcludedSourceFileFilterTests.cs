using MediaEngine.Domain.Contracts;
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
    private readonly FlakyRepository _flaky;
    private readonly ExcludedSourceFileFilter _filter;

    public ExcludedSourceFileFilterTests()
    {
        _excluded = new ExcludedSourceFileRepository(_h.Db.Connection);
        _flaky = new FlakyRepository(_excluded);
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
            _flaky,
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

    [Fact]
    public async Task When_the_refreshed_facts_cannot_be_saved_a_touched_copy_of_the_excluded_file_stays_skipped()
    {
        var path = WriteFile("Dune.epub", "dune book");
        await ExcludeAsync(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-3));
        _flaky.FailWrites = true;

        Assert.True(await _filter.ShouldSkipAsync(path));
    }

    [Fact]
    public async Task Lifting_one_exclusion_leaves_the_others_in_place_without_reloading_them()
    {
        var gone = WriteFile("Dune.epub", "dune book");
        var kept = WriteFile("Emma.epub", "emma book");
        await ExcludeAsync(gone);
        await ExcludeAsync(kept);
        Assert.True(await _filter.ShouldSkipAsync(kept));
        var loadsBefore = _flaky.ListAllCalls;
        File.WriteAllText(gone, "a completely different book, longer than before");

        Assert.False(await _filter.ShouldSkipAsync(gone));

        Assert.True(await _filter.ShouldSkipAsync(kept));
        Assert.Equal(loadsBefore, _flaky.ListAllCalls);
    }

    [Fact]
    public async Task With_nothing_excluded_a_file_is_not_skipped_and_its_source_folder_is_not_looked_up()
    {
        var path = WriteFile("Dune.epub", "dune book");

        Assert.False(await _filter.ShouldSkipAsync(path));
        Assert.Equal(0, _hasher.Count);
    }

    [Fact]
    public async Task When_the_check_itself_fails_the_scan_skips_the_file_for_this_pass()
    {
        var path = WriteFile("Dune.epub", "dune book");
        var failing = new ThrowingFilter();
        var method = typeof(IngestionEngine).GetMethod("IsExcludedScanFileAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var skipped = await (Task<bool>)method.Invoke(null, [failing, NullLogger.Instance, path, CancellationToken.None])!;

        Assert.True(skipped);
    }

    [Fact]
    public async Task Without_a_filter_the_scan_does_not_skip_anything()
    {
        var method = typeof(IngestionEngine).GetMethod("IsExcludedScanFileAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var skipped = await (Task<bool>)method.Invoke(null, [null, NullLogger.Instance, "x.epub", CancellationToken.None])!;

        Assert.False(skipped);
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

    /// <summary>Wraps the real repository and can make writes fail or count how often the whole list is read.</summary>
    private sealed class FlakyRepository(IExcludedSourceFileRepository inner) : IExcludedSourceFileRepository
    {
        private int _listAllCalls;

        public bool FailWrites { get; set; }

        public int ListAllCalls => Volatile.Read(ref _listAllCalls);

        public Task AddOrReplaceAsync(ExcludedSourceFile file, CancellationToken ct = default) =>
            FailWrites ? throw new IOException("The data store is busy") : inner.AddOrReplaceAsync(file, ct);

        public Task<IReadOnlyList<ExcludedSourceFile>> ListByLibraryAsync(string libraryId, CancellationToken ct = default) => inner.ListByLibraryAsync(libraryId, ct);

        public Task<IReadOnlyList<ExcludedSourceFile>> ListAllAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _listAllCalls);
            return inner.ListAllAsync(ct);
        }

        public Task<ExcludedSourceFile?> GetAsync(string sourceId, string relativePath, CancellationToken ct = default) => inner.GetAsync(sourceId, relativePath, ct);

        public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default) => inner.RemoveAsync(id, ct);
    }

    private sealed class ThrowingFilter : IExcludedSourceFileFilter
    {
        public void Invalidate() { }

        public Task<bool> ShouldSkipAsync(string fullPath, CancellationToken ct = default) =>
            throw new InvalidOperationException("The excluded files could not be read");
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
