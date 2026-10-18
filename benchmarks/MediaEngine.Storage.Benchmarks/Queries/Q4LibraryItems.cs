using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaEngine.Storage.Benchmarks.Queries;

/// <summary>
/// Q4: <c>LibraryItemRepository.GetPageAsync</c> (src/MediaEngine.Storage/LibraryItemRepository.cs:25), a page of 50
/// without a filter and with a media-type filter. The heaviest read in the product: a long CTE chain, ROW_NUMBER,
/// about twenty MAX(CASE ...) aggregates per work, correlated subqueries, then a COUNT and a page over the same CTE.
///
/// The projection SQL (<c>BuildProjectionSql</c>, about 700 lines) is private to Storage. It is not copied: it is read
/// from the compiled production assembly by reflection, so the EF variant runs byte-for-byte the SQL the product
/// runs and cannot drift from it. No visibility in src/ was changed.
/// </summary>
internal static class Q4LibraryItems
{
    internal static readonly Lazy<string> ProjectionSql = new(() =>
    {
        var method = typeof(LibraryItemRepository).GetMethod("BuildProjectionSql", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LibraryItemRepository.BuildProjectionSql was not found; the benchmark is pinned to commit 2210bc763.");
        return (string)method.Invoke(null, [false])!;
    });

    /// <summary>Production code, called as the Engine calls it.</summary>
    public static Task<LibraryItemsPage> Dapper(BenchContext context, LibraryItemQuery query) =>
        new LibraryItemRepository(context.Database).GetPageAsync(query);

    /// <summary>
    /// The production SQL through EF raw SQL, then the production row mapping. Supports exactly the query shapes the
    /// benchmark uses (no filter, or a single media type) and the default sort; the condition and ORDER BY text is
    /// what <c>GetPageAsync</c> builds for them.
    /// </summary>
    public static async Task<LibraryItemsPage> EfRaw(BenchContext context, LibraryItemQuery query)
    {
        var conditions = new List<string>();
        var parameters = new List<SqliteParameter>();
        if (!string.IsNullOrWhiteSpace(query.MediaType))
        {
            if (query.MediaType.Contains(',', StringComparison.Ordinal))
            {
                throw new NotSupportedException("The benchmark only filters by a single media type.");
            }

            conditions.Add("fd.media_type = @mediaType");
            parameters.Add(new SqliteParameter("@mediaType", NormalizeMediaType(query.MediaType.Trim())));
        }

        if (query.Search is not null || query.Status is not null || query.MinConfidence.HasValue || query.MatchSource is not null
            || query.DuplicatesOnly || query.MissingUniverseOnly || query.MaxDays.HasValue || query.IncludeAll || query.Sort is not null)
        {
            throw new NotSupportedException("The benchmark does not exercise these filters; extend the copy before using them.");
        }

        conditions.Add("fd.library_visibility = 'visible'");
        var whereClause = "WHERE " + string.Join(" AND ", conditions);
        const string orderBy = "ORDER BY fd.created_at DESC, fd.title ASC";
        var sql = ProjectionSql.Value;

        using var lease = context.Ef.Rent();
        var database = lease.Context.Database;

        var countSql = sql + $"SELECT COUNT(*) AS Value FROM full_data fd {whereClause}";
#pragma warning disable EF1002 // the interpolated text is the product's own SQL and fixed predicates, no caller input
        var counts = await database.SqlQueryRaw<int>(countSql, Clone(parameters).ToArray()).ToListAsync();
        var totalCount = counts.Single();

        var dataSql = sql + $"""
            SELECT
                fd.entity_id, fd.title, fd.year, fd.media_type, fd.cover_url, fd.background_url, fd.banner_url,
                fd.match_source, fd.confidence, fd.status, fd.has_duplicate, fd.review_id, fd.review_trigger,
                fd.has_user_locks, fd.file_name, fd.author, fd.file_path, fd.wikidata_status, fd.wikidata_match,
                fd.retail_match, fd.wikidata_qid, fd.hero_url, fd.created_at, fd.director, fd.artist,
                fd.retail_match_detail, fd.series, fd.series_position, fd.narrator, fd.genre, fd.runtime, fd.rating,
                fd.album, fd.track_number, fd.season_number, fd.episode_number, fd.show_name, fd.duration,
                fd.episode_title, fd.network, fd.top_cast, fd.qid_resolution_method, fd.pipeline_step,
                fd.library_visibility, fd.is_ready_for_library, fd.artwork_state, fd.artwork_source, fd.artwork_settled_at
            FROM full_data fd
            {whereClause}
            {orderBy}
            LIMIT @limit OFFSET @offset
            """;
        var dataParameters = Clone(parameters);
        dataParameters.Add(new SqliteParameter("@limit", query.Limit));
        dataParameters.Add(new SqliteParameter("@offset", query.Offset));
        var rows = await lease.Context.Set<ItemRow>().FromSqlRaw(dataSql, dataParameters.ToArray()).ToListAsync();
#pragma warning restore EF1002

        var items = rows.Select(Map).ToList();
        return new LibraryItemsPage(items, totalCount, query.Offset + items.Count < totalCount);
    }

    public static IEnumerable<string> Lines(LibraryItemsPage page)
    {
        yield return $"total={page.TotalCount} hasMore={page.HasMore} items={page.Items.Count}";
        foreach (var item in page.Items)
        {
            yield return Describe.Row(item);
        }
    }

    private static List<object> Clone(List<SqliteParameter> parameters) =>
        parameters.Select(parameter => (object)new SqliteParameter(parameter.ParameterName, parameter.Value)).ToList();

    // Copied from LibraryItemRepository (private there), commit 2210bc763.
    private static string NormalizeMediaType(string raw) => raw.ToUpperInvariant() switch
    {
        "EPUB" or "BOOK" or "EBOOK" => "Books",
        "AUDIOBOOK" => "Audiobooks",
        "MOVIE" => "Movies",
        "COMIC" => "Comics",
        _ => raw,
    };

    private static DateTimeOffset? ParseDateTimeOffset(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>The null handling and defaults of the production reader loop (LibraryItemRepository.GetPageAsync).</summary>
    private static LibraryCatalogItem Map(ItemRow row) => new()
    {
        EntityId = row.EntityId,
        Title = row.Title ?? throw new InvalidOperationException("title was NULL; production reads it with GetString."),
        Year = row.Year,
        MediaType = row.MediaType ?? "",
        CoverUrl = row.CoverUrl,
        BackgroundUrl = row.BackgroundUrl,
        BannerUrl = row.BannerUrl,
        MatchSource = row.MatchSource?.ToString("D"),
        Confidence = row.Confidence ?? 0.0,
        Status = row.Status ?? "Confirmed",
        HasDuplicate = row.HasDuplicate == 1,
        ReviewItemId = row.ReviewId,
        ReviewTrigger = row.ReviewTrigger,
        HasUserLocks = row.HasUserLocks == 1,
        FileName = row.FileName,
        Author = row.Author,
        FilePath = row.FilePath,
        WikidataStatus = row.WikidataStatus,
        WikidataMatch = row.WikidataMatch ?? "none",
        RetailMatch = row.RetailMatch ?? "none",
        WikidataQid = row.WikidataQid,
        HeroUrl = row.HeroUrl,
        CreatedAt = row.CreatedAt is null ? DateTimeOffset.MinValue : ParseDateTimeOffset(row.CreatedAt) ?? DateTimeOffset.MinValue,
        Director = row.Director,
        Artist = row.Artist,
        RetailMatchDetail = row.RetailMatchDetail,
        Series = row.Series,
        SeriesPosition = row.SeriesPosition,
        Narrator = row.Narrator,
        Genre = row.Genre,
        Runtime = row.Runtime,
        Rating = row.Rating,
        Album = row.Album,
        TrackNumber = row.TrackNumber,
        SeasonNumber = row.SeasonNumber,
        EpisodeNumber = row.EpisodeNumber,
        ShowName = row.ShowName,
        Duration = row.Duration,
        EpisodeTitle = row.EpisodeTitle,
        Network = row.Network,
        TopCast = row.TopCast,
        QidResolutionMethod = row.QidResolutionMethod,
        PipelineStep = row.PipelineStep ?? "Retail",
        LibraryVisibility = row.LibraryVisibility ?? "hidden",
        IsReadyForLibrary = row.IsReadyForLibrary == 1,
        ArtworkState = row.ArtworkState ?? "pending",
        ArtworkSource = row.ArtworkSource,
        ArtworkSettledAt = row.ArtworkSettledAt is null ? null : ParseDateTimeOffset(row.ArtworkSettledAt),
    };

    /// <summary>One row of the page query, columns in the order the production reader uses.</summary>
    internal sealed class ItemRow
    {
        [Column("entity_id")] public Guid EntityId { get; set; }

        [Column("title")] public string? Title { get; set; }

        [Column("year")] public string? Year { get; set; }

        [Column("media_type")] public string? MediaType { get; set; }

        [Column("cover_url")] public string? CoverUrl { get; set; }

        [Column("background_url")] public string? BackgroundUrl { get; set; }

        [Column("banner_url")] public string? BannerUrl { get; set; }

        [Column("match_source")] public Guid? MatchSource { get; set; }

        [Column("confidence")] public double? Confidence { get; set; }

        [Column("status")] public string? Status { get; set; }

        [Column("has_duplicate")] public int? HasDuplicate { get; set; }

        [Column("review_id")] public Guid? ReviewId { get; set; }

        [Column("review_trigger")] public string? ReviewTrigger { get; set; }

        [Column("has_user_locks")] public int? HasUserLocks { get; set; }

        [Column("file_name")] public string? FileName { get; set; }

        [Column("author")] public string? Author { get; set; }

        [Column("file_path")] public string? FilePath { get; set; }

        [Column("wikidata_status")] public string? WikidataStatus { get; set; }

        [Column("wikidata_match")] public string? WikidataMatch { get; set; }

        [Column("retail_match")] public string? RetailMatch { get; set; }

        [Column("wikidata_qid")] public string? WikidataQid { get; set; }

        [Column("hero_url")] public string? HeroUrl { get; set; }

        [Column("created_at")] public string? CreatedAt { get; set; }

        [Column("director")] public string? Director { get; set; }

        [Column("artist")] public string? Artist { get; set; }

        [Column("retail_match_detail")] public string? RetailMatchDetail { get; set; }

        [Column("series")] public string? Series { get; set; }

        [Column("series_position")] public string? SeriesPosition { get; set; }

        [Column("narrator")] public string? Narrator { get; set; }

        [Column("genre")] public string? Genre { get; set; }

        [Column("runtime")] public string? Runtime { get; set; }

        [Column("rating")] public string? Rating { get; set; }

        [Column("album")] public string? Album { get; set; }

        [Column("track_number")] public string? TrackNumber { get; set; }

        [Column("season_number")] public string? SeasonNumber { get; set; }

        [Column("episode_number")] public string? EpisodeNumber { get; set; }

        [Column("show_name")] public string? ShowName { get; set; }

        [Column("duration")] public string? Duration { get; set; }

        [Column("episode_title")] public string? EpisodeTitle { get; set; }

        [Column("network")] public string? Network { get; set; }

        [Column("top_cast")] public string? TopCast { get; set; }

        [Column("qid_resolution_method")] public string? QidResolutionMethod { get; set; }

        [Column("pipeline_step")] public string? PipelineStep { get; set; }

        [Column("library_visibility")] public string? LibraryVisibility { get; set; }

        [Column("is_ready_for_library")] public int? IsReadyForLibrary { get; set; }

        [Column("artwork_state")] public string? ArtworkState { get; set; }

        [Column("artwork_source")] public string? ArtworkSource { get; set; }

        [Column("artwork_settled_at")] public string? ArtworkSettledAt { get; set; }
    }
}
