using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// An item added anyway stays unnumbered (Details overlay D13): nothing that numbers or slots items from a file's tags or a provider may
/// give it a number, whether the file is read again, scanned again or re-identified. Only a later Match, which clears the mark, numbers it.
/// </summary>
public sealed class UnnumberedItemTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_unnumbered_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly CanonicalValueRepository _canonicals;
    private readonly WorkRepository _works;
    private readonly HierarchyResolver _resolver;

    public UnnumberedItemTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _canonicals = new CanonicalValueRepository(_database);
        _works = new WorkRepository(_database);
        _resolver = new HierarchyResolver(_works);
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }

    [Fact]
    public async Task ReReadingTheFilesTags_NeverGivesAnUnnumberedTitleOrItsFileANumber_ButOtherValuesStillLand()
    {
        var (work, asset) = UnnumberedSong();

        await _canonicals.UpsertBatchAsync(
        [
            Value(asset, "track_number", "9"),
            Value(asset, "disc_number", "1"),
            Value(asset, "title", "Hidden Bonus Jam"),
            Value(work, "track_number", "9"),
            Value(work, "episode_number", "3"),
            Value(work, "series_position", "2"),
            Value(asset, "audiobook_part_number", "4"),
        ]);

        Assert.Equal("Hidden Bonus Jam", Canonical(asset, "title"));
        foreach (var key in new[] { "track_number", "disc_number", "episode_number", "series_position" })
        {
            Assert.Null(Canonical(asset, key));
            Assert.Null(Canonical(work, key));
        }

        // A title's mark covers only the title-level numbers: the part number of its file is another list's number (an audiobook part's
        // mark covers that one), so it still lands.
        Assert.Equal("4", Canonical(asset, "audiobook_part_number"));
    }

    [Fact]
    public async Task AnUnnumberedAudiobookPart_AndItsFile_AreNotNumberedEither()
    {
        var work = Work("Audiobooks", "standalone", unmatched: false);
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        Execute(
            """
            INSERT INTO editions (id, work_id, placement_unmatched) VALUES (@edition, @work, 1);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at) VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP);
            """,
            new { edition, work, asset, hash = Guid.NewGuid().ToString("N"), path = $"C:/library/{asset:N}.mp3" });

        await _canonicals.UpsertBatchAsync([Value(asset, "audiobook_part_number", "2"), Value(edition, "audiobook_part_number", "2")]);

        Assert.Null(Canonical(asset, "audiobook_part_number"));
        Assert.Null(Canonical(edition, "audiobook_part_number"));
    }

    [Fact]
    public async Task ATitlePlacedNormally_IsNumberedAsEver_AndNumbersReturnOnceTheMarkIsCleared()
    {
        var (work, asset) = UnnumberedSong();
        var (other, otherAsset) = Song(unmatched: false);

        await _canonicals.UpsertBatchAsync([Value(otherAsset, "track_number", "4"), Value(other, "track_number", "4")]);
        Assert.Equal("4", Canonical(otherAsset, "track_number"));
        Assert.Equal("4", Canonical(other, "track_number"));

        // A later Match clears the mark, and then numbers are written for the title again.
        Execute("UPDATE works SET placement_unmatched = 0 WHERE id = @work;", new { work });
        await _canonicals.UpsertBatchAsync([Value(asset, "track_number", "7")]);

        Assert.Equal("7", Canonical(asset, "track_number"));
    }

    [Fact]
    public async Task AScanOrReIdentify_NeverSlotsOrNumbersAnUnnumberedTitle_ByTagsOrByTitle()
    {
        var first = await _resolver.ResolveAsync(MediaType.Music, Tags("Keeper", track: "1"));
        var parent = first.ParentWorkId!.Value;
        var hidden = Guid.NewGuid();
        Execute(
            """
            INSERT INTO works (id, media_type, work_kind, parent_work_id, placement_unmatched, is_catalog_only, ownership)
            VALUES (@hidden, 'Music', 'child', @parent, 1, 0, 'Owned');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@hidden, 'title', 'Hidden Bonus Jam', CURRENT_TIMESTAMP);
            """,
            new { hidden, parent });

        // The same title with no track number (found by title), and with one (found by slot): neither touches the unnumbered title.
        var byTitle = await _resolver.ResolveAsync(MediaType.Music, Tags("Hidden Bonus Jam", track: null));
        var byTag = await _resolver.ResolveAsync(MediaType.Music, Tags("Hidden Bonus Jam", track: "9"));
        await _works.UpdateOrdinalSortAsync(hidden, 9);
        await _works.GetOrCreateChildAsync(MediaType.Music, parent, ordinal: 9, ordinalSort: 9);

        Assert.NotEqual(hidden, byTitle.WorkId);
        Assert.NotEqual(hidden, byTag.WorkId);
        var row = Row(hidden);
        Assert.Null(row.Ordinal);
        Assert.Null(row.OrdinalSort);
        Assert.Equal(1, row.Unmatched);
        Assert.Equal(parent, row.Parent);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private static Dictionary<string, string> Tags(string title, string? track)
    {
        var tags = new Dictionary<string, string>
        {
            ["artist"] = "Radiohead",
            ["album"] = "OK Computer",
            ["title"] = title,
        };
        if (track is not null)
        {
            tags["track_number"] = track;
        }

        return tags;
    }

    private (Guid Work, Guid Asset) UnnumberedSong() => Song(unmatched: true);

    private (Guid Work, Guid Asset) Song(bool unmatched)
    {
        var work = Work("Music", "child", unmatched);
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        Execute(
            """
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at) VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP);
            """,
            new { edition, work, asset, hash = Guid.NewGuid().ToString("N"), path = $"C:/library/{asset:N}.mp3" });
        return (work, asset);
    }

    private Guid Work(string mediaType, string kind, bool unmatched)
    {
        var id = Guid.NewGuid();
        Execute(
            "INSERT INTO works (id, media_type, work_kind, placement_unmatched) VALUES (@id, @mediaType, @kind, @unmatched);",
            new { id, mediaType, kind, unmatched = unmatched ? 1 : 0 });
        return id;
    }

    private static CanonicalValue Value(Guid entity, string key, string value) => new()
    {
        EntityId = entity,
        Key = key,
        Value = value,
        LastScoredAt = DateTimeOffset.UtcNow,
    };

    private string? Canonical(Guid entity, string key)
    {
        using var connection = _database.CreateConnection();
        return connection.QueryFirstOrDefault<string>("SELECT value FROM canonical_values WHERE entity_id = @entity AND key = @key;", new { entity, key });
    }

    private WorkRow Row(Guid id)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<WorkRow>(
            "SELECT parent_work_id AS Parent, ordinal AS Ordinal, ordinal_sort AS OrdinalSort, placement_unmatched AS Unmatched FROM works WHERE id = @id;",
            new { id });
    }

    private void Execute(string sql, object? args = null)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(sql, args);
    }

    private sealed class WorkRow
    {
        public Guid? Parent { get; init; }
        public int? Ordinal { get; init; }
        public double? OrdinalSort { get; init; }
        public long Unmatched { get; init; }
    }
}
