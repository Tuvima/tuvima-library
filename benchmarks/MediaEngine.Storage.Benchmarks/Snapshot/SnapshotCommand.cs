using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Storage.Benchmarks.Snapshot;

/// <summary>
/// <c>snapshot --db &lt;path&gt; --out &lt;file&gt;</c>: runs the production Home, detail, recent, TV skeleton and library reads over
/// the seeded file and writes every field of every row to one text file (spec "Home and item detail speed", D6). Only
/// production code is called. The file has one <c>## name</c> section per read, one line per row, and ends with
/// <c>sha256=&lt;hex&gt;</c> of everything above it. Run it before the first change and after each wave, then <c>compare</c>.
/// Row order inside a group with the same <c>CreatedAt</c> is normalised (D7); see <see cref="SnapshotRows"/>.
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
