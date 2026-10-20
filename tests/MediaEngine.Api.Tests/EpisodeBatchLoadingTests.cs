using System.Reflection;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The TV show details page used to read every episode with about nine statements each. These tests pin that the
/// batched readers return exactly what the per-episode readers return, and that the statement count stays flat.
/// </summary>
public sealed class EpisodeBatchLoadingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"episode_batch_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly Guid _provider = Guid.NewGuid();

    public EpisodeBatchLoadingTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_path);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO metadata_providers (id, name, version, is_enabled) VALUES (@provider, 'batch-test', '1', 1);",
            new { provider = _provider });
    }

    [Fact]
    public async Task BatchedCanonicalMapsMatchThePerEpisodeReader()
    {
        var episodes = SeedEpisodes(50);
        var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);

        var batched = await composer.LoadWorkAndAssetCanonicalMapsAsync(episodes, CancellationToken.None);

        Assert.Equal(episodes.Count, batched.Count);
        foreach (var episode in episodes)
        {
            var expected = await composer.LoadWorkAndAssetCanonicalMapAsync(episode, CancellationToken.None);
            Assert.Equal(Normalize(expected), Normalize(batched[episode]));
        }

        // The seed must actually reach the interesting branches, or the comparison above proves little.
        Assert.Contains(batched.Values, map => map.ContainsKey("edition_subtitle"));
        Assert.Contains(batched.Values, map => map.TryGetValue("genre", out var genre) && genre.Contains('|'));
        Assert.Contains(batched.Values, map => map.ContainsKey("duration_sec"));
        Assert.DoesNotContain(batched.Values, map => map.ContainsKey("orphaned_only"));
    }

    [Fact]
    public async Task AirDateOnlyReadMatchesTheFullReadForThoseKeys()
    {
        var episodes = SeedEpisodes(60);
        var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);
        string[] keys = ["air_date", "release_date"];

        var restricted = await composer.LoadWorkAndAssetCanonicalMapsAsync(episodes, CancellationToken.None, keys);
        var full = await composer.LoadWorkAndAssetCanonicalMapsAsync(episodes, CancellationToken.None);

        Assert.Equal(episodes.Count, restricted.Count);
        foreach (var episode in episodes)
        {
            Assert.Equal(full[episode].GetValueOrDefault("air_date"), restricted[episode].GetValueOrDefault("air_date"));
            Assert.Equal(full[episode].GetValueOrDefault("release_date"), restricted[episode].GetValueOrDefault("release_date"));
            Assert.All(restricted[episode].Keys, key => Assert.Contains(key, keys, StringComparer.OrdinalIgnoreCase));
        }

        // Work date only, asset date only, work date shadowing an asset date, and release_date.
        Assert.Contains(restricted.Values, map => map.GetValueOrDefault("air_date") == "2019-05-05");
        Assert.Contains(restricted.Values, map => map.GetValueOrDefault("air_date")?.StartsWith("2020-", StringComparison.Ordinal) == true);
        Assert.Contains(restricted.Values, map => map.GetValueOrDefault("release_date") == "2017-03-03");
        Assert.DoesNotContain(restricted.Values, map => map.GetValueOrDefault("air_date") == "2018-04-04");
    }

    [Fact]
    public async Task AirDateOnlyReadStaysFlatForAThousandEpisodes()
    {
        var episodes = SeedEpisodes(1000);
        var counting = new StatementCountingDatabase(_db);
        var composer = new DetailCompositionOrchestrator(counting, null!, null!, null!, null!, null!, null!, null!);

        counting.Reset();
        await composer.LoadWorkAndAssetCanonicalMapsAsync(episodes, CancellationToken.None, ["air_date", "release_date"]);

        Assert.InRange(counting.Statements, 1, 20);
    }

    [Fact]
    public async Task SubsetEnrichmentReadsOnlyTheRequestedEpisodesAndKeepsTheRest()
    {
        var show = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO works(id,media_type,work_kind,curator_state) VALUES (@show,'TV','parent','accepted');",
                new { show });
        }

        var episodes = SeedEpisodes(30, show);
        var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);
        var orchestrator = typeof(DetailCompositionOrchestrator);
        var nonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
        var works = await LoadShowWorksAsync(composer, show);
        var chosen = episodes.Skip(4).Take(5).ToList();
        var enriched = new HashSet<Guid>();

        var outcome = await InvokeSubsetAsync(composer, works, chosen, enriched);

        Assert.Equal(chosen.ToHashSet(), enriched);
        Assert.Equal(chosen.ToHashSet(), outcome.ValueMaps.Keys.ToHashSet());
        Assert.Equal(episodes.Count, outcome.Works.Count);
        var before = works.ToDictionary(work => Read<string>(work, "Id"));
        foreach (var work in outcome.Works)
        {
            var id = Read<string>(work, "Id");
            var was = before[id];
            if (chosen.Contains(Guid.Parse(id)))
            {
                var legacy = await composer.LoadWorkAndAssetCanonicalMapAsync(Guid.Parse(id), CancellationToken.None);
                Assert.Equal(legacy.GetValueOrDefault("episode_still_url"), Read<string?>(work, "BackgroundUrl"));
            }
            else
            {
                Assert.Equal(Read<string?>(was, "Description"), Read<string?>(work, "Description"));
                Assert.Equal(Read<string?>(was, "BackgroundUrl"), Read<string?>(work, "BackgroundUrl"));
            }

            // Subset enrichment never touches the air date the first pass set.
            Assert.Equal(Read<string?>(was, "Year"), Read<string?>(work, "Year"));
        }

        // Asking again for episodes that were already read costs nothing and changes nothing.
        var counting = new StatementCountingDatabase(_db);
        var countingComposer = new DetailCompositionOrchestrator(counting, null!, null!, null!, null!, null!, null!, null!);
        counting.Reset();
        var again = await InvokeSubsetAsync(countingComposer, outcome.Works, chosen, enriched);
        Assert.Equal(0, counting.Statements);
        Assert.Empty(again.ValueMaps);
        Assert.Equal(outcome.Works.Count, again.Works.Count);
        Assert.All(outcome.Works.Zip(again.Works), pair => Assert.Same(pair.First, pair.Second));
        Assert.NotNull(orchestrator.GetMethod("EnrichTvEpisodeSubsetAsync", nonPublic));
    }

    [Fact]
    public async Task LongShowCarriesOneSeasonWindowWhileEveryOtherPathStillGetsTheWholeList()
    {
        var show = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO works(id,media_type,work_kind,curator_state) VALUES (@show,'TV','parent','accepted');",
                new { show });
        }

        // 130 episodes: 70 in season 1, 60 in season 2.
        var episodes = SeedEpisodes(130, show, index => index < 70 ? "1" : "2");
        var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);
        var works = (await LoadShowWorksAsync(composer, show)).Select(Own).ToList();
        Assert.Equal(130, works.Count);

        SequencePlacementViewModel Place(IEnumerable<object> source, SequenceWindowRequest? window, Guid? current = null)
        {
            var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(works[0].GetType()))!;
            foreach (var work in source)
            {
                ((System.Collections.IList)list).Add(work);
            }

            return (SequencePlacementViewModel)typeof(DetailCompositionOrchestrator)
                .GetMethod("BuildCollectionSequencePlacement", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [show, DetailEntityType.TvShow, "Show", null, null, list, null, null, null, current, show, null, window])!;
        }

        // Default window: every season is listed with its count; only season 1's first 50 episodes are carried, once.
        var first = Place(works, new SequenceWindowRequest());
        Assert.Equal(["season-1", "season-2"], first.Groups.Select(group => group.Key));
        Assert.Equal([70, 60], first.Groups.Select(group => group.ItemCount));
        Assert.Equal(50, first.Groups[0].Items.Count);
        Assert.Equal(0, first.Groups[0].ItemOffset);
        Assert.Empty(first.Groups[1].Items);
        Assert.Empty(first.OrderedItems);
        Assert.Equal(130, first.TotalKnownItems);
        Assert.Equal(Enumerable.Range(0, 70).Count(), first.Groups[0].OwnedCount);
        Assert.False(string.IsNullOrWhiteSpace(first.CurrentItem.Id));

        // The window that holds the episode being watched.
        var watching = episodes[64];
        var around = Place(works, new SequenceWindowRequest(), watching);
        Assert.Equal(50, around.Groups[0].ItemOffset);
        Assert.Equal(20, around.Groups[0].Items.Count);
        Assert.Contains(around.Groups[0].Items, item => item.IsCurrent && item.Id == watching.ToString("D"));
        Assert.Equal(watching.ToString("D"), around.CurrentItem.Id);

        // A season asked for by name returns exactly that window, even when it is short enough to send whole.
        var second = Place(works, new SequenceWindowRequest("season-2", 50, 50, null, true));
        Assert.Equal(50, second.Groups[1].ItemOffset);
        Assert.Equal(10, second.Groups[1].Items.Count);
        Assert.Empty(second.Groups[0].Items);

        // The whole list, for playback queues, crosses season boundaries in order.
        var all = Place(works, SequenceWindowRequest.All());
        Assert.Equal(130, all.OrderedItems.Count);
        Assert.Equal([70, 60], all.Groups.Select(group => group.Items.Count));
        Assert.Equal(all.OrderedItems.Select(item => item.Id), all.Groups.SelectMany(group => group.Items).Select(item => item.Id));

        // An unknown season key answers with the season being watched, never an empty list.
        var unknown = Place(works, new SequenceWindowRequest("season-9", 0, 50, watching, true), watching);
        Assert.Equal(20, unknown.Groups[0].Items.Count);
        Assert.Equal(50, unknown.Groups[0].ItemOffset);

        // An offset past the end lands on the last full window, not an empty one.
        var pastEnd = Place(works, new SequenceWindowRequest("season-2", 5000, 50, null, true));
        Assert.Equal(50, pastEnd.Groups[1].ItemOffset);
        Assert.Equal(10, pastEnd.Groups[1].Items.Count);

        // No window at all (a client that sends no sequence parameter): the whole list, as before.
        var legacy = Place(works, null);
        Assert.Equal(130, legacy.OrderedItems.Count);
        Assert.Equal([70, 60], legacy.Groups.Select(group => group.Items.Count));

        // Up to 100 episodes in total nothing changes: every season is carried whole.
        var short90 = Place(works.Take(90), new SequenceWindowRequest());
        Assert.Equal(90, short90.OrderedItems.Count);
        Assert.Equal([70, 20], short90.Groups.Select(group => group.Items.Count));
        Assert.Equal([70, 20], short90.Groups.Select(group => group.ItemCount));
    }

    [Fact]
    public void SequenceWindowRequestReadsTheQueryString()
    {
        Assert.Null(SequenceWindowRequest.FromQuery(null, null, null, null, null));
        Assert.Null(SequenceWindowRequest.FromQuery(" ", null, null, "", false));

        // Without any sequence parameter the Engine keeps sending the whole list (older clients).
        Assert.True(SequenceWindowRequest.All().IsAll);

        var page = SequenceWindowRequest.FromQuery("season-2", 50, 50, null, true)!;
        Assert.Equal(("season-2", 50, 50, true), (page.GroupKey, page.Offset, page.Limit, page.PlacementOnly));
        Assert.True(page.IsExplicit);
        Assert.False(page.IsAll);

        var current = Guid.NewGuid();
        var whole = SequenceWindowRequest.FromQuery(null, null, 0, current.ToString("D"), true)!;
        Assert.True(whole.IsAll);
        Assert.Equal(current, whole.CurrentWorkId);

        Assert.Equal(SequenceWindowRequest.MaxLimit, SequenceWindowRequest.FromQuery("s", 0, 5000, null, null)!.EffectiveLimit);
        Assert.Equal(0, SequenceWindowRequest.FromQuery("s", -4, 10, null, null)!.Offset);
        Assert.Equal(0, SequenceWindowRequest.FromQuery(null, null, null, current.ToString("D"), null)!.Limit);
        Assert.Equal(SequenceWindowRequest.DefaultLimit, SequenceWindowRequest.FromQuery("season-2", null, null, null, null)!.Limit);
    }

    [Fact]
    public async Task BatchedCanonicalMapsGiveUnknownAndEmptyRequestsAnEmptyResult()
    {
        var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);
        Assert.Empty(await composer.LoadWorkAndAssetCanonicalMapsAsync([], CancellationToken.None));

        var unknown = Guid.NewGuid();
        var result = await composer.LoadWorkAndAssetCanonicalMapsAsync([unknown, unknown], CancellationToken.None);
        Assert.Empty(Assert.Single(result).Value);
    }

    [Fact]
    public async Task BatchedDisplayOverridesMatchThePerEpisodeReader()
    {
        var episodes = SeedEpisodes(50);
        var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);

        var batched = await composer.LoadWorkDisplayOverridesBatchAsync(episodes, CancellationToken.None);

        Assert.Equal(episodes.Count, batched.Count);
        foreach (var episode in episodes)
        {
            var expected = await composer.LoadWorkDisplayOverridesAsync(episode, CancellationToken.None);
            Assert.Equal(Normalize(expected), Normalize(batched[episode]));
        }

        Assert.Contains(batched.Values, overrides => overrides.ContainsKey("description"));
    }

    [Fact]
    public async Task TvShowEpisodesGetTheSameDescriptionStillDateAndPlaybackPillsAsTheOldPerEpisodeLoop()
    {
        var show = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO works(id,media_type,work_kind,curator_state) VALUES (@show,'TV','parent','accepted');",
                new { show });
            InsertValue(connection, null, show, "cover_url", "https://img/show.jpg");
        }

        var episodes = SeedEpisodes(30, show);
        var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);
        var orchestrator = typeof(DetailCompositionOrchestrator);
        var nonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

        // The same call the show details page makes to list its episodes.
        var load = (Task)orchestrator.GetMethod("LoadCollectionWorksAsync", nonPublic)!
            .Invoke(composer, [Guid.Empty, show, CancellationToken.None, null, Profile.SeedProfileId, null])!;
        await load;
        var works = load.GetType().GetProperty("Result")!.GetValue(load)!;
        Assert.Equal(episodes.Count, ((System.Collections.IEnumerable)works).Cast<object>().Count());

        var stillRow = orchestrator.GetNestedType("SequenceRow", BindingFlags.NonPublic)!;
        var noStills = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid), stillRow))!;
        var enrich = (Task)orchestrator.GetMethod("EnrichTvEpisodesAsync", nonPublic)!
            .Invoke(composer, [works, noStills, CancellationToken.None])!;
        await enrich;
        var outcome = enrich.GetType().GetProperty("Result")!.GetValue(enrich)!;
        var enriched = ((System.Collections.IEnumerable)outcome.GetType().GetField("Item1")!.GetValue(outcome)!).Cast<object>().ToList();
        var valueMaps = (Dictionary<Guid, Dictionary<string, string>>)outcome.GetType().GetField("Item2")!.GetValue(outcome)!;
        Assert.Equal(episodes.Count, enriched.Count);

        static T Read<T>(object summary, string property) => (T)summary.GetType().GetProperty(property)!.GetValue(summary)!;
        foreach (var episode in enriched)
        {
            var id = Guid.Parse(Read<string>(episode, "Id"));
            var legacyValues = await composer.LoadWorkAndAssetCanonicalMapAsync(id, CancellationToken.None);
            var legacyOverrides = await composer.LoadWorkDisplayOverridesAsync(id, CancellationToken.None);
            var expectedDescription = legacyOverrides.TryGetValue("description", out var overridden) && !string.IsNullOrWhiteSpace(overridden)
                ? overridden.Trim()
                : legacyValues.GetValueOrDefault("episode_description");
            Assert.Equal(expectedDescription, Read<string?>(episode, "Description"));
            Assert.Equal(legacyValues.GetValueOrDefault("episode_still_url"), Read<string?>(episode, "BackgroundUrl"));
            Assert.Equal(legacyValues.GetValueOrDefault("air_date") ?? legacyValues.GetValueOrDefault("release_date"), Read<string?>(episode, "Year"));
            Assert.Equal(Normalize(legacyValues), Normalize(valueMaps[id]));
        }

        Assert.Contains(enriched, episode => Read<string?>(episode, "Description")?.StartsWith("Override", StringComparison.Ordinal) == true);
        Assert.Contains(enriched, episode => Read<string?>(episode, "Year") is not null);

        // The playback pills read the playback episode's own map, which now comes from the batch.
        var pills = orchestrator.GetMethod("BuildCollectionMetadata", BindingFlags.Static | BindingFlags.NonPublic)!;
        IReadOnlyList<MetadataPill> PillsFor(object playbackEpisode, IReadOnlyDictionary<string, string> playbackValues) =>
            (IReadOnlyList<MetadataPill>)pills.Invoke(null,
                [DetailEntityType.TvShow, outcome.GetType().GetField("Item1")!.GetValue(outcome), new Dictionary<string, string>(), playbackEpisode, playbackValues])!;
        var playback = enriched.First(episode => Guid.Parse(Read<string>(episode, "Id")) == episodes[3]);
        var fromBatch = PillsFor(playback, valueMaps[episodes[3]]);
        var fromLegacy = PillsFor(playback, await composer.LoadWorkAndAssetCanonicalMapAsync(episodes[3], CancellationToken.None));
        Assert.NotEmpty(fromBatch);
        Assert.Equal(JsonSerializer.Serialize(fromLegacy), JsonSerializer.Serialize(fromBatch));
    }

    [Fact]
    public async Task ReadingAThousandEpisodesUsesAFlatNumberOfStatements()
    {
        var episodes = SeedEpisodes(1000);
        var counting = new StatementCountingDatabase(_db);
        var composer = new DetailCompositionOrchestrator(counting, null!, null!, null!, null!, null!, null!, null!);

        counting.Reset();
        await composer.LoadWorkAndAssetCanonicalMapsAsync(episodes, CancellationToken.None);
        await composer.LoadWorkDisplayOverridesBatchAsync(episodes, CancellationToken.None);

        // Was about 9,000 (nine per episode). Chunked IN lists keep it to a couple of dozen.
        Assert.InRange(counting.Statements, 1, 40);
    }

    /// <summary>
    /// Seeds standalone TV episodes with deliberately uneven data: extra editions and assets, orphaned assets,
    /// conflicting keys between asset and work, packed and multi-valued genres, blank subtitles, bad override JSON.
    /// </summary>
    private List<Guid> SeedEpisodes(int count, Guid? show = null, Func<int, string>? season = null)
    {
        var ids = new List<Guid>(count);
        using var connection = _db.CreateConnection();
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            var work = Guid.NewGuid();
            ids.Add(work);
            var overrides = i % 5 == 0 ? $"{{\"description\":\"Override {i}\"}}" : i % 7 == 0 ? "{broken" : null;
            connection.Execute(
                "INSERT INTO works(id,media_type,work_kind,parent_work_id,curator_state,display_overrides_json) VALUES (@work,'TV',@kind,@show,'accepted',@overrides);",
                new { work, overrides, show, kind = show is null ? "standalone" : "child" }, transaction);
            if (show is not null)
            {
                InsertValue(connection, transaction, work, "cover_url", $"https://img/cover-{i}.jpg");
            }

            var firstEdition = Guid.NewGuid();
            var firstAsset = Guid.NewGuid();
            connection.Execute("INSERT INTO editions(id,work_id) VALUES (@firstEdition,@work);", new { firstEdition, work }, transaction);
            InsertAsset(connection, transaction, firstAsset, firstEdition, "Normal", $"first-{work:N}");
            InsertValue(connection, transaction, firstAsset, "runtime", $"{40 + i % 20}");
            InsertValue(connection, transaction, firstAsset, "title", $"Asset title {i}");
            InsertValue(connection, transaction, firstAsset, "quality", i % 2 == 0 ? "1080p" : "720p");

            InsertValue(connection, transaction, work, "title", $"Work title {i}");
            InsertValue(connection, transaction, work, "episode_number", $"{i + 1}");
            if (season is not null)
            {
                InsertValue(connection, transaction, work, "season_number", season(i));
            }
            InsertValue(connection, transaction, work, "episode_description", $"Episode {i}");
            if (i % 2 == 0)
            {
                InsertValue(connection, transaction, work, "air_date", $"2020-01-{i % 28 + 1:00}");
            }

            if (i % 5 == 0)
            {
                InsertValue(connection, transaction, work, "episode_still_url", $"https://img/{i}.jpg");
            }

            if (i % 10 == 3)
            {
                // An air date that only the asset knows (the work has none).
                InsertValue(connection, transaction, firstAsset, "air_date", "2019-05-05");
            }

            if (i % 14 == 4)
            {
                // An asset air date the work's own air_date (even i) must override.
                InsertValue(connection, transaction, firstAsset, "air_date", "2018-04-04");
            }

            if (i % 6 == 1)
            {
                InsertValue(connection, transaction, work, "release_date", "2017-03-03");
            }

            if (i % 4 == 0)
            {
                // A second edition with its own assets, one of which shares a key with the first asset.
                var secondEdition = Guid.NewGuid();
                var secondAsset = Guid.NewGuid();
                connection.Execute("INSERT INTO editions(id,work_id) VALUES (@secondEdition,@work);", new { secondEdition, work }, transaction);
                InsertAsset(connection, transaction, secondAsset, secondEdition, "Normal", $"second-{work:N}");
                InsertValue(connection, transaction, secondAsset, "title", $"Other asset title {i}");
                InsertValue(connection, transaction, secondAsset, "resolution", "1920x1080");
                if (i % 12 == 0)
                {
                    InsertValue(connection, transaction, firstEdition, "subtitle", $"First edition subtitle {i}");
                    InsertValue(connection, transaction, secondEdition, "subtitle", $"Second edition subtitle {i}");
                }
            }
            else if (i % 3 == 0)
            {
                InsertValue(connection, transaction, firstEdition, "subtitle", i % 6 == 0 ? string.Empty : $"Subtitle {i}");
            }

            if (i % 8 == 2)
            {
                // Two Normal assets in one edition; the one with the lower id wins shared keys.
                var sibling = Guid.NewGuid();
                InsertAsset(connection, transaction, sibling, firstEdition, "Normal", $"sibling-{work:N}");
                InsertValue(connection, transaction, sibling, "title", $"Sibling asset title {i}");
                InsertValue(connection, transaction, sibling, "audio_codec", "aac");
            }

            if (i % 9 == 0)
            {
                var orphaned = Guid.NewGuid();
                InsertAsset(connection, transaction, orphaned, firstEdition, "Orphaned", $"orphan-{work:N}");
                InsertValue(connection, transaction, orphaned, "orphaned_only", "should never appear");
            }

            if (i % 3 == 0)
            {
                InsertArray(connection, transaction, work, "genre", 0, "Drama");
                InsertArray(connection, transaction, work, "genre", 1, "Comedy");
                InsertArray(connection, transaction, firstAsset, "director", 0, "Someone");
                InsertArray(connection, transaction, firstAsset, "director", 1, string.Empty);
            }

            if (i % 2 == 1)
            {
                InsertClaim(connection, transaction, firstAsset, "duration_sec", $"{2400 + i}", 0.9, "2026-01-02");
                InsertClaim(connection, transaction, firstAsset, "duration_sec", $"{1200 + i}", 0.5, "2026-01-03");
                InsertClaim(connection, transaction, firstAsset, "genre", "Drama|Thriller", 0.8, "2026-01-02");
            }

            if (i % 6 == 1)
            {
                // Tied on confidence and date: the claim id breaks the tie the same way on both paths.
                InsertClaim(connection, transaction, firstAsset, "duration_seconds", "111", 0.5, "2026-01-06");
                InsertClaim(connection, transaction, firstAsset, "duration_seconds", "222", 0.5, "2026-01-06");
                InsertClaim(connection, transaction, work, "genre", "Mystery", 0.4, "2026-01-07");
                InsertClaim(connection, transaction, work, "genre", "Horror", 0.4, "2026-01-07");
            }

            if (i % 4 == 1)
            {
                InsertClaim(connection, transaction, work, "genre", "Comedy|Action", 0.7, "2026-01-04");
                InsertClaim(connection, transaction, work, "duration_seconds", $"{3000 + i}", 0.6, "2026-01-05");
            }
        }

        transaction.Commit();
        return ids;
    }

    private static void InsertAsset(SqliteConnection connection, SqliteTransaction transaction, Guid asset, Guid edition, string status, string hash) =>
        connection.Execute(
            "INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,status) VALUES (@asset,@edition,@hash,@path,@status);",
            new { asset, edition, hash, path = $"C:/qa/{hash}.mkv", status }, transaction);

    private static void InsertValue(SqliteConnection connection, SqliteTransaction? transaction, Guid entity, string key, string value) =>
        connection.Execute(
            "INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES (@entity,@key,@value,'2026-01-01');",
            new { entity, key, value }, transaction);

    private static void InsertArray(SqliteConnection connection, SqliteTransaction transaction, Guid entity, string key, int ordinal, string value) =>
        connection.Execute(
            "INSERT INTO canonical_value_arrays(entity_id,key,ordinal,value) VALUES (@entity,@key,@ordinal,@value);",
            new { entity, key, ordinal, value }, transaction);

    private void InsertClaim(SqliteConnection connection, SqliteTransaction transaction, Guid entity, string key, string value, double confidence, string claimedAt) =>
        connection.Execute(
            "INSERT INTO metadata_claims(id,entity_id,provider_id,claim_key,claim_value,confidence,claimed_at) VALUES (@id,@entity,@provider,@key,@value,@confidence,@claimedAt);",
            new { id = Guid.NewGuid(), entity, provider = _provider, key, value, confidence, claimedAt }, transaction);

    private static T Read<T>(object summary, string property) => (T)summary.GetType().GetProperty(property)!.GetValue(summary)!;

    private static async Task<IReadOnlyList<object>> LoadShowWorksAsync(DetailCompositionOrchestrator composer, Guid show)
    {
        var load = (Task)typeof(DetailCompositionOrchestrator).GetMethod("LoadCollectionWorksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(composer, [Guid.Empty, show, CancellationToken.None, null, Profile.SeedProfileId, null])!;
        await load;
        var works = load.GetType().GetProperty("Result")!.GetValue(load)!;
        return ((System.Collections.IEnumerable)works).Cast<object>().ToList();
    }

    /// <summary>The seeded works have no owned-asset link row; mark them owned the way the reader would.</summary>
    private static object Own(object work)
    {
        var clone = work.GetType().GetMethod("<Clone>$")!.Invoke(work, null)!;
        clone.GetType().GetProperty("HasAsset")!.SetValue(clone, true);
        return clone;
    }

    private static async Task<(IReadOnlyList<object> Works, Dictionary<Guid, Dictionary<string, string>> ValueMaps)> InvokeSubsetAsync(
        DetailCompositionOrchestrator composer,
        IReadOnlyList<object> works,
        IReadOnlyCollection<Guid> ids,
        HashSet<Guid> alreadyEnriched)
    {
        var summaryType = works[0].GetType();
        var typed = Activator.CreateInstance(typeof(List<>).MakeGenericType(summaryType))!;
        foreach (var work in works)
        {
            ((System.Collections.IList)typed).Add(work);
        }

        var task = (Task)typeof(DetailCompositionOrchestrator).GetMethod("EnrichTvEpisodeSubsetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(composer, [typed, ids, alreadyEnriched, CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var merged = result.GetType().GetField("Item1")!.GetValue(result)!;
        var maps = (Dictionary<Guid, Dictionary<string, string>>)result.GetType().GetField("Item2")!.GetValue(result)!;
        return (new ReferenceTrackingList(merged), maps);
    }

    /// <summary>Wraps the typed list the Engine returned so reference identity can be asserted.</summary>
    private sealed class ReferenceTrackingList : IReadOnlyList<object>
    {
        public ReferenceTrackingList(object inner) => Inner = inner;

        public object Inner { get; }

        private System.Collections.IList List => (System.Collections.IList)Inner;

        public int Count => List.Count;

        public object this[int index] => List[index]!;

        public IEnumerator<object> GetEnumerator() => List.Cast<object>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static string[] Normalize(IReadOnlyDictionary<string, string> map) =>
        map.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}").ToArray();

    public void Dispose()
    {
        _db.Dispose();
        // Only this fixture's pool is released; other concurrently running
        // SQLite tests retain their own connections and pools.
        using var fixturePool = new SqliteConnection($"Data Source={_path}");
        SqliteConnection.ClearPool(fixturePool);
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    /// <summary>Counts every statement SQLite starts on connections handed out after construction.</summary>
    private sealed class StatementCountingDatabase(IDatabaseConnection inner) : IDatabaseConnection
    {
        private int _statements;

        public int Statements => Volatile.Read(ref _statements);

        public void Reset() => Volatile.Write(ref _statements, 0);

        public SqliteConnection CreateConnection()
        {
            var connection = inner.CreateConnection();
            raw.sqlite3_trace(connection.Handle!, (object _, string _) => Interlocked.Increment(ref _statements), null);
            return connection;
        }

        public SqliteConnection Open() => inner.Open();

        public Task<T> ExecuteReadAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default) =>
            inner.ExecuteReadAsync(body, ct);

        public void InitializeSchema() => inner.InitializeSchema();

        public void RunStartupChecks() => inner.RunStartupChecks();

        public Task AcquireWriteLockAsync(CancellationToken ct = default, string holderFile = "", string holderMember = "") => inner.AcquireWriteLockAsync(ct, holderFile, holderMember);

        public void ReleaseWriteLock() => inner.ReleaseWriteLock();
        public string? DescribeWriteLockHolder() => inner.DescribeWriteLockHolder();

        public Task<T> ExecuteWriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default, string holderFile = "", string holderMember = "") =>
            inner.ExecuteWriteAsync(body, ct, holderFile, holderMember);

        public Task ExecuteWriteAsync(Action<SqliteConnection, SqliteTransaction, CancellationToken> body, CancellationToken ct = default, string holderFile = "", string holderMember = "") =>
            inner.ExecuteWriteAsync(body, ct, holderFile, holderMember);

        public void Dispose()
        {
        }
    }
}
