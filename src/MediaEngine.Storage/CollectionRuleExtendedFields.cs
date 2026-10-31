using System.Globalization;
using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// The rule fields added by the Collections and lists spec (4c): release date, age rating levels, tags, collections,
/// Universe, the viewer's own activity (progress, plays, My List, Favourites, rating), when an item was added, which
/// library and folder it sits in, its quality tier and its variants.
///
/// Activity fields are per profile (D4): they read the profile that is looking at the collection, so one "Unwatched"
/// collection shows each person their own unwatched titles. With no viewing profile (a service application, or a read
/// that names nobody) an activity condition matches nothing rather than guessing.
///
/// Every field here reads data the Engine already keeps: <c>user_states</c> and <c>consumption_history</c> for progress
/// and plays, <c>music_play_stats</c> for songs, <c>profile_saved_items</c> for My List, <c>profile_reactions</c> for
/// Favourites (Love) and the viewer's rating, <c>profile_work_preferences</c> for the viewer's own tags, and the
/// canonical values for dates, ratings and video size.
/// </summary>
internal sealed class CollectionRuleExtendedFields(IDatabaseConnection db, TimeProvider clock)
{
    /// <summary>A work's own media assets that are still on disk (the same set the Library counts as owned).</summary>
    private const string OwnedAssetIds = """
        SELECT woa.asset_id
        FROM work_owned_assets woa
        INNER JOIN media_assets ma_o ON ma_o.id = woa.asset_id
        WHERE woa.work_id = w.id AND ma_o.status = 'Normal' AND ma_o.is_orphaned = 0
        """;

    /// <summary>The work, its parent and its grandparent (episode, season, show), so state saved on the show counts for its episodes.</summary>
    private const string LineageWorkIds = """
        -- readiness: expression used inside the rule query, which filters on the Ready rule
        SELECT w.id
        UNION SELECT p_l.id FROM works p_l WHERE p_l.id = w.parent_work_id
        UNION SELECT gp_l.id FROM works p_l2 INNER JOIN works gp_l ON gp_l.id = p_l2.parent_work_id WHERE p_l2.id = w.parent_work_id
        """;

    private const string MatchesNothing = "0 = 1";

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "release_date", "age_level", "in_collection", "in_universe",
        "your_rating", "watch_state", "play_count", "last_played", "on_my_list", "favourite",
        "added_date", "library", "file_location", "quality_tier", "has_variants", "available_as",
    };

    /// <summary>True when this class translates the field (everything in spec 4c that the generic canonical lookup cannot).</summary>
    public static bool Handles(string field) => Fields.Contains(field);

    /// <summary>True for fields whose result depends on the profile looking at the collection.</summary>
    public static bool IsPerProfile(string field) =>
        field is "your_rating" or "watch_state" or "play_count" or "last_played" or "on_my_list" or "favourite";

    public (string? Sql, List<(string Name, object Value)> Parameters) Translate(
        CollectionRulePredicate predicate,
        Guid? viewerProfileId,
        ref int paramIdx)
    {
        var parameters = new List<(string, object)>();
        var b = new Binder(parameters, paramIdx);
        var field = predicate.Field.ToLowerInvariant().Trim();
        var op = predicate.Op.ToLowerInvariant().Trim();
        var values = predicate.GetEffectiveValues();

        if (IsPerProfile(field) && viewerProfileId is null)
        {
            return (MatchesNothing, parameters);
        }

        string? sql = field switch
        {
            "release_date" => ReleaseDate(op, values, b),
            "age_level" => AgeLevel(op, values, b),
            "in_collection" => InCollection(op, values, b),
            "in_universe" => Wrap(InUniverse(), values),
            "your_rating" => YourRating(op, values, viewerProfileId!.Value, b),
            "watch_state" => WatchState(op, values, viewerProfileId!.Value, b),
            "play_count" => PlayCount(op, values, viewerProfileId!.Value, b),
            "last_played" => LastPlayed(op, values, viewerProfileId!.Value, b),
            "on_my_list" => Wrap(OnMyList(viewerProfileId!.Value, b), values),
            "favourite" => Wrap(Favourite(viewerProfileId!.Value, b), values),
            "added_date" => AddedDate(op, values, b),
            "library" => Library(op, values, b),
            "file_location" => FileLocation(op, values, b),
            "quality_tier" => QualityTier(op, values, b),
            "has_variants" => Wrap(HasVariants(), values),
            "available_as" => AvailableAs(op, values, b),
            _ => null,
        };
        paramIdx = b.Index;
        return (sql, parameters);
    }

    /// <summary>The viewer's own tags, matched the way the shared tag condition matches library tags.</summary>
    public string? ViewerTags(string op, string[] values, Guid viewerProfileId, Binder b)
    {
        var profile = b.Bind(GuidSql.ToBlob(viewerProfileId));
        string Exists(string where) => $"""
            EXISTS (
                SELECT 1 FROM profile_work_preferences pref_t, json_each(pref_t.local_tags_json) tag_t
                WHERE pref_t.profile_id = {profile}
                  AND pref_t.work_id IN ({LineageWorkIds})
                  AND json_valid(pref_t.local_tags_json)
                  {where}
            )
            """;
        if (op is "known" or "is_known" or "has_any_value")
        {
            return Exists(string.Empty);
        }

        if (values.Length == 0)
        {
            return null;
        }

        switch (op)
        {
            case "contains":
                return Exists($"AND tag_t.value LIKE {b.Bind($"%{values[0]}%")} COLLATE NOCASE");
            case "in":
                var names = values.Select(value => b.Bind(value)).ToList();
                return Exists($"AND tag_t.value COLLATE NOCASE IN ({string.Join(", ", names)})");
            default:
                return Exists($"AND tag_t.value = {b.Bind(values[0])} COLLATE NOCASE");
        }
    }

    // ── Media ───────────────────────────────────────────────────────────────────

    private string? ReleaseDate(string op, string[] values, Binder b)
    {
        const string keys = "'release_date','original_release_date','original_publication_date','publication_date','air_date'";
        const string normalised = "substr(cv.value || '-01-01', 1, 10)";
        string Lookup(string comparison) => Cv(
            $"cv.key IN ({keys}) AND cv.value GLOB '[0-9][0-9][0-9][0-9]*' AND {normalised} {comparison}");

        if (op is "known" or "is_known" or "has_any_value")
        {
            return Cv($"cv.key IN ({keys}) AND cv.value GLOB '[0-9][0-9][0-9][0-9]*'");
        }

        if (op is "unknown" or "is_unknown" or "has_no_known_value")
        {
            return Cv($"cv.key IN ({keys}) AND cv.value GLOB '[0-9][0-9][0-9][0-9]*'", negate: true);
        }

        if (values.Length == 0)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (op is "in_last_days" or "in_last_months" or "in_last_years")
        {
            if (!int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
            {
                return null;
            }

            var since = op switch
            {
                "in_last_days" => today.AddDays(-count),
                "in_last_months" => today.AddMonths(-count),
                _ => today.AddYears(-count),
            };
            var from = b.Bind(IsoDate(since));
            var to = b.Bind(IsoDate(today));
            return Lookup($"BETWEEN {from} AND {to}");
        }

        return DateComparison(op, values, b, Lookup);
    }

    private static string? DateComparison(
        string op,
        string[] values,
        Binder b,
        Func<string, string> lookup)
    {
        if (!TryDate(values[0], out var first))
        {
            return null;
        }

        switch (op)
        {
            case "gt":
                return lookup($"> {b.Bind(IsoDate(first))}");
            case "lt":
                return lookup($"< {b.Bind(IsoDate(first))}");
            case "between":
                if (values.Length < 2 || !TryDate(values[1], out var second))
                {
                    return null;
                }

                var (low, high) = first <= second ? (first, second) : (second, first);
                return lookup($"BETWEEN {b.Bind(IsoDate(low))} AND {b.Bind(IsoDate(high))}");
            default:
                return lookup($"= {b.Bind(IsoDate(first))}");
        }
    }

    /// <summary>
    /// Age rating by Kids level: the same ladder the household uses to limit a Kids profile (G, PG, PG-13, R), so
    /// "suitable for up to PG" means exactly what a Kids profile on PG would be allowed to see.
    /// </summary>
    private string? AgeLevel(string op, string[] values, Binder b)
    {
        var ratingKeys = "cv.key IN ('content_rating','certification')";
        if (op is "unknown" or "is_unknown" or "has_no_known_value")
        {
            return Cv(ratingKeys, negate: true);
        }

        if (op is "known" or "is_known" or "has_any_value")
        {
            return Cv(ratingKeys);
        }

        if (values.Length == 0 || ProfileContentLimits.Normalize(values[0]) is not { } level)
        {
            return null;
        }

        var cap = ProfileContentLimits.RankOfLimit(level);
        var withinLevel = new List<string>();
        var aboveLevel = new List<string>();
        foreach (var rating in DistinctRatings())
        {
            if (ProfileContentLimits.RankOfRating(rating) is not { } rank)
            {
                continue;
            }

            (rank <= cap ? withinLevel : aboveLevel).Add(rating);
        }

        string AnyOf(List<string> ratings) => ratings.Count == 0
            ? MatchesNothing
            : Cv($"{ratingKeys} AND cv.value COLLATE NOCASE IN ({string.Join(", ", ratings.Select(rating => b.Bind(rating)))})");

        if (op == "gt")
        {
            return AnyOf(aboveLevel);
        }

        // "up to": rated at or below the level, and nothing on the item is rated above it.
        return $"({AnyOf(withinLevel)} AND NOT {AnyOf(aboveLevel)})";
    }

    private IReadOnlyList<string> DistinctRatings()
    {
        using var connection = db.CreateConnection();
        return connection.Query<string>(
            """
            SELECT value FROM canonical_values WHERE key IN ('content_rating','certification') AND NULLIF(TRIM(value), '') IS NOT NULL
            UNION
            SELECT value FROM canonical_value_arrays WHERE key IN ('content_rating','certification') AND NULLIF(TRIM(value), '') IS NOT NULL;
            """).AsList();
    }

    // ── Story and world ─────────────────────────────────────────────────────────

    /// <summary>Hand-picked collections and playlists the work was added to (rule-driven collections hold no stored members).</summary>
    private static string? InCollection(string op, string[] values, Binder b)
    {
        const string member = "SELECT 1 FROM collection_items ci_c INNER JOIN collections c_c ON c_c.id = ci_c.collection_id AND c_c.is_enabled = 1 WHERE ci_c.work_id IN ({0}){1}";
        var any = string.Format(CultureInfo.InvariantCulture, member, LineageWorkIds, string.Empty);
        if (op is "known" or "is_known" or "has_any_value")
        {
            return $"EXISTS ({any})";
        }

        if (op is "unknown" or "is_unknown" or "has_no_known_value")
        {
            return $"NOT EXISTS ({any})";
        }

        var ids = values
            .Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => b.Bind(GuidSql.ToBlob(id!.Value)))
            .ToList();
        if (ids.Count == 0)
        {
            return null;
        }

        var inChosen = string.Format(CultureInfo.InvariantCulture, member, LineageWorkIds,
            $" AND ci_c.collection_id IN ({string.Join(", ", ids)})");
        return op == "neq" ? $"NOT EXISTS ({inChosen})" : $"EXISTS ({inChosen})";
    }

    /// <summary>In a Universe: its Series sits under a Universe, or the title carries a franchise or fictional universe.</summary>
    private static string InUniverse() => """
        (
            EXISTS (SELECT 1 FROM collections c_u WHERE c_u.id = w.collection_id AND (c_u.parent_collection_id IS NOT NULL OR NULLIF(c_u.universe_qid, '') IS NOT NULL))
            OR EXISTS (SELECT 1 FROM collection_relationships cr_u WHERE cr_u.collection_id = w.collection_id AND cr_u.rel_type IN ('franchise','fictional_universe'))
        )
        """;

    // ── Recognition and the viewer's own activity ───────────────────────────────

    /// <summary>The viewer's rating is the reaction they gave: Love, Like or Dislike (there is no star rating per profile).</summary>
    private static string? YourRating(string op, string[] values, Guid viewer, Binder b)
    {
        var profile = b.Bind(GuidSql.ToBlob(viewer));
        string Reaction(string where) =>
            $"EXISTS (SELECT 1 FROM profile_reactions pr_r WHERE pr_r.profile_id = {profile} AND pr_r.entity_id IN ({LineageWorkIds}){where})";

        if (op is "known" or "is_known" or "has_any_value")
        {
            return Reaction(string.Empty);
        }

        if (op is "unknown" or "is_unknown" or "has_no_known_value")
        {
            return $"NOT {Reaction(string.Empty)}";
        }

        var wanted = values
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value is "love" or "like" or "dislike" or "unrated")
            .Distinct()
            .ToList();
        if (wanted.Count == 0)
        {
            return null;
        }

        var clauses = wanted.Select(value => value == "unrated"
            ? $"NOT {Reaction(string.Empty)}"
            : Reaction($" AND pr_r.reaction = {b.Bind(char.ToUpperInvariant(value[0]) + value[1..])}")).ToList();
        var any = $"({string.Join(" OR ", clauses)})";
        return op == "neq" ? $"NOT {any}" : any;
    }

    private static string WatchedSql(string profile) => $"""
        (
            EXISTS (SELECT 1 FROM user_states us_w WHERE us_w.user_id = {profile} AND us_w.asset_id IN ({OwnedAssetIds}) AND us_w.progress_pct >= 99.5)
            OR EXISTS (SELECT 1 FROM music_play_stats mps_w WHERE mps_w.profile_id = {profile} AND mps_w.work_id = w.id AND mps_w.play_count > 0)
        )
        """;

    private static string StartedSql(string profile) =>
        $"EXISTS (SELECT 1 FROM user_states us_s WHERE us_s.user_id = {profile} AND us_s.asset_id IN ({OwnedAssetIds}) AND us_s.progress_pct > 0 AND us_s.progress_pct < 99.5)";

    /// <summary>Unwatched (nothing played), In progress (started, not finished) or Watched (finished, or a song played).</summary>
    private static string? WatchState(string op, string[] values, Guid viewer, Binder b)
    {
        var profile = b.Bind(GuidSql.ToBlob(viewer));
        var watched = WatchedSql(profile);
        var started = StartedSql(profile);
        var clauses = values
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct()
            .Select(value => value switch
            {
                "watched" => watched,
                "in_progress" => $"({started} AND NOT {watched})",
                "unwatched" => $"(NOT {watched} AND NOT {started})",
                _ => null,
            })
            .Where(clause => clause is not null)
            .ToList();
        if (clauses.Count == 0)
        {
            return null;
        }

        var any = $"({string.Join(" OR ", clauses)})";
        return op == "neq" ? $"NOT {any}" : any;
    }

    /// <summary>Times the viewer finished the title (songs: the play count; everything else: finished sessions).</summary>
    private static string PlayCountExpression(string profile) => $"""
        (CASE WHEN w.media_type = 'Music'
              THEN COALESCE((SELECT mps_c.play_count FROM music_play_stats mps_c WHERE mps_c.profile_id = {profile} AND mps_c.work_id = w.id), 0)
              ELSE (SELECT COUNT(*) FROM consumption_history ch_c WHERE ch_c.profile_id = {profile} AND ch_c.asset_id IN ({OwnedAssetIds}) AND ch_c.progress_pct >= 99.5)
         END)
        """;

    private static string? PlayCount(string op, string[] values, Guid viewer, Binder b)
    {
        if (values.Length == 0)
        {
            return null;
        }

        var profile = b.Bind(GuidSql.ToBlob(viewer));
        var count = PlayCountExpression(profile);
        if (op == "between")
        {
            if (values.Length < 2)
            {
                return null;
            }

            return $"{count} BETWEEN CAST({b.Bind(values[0])} AS INTEGER) AND CAST({b.Bind(values[1])} AS INTEGER)";
        }

        var sqlOp = op switch { "gt" => ">", "gte" => ">=", "lt" => "<", "lte" => "<=", "neq" => "<>", _ => "=" };
        return $"{count} {sqlOp} CAST({b.Bind(values[0])} AS INTEGER)";
    }

    private string? LastPlayed(string op, string[] values, Guid viewer, Binder b)
    {
        var profile = b.Bind(GuidSql.ToBlob(viewer));
        var lastPlayed = $"""
            (SELECT MAX(played.moment) FROM (
                SELECT julianday(us_l.last_accessed) AS moment FROM user_states us_l
                 WHERE us_l.user_id = {profile} AND us_l.asset_id IN ({OwnedAssetIds}) AND us_l.progress_pct > 0
                UNION ALL
                SELECT julianday(mps_l.last_played_at) FROM music_play_stats mps_l
                 WHERE mps_l.profile_id = {profile} AND mps_l.work_id = w.id
            ) played)
            """;
        if (op is "known" or "is_known" or "has_any_value")
        {
            return $"{lastPlayed} IS NOT NULL";
        }

        if (op is "unknown" or "is_unknown" or "has_no_known_value")
        {
            return $"{lastPlayed} IS NULL";
        }

        if (values.Length == 0 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days <= 0)
        {
            return null;
        }

        var threshold = b.Bind(clock.GetUtcNow().UtcDateTime.AddDays(-days).ToString("O", CultureInfo.InvariantCulture));
        return op == "not_within_days"
            ? $"({lastPlayed} IS NULL OR {lastPlayed} < julianday({threshold}))"
            : $"{lastPlayed} >= julianday({threshold})";
    }

    private static string OnMyList(Guid viewer, Binder b)
    {
        var profile = b.Bind(GuidSql.ToBlob(viewer));
        return $"EXISTS (SELECT 1 FROM profile_saved_items psi_m WHERE psi_m.profile_id = {profile} AND psi_m.entity_id IN ({LineageWorkIds}))";
    }

    private static string Favourite(Guid viewer, Binder b)
    {
        var profile = b.Bind(GuidSql.ToBlob(viewer));
        return $"EXISTS (SELECT 1 FROM profile_reactions pr_f WHERE pr_f.profile_id = {profile} AND pr_f.reaction = 'Love' AND pr_f.entity_id IN ({LineageWorkIds}))";
    }

    // ── My library ──────────────────────────────────────────────────────────────

    private string? AddedDate(string op, string[] values, Binder b)
    {
        if (values.Length == 0)
        {
            return null;
        }

        string Lookup(string comparison) =>
            $"w.id IN (SELECT e_a.work_id FROM editions e_a INNER JOIN media_assets ma_a ON ma_a.edition_id = e_a.id INNER JOIN metadata_claims mc_a ON mc_a.entity_id = ma_a.id GROUP BY e_a.work_id HAVING substr(MIN(mc_a.claimed_at), 1, 10) {comparison})";
        return DateComparison(op, values, b, Lookup);
    }

    private static string? Library(string op, string[] values, Binder b)
    {
        if (values.Length == 0)
        {
            return null;
        }

        var ids = values.Select(value => b.Bind(value)).ToList();
        var inLibrary = $"EXISTS (SELECT 1 FROM work_owned_assets woa_l INNER JOIN media_assets ma_l ON ma_l.id = woa_l.asset_id WHERE woa_l.work_id = w.id AND ma_l.status = 'Normal' AND ma_l.is_orphaned = 0 AND ma_l.library_id IN ({string.Join(", ", ids)}))";
        return op == "neq" ? $"NOT {inLibrary}" : inLibrary;
    }

    private static string? FileLocation(string op, string[] values, Binder b)
    {
        if (values.Length == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            return null;
        }

        var pattern = b.Bind("%" + EscapeLike(values[0].Trim()) + "%");
        var matches = $"EXISTS (SELECT 1 FROM work_owned_assets woa_f INNER JOIN media_assets ma_f ON ma_f.id = woa_f.asset_id WHERE woa_f.work_id = w.id AND ma_f.is_orphaned = 0 AND ma_f.file_path_root LIKE {pattern} ESCAPE '\\')";
        return op is "not_contains" or "neq" ? $"NOT {matches}" : matches;
    }

    /// <summary>Quality tier of any owned file: 4K (2000 px or taller), HD (700 and up) or SD. A filter only.</summary>
    private static string? QualityTier(string op, string[] values, Binder b)
    {
        var tiers = values
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value is "4k" or "hd" or "sd")
            .Distinct()
            .Select(value => value switch
            {
                "4k" => "CAST(cv_q.value AS INTEGER) >= 2000",
                "hd" => "CAST(cv_q.value AS INTEGER) >= 700 AND CAST(cv_q.value AS INTEGER) < 2000",
                _ => "CAST(cv_q.value AS INTEGER) > 0 AND CAST(cv_q.value AS INTEGER) < 700",
            })
            .Select(clause => $"({clause})")
            .ToList();
        if (tiers.Count == 0)
        {
            return null;
        }

        var any = $"EXISTS (SELECT 1 FROM work_owned_assets woa_q INNER JOIN media_assets ma_q ON ma_q.id = woa_q.asset_id INNER JOIN canonical_values cv_q ON cv_q.entity_id = ma_q.id AND cv_q.key = 'video_height' WHERE woa_q.work_id = w.id AND ma_q.status = 'Normal' AND ma_q.is_orphaned = 0 AND ({string.Join(" OR ", tiers)}))";
        return op == "neq" ? $"NOT {any}" : any;
    }

    /// <summary>More than one owned edition, or the same title owned under another media type (the ebook beside its audiobook).</summary>
    private static string HasVariants() => """
        -- readiness: expression used inside the rule query, which filters on the Ready rule
        (
            (SELECT COUNT(DISTINCT e_v.id) FROM editions e_v INNER JOIN media_assets ma_v ON ma_v.edition_id = e_v.id
              WHERE e_v.work_id = w.id AND ma_v.status = 'Normal' AND ma_v.is_orphaned = 0) >= 2
            OR (NULLIF(w.wikidata_qid, '') IS NOT NULL AND EXISTS (
                SELECT 1 FROM works s_v WHERE s_v.id <> w.id AND s_v.is_catalog_only = 0
                   AND s_v.wikidata_qid = w.wikidata_qid COLLATE NOCASE AND s_v.media_type <> w.media_type))
        )
        """;

    /// <summary>The title is owned as an ebook, an audiobook or a comic (the Work itself, or the same title under that media type).</summary>
    private static string? AvailableAs(string op, string[] values, Binder b)
    {
        var clauses = values
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct()
            .Select(value => value switch { "ebook" => "Books", "audiobook" => "Audiobooks", "comic" => "Comics", _ => null })
            .Where(mediaType => mediaType is not null)
            .Select(mediaType => $"""
                -- readiness: expression used inside the rule query, which filters on the Ready rule
                EXISTS (SELECT 1 FROM works s_a WHERE s_a.is_catalog_only = 0 AND s_a.media_type = {b.Bind(mediaType!)} AND (s_a.id = w.id OR (NULLIF(w.wikidata_qid, '') IS NOT NULL AND s_a.wikidata_qid = w.wikidata_qid COLLATE NOCASE)))
                """)
            .ToList();
        if (clauses.Count == 0)
        {
            return null;
        }

        return op switch
        {
            "all_of" => $"({string.Join(" AND ", clauses)})",
            "neq" => $"NOT ({string.Join(" OR ", clauses)})",
            _ => $"({string.Join(" OR ", clauses)})",
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>A yes/no field: the value "false" negates the condition.</summary>
    private static string Wrap(string expression, string[] values) =>
        values.Length > 0 && (values[0].Equals("false", StringComparison.OrdinalIgnoreCase) || values[0] == "0")
            ? $"NOT {expression}"
            : expression;

    private static string Cv(string predicate, bool negate = false)
    {
        var entities = $"""
            -- readiness: expression used inside the rule query, which filters on the Ready rule
            SELECT w.id
            UNION SELECT p_c.id FROM works p_c WHERE p_c.id = w.parent_work_id
            UNION SELECT gp_c.id FROM works p_c2 INNER JOIN works gp_c ON gp_c.id = p_c2.parent_work_id WHERE p_c2.id = w.parent_work_id
            UNION SELECT e_c.id FROM editions e_c WHERE e_c.work_id = w.id
            UNION SELECT ma_c.id FROM editions e_c2 INNER JOIN media_assets ma_c ON ma_c.edition_id = e_c2.id WHERE e_c2.work_id = w.id
            """;
        return $"""
            {(negate ? "NOT EXISTS" : "EXISTS")} (
                SELECT 1 FROM canonical_values cv WHERE cv.entity_id IN ({entities}) AND {predicate}
                UNION ALL
                SELECT 1 FROM canonical_value_arrays cv WHERE cv.entity_id IN ({entities}) AND {predicate}
            )
            """;
    }

    /// <summary>Hands out the next statement parameter name and remembers its value.</summary>
    internal sealed class Binder(List<(string, object)> parameters, int index)
    {
        public int Index { get; private set; } = index;

        public string Bind(object value)
        {
            var name = $"@p{Index++}";
            parameters.Add((name, value));
            return name;
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string IsoDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
