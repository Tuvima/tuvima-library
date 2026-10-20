using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Dashboard's Universe page read (<c>GET /universe/{qid}/overview</c>) and the "Part of" line, run through the same
/// real host, display projection and data store as the helper-app Universe read it is built on.
/// </summary>
public sealed partial class CollectionClientEndpointsTests
{
    private const string GraphActions = "/api/v1/graph-actions";
    private const string BetaSeriesQid = "Q9000";

    private LibraryPreferencesSettings? _libraryPreferences;

    [Fact]
    public async Task Overview_UsesTheSameShelfOrderAndCountsAsTheHelperAppRead()
    {
        SeedCast();
        await using var app = await StartAsync(mapGraphActions: true);

        var overview = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview");
        var helperRead = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");

        // The page's detail is the helper-app answer, byte for byte: same shelves in the same order, same counts.
        Assert.True(
            JsonNode.DeepEquals(helperRead, overview["detail"]),
            $"The page detail differs from the helper-app read.\npage:   {overview["detail"]!.ToJsonString()}\nhelper: {helperRead.ToJsonString()}");
        Assert.Equal(
            helperRead["shelves"]!.AsArray().Select(shelf => shelf!["collection_id"]!.GetValue<Guid>()),
            overview["detail"]!["shelves"]!.AsArray().Select(shelf => shelf!["collection_id"]!.GetValue<Guid>()));
        Assert.Equal(helperRead["item_count"]!.GetValue<int>(), overview["detail"]!["item_count"]!.GetValue<int>());
        Assert.Equal(helperRead["series_count"]!.GetValue<int>(), overview["detail"]!["series_count"]!.GetValue<int>());
        // Hero, Villain, Hidden Villain and the three works' things are the entities the profile can see here.
        Assert.Equal(6, overview["graph_entity_count"]!.GetValue<int>());
        Assert.Equal(0, overview["graph_relationship_count"]!.GetValue<int>());
        Assert.Null(overview["standalone"]);
    }

    [Fact]
    public async Task Overview_IsNotFound_ForAProfileThatCannotSeeTheUniverse()
    {
        // The adult Universe holds only R-rated items; a limited profile gets the same answer as for a Universe that does not exist.
        SetContentLimit("PG");
        await using var app = await StartAsync(mapGraphActions: true);

        var hidden = await SnapshotAsync(app, $"{GraphActions}/universe/Q5000/overview");
        var unknown = await SnapshotAsync(app, $"{GraphActions}/universe/Q999999/overview");
        var notAQid = await SnapshotAsync(app, $"{GraphActions}/universe/not-a-qid/overview");

        Assert.Equal(HttpStatusCode.NotFound, hidden.Status);
        Assert.Equal(unknown, hidden);
        Assert.Equal(HttpStatusCode.NotFound, notAQid.Status);

        SetContentLimit(null);
        using var adult = await app.Client.GetAsync($"{GraphActions}/universe/Q5000/overview");
        Assert.Equal(HttpStatusCode.OK, adult.StatusCode);
    }

    [Fact]
    public async Task MissingEntries_AreOmitted_WhenTheProfileHidesMissingItems()
    {
        SeedBetaManifest();
        await using var app = await StartAsync(mapGraphActions: true);

        // The media default hides them: the count is still told, the entries are not listed.
        _libraryPreferences = BooksShowMissing("hidden");
        var byDefault = BetaMissing(await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"));
        Assert.False(byDefault["show_missing"]!.GetValue<bool>());
        Assert.Equal(1, byDefault["missing_count"]!.GetValue<int>());
        Assert.Empty(byDefault["items"]!.AsArray());

        // The default shows them but this profile chose to hide this Series: the person's own choice wins.
        _libraryPreferences = BooksShowMissing("shown");
        await new ProfileSequencePreferencesRepository(_database)
            .SaveAsync(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, "books", BetaSeriesQid, showMissing: false);
        var overridden = BetaMissing(await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"));
        Assert.False(overridden["show_missing"]!.GetValue<bool>());
        Assert.Equal(1, overridden["missing_count"]!.GetValue<int>());
        Assert.Empty(overridden["items"]!.AsArray());

        // A media type whose policy is switched off never shows missing entries, whatever the profile chose.
        _libraryPreferences = BooksShowMissing("shown", enabled: false);
        await new ProfileSequencePreferencesRepository(_database)
            .SaveAsync(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, "books", BetaSeriesQid, showMissing: true);
        var disabled = BetaMissing(await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"));
        Assert.False(disabled["show_missing"]!.GetValue<bool>());
        Assert.Empty(disabled["items"]!.AsArray());
    }

    [Fact]
    public async Task MissingEntries_AreListed_WhenTheProfileShowsMissingItems()
    {
        var (betaOne, _) = SeedBetaManifest();
        await using var app = await StartAsync(mapGraphActions: true);

        _libraryPreferences = BooksShowMissing("shown");
        var byDefault = BetaMissing(await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"));
        Assert.True(byDefault["show_missing"]!.GetValue<bool>());
        Assert.Equal(1, byDefault["missing_count"]!.GetValue<int>());
        // Only the main-sequence entry is listed (the supplementary one is not), placed after the work it follows.
        var item = Assert.Single(byDefault["items"]!.AsArray())!;
        Assert.Equal("Beta Missing", item["label"]!.GetValue<string>());
        Assert.Equal("2", item["raw_ordinal"]!.GetValue<string>());
        Assert.Equal(2.0, item["parsed_ordinal"]!.GetValue<double>());
        Assert.Equal("Q9002", item["item_qid"]!.GetValue<string>());
        Assert.Equal(betaOne, item["after_work_id"]!.GetValue<Guid>());

        // The default hides them but this profile chose to see this Series' missing entries.
        _libraryPreferences = BooksShowMissing("hidden");
        await new ProfileSequencePreferencesRepository(_database)
            .SaveAsync(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, "books", BetaSeriesQid, showMissing: true);
        var overridden = BetaMissing(await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"));
        Assert.True(overridden["show_missing"]!.GetValue<bool>());
        Assert.Single(overridden["items"]!.AsArray());
    }

    [Fact]
    public async Task Overview_ListsWorksThatBelongDirectlyToTheUniverse_AsStandalone()
    {
        AddWork("Movies", "Standalone Film", "PG", _universe, ".mkv");
        await using var app = await StartAsync(mapGraphActions: true);

        var overview = await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview");

        var standalone = overview["standalone"]!;
        Assert.Equal("Standalone", standalone["title"]!.GetValue<string>());
        Assert.Equal(
            ["Standalone Film"],
            standalone["items"]!.AsArray().Select(card => card!["title"]!.GetValue<string>()).ToArray());
        // It is not counted as a Series, and the Series shelves are unchanged.
        Assert.Equal(3, overview["detail"]!["series_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Overview_ReportsHowFarEnrichmentHasGot()
    {
        await using var app = await StartAsync(mapGraphActions: true);

        var before = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["enrichment"]!;
        Assert.Equal("not_started", before["state"]!.GetValue<string>());
        Assert.Equal(0, before["enriched_work_count"]!.GetValue<int>());
        var workCount = before["work_count"]!.GetValue<int>();
        Assert.True(workCount >= 2);

        // One work finished its universe enrichment: the Engine is part way through.
        MarkEnriched("Alpha One");
        var partway = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["enrichment"]!;
        Assert.Equal("running", partway["state"]!.GetValue<string>());
        Assert.Equal(1, partway["enriched_work_count"]!.GetValue<int>());
        Assert.Equal(workCount, partway["work_count"]!.GetValue<int>());

        MarkEveryWorkEnriched();
        var done = (await GetJsonAsync(app, $"{GraphActions}/universe/{UniverseQid}/overview"))["enrichment"]!;
        Assert.Equal("ready", done["state"]!.GetValue<string>());
        Assert.Equal(workCount, done["enriched_work_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task PartOfLine_Appears_OnlyWithTwoSeriesOrTwoFormats()
    {
        // One Series in one format: not enough to call it a Universe worth a link.
        SeedUniverse("Q7001", "Lonely World", [("Movies", "Lonely One")]);
        // Two Series in one format.
        SeedUniverse("Q7002", "Paired World", [("Movies", "Paired One"), ("Movies", "Paired Two")]);
        // One Series holding two formats.
        SeedUniverse("Q7003", "Mixed World", [("Movies", "Mixed Film"), ("Books", "Mixed Novel")], oneSeries: true);
        await using var app = await StartAsync(mapGraphActions: true);

        Assert.Null((await GetJsonAsync(app, "/test/universe-link/Q7001"))["link"]);
        var paired = (await GetJsonAsync(app, "/test/universe-link/Q7002"))["link"]!;
        Assert.Equal("Q7002", paired["qid"]!.GetValue<string>());
        Assert.Equal("Paired World", paired["name"]!.GetValue<string>());
        Assert.Equal("Mixed World", (await GetJsonAsync(app, "/test/universe-link/Q7003"))["link"]!["name"]!.GetValue<string>());

        // Nothing is linked for a QID that is no Universe, or one the profile cannot see.
        Assert.Null((await GetJsonAsync(app, "/test/universe-link/Q4243"))["link"]);
        Assert.Null((await GetJsonAsync(app, "/test/universe-link/not-a-qid"))["link"]);
        SetContentLimit("PG");
        Assert.Null((await GetJsonAsync(app, "/test/universe-link/Q5000"))["link"]);
    }

    [Fact]
    public void PartOfRule_NeedsTwoOfEither()
    {
        Assert.False(UniverseClientReadService.ShowsPartOfLine(seriesCount: 1, formatCount: 1));
        Assert.True(UniverseClientReadService.ShowsPartOfLine(seriesCount: 2, formatCount: 1));
        Assert.True(UniverseClientReadService.ShowsPartOfLine(seriesCount: 1, formatCount: 2));
    }

    [Fact]
    public async Task MembershipSummary_CountsOnlyWhatTheProfileCanSee()
    {
        await using var app = await StartAsync(mapGraphActions: true);

        var everything = (await GetJsonAsync(app, $"/test/universe-link/{UniverseQid}"))["summary"]!;
        Assert.Equal(3, everything["series"]!.GetValue<int>());
        Assert.Equal(2, everything["formats"]!.GetValue<int>());
        Assert.Equal("Test Universe", everything["name"]!.GetValue<string>());

        // The R-rated items go away, but every Series and both formats keep at least one visible item.
        SetContentLimit("PG");
        var limited = (await GetJsonAsync(app, $"/test/universe-link/{UniverseQid}"))["summary"]!;
        Assert.Equal(3, limited["series"]!.GetValue<int>());
        Assert.Equal(2, limited["formats"]!.GetValue<int>());
        Assert.Null((await GetJsonAsync(app, "/test/universe-link/Q5000"))["summary"]);
    }

    // A test-only route that runs the Universe read behind the item page's "Part of" line inside a real request, because the
    // display projection it reads is scoped to the request's profile.
    private static void MapUniverseMembershipProbe(WebApplication app) =>
        app.MapGet("/test/universe-link/{qid}", async (
            string qid,
            HttpContext http,
            IProfileRepository profiles,
            UniverseClientReadService universes,
            CancellationToken ct) =>
        {
            var profile = await MediaEngine.Api.Endpoints.CollectionClientEndpoints.ResolveActiveProfileAsync(http, profiles, ct);
            var link = await universes.GetUniverseLinkAsync(qid, profile, ct);
            var summary = await universes.GetMembershipSummaryAsync(qid, profile, ct);
            return Results.Ok(new
            {
                link = link is null ? null : new { qid = link.Qid, name = link.Name },
                summary = summary is { } s ? new { series = s.SeriesCount, formats = s.FormatCount, name = s.Name } : null,
            });
        });


    // Seeds ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Series Beta (books) gets a manifest: Beta One owned at 1, a main-sequence entry missing at 2, Beta Two owned at 3,
    /// and a supplementary entry that is missing too (never part of the numbered run). Returns the two owned works.
    /// </summary>
    private (Guid BetaOne, Guid BetaTwo) SeedBetaManifest()
    {
        var betaOne = WorkIdByTitle("Beta One");
        var betaTwo = WorkIdByTitle("Beta Two");
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO series_manifest_hydrations (series_qid, collection_id, series_label, last_hydrated_at)
            VALUES (@seriesQid, @beta, 'Beta Series', '2026-01-01 00:00:00');
            INSERT INTO series_manifest_items (id, collection_id, series_qid, item_qid, item_label, raw_ordinal, parsed_ordinal, sort_order,
                                               membership_scope, order_source, ownership_state, linked_work_id, last_hydrated_at)
            VALUES (@i1, @beta, @seriesQid, 'Q9001', 'Beta One', '1', 1, 1, 'MainSequence', 'test', 'Owned', @betaOne, '2026-01-01 00:00:00'),
                   (@i2, @beta, @seriesQid, 'Q9002', 'Beta Missing', '2', 2, 2, 'MainSequence', 'test', 'Missing', NULL, '2026-01-01 00:00:00'),
                   (@i3, @beta, @seriesQid, 'Q9003', 'Beta Two', '3', 3, 3, 'MainSequence', 'test', 'Owned', @betaTwo, '2026-01-01 00:00:00'),
                   (@i4, @beta, @seriesQid, 'Q9004', 'Beta Side Story', NULL, NULL, 4, 'Supplementary', 'test', 'Missing', NULL, '2026-01-01 00:00:00');
            """,
            new
            {
                seriesQid = BetaSeriesQid,
                beta = _beta,
                betaOne,
                betaTwo,
                i1 = Guid.NewGuid(),
                i2 = Guid.NewGuid(),
                i3 = Guid.NewGuid(),
                i4 = Guid.NewGuid(),
            });
        return (betaOne, betaTwo);
    }

    private Guid WorkIdByTitle(string title)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<Guid>(
            "SELECT entity_id FROM canonical_values WHERE key = 'title' AND value = @title AND entity_id IN (SELECT id FROM works);",
            new { title });
    }

    private void MarkEnriched(string title)
    {
        var work = WorkIdByTitle(title);
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, 'stage3_enriched_at', '2026-01-01T00:00:00.0000000+00:00', CURRENT_TIMESTAMP);",
            new { work });
    }

    private void MarkEveryWorkEnriched()
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT OR REPLACE INTO canonical_values (entity_id, key, value, last_scored_at)
            SELECT id, 'stage3_enriched_at', '2026-01-01T00:00:00.0000000+00:00', CURRENT_TIMESTAMP FROM works;
            """);
    }

    /// <summary>A new root Universe whose Series each hold the listed works: one Series per work, or all in one.</summary>
    private string SeedUniverse(string qid, string name, (string MediaType, string Title)[] works, bool oneSeries = false)
    {
        var universe = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, wikidata_qid, parent_collection_id)
                VALUES (@universe, @name, 'Universe', 'library', 'materialized', 1, @qid, NULL);
                """,
                new { universe, name, qid });
        }

        Guid? shared = null;
        foreach (var (mediaType, title) in works)
        {
            var series = shared ?? Guid.NewGuid();
            if (shared is null)
            {
                using var connection = _database.CreateConnection();
                connection.Execute(
                    """
                    INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, parent_collection_id)
                    VALUES (@series, @title, 'Series', 'library', 'query', 1, @universe);
                    """,
                    new { series, title = $"{title} Series", universe });
                if (oneSeries)
                {
                    shared = series;
                }
            }

            AddWork(mediaType, title, "G", series, mediaType == "Books" ? ".epub" : ".mkv");
        }

        return qid;
    }

    private static LibraryPreferencesSettings BooksShowMissing(string defaultVisibility, bool enabled = true) => new()
    {
        MissingItemDisplay = new Dictionary<string, MissingItemDisplayPolicy>(StringComparer.OrdinalIgnoreCase)
        {
            ["books"] = new MissingItemDisplayPolicy
            {
                Enabled = enabled,
                DefaultVisibility = defaultVisibility,
                Presentation = "all",
                PageSize = 50,
                DetailHydration = "owned_only",
            },
        },
    };

    private static JsonNode BetaMissing(JsonObject overview)
    {
        var missing = overview["missing"]!.AsArray();
        return Assert.Single(missing)!;
    }

    /// <summary>The library preferences file, as the Engine reads it; everything else about configuration is empty.</summary>
    private sealed class LibraryPreferencesLoader(Func<LibraryPreferencesSettings?> preferences) : IConfigurationLoader
    {
        public string ConfigDirectoryPath => string.Empty;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public LibrariesConfiguration LoadLibraries() => new();
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];

        public T? LoadConfig<T>(string subdirectory, string name) where T : class =>
            typeof(T) == typeof(LibraryPreferencesSettings) && subdirectory == "ui" && name == "library-preferences"
                ? preferences() as T
                : null;

        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
