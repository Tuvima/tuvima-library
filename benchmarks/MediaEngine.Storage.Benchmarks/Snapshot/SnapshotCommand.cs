using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Storage.Benchmarks.Snapshot;

/// <summary>
/// <c>snapshot --db &lt;path&gt; --out &lt;file&gt;</c>: runs the production Home, detail, recent, TV skeleton and library reads over
/// the seeded file and writes every field of every row to one text file (spec "Home and item detail speed", D6). Only
/// production code is called. The file has one <c>## name</c> section per read, one line per row, and ends with
/// <c>sha256=&lt;hex&gt;</c> of everything above it. Run it before the first change and after each wave, then <c>compare</c>.
/// Row order inside a group with the same <c>CreatedAt</c> is normalised (D7); see <see cref="SnapshotRows"/>.
/// Sections for the seven reads of spec "Audiobook year on the remaining screens" (series page, Continue and play history,
/// collection editor details and "add media", series search, a person's credits, taste profile) were added on 2026-10-20.
/// </summary>
internal static class SnapshotCommand
{
    private const string Newline = "\n";

    public static async Task<int> RunAsync(string databasePath, string outPath, string? only)
    {
        var filters = (only ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Wanted(string name) => filters.Length == 0 || filters.Any(filter =>
            name.Equals(filter, StringComparison.OrdinalIgnoreCase) || name.StartsWith(filter, StringComparison.OrdinalIgnoreCase));

        using var context = BenchContext.Open(databasePath);
        var samples = SnapshotSamples.Choose(context);
        var reader = new DisplayWorkProjectionReader(context.Database);
        var library = new LibraryItemRepository(context.Database);
        var ct = CancellationToken.None;

        var text = new StringBuilder();
        var failed = false;

        async Task Section(string name, string headerSuffix, Func<Task<IReadOnlyList<string>>> lines)
        {
            if (!Wanted(name))
            {
                return;
            }

            var timer = Stopwatch.StartNew();
            var rows = await lines();
            Console.WriteLine($"{name,-34} {timer.Elapsed.TotalMilliseconds,10:N0} ms  {rows.Count,7:N0} lines");
            text.Append("## ").Append(name).Append(headerSuffix).Append(Newline);
            foreach (var line in rows)
            {
                text.Append(line).Append(Newline);
            }
        }

        Task<IReadOnlyList<string>> Empty() => Task.FromResult<IReadOnlyList<string>>([]);
        string IdSuffix(Guid id) => " id=" + id.ToString("N", CultureInfo.InvariantCulture);
        IReadOnlyList<string> InOrder<T>(IEnumerable<T> rows) where T : notnull => SnapshotRows.InReaderOrder(rows.Cast<object>());

        await Section("home", string.Empty, async () => InOrder(await reader.LoadAsync(ct, Workload.HomeLimit)));
        await Section("all-works", string.Empty, async () => SnapshotRows.Sorted((await reader.LoadAsync(ct)).Cast<object>()));

        var recentGroups = new DisplayRecentGroupFilter([.. samples.TvShowRoots, .. samples.AlbumRoots], samples.StandaloneWorks);
        await Section("recent", string.Empty, async () => InOrder(await reader.LoadAsync(ct, groups: recentGroups, lean: true)));

        foreach (var (label, id) in samples.Works)
        {
            var sectionName = "detail:" + label;
            if (id is not { } workId)
            {
                failed |= Missing(sectionName);
                await Section(sectionName, " (no sample found)", Empty);
                continue;
            }

            await Section(sectionName, IdSuffix(workId), async () => InOrder(await reader.LoadAsync(ct, detailId: workId)));
        }

        if (samples.Person is { } personId)
        {
            await Section("detail:person", IdSuffix(personId), async () => InOrder(await reader.LoadAsync(ct, detailId: personId)));
        }
        else
        {
            failed |= Missing("detail:person");
            await Section("detail:person", " (no sample found)", Empty);
        }

        var skeletonRoots = samples.TvShowRoots;
        await Section("tv-skeleton", string.Empty, async () => InOrder(await LoadTvSkeletonAsync(reader, skeletonRoots, ct)));

        await Section("library-list", string.Empty, async () =>
        {
            var page = await library.GetPageAsync(new LibraryItemQuery(Offset: 0, Limit: 5000, IncludeAll: true), ct);
            var lines = new List<string> { "total=" + page.TotalCount.ToString(CultureInfo.InvariantCulture) };
            lines.AddRange(InOrder(page.Items));
            return lines;
        });

        foreach (var label in SnapshotSamples.LibraryDetailLabels)
        {
            var sectionName = "library-detail:" + label;
            var id = samples.Works.FirstOrDefault(work => work.Label == label).Id;
            if (id is not { } workId)
            {
                failed |= Missing(sectionName);
                await Section(sectionName, " (no sample found)", Empty);
                continue;
            }

            await Section(sectionName, IdSuffix(workId), async () =>
            {
                var detail = await library.GetDetailAsync(workId, ct);
                return detail is null ? [RowFormatter.Null] : [RowFormatter.Format(detail).Line];
            });
        }

        // Sections added 2026-10-20 (spec "Audiobook year on the remaining screens", WP1). Each calls one production read.
        foreach (var label in SeriesPageLabels)
        {
            var sectionName = "series-page:" + label;
            var page = samples.SeriesPages.FirstOrDefault(sample => sample.Label == label);
            if (page is null)
            {
                failed |= Missing(sectionName);
                await Section(sectionName, " (no sample found)", Empty);
                continue;
            }

            var header = " collection=" + page.CollectionId.ToString("N", CultureInfo.InvariantCulture)
                + (page.RootWorkId is { } root ? " root=" + root.ToString("N", CultureInfo.InvariantCulture) : string.Empty)
                + " (" + page.Note + ")";
            await Section(sectionName, header, async () => InOrder(await LoadSeriesPageAsync(context.Database, page, ct)));
        }

        if (samples.PlayProfile is { } profileId)
        {
            var journeys = new DisplayJourneyProjectionReader(context.Database);
            var profileSuffix = " profile=" + profileId.ToString("N", CultureInfo.InvariantCulture);
            await Section("continue", profileSuffix, async () => InOrder(await journeys.LoadAsync(profileId, null, ct)));
            await Section("states", profileSuffix, async () => InOrder(await journeys.LoadAsync(profileId, null, ct, includeCompleted: true)));
        }
        else
        {
            foreach (var sectionName in new[] { "continue", "states" })
            {
                failed |= Missing(sectionName);
                await Section(sectionName, " (no sample found)", Empty);
            }
        }

        var lookup = new CollectionMediaLookupReadService(context.Database);
        if (samples.ResolveWorkIds.Count > 0)
        {
            await Section(
                "collection-resolve",
                " works=" + samples.ResolveWorkIds.Count.ToString(CultureInfo.InvariantCulture),
                async () => InOrder(await lookup.ResolveMetadataAsync(samples.ResolveWorkIds, ct)));
        }
        else
        {
            failed |= Missing("collection-resolve");
            await Section("collection-resolve", " (no sample found)", Empty);
        }

        var allowedWorkIds = samples.AllWorkIds.ToHashSet();
        var noWorkIds = new HashSet<Guid>();
        foreach (var (label, mediaTypes, offset) in LookupPages)
        {
            await Section(
                "collection-lookup:" + label,
                " mediaTypes=" + (mediaTypes ?? "<none>") + " offset=" + offset.ToString(CultureInfo.InvariantCulture) + " limit=24",
                async () => InOrder(await lookup.LookupAsync(null, mediaTypes, noWorkIds, allowedWorkIds, offset, 24, ct)));
        }

        // Series search goes through the product's search index, which the seeder leaves empty; rebuild it first (the same
        // production call the Engine makes). While the section 7 bug of the spec is open this path returns no rows.
        var searchIndex = new SearchIndexRepository(context.Database);
        if (Wanted("series-search"))
        {
            await searchIndex.RebuildAsync(ct);
        }

        var searchService = new CollectionSearchReadService(context.Database, searchIndex);
        foreach (var (label, query) in samples.SearchQueries)
        {
            var sectionName = "series-search:" + label;
            if (query is null)
            {
                failed |= Missing(sectionName);
                await Section(sectionName, " (no sample found)", Empty);
                continue;
            }

            await Section(sectionName, " query=\"" + query + "\"", async () => InOrder(await searchService.SearchAsync(query, ct)));
        }

        var credits = new PersonCreditReadService(
            new CanonicalValueArrayRepository(context.Database),
            new PersonRepository(context.Database),
            context.Database);
        foreach (var (label, id) in samples.CreditPersons)
        {
            var sectionName = "person-credits:" + label;
            if (id is not { } personCreditId)
            {
                failed |= Missing(sectionName);
                await Section(sectionName, " (no sample found)", Empty);
                continue;
            }

            await Section(sectionName, IdSuffix(personCreditId), async () => InOrder(await credits.GetLibraryCreditsAsync(personCreditId, ct)));
        }

        // person-presence: how much of the library each sampled person is credited in, worked out for all of them in one call.
        var presencePeople = samples.CreditPersons.Where(sample => sample.Id.HasValue).Select(sample => sample.Id!.Value).ToList();
        if (presencePeople.Count > 0)
        {
            await Section(
                "person-presence",
                " people=" + presencePeople.Count.ToString(CultureInfo.InvariantCulture),
                async () =>
                {
                    var presence = await new PersonRepository(context.Database).GetPresenceBatchAsync(presencePeople, ct);
                    return SnapshotRows.Sorted(presence
                        .SelectMany(person => person.Value.Select(media => (object)new { Person = person.Key, MediaType = media.Key, media.Value })));
                });
        }
        else
        {
            failed |= Missing("person-presence");
            await Section("person-presence", " (no sample found)", Empty);
        }

        if (samples.PlayProfile is { } tasteProfileId)
        {
            var taste = new TasteProfileRepository(context.Database);
            await Section(
                "taste",
                " profile=" + tasteProfileId.ToString("N", CultureInfo.InvariantCulture),
                async () => InOrder(await taste.GetSignalsAsync(tasteProfileId, 1000, ct)));
        }
        else
        {
            failed |= Missing("taste");
            await Section("taste", " (no sample found)", Empty);
        }

        var body = text.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var outputPath = Path.GetFullPath(outPath);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(outputPath, body + "sha256=" + hash + Newline, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        Console.WriteLine($"wrote {outputPath} (sha256={hash})");
        if (failed)
        {
            Console.Error.WriteLine("Some samples were not found in this file (see warnings above); the snapshot still compares like with like.");
        }

        return 0;
    }

    private static readonly string[] SeriesPageLabels = ["audiobook-series", "book-series", "tv-season"];

    /// <summary>The collection editor's "add media" pages: label, media type filter and offset (24 rows per page).</summary>
    private static readonly (string Label, string? MediaTypes, int Offset)[] LookupPages =
    [
        ("audiobooks-p1", "Audiobooks", 0),
        ("audiobooks-p2", "Audiobooks", 24),
        ("books-p1", "Books", 0),
        ("all-p1", null, 0),
    ];

    /// <summary>
    /// <c>DetailCompositionOrchestrator</c> and its <c>LoadCollectionWorksAsync</c> are internal to the Api assembly; no visibility in
    /// src/ was changed. The method only uses the <c>_db</c> field, so the instance is created without running its constructor
    /// and <c>_db</c> is set. The result type is a private record; its public properties are formatted like any other row.
    /// </summary>
    private static async Task<IReadOnlyList<object>> LoadSeriesPageAsync(DatabaseConnection database, SeriesPageSample page, CancellationToken ct)
    {
        var type = typeof(DisplayWorkProjectionReader).Assembly.GetType(
                "MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator",
                throwOnError: false)
            ?? throw new InvalidOperationException("DetailCompositionOrchestrator was not found.");
        var orchestrator = RuntimeHelpers.GetUninitializedObject(type);
        (type.GetField("_db", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DetailCompositionOrchestrator._db was not found.")).SetValue(orchestrator, database);
        var method = type.GetMethod("LoadCollectionWorksAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DetailCompositionOrchestrator.LoadCollectionWorksAsync was not found.");
        var task = (Task)method.Invoke(orchestrator, [page.CollectionId, page.RootWorkId, ct, page.ResolvedWorkIds, null, null])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return [.. ((IEnumerable)result).Cast<object>()];
    }

    private static bool Missing(string section)
    {
        Console.Error.WriteLine($"warning: no sample found for {section}; the section is written empty.");
        return true;
    }

    /// <summary><c>LoadTvEpisodeSkeletonAsync</c> is internal to the Api assembly; no visibility in src/ was changed, so it is read by reflection like Q5 reads BuildSql.</summary>
    private static async Task<IReadOnlyList<DisplayWorkRow>> LoadTvSkeletonAsync(
        DisplayWorkProjectionReader reader,
        IReadOnlyCollection<Guid> rootIds,
        CancellationToken ct)
    {
        var method = typeof(DisplayWorkProjectionReader).GetMethod(
                "LoadTvEpisodeSkeletonAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DisplayWorkProjectionReader.LoadTvEpisodeSkeletonAsync was not found.");
        var task = (Task<IReadOnlyList<DisplayWorkRow>>)method.Invoke(reader, [rootIds, ct])!;
        return await task;
    }
}
