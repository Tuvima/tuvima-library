using System.Collections;
using System.Reflection;
using Dapper;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// An audiobook with no original year of its own borrows the year of the book with the same title and an author in common.
/// The series page list and the Continue shelf work that out for their own audiobooks only (never from the whole library's
/// credit list); these tests check the values they show.
/// </summary>
public sealed class AudiobookYearReadTests : IDisposable
{
    private const string Author = "Frank Herbert";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"audiobook_year_reads_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly Guid _collectionId = Guid.NewGuid();

    public AudiobookYearReadTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_path);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_db);
    }

    [Fact]
    public async Task SeriesPage_AudiobookWithoutAYear_ShowsTheMatchingBooksYear()
    {
        AddWork("Books", "Dune", ownYear: "1965");
        var audiobook = AddWork("Audiobooks", "Dune");

        var rows = await LoadSeriesPageAsync();

        Assert.Equal("1965", YearOf(rows, audiobook.WorkId));
    }

    [Fact]
    public async Task SeriesPage_AudiobookWithItsOwnYear_KeepsIt()
    {
        AddWork("Books", "Dune", ownYear: "1965");
        var audiobook = AddWork("Audiobooks", "Dune", ownYear: "2007");

        var rows = await LoadSeriesPageAsync();

        Assert.Equal("2007", YearOf(rows, audiobook.WorkId));
    }

    [Fact]
    public async Task SeriesPage_WhenTheMostRecentlyPlayedFileIsNotTheFirst_StillShowsTheBorrowedYear()
    {
        AddWork("Books", "Dune", ownYear: "1965");
        var audiobook = AddWork("Audiobooks", "Dune");
        var secondFile = AddFile(audiobook, "Dune");
        // The first file was played a month ago, the second one yesterday, so the row shows the second.
        Play(Profile.SeedProfileId, audiobook.AssetId, 20, "2026-09-01T00:00:00Z");
        Play(Profile.SeedProfileId, secondFile, 20, "2026-10-01T00:00:00Z");

        var rows = await LoadSeriesPageAsync();

        var row = Assert.Single(rows, r => Read<string>(r, "Id") == audiobook.WorkId.ToString());
        Assert.Equal(secondFile.ToString(), Read<string>(row, "AssetId"));
        Assert.Equal("1965", Read<string>(row, "Year"));
    }

    [Fact]
    public async Task ContinueShelf_AudiobookWithoutAYear_ShowsTheMatchingBooksYear()
    {
        AddWork("Books", "Dune", ownYear: "1965");
        var audiobook = AddWork("Audiobooks", "Dune");
        Play(Profile.SeedProfileId, audiobook.AssetId, 40, "2026-10-01T00:00:00Z");

        var rows = await new DisplayJourneyProjectionReader(_db).LoadAsync(Profile.SeedProfileId, null, CancellationToken.None);

        Assert.Equal("1965", Assert.Single(rows).Year);
    }

    [Fact]
    public async Task ContinueShelf_AudiobookWithItsOwnYear_KeepsIt()
    {
        AddWork("Books", "Dune", ownYear: "1965");
        var audiobook = AddWork("Audiobooks", "Dune", ownYear: "2007");
        Play(Profile.SeedProfileId, audiobook.AssetId, 40, "2026-10-01T00:00:00Z");

        var rows = await new DisplayJourneyProjectionReader(_db).LoadAsync(Profile.SeedProfileId, null, CancellationToken.None);

        Assert.Equal("2007", Assert.Single(rows).Year);
    }

    [Fact]
    public async Task ContinueShelf_OnlyBorrowsForTheProfilesOwnPlays()
    {
        AddWork("Books", "Dune", ownYear: "1965");
        var audiobook = AddWork("Audiobooks", "Dune");
        var otherProfile = Guid.NewGuid();
        Play(otherProfile, audiobook.AssetId, 40, "2026-10-01T00:00:00Z");
        Play(Profile.SeedProfileId, audiobook.AssetId, 40, "2026-10-02T00:00:00Z");

        var rows = await new DisplayJourneyProjectionReader(_db).LoadAsync(Profile.SeedProfileId, null, CancellationToken.None);

        Assert.Equal("1965", Assert.Single(rows).Year);
    }

    private async Task<List<object>> LoadSeriesPageAsync()
    {
        var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);
        var method = typeof(DetailCompositionOrchestrator).GetMethod("LoadCollectionWorksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(composer, [_collectionId, null, CancellationToken.None, null, Profile.SeedProfileId, null])!;
        await task;
        var result = (IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!;
        return result.Cast<object>().ToList();
    }

    private static string? YearOf(IEnumerable<object> rows, Guid workId) =>
        Read<string?>(Assert.Single(rows, row => Read<string>(row, "Id") == workId.ToString()), "Year");

    private static T Read<T>(object row, string property) => (T)row.GetType().GetProperty(property)!.GetValue(row)!;

    private SeededWork AddWork(string mediaType, string title, string? ownYear = null)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now')) ON CONFLICT(id) DO NOTHING;
            INSERT INTO works (id, collection_id, media_type, work_kind, curator_state)
                VALUES (@workId, @collectionId, @mediaType, 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, @path, 'Normal');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'title', @title, datetime('now'));
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@workId, 'author', 0, @author);
            INSERT INTO persons (id, name, created_at)
                SELECT @personId, @author, datetime('now') WHERE NOT EXISTS (SELECT 1 FROM persons WHERE name = @author COLLATE NOCASE);
            """,
            new
            {
                collectionId = _collectionId,
                workId,
                editionId,
                assetId,
                mediaType,
                title,
                author = Author,
                personId = Guid.NewGuid(),
                hash = $"hash_{assetId:N}",
                path = $"C:/qa/{assetId:N}.bin",
            });
        if (ownYear is not null)
        {
            conn.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'original_publication_year', @ownYear, datetime('now'));",
                new { workId, ownYear });
        }

        return new SeededWork(workId, editionId, assetId);
    }

    /// <summary>Adds a second file to the work's edition, with the same title.</summary>
    private Guid AddFile(SeededWork work, string title)
    {
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, @path, 'Normal');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'title', @title, datetime('now'));
            """,
            new { assetId, editionId = work.EditionId, title, hash = $"hash_{assetId:N}", path = $"C:/qa/{assetId:N}.bin" });
        return assetId;
    }

    private void Play(Guid profileId, Guid assetId, double progress, string lastAccessed)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@profileId, @assetId, @progress, @lastAccessed);",
            new { profileId, assetId, progress, lastAccessed });
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A pooled connection can still hold the temp file; the OS temp folder clears it later.
        }
    }

    private sealed record SeededWork(Guid WorkId, Guid EditionId, Guid AssetId);
}
