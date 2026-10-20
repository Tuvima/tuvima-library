using Dapper;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Items;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Match step's Engine side (details overlay spec D12 to D17): the best guess, the taken-slot choice, the plain sentence and the
/// fingerprint that ties a confirmation to the exact plan. Real temp SQLite.
/// </summary>
public sealed class ItemMoveServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_moveservice_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly ItemMoveService _service;

    public ItemMoveServiceTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _service = new ItemMoveService(new ItemMoveRepository(_db));
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { /* temp database cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch { /* temp database cleanup is best effort */ }
    }

    [Fact]
    public async Task Preview_PreselectsTheSameTitle_AndSaysWhy_AndWritesTheSentence()
    {
        var song = Seed(out var ok, out var track2, out _);

        var preview = (await _service.PreviewAsync(new ItemMovePreviewRequest { WorkId = song, DestinationId = ok })).Value!;

        Assert.Equal(track2, preview.SelectedEntryId);
        Assert.Equal("Same title", preview.GuessReason);
        Assert.Equal(ItemMoveModes.Match, preview.Mode);
        Assert.Null(preview.BlockedReason);
        Assert.Contains("Move Paranoid Android to OK Computer as 2. Paranoid Android?", preview.Message);
        Assert.Contains("leaves The Best Of", preview.Message);
        Assert.True(preview.RenamesFile);
        Assert.True(preview.LeavesOldPlaceEmpty);
    }

    [Fact]
    public async Task Preview_OfATakenEntry_StaysBlockedUntilAnotherVersionIsChosen()
    {
        var song = Seed(out var ok, out _, out var taken);

        var open = (await _service.PreviewAsync(new ItemMovePreviewRequest { WorkId = song, DestinationId = ok, EntryId = taken })).Value!;
        var chosen = (await _service.PreviewAsync(new ItemMovePreviewRequest
        {
            WorkId = song,
            DestinationId = ok,
            EntryId = taken,
            Mode = ItemMoveModes.AnotherVersion,
        })).Value!;

        Assert.NotNull(open.BlockedReason);
        Assert.Contains("already has a file", open.BlockedReason);
        Assert.True(open.SelectedEntryTaken);
        Assert.Null(chosen.BlockedReason);
        Assert.Equal(ItemMoveModes.AnotherVersion, chosen.Mode);
        Assert.NotEqual(open.Fingerprint, chosen.Fingerprint);
    }

    [Fact]
    public async Task Move_WithTheConfirmedFingerprint_MovesTheFile()
    {
        var song = Seed(out var ok, out var track2, out _);
        var preview = (await _service.PreviewAsync(new ItemMovePreviewRequest { WorkId = song, DestinationId = ok })).Value!;

        var result = await _service.MoveAsync(new ItemMoveRequest
        {
            WorkId = song,
            DestinationId = ok,
            EntryId = preview.SelectedEntryId,
            Mode = preview.Mode,
            Fingerprint = preview.Fingerprint,
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(track2, result.Value!.LandedWorkId);
        using var conn = _db.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM editions WHERE work_id = @track2;", new { track2 }));
    }

    [Fact]
    public async Task Move_WhenThePlanChangedSincePreview_IsRefusedWithAFreshPreview_AndNothingMoves()
    {
        var song = Seed(out var ok, out var track2, out _);
        var preview = (await _service.PreviewAsync(new ItemMovePreviewRequest { WorkId = song, DestinationId = ok })).Value!;
        using (var conn = _db.CreateConnection())
        {
            // Someone else files a song into the same empty entry first.
            var edition = Guid.NewGuid();
            conn.Execute("INSERT INTO editions(id, work_id, format_label) VALUES(@edition, @track2, 'FLAC');", new { edition, track2 });
            conn.Execute("INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, status) VALUES(@id, @edition, 'h2', '/x.flac', 'Normal');",
                new { id = Guid.NewGuid(), edition });
        }

        var result = await _service.MoveAsync(new ItemMoveRequest
        {
            WorkId = song,
            DestinationId = ok,
            EntryId = preview.SelectedEntryId,
            Mode = preview.Mode,
            Fingerprint = preview.Fingerprint,
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ItemMoveRefusalKind.Changed, result.Refusal!.Kind);
        Assert.NotNull(result.Refusal.FreshPreview);
        Assert.True(result.Refusal.FreshPreview!.SelectedEntryTaken);
        using var check = _db.CreateConnection();
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM editions WHERE work_id = @song;", new { song }));
    }

    [Fact]
    public async Task AddAnyway_NeedsNoEntry_AndTheSentenceSaysItGetsNoNumber()
    {
        var song = Seed(out var ok, out _, out _);

        var preview = (await _service.PreviewAsync(new ItemMovePreviewRequest { WorkId = song, DestinationId = ok, Mode = ItemMoveModes.AddAnyway })).Value!;

        Assert.Null(preview.BlockedReason);
        Assert.Null(preview.SelectedEntryId);
        Assert.Contains("won't get a number or details from OK Computer", preview.Message);
    }

    [Fact]
    public async Task Preview_OfADestinationThatAcceptsAnotherKind_IsRefused()
    {
        var song = Seed(out _, out _, out _);
        var show = Insert("TV", "parent", null, "A Show", null, owned: false);

        var result = await _service.PreviewAsync(new ItemMovePreviewRequest { WorkId = song, DestinationId = show });

        Assert.False(result.IsSuccess);
        Assert.Equal(ItemMoveRefusalKind.DestinationUnavailable, result.Refusal!.Kind);
    }

    // The Best Of: Paranoid Android (owned, track 1). OK Computer: Karma Police (owned, track 1), Paranoid Android (empty, track 2).
    private Guid Seed(out Guid ok, out Guid emptyTrack2, out Guid takenTrack1)
    {
        var best = Insert("Music", "parent", null, "The Best Of", null, owned: false);
        ok = Insert("Music", "parent", null, "OK Computer", null, owned: false);
        takenTrack1 = Insert("Music", "child", ok, "Karma Police", 1, owned: true);
        emptyTrack2 = Insert("Music", "catalog", ok, "Paranoid Android", 2, owned: false);
        return Insert("Music", "child", best, "Paranoid Android", 1, owned: true);
    }

    private Guid Insert(string mediaType, string kind, Guid? parent, string title, int? number, bool owned)
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO works(id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, ownership, is_catalog_only, wikidata_status)
            VALUES(@id, @mediaType, @kind, @parent, @number, @number, @ownership, @catalog, 'pending');
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES(@id, 'title', @title, '2026-01-01T00:00:00Z');
            """, new { id, mediaType, kind, parent, number, title, ownership = kind == "catalog" ? "Unowned" : "Owned", catalog = kind == "catalog" ? 1 : 0 });
        if (owned)
        {
            var edition = Guid.NewGuid();
            conn.Execute("""
                INSERT INTO editions(id, work_id, format_label) VALUES(@edition, @id, 'FLAC');
                INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, status) VALUES(@asset, @edition, @hash, @path, 'Normal');
                """, new { edition, id, asset = Guid.NewGuid(), hash = Guid.NewGuid().ToString("N"), path = $"/music/{title}.flac" });
        }

        return id;
    }
}
