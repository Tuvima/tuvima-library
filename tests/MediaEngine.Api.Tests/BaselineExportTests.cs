using System.Text.RegularExpressions;
using Dapper;
using MediaEngine.Api.DevSupport;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class BaselineExportTests : IDisposable
{
    // A GUID that belongs to the provider (inside an image address), not to a row of the run: it must survive.
    private const string ProviderGuid = "2a1afcc9-6179-5fdf-3bea-aec376486895";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_baseline_export_{Guid.NewGuid():N}");

    public BaselineExportTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        DatabaseConnection.ReleasePooledConnections(Path.Combine(_root, "a", "library.db"));
        DatabaseConnection.ReleasePooledConnections(Path.Combine(_root, "b", "library.db"));
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Left behind only if a scanner holds a temp file; the unique folder name keeps runs apart.
        }
    }

    private string SampleRoot(string run) => Path.Combine(_root, run, "samples");

    private BaselineRecord Export(string run, bool reversed)
    {
        var folder = Path.Combine(_root, run);
        Directory.CreateDirectory(folder);
        var databasePath = Path.Combine(folder, "library.db");
        Seed(databasePath, SampleRoot(run), Path.Combine(folder, "data"), reversed);
        var info = new BaselineRunInfo(SampleRoot(run), ["books", "tv"], [], run, "abc123", null, null, new Dictionary<string, int>());
        return BaselineExport.Build(databasePath, info, Path.Combine(folder, "config"));
    }

    private static string Comparable(BaselineRecord record)
    {
        record.Metadata = new BaselineMetadata();
        record.Timings = new BaselineTimings();
        return BaselineRecordJson.Serialize(record);
    }

    private static void Seed(string databasePath, string sampleRoot, string dataRoot, bool reversed)
    {
        using var database = new DatabaseConnection(databasePath);
        database.InitializeSchema();
        database.RunStartupChecks();
        using var connection = database.CreateConnection();
        var apple = connection.QuerySingle<Guid>("SELECT id FROM metadata_providers WHERE name = 'apple_api'");
        var tmdb = connection.QuerySingle<Guid>("SELECT id FROM metadata_providers WHERE name = 'tmdb'");

        var universe = Guid.NewGuid();
        var series = Guid.NewGuid();
        connection.Execute(
            "INSERT INTO collections (id, collection_type, display_name, wikidata_qid) VALUES (@universe, 'Universe', 'The Expanse Universe', 'Q1001')",
            new { universe });
        connection.Execute(
            "INSERT INTO collections (id, parent_collection_id, collection_type, display_name, wikidata_qid) VALUES (@series, @universe, 'Series', 'The Expanse', 'Q1002')",
            new { series, universe });

        var books = new (string File, string Title, string Qid, bool InSeries)[]
        {
            ("books/Dune.epub", "Dune", "Q190192", false),
            ("books/Leviathan Wakes.epub", "Leviathan Wakes", "Q1003", true),
        };
        foreach (var (file, title, qid, inSeries) in reversed ? books.AsEnumerable().Reverse() : books)
        {
            var work = Guid.NewGuid();
            var edition = Guid.NewGuid();
            var asset = Guid.NewGuid();
            var artwork = Guid.NewGuid();
            var preferredLink = Guid.NewGuid();
            var spareLink = Guid.NewGuid();
            var stamp = DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 5000)).ToString("O");
            connection.Execute(
                "INSERT INTO works (id, collection_id, media_type, wikidata_qid) VALUES (@work, @collection, 'Books', @qid)",
                new { work, collection = inSeries ? series : (Guid?)null, qid });
            connection.Execute("INSERT INTO editions (id, work_id) VALUES (@edition, @work)", new { edition, work });
            connection.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@asset, @edition, @hash, @path)",
                new { asset, edition, hash = "hash-" + title, path = Path.Combine(sampleRoot, file.Replace('/', Path.DirectorySeparatorChar)) });
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at, winning_provider_id) VALUES (@work, 'title', @title, @stamp, @apple)",
                new { work, title, stamp, apple });
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, 'artwork_settled_at', @stamp, @stamp)",
                new { work, stamp });
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, 'cover_url', @url, @stamp)",
                new { work, url = $"/stream/artwork/{artwork}", stamp });
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, 'cover_path', @path, @stamp)",
                new { work, path = Path.Combine(dataRoot, "library", "cover.jpg"), stamp });
            foreach (var (value, ordinal) in new[] { ("Sci-Fi", 1), ("Adventure", 0) })
            {
                connection.Execute(
                    "INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@work, 'genre', @ordinal, @value)",
                    new { work, ordinal, value });
            }

            connection.Execute(
                "INSERT INTO artwork_assets (id, content_hash, source_provider, source_url) VALUES (@artwork, @hash, 'provider', @url)",
                new { artwork, hash = "art-" + title, url = $"https://cdn.example/{title}/{ProviderGuid}/cover.jpg" });
            connection.Execute(
                "INSERT INTO entity_artwork_links (id, entity_id, entity_type, artwork_asset_id, role, is_preferred) VALUES (@preferredLink, @work, 'Work', @artwork, 'Primary', 1)",
                new { preferredLink, work, artwork });
            connection.Execute(
                "INSERT INTO entity_artwork_links (id, entity_id, entity_type, artwork_asset_id, role, is_preferred, context) VALUES (@spareLink, @work, 'Work', @artwork, 'Background', 0, 'spare')",
                new { spareLink, work, artwork });
            connection.Execute(
                "INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value) VALUES (@id, @work, @apple, 'apple_books_id', @value)",
                new { id = Guid.NewGuid(), work, apple, value = "id-" + title });
            connection.Execute(
                "INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value) VALUES (@id, @work, @tmdb, 'cast_member_tmdb_id', '999')",
                new { id = Guid.NewGuid(), work, tmdb });
            connection.Execute(
                "INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, resolved_qid) VALUES (@id, @asset, 'MediaAsset', 'Books', 'Ready', @qid)",
                new { id = Guid.NewGuid(), asset, qid });
            connection.Execute(
                "INSERT INTO review_queue (id, entity_id, entity_type, trigger, status) VALUES (@id, @asset, 'MediaAsset', 'RetailMatchAmbiguous', 'Resolved')",
                new { id = Guid.NewGuid(), asset });
        }
    }

    [Fact]
    public void Build_GivesTheSameComparableRecordWhateverTheRowIdsAndInsertionOrder()
    {
        var first = Export("a", reversed: false);
        var second = Export("b", reversed: true);

        Assert.Equal(Comparable(first), Comparable(second));
        Assert.True(BaselineComparer.Compare(first, second).IsIdentical);
    }

    [Fact]
    public void Build_ListsItemsByRelativePathAndNamesThemWithStableKeys()
    {
        var record = Export("a", reversed: true);

        Assert.Equal(["books/Dune.epub", "books/Leviathan Wakes.epub"], record.Items.Select(item => item.Path));
        var dune = record.Items[0];
        Assert.Equal("Books", dune.MediaType);
        Assert.Equal("Dune", dune.Work.Title);
        Assert.Equal("Q190192", dune.Work.Qid);
        Assert.Equal(["books/Dune.epub"], dune.Work.Assets);
        Assert.Equal("Ready", dune.Identity.JobState);
        Assert.Equal("Q190192", dune.Identity.ResolvedQid);
    }

    [Fact]
    public void Build_SortsMultiValuedMetadataAndKeepsTheWinningProvider()
    {
        var work = Export("a", reversed: false).Items[0].Values["work"];

        Assert.Equal(["Adventure", "Sci-Fi"], work.Arrays["genre"]);
        Assert.Equal("apple_api", work.Scalars["title"].Provider);
    }

    [Fact]
    public void Build_NormalisesOnlyVolatileValues()
    {
        var work = Export("a", reversed: false).Items[0].Values["work"];

        Assert.Equal(BaselineNormalizer.TimestampToken, work.Scalars["artwork_settled_at"].Value);
        Assert.Equal($"/stream/artwork/{BaselineNormalizer.GuidToken}", work.Scalars["cover_url"].Value);
        Assert.StartsWith("<data>", work.Scalars["cover_path"].Value, StringComparison.Ordinal);
        Assert.Equal("Dune", work.Scalars["title"].Value);
    }

    [Fact]
    public void Build_RecordsOnlyThePreferredArtworkWithItsSourceAndContentHash()
    {
        var artwork = Export("a", reversed: false).Items[0].Artwork;

        var chosen = Assert.Single(artwork);
        Assert.Equal("work/Primary", chosen.Key);
        Assert.Equal("provider", chosen.Value.Provider);
        Assert.Equal("art-Dune", chosen.Value.ContentHash);
        Assert.Contains(ProviderGuid, chosen.Value.SourceUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_KeepsProviderIdsButLeavesOutPerPersonCreditIds()
    {
        var ids = Export("a", reversed: false).Items[0].ExternalIds;

        Assert.Equal(["id-Dune"], ids["apple_books_id"]);
        Assert.DoesNotContain("cast_member_tmdb_id", ids.Keys);
    }

    [Fact]
    public void Build_NamesSeriesAndUniverseMembershipByNameAndQid()
    {
        var leviathan = Export("a", reversed: false).Items[1];

        Assert.Collection(
            leviathan.Groups,
            series => Assert.Equal(("Series", "The Expanse", "Q1002"), (series.Type, series.Name, series.Qid)),
            universe => Assert.Equal(("Universe", "The Expanse Universe", "Q1001"), (universe.Type, universe.Name, universe.Qid)));
        Assert.Empty(Export("b", reversed: false).Items[0].Groups);
    }

    [Fact]
    public void Build_ListsReviewEntriesWithTriggerAndStatusOnly()
    {
        var review = Assert.Single(Export("a", reversed: false).Items[0].Review);

        Assert.Equal("RetailMatchAmbiguous", review.Trigger);
        Assert.Equal("Resolved", review.Status);
    }

    [Fact]
    public void Build_KeepsRowIdsRunFoldersAndClockTimesOutOfTheComparableSections()
    {
        var json = Comparable(Export("a", reversed: false));

        var guids = Regex.Matches(json, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Select(match => match.Value).Distinct();
        Assert.Equal([ProviderGuid], guids);
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"20\d\d-\d\d-\d\dT\d\d:\d\d", json);
    }
}
