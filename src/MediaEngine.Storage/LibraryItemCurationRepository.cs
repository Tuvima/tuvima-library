using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Persistence boundary for curator-driven library item operations. All commands use
/// short-lived connections, explicit transactions for multi-table state changes, and
/// guid-blob-v1-safe parameters.
/// </summary>
public sealed class LibraryItemCurationRepository(IDatabaseConnection db) : ILibraryItemCurationRepository
{
    public Task<IReadOnlyList<LibraryItemHistoryEntry>> GetHistoryAsync(
        Guid workId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<HistoryRow>(new CommandDefinition("""
            WITH RECURSIVE work_tree AS (
                SELECT w.id,
                       w.parent_work_id,
                       w.media_type,
                       w.work_kind,
                       w.ordinal,
                       0 AS depth
                FROM works w
                WHERE w.id = @workId

                UNION ALL

                SELECT child.id,
                       child.parent_work_id,
                       child.media_type,
                       child.work_kind,
                       child.ordinal,
                       parent.depth + 1
                FROM works child
                INNER JOIN work_tree parent ON child.parent_work_id = parent.id
            ),
            scoped_activity AS (
                SELECT sa.id AS Id,
                       sa.occurred_at AS OccurredAt,
                       sa.action_type AS EventType,
                       sa.entity_id AS EntityId,
                       sa.profile_id AS ProfileId,
                       sa.detail AS Detail,
                       wt.id AS ContextWorkId,
                       wt.media_type AS ContextMediaType,
                       wt.work_kind AS ContextWorkKind,
                       wt.ordinal AS ContextOrdinal,
                       parent.ordinal AS ParentOrdinal,
                       wt.depth AS ContextDepth,
                       (
                           SELECT cv.value
                           FROM canonical_values cv
                           WHERE (cv.entity_id = wt.id
                                  OR cv.entity_id IN (
                                      SELECT ma_title.id
                                      FROM editions e_title
                                      INNER JOIN media_assets ma_title ON ma_title.edition_id = e_title.id
                                      WHERE e_title.work_id = wt.id
                                  ))
                             AND cv.key IN ('episode_title', 'title', 'track_title', 'show_name', 'album')
                             AND NULLIF(cv.value, '') IS NOT NULL
                           ORDER BY CASE cv.key
                               WHEN 'episode_title' THEN 0
                               WHEN 'track_title' THEN 1
                               WHEN 'title' THEN 2
                               WHEN 'show_name' THEN 3
                               ELSE 4
                           END
                           LIMIT 1
                       ) AS ContextTitle
                FROM work_tree wt
                INNER JOIN system_activity sa ON sa.entity_id = wt.id
                LEFT JOIN works parent ON parent.id = wt.parent_work_id

                UNION ALL

                SELECT sa.id AS Id,
                       sa.occurred_at AS OccurredAt,
                       sa.action_type AS EventType,
                       sa.entity_id AS EntityId,
                       sa.profile_id AS ProfileId,
                       sa.detail AS Detail,
                       wt.id AS ContextWorkId,
                       wt.media_type AS ContextMediaType,
                       wt.work_kind AS ContextWorkKind,
                       wt.ordinal AS ContextOrdinal,
                       parent.ordinal AS ParentOrdinal,
                       wt.depth AS ContextDepth,
                       COALESCE(
                           (
                               SELECT cv.value
                               FROM canonical_values cv
                               WHERE cv.entity_id = ma.id
                                 AND cv.key IN ('episode_title', 'title', 'track_title', 'show_name', 'album')
                                 AND NULLIF(cv.value, '') IS NOT NULL
                               ORDER BY CASE cv.key
                                   WHEN 'episode_title' THEN 0
                                   WHEN 'track_title' THEN 1
                                   WHEN 'title' THEN 2
                                   WHEN 'show_name' THEN 3
                                   ELSE 4
                               END
                               LIMIT 1
                           ),
                           (
                               SELECT cv.value
                               FROM canonical_values cv
                               WHERE cv.entity_id = wt.id
                                 AND cv.key IN ('episode_title', 'title', 'track_title', 'show_name', 'album')
                                 AND NULLIF(cv.value, '') IS NOT NULL
                               ORDER BY CASE cv.key
                                   WHEN 'episode_title' THEN 0
                                   WHEN 'track_title' THEN 1
                                   WHEN 'title' THEN 2
                                   WHEN 'show_name' THEN 3
                                   ELSE 4
                               END
                               LIMIT 1
                           )
                       ) AS ContextTitle
                FROM work_tree wt
                INNER JOIN editions e ON e.work_id = wt.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                INNER JOIN system_activity sa ON sa.entity_id = ma.id
                LEFT JOIN works parent ON parent.id = wt.parent_work_id
            )
            SELECT *
            FROM scoped_activity
            ORDER BY OccurredAt DESC
            LIMIT 200;
            """,
            new { workId },
            cancellationToken: ct));

        IReadOnlyList<LibraryItemHistoryEntry> history = rows
            .GroupBy(row => IsRepeatableNarrativeEvent(row.EventType)
                ? $"{row.EntityId:D}|{row.EventType}|{row.Detail}"
                : row.Id.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(row => row.OccurredAt).First())
            .OrderByDescending(row => row.OccurredAt)
            .Select(row => new LibraryItemHistoryEntry(
                row.Id.ToString(),
                row.EntityId ?? workId,
                row.OccurredAt,
                row.EventType ?? string.Empty,
                FormatActionTypeLabel(row.EventType ?? string.Empty),
                FormatHistoryDetail(row.EventType, row.Detail, BuildHistoryContext(row)),
                ResolveHistoryCategory(row.EventType),
                row.ProfileId.HasValue ? "You" : "System"))
            .ToList();
        return Task.FromResult(history);
    }

    private static string FormatActionTypeLabel(string actionType) => actionType switch
    {
        "FileDetected" => "File detected",
        "FileIngested" => "File ingested",
        "MetadataExtracted" => "Metadata extracted",
        "ConfidenceScored" => "Confidence scored",
        "MovedToStaging" => "Moved to staging",
        "Promoted" => "Promoted to library",
        "ReviewItemCreated" => "Sent for review",
        "ReviewItemResolved" => "Review resolved",
        "HydrationStarted" => "Enrichment started",
        "HydrationCompleted" => "Enrichment complete",
        "WikidataMatched" => "Identified on Wikidata",
        "WikidataMatchFailed" => "No Wikidata match found",
        "RetailEnriched" => "Cover art retrieved",
        "RetailEnrichFailed" => "No cover art found",
        "MetadataManualOverride" => "Manual metadata override",
        "MetadataWrittenToFile" => "Metadata written to file",
        "ArtworkWrittenToFile" => "Artwork embedded in file",
        "ArtworkWritebackFailed" => "Artwork file write-back failed",
        "CoverArtSaved" => "Cover art saved",
        "HeroBannerGenerated" => "Hero banner generated",
        "CollectionCreated" => "Collection created",
        "CollectionAssigned" => "Assigned to collection",
        "PersonHydrated" => "Person enriched",
        "FileRejected" => "Rejected",
        "Recovered" => "Recovered from rejection",
        "FileHashed" => "Content fingerprinted",
        "DuplicateSkipped" => "Duplicate skipped",
        "EntityChainCreated" => "Library records created",
        "HydrationEnqueued" => "Queued for enrichment",
        "NarrativeRootResolved" => "TV show relationship confirmed",
        "PathUpdated" => "File location updated",
        "FileScored" => "File quality checked",
        "IdentityResolved" => "Identity confirmed",
        "ArtworkUpdated" => "Artwork updated",
        "MatchUpdated" => "Match updated",
        _ => HumanizeActionType(actionType),
    };

    private static bool IsRepeatableNarrativeEvent(string? eventType) =>
        string.Equals(eventType, "NarrativeRootResolved", StringComparison.OrdinalIgnoreCase);

    private static string ResolveHistoryCategory(string? eventType)
    {
        var value = eventType ?? string.Empty;
        if (value.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || value.Contains("reject", StringComparison.OrdinalIgnoreCase)
            || value.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }

        if (value.Contains("artwork", StringComparison.OrdinalIgnoreCase)
            || value.Contains("cover", StringComparison.OrdinalIgnoreCase)
            || value.Contains("hero", StringComparison.OrdinalIgnoreCase))
        {
            return "artwork";
        }

        if (value.Contains("match", StringComparison.OrdinalIgnoreCase)
            || value.Contains("identity", StringComparison.OrdinalIgnoreCase)
            || value.Contains("narrative", StringComparison.OrdinalIgnoreCase))
        {
            return "match";
        }

        if (value.Contains("review", StringComparison.OrdinalIgnoreCase)
            || value.Contains("recover", StringComparison.OrdinalIgnoreCase))
        {
            return "review";
        }

        if (value.Contains("file", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ingest", StringComparison.OrdinalIgnoreCase)
            || value.Contains("path", StringComparison.OrdinalIgnoreCase)
            || value.Contains("hash", StringComparison.OrdinalIgnoreCase))
        {
            return "file";
        }

        if (value.Contains("manual", StringComparison.OrdinalIgnoreCase)
            || value.Contains("note", StringComparison.OrdinalIgnoreCase))
        {
            return "manual";
        }

        return "metadata";
    }

    private static string? BuildHistoryContext(HistoryRow row)
    {
        if (row.ContextDepth <= 0)
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(row.ContextTitle) ? null : row.ContextTitle.Trim();
        if (string.Equals(row.ContextMediaType, "Music", StringComparison.OrdinalIgnoreCase))
        {
            var track = row.ContextOrdinal.HasValue ? $"Track {row.ContextOrdinal.Value}" : "Track";
            return title is null ? track : $"{track}: {title}";
        }

        if (string.Equals(row.ContextMediaType, "TV", StringComparison.OrdinalIgnoreCase))
        {
            var episode = row.ContextOrdinal.HasValue ? $"E{row.ContextOrdinal.Value}" : "Episode";
            var position = row.ParentOrdinal.HasValue ? $"S{row.ParentOrdinal.Value} {episode}" : episode;
            return title is null ? position : $"{position}: {title}";
        }

        return title;
    }

    private static string? FormatHistoryDetail(string? eventType, string? detail, string? context = null)
    {
        string? formatted;
        if (string.Equals(eventType, "NarrativeRootResolved", StringComparison.OrdinalIgnoreCase))
        {
            formatted = "Connected this episode to its TV show using the library's identified series metadata.";
        }
        else if (string.Equals(eventType, "PathUpdated", StringComparison.OrdinalIgnoreCase))
        {
            formatted = "The library refreshed the stored file location.";
        }
        else if (string.Equals(eventType, "FileScored", StringComparison.OrdinalIgnoreCase))
        {
            formatted = "The file was checked for metadata completeness and media quality.";
        }
        else
        {
            formatted = detail;
        }

        if (string.IsNullOrWhiteSpace(context))
        {
            return formatted;
        }

        return string.IsNullOrWhiteSpace(formatted) ? context : $"{context} — {formatted}";
    }

    private static string HumanizeActionType(string actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return "Library activity";
        }

        var spaced = actionType.Replace('_', ' ').Replace('-', ' ');
        spaced = Regex.Replace(spaced, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }

    private sealed class HistoryRow
    {
        public long Id { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public string? EventType { get; init; }
        public Guid? EntityId { get; init; }
        public Guid? ProfileId { get; init; }
        public string? Detail { get; init; }
        public Guid ContextWorkId { get; init; }
        public string? ContextMediaType { get; init; }
        public string? ContextWorkKind { get; init; }
        public int? ContextOrdinal { get; init; }
        public int? ParentOrdinal { get; init; }
        public int ContextDepth { get; init; }
        public string? ContextTitle { get; init; }
    }
}
