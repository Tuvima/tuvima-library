using Dapper;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The taste profile's recent plays carry a release year. An audiobook with no original year of its own borrows the year of
/// the book with the same title and an author in common, worked out for the profile's own audiobooks only.
/// </summary>
public sealed class TasteSignalAudiobookYearTests : IDisposable
{
    private const string Author = "Frank Herbert";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly TasteProfileRepository _tasteProfiles;

    public TasteSignalAudiobookYearTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-taste-year-{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _tasteProfiles = new TasteProfileRepository(_db);
    }

    [Fact]
    public async Task TasteSignals_AudiobookWithoutAYear_ShowTheMatchingBooksYear()
    {
        var profile = Guid.NewGuid();
        AddWork("Books", "Dune", ownYear: "1965");
        var audiobook = AddWork("Audiobooks", "Dune");
        Play(profile, audiobook, "2026-10-01T00:00:00Z");

        var signal = Assert.Single(await _tasteProfiles.GetSignalsAsync(profile, 50));

        Assert.Equal(audiobook, signal.AssetId);
        Assert.Equal(1965, signal.ReleaseYear);
    }

    [Fact]
    public async Task TasteSignals_AudiobookWithItsOwnYear_KeepsIt()
    {
        var profile = Guid.NewGuid();
        AddWork("Books", "Dune", ownYear: "1965");
        var audiobook = AddWork("Audiobooks", "Dune", ownYear: "2007");
        Play(profile, audiobook, "2026-10-01T00:00:00Z");

        var signal = Assert.Single(await _tasteProfiles.GetSignalsAsync(profile, 50));

        Assert.Equal(2007, signal.ReleaseYear);
    }

    [Fact]
    public async Task TasteSignals_AudiobookBeyondTheLimit_DoesNotChangeTheYearOfTheOnesReturned()
    {
        var profile = Guid.NewGuid();
        AddWork("Books", "Dune", ownYear: "1965");
        AddWork("Books", "Emma", ownYear: "1815");
        var dune = AddWork("Audiobooks", "Dune");
        var emma = AddWork("Audiobooks", "Emma");
        Play(profile, dune, "2026-10-02T00:00:00Z");
        Play(profile, emma, "2026-10-01T00:00:00Z");

        var signal = Assert.Single(await _tasteProfiles.GetSignalsAsync(profile, 1));

        Assert.Equal(dune, signal.AssetId);
        Assert.Equal(1965, signal.ReleaseYear);
    }

    [Fact]
    public async Task TasteSignals_AnotherProfilesAudiobook_IsNotReturned()
    {
        var profile = Guid.NewGuid();
        AddWork("Books", "Dune", ownYear: "1965");
        var audiobook = AddWork("Audiobooks", "Dune");
        Play(Guid.NewGuid(), audiobook, "2026-10-01T00:00:00Z");

        Assert.Empty(await _tasteProfiles.GetSignalsAsync(profile, 50));
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // A pooled connection can still hold the temp file; the OS temp folder clears it later.
        }
    }

    /// <summary>Adds a work with one file titled <paramref name="title"/> and the shared author; returns the file's id.</summary>
    private Guid AddWork(string mediaType, string title, string? ownYear = null)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO works (id, media_type) VALUES (@workId, @mediaType);
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
                workId,
                editionId,
                assetId,
                mediaType,
                title,
                author = Author,
                personId = Guid.NewGuid(),
                hash = $"hash_{assetId:N}",
                path = $"C:/library/{assetId:N}.bin",
            });
        if (ownYear is not null)
        {
            conn.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'original_publication_year', @ownYear, datetime('now'));",
                new { workId, ownYear });
        }

        return assetId;
    }

    private void Play(Guid profileId, Guid assetId, string lastAccessed)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@profileId, @assetId, 40, @lastAccessed);",
            new { profileId, assetId, lastAccessed });
    }
}
