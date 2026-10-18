using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;

namespace MediaEngine.Storage.Tests;

/// <summary>Highlights made by the book reader are anchored by an EPUB CFI range and stay private to their profile.</summary>
public sealed class ReaderHighlightRepositoryTests : IDisposable
{
    private const string Range = "epubcfi(/6/6!/4/2/4,/1:0,/1:24)";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ReaderHighlightRepository _highlights;
    private readonly ReaderBookmarkRepository _bookmarks;
    private readonly Guid _assetId;

    public ReaderHighlightRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_reader_highlights_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _highlights = new ReaderHighlightRepository(_db);
        _bookmarks = new ReaderBookmarkRepository(_db);
        _assetId = CreateAsset();
    }

    [Fact]
    public async Task CfiRange_RoundTripsAndOffsetOnlyRowsStayReadable()
    {
        var profile = Guid.NewGuid().ToString("D");
        var anchored = NewHighlight(profile, Range);
        var legacy = NewHighlight(profile, null);
        legacy.StartOffset = 10;
        legacy.EndOffset = 20;
        await _highlights.InsertAsync(anchored);
        await _highlights.InsertAsync(legacy);

        var listed = await _highlights.ListByAssetAsync(profile, _assetId);

        Assert.Equal(2, listed.Count);
        Assert.Equal(Range, listed.Single(h => h.Id == anchored.Id).CfiRange);
        Assert.Null(listed.Single(h => h.Id == legacy.Id).CfiRange);
        Assert.Equal(Range, (await _highlights.FindByIdAsync(anchored.Id))!.CfiRange);
    }

    [Fact]
    public async Task Highlights_AreListedOnlyForTheirOwnProfile()
    {
        var mine = Guid.NewGuid().ToString("D");
        var theirs = Guid.NewGuid().ToString("D");
        await _highlights.InsertAsync(NewHighlight(mine, Range));
        await _highlights.InsertAsync(NewHighlight(theirs, Range));

        Assert.Single(await _highlights.ListByAssetAsync(mine, _assetId));
        Assert.Single(await _highlights.ListByAssetAsync(theirs, _assetId));
        Assert.Empty(await _highlights.ListByAssetAsync(Guid.NewGuid().ToString("D"), _assetId));
    }

    [Fact]
    public async Task UpdateAndDelete_ChangeColourAndNoteAndLeaveThePositionAlone()
    {
        var profile = Guid.NewGuid().ToString("D");
        var highlight = NewHighlight(profile, Range);
        await _highlights.InsertAsync(highlight);

        await _highlights.UpdateAsync(highlight.Id, HighlightColor.Pink, "Remember this");
        var updated = (await _highlights.FindByIdAsync(highlight.Id))!;
        Assert.Equal(HighlightColor.Pink, updated.Color);
        Assert.Equal("Remember this", updated.NoteText);
        Assert.Equal(Range, updated.CfiRange);

        // A missing colour keeps the colour; a missing note clears the note.
        await _highlights.UpdateAsync(highlight.Id, null, null);
        var cleared = (await _highlights.FindByIdAsync(highlight.Id))!;
        Assert.Equal(HighlightColor.Pink, cleared.Color);
        Assert.Null(cleared.NoteText);

        await _highlights.DeleteAsync(highlight.Id);
        Assert.Null(await _highlights.FindByIdAsync(highlight.Id));
    }

    [Fact]
    public async Task Bookmarks_KeepTheirCfiPositionAndStayPrivate()
    {
        var mine = Guid.NewGuid().ToString("D");
        await _bookmarks.InsertAsync(new ReaderBookmark
        {
            Id = Guid.NewGuid(),
            UserId = mine,
            AssetId = _assetId,
            ChapterIndex = 0,
            CfiPosition = "epubcfi(/6/6!/4/2/4/1:0)",
            Label = "Chapter two · 12%",
            CreatedAt = DateTime.UtcNow,
        });

        var listed = await _bookmarks.ListByAssetAsync(mine, _assetId);
        Assert.Equal("epubcfi(/6/6!/4/2/4/1:0)", Assert.Single(listed).CfiPosition);
        Assert.Empty(await _bookmarks.ListByAssetAsync(Guid.NewGuid().ToString("D"), _assetId));
    }

    private ReaderHighlight NewHighlight(string userId, string? cfiRange) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AssetId = _assetId,
        ChapterIndex = 0,
        StartOffset = 0,
        EndOffset = 0,
        CfiRange = cfiRange,
        SelectedText = "The first words",
        Color = HighlightColor.Yellow,
        CreatedAt = DateTime.UtcNow,
    };

    private Guid CreateAsset()
    {
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO collections (id, created_at) VALUES (@collectionId, @createdAt);", new { collectionId, createdAt = DateTimeOffset.UtcNow.ToString("O") });
        conn.Execute("""
            INSERT INTO works (id, collection_id, media_type, work_kind, is_catalog_only)
            VALUES (@workId, @collectionId, 'Books', 'standalone', 0);
            """, new { workId, collectionId });
        conn.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        conn.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @contentHash, '/library/book.epub', 'Normal');
            """, new { assetId, editionId, contentHash = $"hash_{assetId:N}" });
        return assetId;
    }

    public void Dispose()
    {
        _db.Dispose();
        TryDelete(_dbPath);
        TryDelete($"{_dbPath}-wal");
        TryDelete($"{_dbPath}-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A connection pool may still hold the temp file for a moment; the OS temp folder cleans it up.
        }
    }
}
