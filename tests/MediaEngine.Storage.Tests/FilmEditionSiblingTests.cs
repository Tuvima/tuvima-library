using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Several cuts of one film, and one cut in several qualities, must resolve to one film Work with one
/// Edition each, labeled by cut and quality. Two different films that share a title stay apart.
/// Real temp SQLite, same chain factory the ingestion pipeline uses.
/// </summary>
public sealed class FilmEditionSiblingTests : IDisposable
{
    private const string Folder = @"C:\import\films\Blade Runner (1982)\";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly MediaEntityChainFactory _factory;

    public FilmEditionSiblingTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_filmsib_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();

        var resolver = new HierarchyResolver(new WorkRepository(_db), null, null, new MovieEditionSiblingFinder(_db));
        _factory = new MediaEntityChainFactory(_db, resolver);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ThreeCutsOfOneFilm_ShareOneWork_WithThreeLabeledEditions()
    {
        var final = await IngestAsync(Folder + "Blade Runner (1982) - Final Cut.mkv", cut: "Final Cut");
        var theatrical = await IngestAsync(Folder + "Blade Runner (1982) - Theatrical Cut.mkv", cut: "Theatrical Cut");
        var directors = await IngestAsync(Folder + "Blade Runner (1982) - Director's Cut.mkv", cut: "Director's Cut");

        Assert.Equal(final.WorkId, theatrical.WorkId);
        Assert.Equal(final.WorkId, directors.WorkId);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM works"));
        Assert.Equal(
            new[] { "Director's Cut", "Final Cut", "Theatrical Cut" },
            Labels(final.WorkId));
    }

    [Fact]
    public async Task SameCutInTwoQualities_IsOneFilm_WithTwoEditionsLabeledByQuality()
    {
        var hd = await IngestAsync(Folder + "Blade Runner (1982).1080p.mkv", quality: "1080p");
        var uhd = await IngestAsync(@"C:\import\other\Blade Runner (1982).2160p.mkv", quality: "4K");

        Assert.Equal(hd.WorkId, uhd.WorkId);
        Assert.Equal(new[] { "1080p", "4K" }, Labels(hd.WorkId));
    }

    [Fact]
    public async Task CutAndQuality_AreBothInTheLabel()
    {
        var first = await IngestAsync(Folder + "a.mkv", cut: "Final Cut", quality: "4K");
        await IngestAsync(Folder + "b.mkv", cut: "Final Cut", quality: "1080p");

        Assert.Equal(new[] { "Final Cut · 1080p", "Final Cut · 4K" }, Labels(first.WorkId));
    }

    [Fact]
    public async Task SameTitleDifferentYear_StaysTwoFilms()
    {
        var original = await IngestAsync(Folder + "Dune (1984).mkv", title: "Dune", year: "1984");
        var remake = await IngestAsync(Folder + "Dune (2021).mkv", title: "Dune", year: "2021");

        Assert.NotEqual(original.WorkId, remake.WorkId);
    }

    [Fact]
    public async Task MissingYear_JoinsOnlyInTheSameFolder()
    {
        var first = await IngestAsync(Folder + "Heat.mkv", title: "Heat", year: null);
        var sameFolder = await IngestAsync(Folder + "Heat - Director's Cut.mkv", title: "Heat", year: null, cut: "Director's Cut");
        var elsewhere = await IngestAsync(@"C:\import\other\Heat.mkv", title: "Heat", year: null);

        Assert.Equal(first.WorkId, sameFolder.WorkId);
        Assert.NotEqual(first.WorkId, elsewhere.WorkId);
    }

    [Fact]
    public async Task DifferentTitles_StayTwoFilms()
    {
        var a = await IngestAsync(Folder + "a.mkv", title: "Alien", year: "1979");
        var b = await IngestAsync(Folder + "b.mkv", title: "Aliens", year: "1979");

        Assert.NotEqual(a.WorkId, b.WorkId);
    }

    [Fact]
    public async Task FilmWithNoCutOrQuality_KeepsNoLabel()
    {
        var film = await IngestAsync(Folder + "x.mkv");

        Assert.Empty(Labels(film.WorkId));
    }

    private string[] Labels(Guid workId)
    {
        using var conn = _db.CreateConnection();
        return conn.Query<string>(
            "SELECT format_label FROM editions WHERE work_id = @w AND format_label IS NOT NULL ORDER BY format_label;",
            new { w = workId }).ToArray();
    }

    // Mirrors the pipeline: claims + canonical values first, then the chain, then the asset row.
    private async Task<Ingested> IngestAsync(
        string path,
        string title = "Blade Runner",
        string? year = "1982",
        string? cut = null,
        string? quality = null)
    {
        var assetId = Guid.NewGuid();
        var metadata = new Dictionary<string, string> { ["title"] = title };
        if (year is not null) { metadata["year"] = year; }
        if (cut is not null) { metadata["edition_cut"] = cut; }
        if (quality is not null) { metadata["edition_quality"] = quality; }

        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                """
                INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
                VALUES (@provider, 'local_processor', '1', 1);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                VALUES (@c1, @asset, @provider, 'title', @title, 1, '2026-01-01');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@asset, 'title', @title, '2026-01-01');
                """,
                new { provider = WellKnownProviders.LocalProcessor, c1 = Guid.NewGuid(), asset = assetId, title });
            if (year is not null)
            {
                conn.Execute(
                    "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'year', @year, '2026-01-01');",
                    new { asset = assetId, year });
            }
        }

        var editionId = await _factory.EnsureEntityChainAsync(MediaType.Movies, metadata, path);

        using var conn2 = _db.CreateConnection();
        conn2.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@asset, @edition, @hash, @path, 'Normal');",
            new { asset = assetId, edition = editionId, hash = assetId.ToString("N"), path });
        var workId = conn2.QuerySingle<Guid>("SELECT work_id FROM editions WHERE id = @edition;", new { edition = editionId });
        return new Ingested(workId, editionId);
    }

    private int Count(string sql, object? args = null)
    {
        using var conn = _db.CreateConnection();
        return conn.ExecuteScalar<int>(sql, args);
    }

    private sealed record Ingested(Guid WorkId, Guid EditionId);
}
