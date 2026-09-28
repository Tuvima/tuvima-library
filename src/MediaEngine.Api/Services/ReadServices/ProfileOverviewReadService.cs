using System.Text.Json;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class ProfileOverviewReadService(
    IProfileService profiles,
    IDatabaseConnection db,
    ISystemActivityRepository activity,
    ITasteProfiler tasteProfiler) : IProfileOverviewReadService
{
    public async Task<ProfileOverviewResponseDto?> GetOverviewAsync(Guid profileId, CancellationToken ct)
    {
        var profile = await profiles.GetProfileAsync(profileId, ct);
        if (profile is null)
        {
            return null;
        }

        var items = ReadProfileOverviewItems(profileId, limit: 80);
        var recentlyAdded = ReadRecentlyAddedItems(limit: 12);
        var libraryCounts = ReadLibraryCounts();
        var profileActivity = await activity.GetRecentByProfileAsync(profileId, 20, ct);
        var tasteResult = await tasteProfiler.GetProfileAsync(profileId, ct);
        var completedThreshold = 95d;

        var stats = new ProfileOverviewStatsDto
        {
            TotalItems = items.Count,
            InProgress = items.Count(item => item.ProgressPct > 0 && item.ProgressPct < completedThreshold),
            Completed = items.Count(item => item.ProgressPct >= completedThreshold),
            RecentActivity = profileActivity.Count,
            MediaTypeMix = items
                .GroupBy(item => item.MediaType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            LibraryCounts = libraryCounts,
            ActivityBuckets = profileActivity
                .GroupBy(entry => entry.ActionType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            TopGenres = items
                .Where(item => !string.IsNullOrWhiteSpace(item.Genre))
                .GroupBy(item => item.Genre!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Take(8)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            ConsumedSeconds = items.Sum(EstimateConsumedSeconds),
            ConsumedSecondsByMediaType = items
                .GroupBy(item => item.MediaType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(EstimateConsumedSeconds), StringComparer.OrdinalIgnoreCase),
        };

        return new ProfileOverviewResponseDto
        {
            Profile = ProfileContractMapper.ToResponse(profile),
            Stats = stats,
            RecentItems = items.Take(12).ToList(),
            ContinueItems = items.Where(item => item.ProgressPct > 0 && item.ProgressPct < completedThreshold).Take(12).ToList(),
            CompletedItems = items.Where(item => item.ProgressPct >= completedThreshold).Take(12).ToList(),
            RecentlyAddedItems = recentlyAdded,
            Activity = profileActivity.Select(entry => new ProfileOverviewActivityDto
            {
                Id = entry.Id,
                OccurredAt = entry.OccurredAt,
                ActionType = entry.ActionType,
                Detail = entry.Detail,
                EntityId = entry.EntityId,
            }).ToList(),
            Taste = tasteResult.Status == Domain.Models.TasteProfileBuildStatus.Generated
                ? ProfileContractMapper.ToResponse(tasteResult.Profile!)
                : null,
        };
    }

    private List<ProfileOverviewItemDto> ReadProfileOverviewItems(Guid profileId, int limit)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                us.asset_id,
                w.id AS work_id,
                w.media_type,
                us.progress_pct,
                us.last_accessed,
                us.extended_properties,
                h.display_name AS collection_name,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'title' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'title' LIMIT 1),
                    NULLIF(ma.file_path_root, ''),
                    'Untitled') AS title,
                COALESCE(
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('author', 'album_artist', 'artist', 'narrator') ORDER BY ordinal LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('show_name', 'series') LIMIT 1),
                    h.display_name) AS subtitle,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'media_type' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'media_type' LIMIT 1),
                    w.media_type,
                    'Media') AS media_type,
                CASE WHEN w.media_type = 'TV' THEN
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('cover_url_s', 'poster_url_s', 'cover_url', 'poster_url') ORDER BY CASE WHEN key LIKE '%_s' THEN 0 ELSE 1 END LIMIT 1)
                ELSE COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('cover_url', 'cover', 'image_url') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('cover_url', 'cover', 'image_url') LIMIT 1)) END AS cover_url,
                (SELECT value FROM canonical_value_arrays WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'genre' ORDER BY ordinal LIMIT 1) AS genre
            FROM user_states us
            JOIN media_assets ma ON ma.id = us.asset_id
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            LEFT JOIN works pw ON pw.id = w.parent_work_id
            LEFT JOIN works gpw ON gpw.id = pw.parent_work_id
            LEFT JOIN collections h ON h.id = w.collection_id
            WHERE us.user_id = @profileId
            ORDER BY us.last_accessed DESC
            LIMIT @limit;
            """;
        cmd.Parameters.Add("@profileId", SqliteType.Blob).Value = GuidSql.ToBlob(profileId);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        var items = new List<ProfileOverviewItemDto>();
        while (reader.Read())
        {
            var assetId = GuidSql.FromDb(reader.GetValue(0));
            var workId = GuidSql.FromDb(reader.GetValue(1));
            var mediaType = ReadString(reader, 9) ?? ReadString(reader, 2) ?? "Media";
            var ext = ReadExtendedProperties(ReadString(reader, 5));
            var positionSeconds = ReadDouble(ext, "position_seconds");
            var durationSeconds = ReadDouble(ext, "duration_seconds");

            items.Add(new ProfileOverviewItemDto
            {
                AssetId = assetId,
                WorkId = workId,
                MediaType = mediaType,
                ProgressPct = Math.Clamp(ReadDouble(reader, 3) ?? 0d, 0d, 100d),
                LastAccessed = ReadDateTimeOffset(reader, 4) ?? DateTimeOffset.UtcNow,
                CollectionName = ReadString(reader, 6),
                Title = NormalizeTitle(ReadString(reader, 7)),
                Subtitle = ReadString(reader, 8),
                CoverUrl = ReadString(reader, 10),
                Genre = ReadString(reader, 11),
                PositionSeconds = positionSeconds,
                DurationSeconds = durationSeconds,
                Route = BuildItemRoute(mediaType, assetId, workId),
            });
        }

        return items;
    }

    private List<ProfileOverviewItemDto> ReadRecentlyAddedItems(int limit)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                ma.id AS asset_id,
                w.id AS work_id,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'media_type' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'media_type' LIMIT 1),
                    w.media_type,
                    'Media') AS media_type,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'title' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'title' LIMIT 1),
                    NULLIF(ma.file_path_root, ''),
                    'Untitled') AS title,
                COALESCE(
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('author', 'album_artist', 'artist', 'narrator') ORDER BY ordinal LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('show_name', 'series') LIMIT 1),
                    h.display_name) AS subtitle,
                CASE WHEN w.media_type = 'TV' THEN
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('cover_url_s', 'poster_url_s', 'cover_url', 'poster_url') ORDER BY CASE WHEN key LIKE '%_s' THEN 0 ELSE 1 END LIMIT 1)
                ELSE COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('cover_url', 'cover', 'image_url') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('cover_url', 'cover', 'image_url') LIMIT 1)) END AS cover_url,
                h.display_name AS collection_name,
                (SELECT value FROM canonical_value_arrays WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'genre' ORDER BY ordinal LIMIT 1) AS genre,
                COALESCE(MAX(mc.claimed_at), datetime('now')) AS added_at
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            LEFT JOIN works pw ON pw.id = w.parent_work_id
            LEFT JOIN works gpw ON gpw.id = pw.parent_work_id
            LEFT JOIN collections h ON h.id = w.collection_id
            LEFT JOIN metadata_claims mc ON mc.entity_id IN (ma.id, e.id, w.id, COALESCE(gpw.id, pw.id, w.id))
            WHERE COALESCE(w.ownership, 'Owned') = 'Owned'
              AND COALESCE(w.is_catalog_only, 0) = 0
            GROUP BY ma.id, w.id, w.media_type, ma.file_path_root, h.display_name
            ORDER BY added_at DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        var items = new List<ProfileOverviewItemDto>();
        while (reader.Read())
        {
            var assetId = GuidSql.FromDb(reader.GetValue(0));
            var workId = GuidSql.FromDb(reader.GetValue(1));
            var mediaType = ReadString(reader, 2) ?? "Media";
            items.Add(new ProfileOverviewItemDto
            {
                AssetId = assetId,
                WorkId = workId,
                MediaType = mediaType,
                Title = NormalizeTitle(ReadString(reader, 3)),
                Subtitle = ReadString(reader, 4),
                CoverUrl = ReadString(reader, 5),
                CollectionName = ReadString(reader, 6),
                Genre = ReadString(reader, 7),
                LastAccessed = ReadDateTimeOffset(reader, 8) ?? DateTimeOffset.UtcNow,
                AddedAt = ReadDateTimeOffset(reader, 8),
                Route = BuildItemRoute(mediaType, assetId, workId),
            });
        }

        return items;
    }

    private Dictionary<string, int> ReadLibraryCounts()
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'media_type' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'media_type' LIMIT 1),
                       w.media_type,
                       'Media') AS media_type,
                   COUNT(DISTINCT ma.id) AS total
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            WHERE COALESCE(w.ownership, 'Owned') = 'Owned'
              AND COALESCE(w.is_catalog_only, 0) = 0
            GROUP BY media_type
            ORDER BY total DESC;
            """;

        using var reader = cmd.ExecuteReader();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var key = ReadString(reader, 0) ?? "Media";
            counts[key] = reader.GetInt32(1);
        }

        return counts;
    }

    private static double EstimateConsumedSeconds(ProfileOverviewItemDto item)
    {
        if (item.PositionSeconds is > 0)
        {
            return item.PositionSeconds.Value;
        }

        if (item.DurationSeconds is > 0)
        {
            return item.DurationSeconds.Value * Math.Clamp(item.ProgressPct, 0d, 100d) / 100d;
        }

        return 0d;
    }

    private static string? BuildItemRoute(string mediaType, Guid assetId, Guid workId)
    {
        var normalized = mediaType.Trim().ToLowerInvariant();
        if (normalized.Contains("book") || normalized.Contains("epub") || normalized.Contains("comic"))
        {
            return $"/read/{assetId}";
        }

        if (normalized.Contains("audio"))
        {
            return $"/details/work/{workId}?context=listen";
        }

        if (normalized.Contains("music"))
        {
            return $"/listen/music?browse=songs&track={workId}";
        }

        if (normalized.Contains("movie") || normalized.Contains("show") || normalized.Contains("tv") || normalized.Contains("episode") || normalized.Contains("video"))
        {
            return $"/watch/player/{workId}";
        }

        return null;
    }

    private static Dictionary<string, string> ReadExtendedProperties(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Untitled";
        }

        var fileName = Path.GetFileNameWithoutExtension(value);
        return string.IsNullOrWhiteSpace(fileName) ? value.Trim() : fileName.Trim();
    }

    private static string? ReadString(System.Data.IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static double? ReadDouble(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var raw) && double.TryParse(raw, out var parsed)
            ? parsed
            : null;
    }

    private static double? ReadDouble(System.Data.IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            double number => number,
            float number => number,
            int number => number,
            long number => number,
            string raw when double.TryParse(raw, out var parsed) => parsed,
            _ => null,
        };
    }

    private static DateTimeOffset? ReadDateTimeOffset(System.Data.IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var raw = reader.GetValue(ordinal)?.ToString();
        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : null;
    }
}
