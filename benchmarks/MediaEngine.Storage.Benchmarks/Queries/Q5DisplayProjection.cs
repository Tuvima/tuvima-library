using System.Reflection;
using MediaEngine.Api.Services.Display;
using MediaEngine.Storage.Benchmarks.Ef;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaEngine.Storage.Benchmarks.Queries;

/// <summary>
/// Q5: <c>DisplayWorkProjectionReader.LoadAsync</c> (src/MediaEngine.Api/Services/Display/DisplayWorkProjectionReader.cs:623)
/// with the Home limit of 1000. MATERIALIZED CTEs, window functions, about forty correlated subqueries per row and
/// json_extract. The reader is measured directly, so the Engine's 30-second IMemoryCache is bypassed.
///
/// The SQL (<c>BuildSql</c>, internal to the Api assembly) is read from the compiled production assembly by reflection
/// rather than copied, so the EF variant runs byte-for-byte the product's text. No visibility in src/ was changed.
/// </summary>
internal static class Q5DisplayProjection
{
    internal static readonly Lazy<string> Sql = new(() =>
    {
        var method = typeof(DisplayWorkProjectionReader).GetMethod("BuildSql", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DisplayWorkProjectionReader.BuildSql was not found; the benchmark is pinned to commit 2210bc763.");
        var sql = (string)method.Invoke(null, [false, false, false])!;
        // EF composes raw SQL as a sub-select, which does not allow the statement terminator.
        return sql.TrimEnd().TrimEnd(';');
    });

    /// <summary>Production code, called as the Engine calls it (default arguments: no detail, no group filter, not lean).</summary>
    public static Task<IReadOnlyList<DisplayWorkRow>> Dapper(BenchContext context, int limit) =>
        new DisplayWorkProjectionReader(context.Database).LoadAsync(CancellationToken.None, limit);

    /// <summary>The production SQL through EF raw SQL, then the production post-processing of LoadAsync.</summary>
    public static async Task<IReadOnlyList<DisplayWorkRow>> EfRaw(BenchContext context, int limit)
    {
        using var lease = context.Ef.Rent();
        var database = lease.Context.Database;

#pragma warning disable EF1002 // the SQL is the product's own text; the only caller input is the bound @limit parameter
        var efRows = await lease.Context.Set<DisplayWorkEfRow>().FromSqlRaw(Sql.Value, new SqliteParameter("@limit", limit)).ToListAsync();
#pragma warning restore EF1002
        var rows = efRows.ConvertAll(RowMapper<DisplayWorkEfRow, DisplayWorkRow>.Convert);

        var pseudonymNames = (await database
                .SqlQueryRaw<string>("SELECT name AS Value FROM persons WHERE is_pseudonym = 1 AND NULLIF(TRIM(name), '') IS NOT NULL")
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            row.Author = NormalizeContributorList(row.Author, pseudonymNames, preferCollectivePseudonym: true);
            row.Artist = NormalizeContributorList(row.Artist, pseudonymNames, preferCollectivePseudonym: false);
            row.Director = NormalizeContributorList(row.Director, pseudonymNames, preferCollectivePseudonym: false);
            row.CoverUrl = ResolveArtworkUrl(row.CoverUrl, row.AssetId, "cover", row.CoverState);
            row.SquareUrl = ResolveArtworkUrl(row.SquareUrl, row.AssetId, "square", row.SquareState);
            row.BannerUrl = ResolveArtworkUrl(row.BannerUrl, row.AssetId, "banner", row.BannerState);
            row.BackgroundUrl = ResolveArtworkUrl(row.BackgroundUrl, row.AssetId, "background", row.BackgroundState);
            row.LogoUrl = ResolveArtworkUrl(row.LogoUrl, row.AssetId, "logo", row.LogoState);
            row.RootCoverUrl = ResolveArtworkUrl(row.RootCoverUrl, row.AssetId, "cover", row.RootCoverState);
            row.RootSquareUrl = ResolveArtworkUrl(row.RootSquareUrl, row.AssetId, "square", row.RootSquareState);
            row.RootBannerUrl = ResolveArtworkUrl(row.RootBannerUrl, row.AssetId, "banner", row.RootBannerState);
            row.RootBackgroundUrl = ResolveArtworkUrl(row.RootBackgroundUrl, row.AssetId, "background", row.RootBackgroundState);
            row.RootLogoUrl = ResolveArtworkUrl(row.RootLogoUrl, row.AssetId, "logo", row.RootLogoState);
        }

        return rows;
    }

    public static IEnumerable<string> Lines(IReadOnlyList<DisplayWorkRow> rows)
    {
        yield return $"rows={rows.Count}";
        foreach (var row in rows)
        {
            yield return Describe.Row(row);
        }
    }

    // Copied from DisplayWorkProjectionReader.NormalizeContributorList (private), commit 2210bc763.
    private static string? NormalizeContributorList(
        string? value,
        IReadOnlySet<string> pseudonymNames,
        bool preferCollectivePseudonym)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var names = value
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (preferCollectivePseudonym)
        {
            var pseudonyms = names.Where(pseudonymNames.Contains).ToList();
            if (pseudonyms.Count > 0)
            {
                names = pseudonyms;
            }
        }

        return string.Join("; ", names);
    }

    // Copied from DisplayArtworkUrlResolver.Resolve (internal to the Api assembly), commit 2210bc763.
    private static string? ResolveArtworkUrl(string? value, Guid assetId, string kind, string? state)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return IsExternalProviderUrl(value) ? $"/stream/{assetId}/{kind}" : value;
        }

        return string.Equals(state, "present", StringComparison.OrdinalIgnoreCase)
            ? $"/stream/{assetId}/{kind}"
            : null;
    }

    private static bool IsExternalProviderUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}
