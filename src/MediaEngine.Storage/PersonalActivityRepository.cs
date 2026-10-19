using System.Globalization;
using System.Text;
using Dapper;
using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IPersonalActivityRepository"/>, backed by <c>personal_activity</c> and <c>personal_activity_artwork</c>.</summary>
public sealed class PersonalActivityRepository(IDatabaseConnection database) : IPersonalActivityRepository
{
    private const string EntrySelect = """
        a.id AS Id, a.profile_id AS ProfileId, p.display_name AS PersonName, a.kind AS Kind,
        a.telemetry_session_id AS TelemetrySessionId, a.music_session_id AS MusicSessionId,
        a.started_at AS StartedAt, a.ended_at AS EndedAt, a.played_seconds AS PlayedSeconds,
        a.progress_pct AS ProgressPct, a.finished AS Finished, a.device_name AS DeviceName, a.device_kind AS DeviceKind,
        a.work_id AS WorkId, a.asset_id AS AssetId, a.media_type AS MediaType, a.title AS Title,
        a.subtitle AS Subtitle, a.parent_title AS ParentTitle, a.year AS Year, a.artwork_key AS ArtworkKey
        """;

    private const string MusicSessionKey = "'S' || HEX(a.music_session_id)";

    public Task<bool> AddPlaybackAsync(
        Guid profileId,
        string kind,
        bool isMusic,
        Guid telemetrySessionId,
        DateTimeOffset startedAt,
        PersonalActivityDevice device,
        PersonalActivitySnapshot snapshot,
        PersonalActivityProgress progress,
        CancellationToken ct = default)
    {
        if (profileId == Guid.Empty || telemetrySessionId == Guid.Empty)
        {
            throw new ArgumentException("A profile and a playback session are required.");
        }

        if (kind is not (PersonalActivityKinds.Watch or PersonalActivityKinds.Listen or PersonalActivityKinds.Read))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Playback is watch, listen or read.");
        }

        ArgumentNullException.ThrowIfNull(snapshot);
        return database.ExecuteWriteAsync<bool>((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM personal_activity WHERE telemetry_session_id = @telemetrySessionId;",
                    new { telemetrySessionId }, transaction) > 0)
            {
                return false;
            }

            Guid? musicSessionId = null;
            if (isMusic)
            {
                musicSessionId = ReadOpenMusicSession(connection, transaction, profileId, startedAt) ?? Guid.CreateVersion7();
            }

            var inserted = connection.Execute("""
                INSERT INTO personal_activity
                  (id, profile_id, kind, telemetry_session_id, music_session_id, started_at, ended_at, played_seconds,
                   progress_pct, finished, device_name, device_kind, work_id, asset_id, media_type, title, subtitle,
                   parent_title, year, artwork_key)
                VALUES
                  (@id, @profileId, @kind, @telemetrySessionId, @musicSessionId, @startedAt, @endedAt, @playedSeconds,
                   @progressPct, @finished, @deviceName, @deviceKind, @workId, @assetId, @mediaType, @title, @subtitle,
                   @parentTitle, @year, @artworkKey)
                ON CONFLICT(telemetry_session_id) DO NOTHING;
                """, new
            {
                id = Guid.CreateVersion7(),
                profileId,
                kind,
                telemetrySessionId,
                musicSessionId,
                startedAt = Iso(startedAt),
                endedAt = Iso(progress.LastSeenAt < startedAt ? startedAt : progress.LastSeenAt),
                playedSeconds = Math.Max(0, progress.PlayedSeconds),
                progressPct = ClampPercent(progress.ProgressPct),
                finished = progress.Finished ? 1 : 0,
                deviceName = device.Name,
                deviceKind = device.Kind,
                workId = snapshot.WorkId,
                assetId = snapshot.AssetId,
                mediaType = snapshot.MediaType,
                title = snapshot.Title,
                subtitle = snapshot.Subtitle,
                parentTitle = snapshot.ParentTitle,
                year = snapshot.Year,
                artworkKey = KeptArtworkKey(connection, transaction, snapshot.ArtworkKey),
            }, transaction);
            return inserted > 0;
        }, ct);
    }

    public Task UpdateProgressAsync(Guid telemetrySessionId, PersonalActivityProgress progress, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                UPDATE personal_activity SET
                    ended_at = CASE WHEN ended_at IS NULL OR ended_at < @lastSeenAt THEN @lastSeenAt ELSE ended_at END,
                    played_seconds = MAX(played_seconds, @playedSeconds),
                    progress_pct = CASE
                        WHEN @progressPct IS NULL THEN progress_pct
                        WHEN progress_pct IS NULL OR progress_pct < @progressPct THEN @progressPct
                        ELSE progress_pct END,
                    finished = MAX(finished, @finished)
                WHERE telemetry_session_id = @telemetrySessionId;
                """, new
            {
                telemetrySessionId,
                lastSeenAt = Iso(progress.LastSeenAt),
                playedSeconds = Math.Max(0, progress.PlayedSeconds),
                progressPct = ClampPercent(progress.ProgressPct),
                finished = progress.Finished ? 1 : 0,
            }, transaction);
        }, ct);

    public Task AddMyListAsync(Guid profileId, DateTimeOffset at, PersonalActivitySnapshot snapshot, CancellationToken ct = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile is required.", nameof(profileId));
        }

        ArgumentNullException.ThrowIfNull(snapshot);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                INSERT INTO personal_activity
                  (id, profile_id, kind, started_at, ended_at, played_seconds, finished, work_id, asset_id, media_type,
                   title, subtitle, parent_title, year, artwork_key)
                VALUES
                  (@id, @profileId, 'my_list', @at, @at, 0, 0, @workId, @assetId, @mediaType,
                   @title, @subtitle, @parentTitle, @year, @artworkKey);
                """, new
            {
                id = Guid.CreateVersion7(),
                profileId,
                at = Iso(at),
                workId = snapshot.WorkId,
                assetId = snapshot.AssetId,
                mediaType = snapshot.MediaType,
                title = snapshot.Title,
                subtitle = snapshot.Subtitle,
                parentTitle = snapshot.ParentTitle,
                year = snapshot.Year,
                artworkKey = KeptArtworkKey(connection, transaction, snapshot.ArtworkKey),
            }, transaction);
        }, ct);
    }

    public Task<PersonalActivityItemPage> GetItemsAsync(
        PersonalActivityFilter filter,
        PersonalActivityGrouping grouping,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.ProfileIds is { Count: 0 })
        {
            return Task.FromResult(new PersonalActivityItemPage([], null));
        }

        limit = Math.Clamp(limit, 1, 200);
        var decoded = DecodeCursor(cursor);
        var (where, parameters) = Where(filter);
        var key = ItemKey(grouping);
        parameters.Add("cursorAt", decoded?.At);
        parameters.Add("cursorKey", decoded?.Key);
        parameters.Add("limit", limit + 1);

        using var connection = database.CreateConnection();
        var keys = connection.Query<ItemKeyRow>(ItemKeysSql(where, key), parameters).ToList();

        var hasMore = keys.Count > limit;
        if (hasMore)
        {
            keys.RemoveAt(keys.Count - 1);
        }

        if (keys.Count == 0)
        {
            return Task.FromResult(new PersonalActivityItemPage([], null));
        }

        parameters.Add("itemKeys", keys.Select(row => row.ItemKey).ToArray());
        var rows = connection.Query<EntryRow>($"""
            SELECT {EntrySelect}, {key} AS ItemKey
            FROM personal_activity a JOIN profiles p ON p.id = a.profile_id
            WHERE {where} AND {key} IN @itemKeys
            ORDER BY a.started_at, a.id;
            """, parameters).ToList();
        var byKey = rows.GroupBy(row => row.ItemKey!).ToDictionary(group => group.Key, group => group.Select(Map).ToList());

        var items = new List<PersonalActivityItem>(keys.Count);
        foreach (var row in keys)
        {
            if (!byKey.TryGetValue(row.ItemKey, out var entries))
            {
                continue;
            }

            var isSession = row.ItemKey.StartsWith('S');
            var isGroup = grouping == PersonalActivityGrouping.Title && !isSession;
            items.Add(new PersonalActivityItem(row.ItemKey, isSession, isGroup, entries));
        }

        var next = hasMore ? EncodeCursor(keys[^1].ItemAt, keys[^1].ItemKey) : null;
        return Task.FromResult(new PersonalActivityItemPage(items, next));
    }

    public Task<(IReadOnlyList<PersonalActivityEntry> Entries, string? NextCursor)> GetEntriesAsync(
        PersonalActivityFilter filter,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.ProfileIds is { Count: 0 })
        {
            return Task.FromResult<(IReadOnlyList<PersonalActivityEntry>, string?)>(([], null));
        }

        limit = Math.Clamp(limit, 1, 1000);
        var decoded = DecodeCursor(cursor);
        var (where, parameters) = Where(filter);
        parameters.Add("cursorAt", decoded?.At);
        Guid? cursorId = null;
        if (decoded is not null)
        {
            cursorId = Guid.TryParse(decoded.Key, out var parsed)
                ? parsed
                : throw new ArgumentException("The paging cursor is not valid.", nameof(cursor));
        }

        parameters.Add("cursorId", cursorId);
        parameters.Add("limit", limit + 1);

        using var connection = database.CreateConnection();
        var rows = connection.Query<EntryRow>($"""
            SELECT {EntrySelect}
            FROM personal_activity a JOIN profiles p ON p.id = a.profile_id
            WHERE {where}
              AND (@cursorAt IS NULL OR a.started_at < @cursorAt OR (a.started_at = @cursorAt AND a.id < @cursorId))
            ORDER BY a.started_at DESC, a.id DESC
            LIMIT @limit;
            """, parameters).ToList();
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var entries = rows.Select(Map).ToList();
        var next = hasMore ? EncodeCursor(rows[^1].StartedAt, rows[^1].Id.ToString("D")) : null;
        return Task.FromResult<(IReadOnlyList<PersonalActivityEntry>, string?)>((entries, next));
    }

    public Task<IReadOnlyList<PersonalActivityPeriodCount>> GetPeriodsAsync(
        PersonalActivityFilter filter,
        PersonalActivityGrouping grouping,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.ProfileIds is { Count: 0 })
        {
            return Task.FromResult<IReadOnlyList<PersonalActivityPeriodCount>>([]);
        }

        var (where, parameters) = Where(filter);
        using var connection = database.CreateConnection();
        var rows = connection.Query<PeriodRow>($"""
            SELECT CAST(SUBSTR(item_at, 1, 4) AS INTEGER) AS Year, CAST(SUBSTR(item_at, 6, 2) AS INTEGER) AS Month, COUNT(*) AS Count
            FROM (
                SELECT MAX(started_at) AS item_at FROM (
                    SELECT {ItemKey(grouping)} AS item_key, a.started_at AS started_at
                    FROM personal_activity a JOIN profiles p ON p.id = a.profile_id
                    WHERE {where}
                )
                GROUP BY item_key
            )
            GROUP BY Year, Month
            ORDER BY Year DESC, Month DESC;
            """, parameters).Select(row => new PersonalActivityPeriodCount(row.Year, row.Month, row.Count)).ToList();
        return Task.FromResult<IReadOnlyList<PersonalActivityPeriodCount>>(rows);
    }

    public Task<bool> HasArtworkAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM personal_activity_artwork WHERE key = @key;", new { key }) > 0);
    }

    public Task SaveArtworkAsync(PersonalActivityArtwork artwork, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                INSERT INTO personal_activity_artwork (key, image, content_type, width, height, created_at)
                VALUES (@Key, @Image, @ContentType, @Width, @Height, @createdAt)
                ON CONFLICT(key) DO NOTHING;
                """, new
            {
                artwork.Key,
                artwork.Image,
                artwork.ContentType,
                artwork.Width,
                artwork.Height,
                createdAt = Iso(DateTimeOffset.UtcNow),
            }, transaction);
        }, ct);
    }

    public Task<PersonalActivityArtwork?> GetArtworkAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QuerySingleOrDefault<ArtworkRow>(
            "SELECT key AS Key, image AS Image, content_type AS ContentType, width AS Width, height AS Height FROM personal_activity_artwork WHERE key = @key;",
            new { key });
        return Task.FromResult(row is null ? null : new PersonalActivityArtwork(row.Key, row.Image, row.ContentType, row.Width, row.Height));
    }

    public Task<bool> IsArtworkUsedAsync(string key, IReadOnlySet<Guid>? profileIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (profileIds is { Count: 0 })
        {
            return Task.FromResult(false);
        }

        var parameters = new DynamicParameters();
        parameters.Add("key", key);
        var people = ProfileClause(profileIds, parameters, "a");
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.ExecuteScalar<long>(
            $"SELECT COUNT(*) FROM personal_activity a WHERE a.artwork_key = @key AND {people};", parameters) > 0);
    }

    public Task<int> CountBeforeAsync(DateTimeOffset before, IReadOnlySet<Guid>? profileIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (profileIds is { Count: 0 })
        {
            return Task.FromResult(0);
        }

        var parameters = new DynamicParameters();
        parameters.Add("before", Iso(before));
        var people = ProfileClause(profileIds, parameters, "a");
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM personal_activity a WHERE a.started_at < @before AND {people};", parameters));
    }

    public Task<int> DeleteBeforeAsync(
        DateTimeOffset before,
        IReadOnlySet<Guid>? profileIds,
        Func<int, AuthorizationAuditEvent> audit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters();
            parameters.Add("before", Iso(before));
            var people = profileIds is { Count: 0 } ? "1 = 0" : ProfileClause(profileIds, parameters, "a");

            // Only pictures the removed entries used can become unused, and they are checked again after the delete
            // in this same transaction. A picture saved for an entry that is not written yet is never a candidate.
            var candidates = connection.Query<string>(
                $"SELECT DISTINCT a.artwork_key FROM personal_activity a WHERE a.started_at < @before AND a.artwork_key IS NOT NULL AND {people};",
                parameters, transaction).ToArray();
            var removed = connection.Execute(
                $"DELETE FROM personal_activity AS a WHERE a.started_at < @before AND {people};", parameters, transaction);
            if (candidates.Length > 0)
            {
                connection.Execute("""
                    DELETE FROM personal_activity_artwork
                    WHERE key IN @candidates
                      AND key NOT IN (SELECT artwork_key FROM personal_activity WHERE artwork_key IS NOT NULL);
                    """, new { candidates }, transaction);
            }

            // The record of the deletion commits or rolls back with the deletion: never one without the other.
            AccountRepository.InsertAuthorizationAudit(connection, transaction, audit(removed));
            return removed;
        }, ct);
    }

    public Task<PersonalActivitySummary> GetSummaryAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var totals = connection.QuerySingle<SummaryRow>("""
            SELECT COALESCE(SUM(CASE WHEN kind = 'watch' THEN played_seconds END), 0) AS WatchSeconds,
                   COALESCE(SUM(CASE WHEN kind = 'read' THEN played_seconds END), 0) AS ReadSeconds,
                   COALESCE(SUM(CASE WHEN kind = 'listen' AND media_type = 'Music' THEN played_seconds END), 0) AS MusicSeconds,
                   COALESCE(SUM(CASE WHEN kind = 'listen' AND media_type <> 'Music' THEN played_seconds END), 0) AS AudiobookSeconds,
                   MIN(started_at) AS FirstActivityAt
            FROM personal_activity WHERE profile_id = @profileId;
            """, new { profileId });

        // Titles, not songs or My list adds: a title is finished when any attempt finished, and in progress when its
        // latest attempt did not.
        var titles = connection.QuerySingle<TitleCounts>("""
            SELECT COALESCE(SUM(CASE WHEN any_finished = 1 THEN 1 ELSE 0 END), 0) AS Finished,
                   COALESCE(SUM(CASE WHEN latest_finished = 0 THEN 1 ELSE 0 END), 0) AS InProgress
            FROM (
                SELECT MAX(finished) AS any_finished,
                       (SELECT b.finished FROM personal_activity b
                        WHERE b.profile_id = @profileId AND b.kind IN ('watch', 'read', 'listen') AND b.media_type <> 'Music'
                          AND COALESCE(HEX(b.work_id), 'T' || LOWER(b.title)) = title_key
                        ORDER BY b.started_at DESC, b.id DESC LIMIT 1) AS latest_finished
                FROM (
                    SELECT finished, COALESCE(HEX(work_id), 'T' || LOWER(title)) AS title_key
                    FROM personal_activity
                    WHERE profile_id = @profileId AND kind IN ('watch', 'read', 'listen') AND media_type <> 'Music'
                )
                GROUP BY title_key
            );
            """, new { profileId });
        return Task.FromResult(new PersonalActivitySummary(
            totals.WatchSeconds, totals.ReadSeconds, totals.MusicSeconds, totals.AudiobookSeconds,
            titles.Finished, titles.InProgress,
            totals.FirstActivityAt is null ? null : Parse(totals.FirstActivityAt)));
    }

    /// <summary>
    /// The picture key to store on a new entry: the given key when that picture still exists, checked in the writing
    /// transaction, otherwise none. A delete of history that ran in between can remove a picture no entry used yet;
    /// the entry is then kept without a picture and never points at a missing one.
    /// </summary>
    private static string? KeptArtworkKey(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, string? key) =>
        key is not null && connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM personal_activity_artwork WHERE key = @key;", new { key }, transaction) > 0
            ? key
            : null;

    /// <summary>The open music session of this person, when their latest activity is a song that ended less than 30 minutes before this one starts.</summary>
    private static Guid? ReadOpenMusicSession(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        Guid profileId,
        DateTimeOffset startedAt)
    {
        var latest = connection.QueryFirstOrDefault<LatestRow>("""
            SELECT music_session_id AS MusicSessionId, started_at AS StartedAt, ended_at AS EndedAt
            FROM personal_activity
            WHERE profile_id = @profileId AND kind <> 'my_list' AND started_at <= @startedAt
            ORDER BY started_at DESC, id DESC
            LIMIT 1;
            """, new { profileId, startedAt = Iso(startedAt) }, transaction);
        if (latest?.MusicSessionId is not { } session)
        {
            return null;
        }

        var lastSeen = Parse(latest.EndedAt ?? latest.StartedAt);
        return startedAt - lastSeen < IPersonalActivityRepository.MusicSessionGap ? session : null;
    }

    /// <summary>The query that finds the keys of one page of items, newest first.</summary>
    internal static string ItemKeysSql(string where, string key) => $"""
        SELECT item_key AS ItemKey, MAX(started_at) AS ItemAt FROM (
            SELECT {key} AS item_key, a.started_at AS started_at
            FROM personal_activity a JOIN profiles p ON p.id = a.profile_id
            WHERE {where}
        )
        GROUP BY item_key
        HAVING @cursorAt IS NULL OR MAX(started_at) < @cursorAt
            OR (MAX(started_at) = @cursorAt AND item_key < @cursorKey)
        ORDER BY ItemAt DESC, item_key DESC
        LIMIT @limit;
        """;

    internal static (string Sql, DynamicParameters Parameters) Where(PersonalActivityFilter filter)
    {
        var parameters = new DynamicParameters();
        var clauses = new List<string> { ProfileClause(filter.ProfileIds, parameters, "a") };

        if (filter.Kinds is { Count: > 0 } kinds)
        {
            parameters.Add("kinds", kinds.ToArray());
            clauses.Add("a.kind IN @kinds");
        }

        if (filter.From is { } from)
        {
            parameters.Add("from", Iso(from));
            clauses.Add("a.started_at >= @from");
        }

        if (filter.To is { } to)
        {
            parameters.Add("to", Iso(to));
            clauses.Add("a.started_at < @to");
        }

        if (filter.Finished is { } finished)
        {
            // Added-to-My-list entries have no outcome, so they only match "finished"-neutral searches.
            clauses.Add(finished ? "a.finished = 1" : "(a.finished = 0 AND a.kind <> 'my_list')");
        }

        if (!string.IsNullOrWhiteSpace(filter.Device))
        {
            parameters.Add("device", filter.Device.Trim());
            clauses.Add("a.device_name = @device COLLATE NOCASE");
        }

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            parameters.Add("query", "%" + EscapeLike(filter.Query.Trim()) + "%");
            var columns = new List<string> { "a.title", "a.subtitle", "a.parent_title", "a.device_name" };
            if (filter.SearchPeople)
            {
                columns.Add("p.display_name");
            }

            clauses.Add("(" + string.Join(" OR ", columns.Select(column => $"{column} LIKE @query ESCAPE '\\'")) + ")");
        }

        return (string.Join(" AND ", clauses), parameters);
    }

    private static string ProfileClause(IReadOnlySet<Guid>? profileIds, DynamicParameters parameters, string alias)
    {
        if (profileIds is null)
        {
            return "1 = 1";
        }

        // The ids are bound as they are stored (BLOB), so the profile index can be used.
        var names = new List<string>(profileIds.Count);
        foreach (var id in profileIds)
        {
            var name = "profile" + names.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(name, GuidSql.ToBlob(id), System.Data.DbType.Binary);
            names.Add("@" + name);
        }

        return $"{alias}.profile_id IN ({string.Join(',', names)})";
    }

    internal static string ItemKey(PersonalActivityGrouping grouping) => grouping == PersonalActivityGrouping.Title
        ? $"""
            CASE WHEN a.music_session_id IS NOT NULL THEN {MusicSessionKey}
                 WHEN a.work_id IS NOT NULL THEN 'W' || HEX(a.profile_id) || HEX(a.work_id)
                 ELSE 'T' || HEX(a.profile_id) || LOWER(a.title) END
            """
        : $"CASE WHEN a.music_session_id IS NOT NULL THEN {MusicSessionKey} ELSE 'E' || HEX(a.id) END";

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static double? ClampPercent(double? value) =>
        value is { } number && double.IsFinite(number) ? Math.Clamp(number, 0d, 100d) : null;

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string EncodeCursor(string at, string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{at}|{key}"));

    private static ItemCursor? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var split = text.IndexOf('|', StringComparison.Ordinal);
            if (split <= 0 || split == text.Length - 1)
            {
                throw new ArgumentException("The paging cursor is not valid.", nameof(cursor));
            }

            return new ItemCursor(text[..split], text[(split + 1)..]);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The paging cursor is not valid.", nameof(cursor), exception);
        }
    }

    private static PersonalActivityEntry Map(EntryRow row) => new()
    {
        Id = row.Id,
        ProfileId = row.ProfileId,
        PersonName = row.PersonName,
        Kind = row.Kind,
        TelemetrySessionId = row.TelemetrySessionId,
        MusicSessionId = row.MusicSessionId,
        StartedAt = Parse(row.StartedAt),
        EndedAt = row.EndedAt is null ? null : Parse(row.EndedAt),
        PlayedSeconds = row.PlayedSeconds,
        ProgressPct = row.ProgressPct,
        Finished = row.Finished != 0,
        DeviceName = row.DeviceName,
        DeviceKind = row.DeviceKind,
        WorkId = row.WorkId,
        AssetId = row.AssetId,
        MediaType = row.MediaType,
        Title = row.Title,
        Subtitle = row.Subtitle,
        ParentTitle = row.ParentTitle,
        Year = row.Year,
        ArtworkKey = row.ArtworkKey,
    };

    private sealed record ItemCursor(string At, string Key);

    private sealed class SummaryRow
    {
        public long WatchSeconds { get; set; }
        public long ReadSeconds { get; set; }
        public long MusicSeconds { get; set; }
        public long AudiobookSeconds { get; set; }
        public string? FirstActivityAt { get; set; }
    }

    private sealed class TitleCounts
    {
        public int Finished { get; set; }
        public int InProgress { get; set; }
    }

    private sealed class ItemKeyRow
    {
        public string ItemKey { get; set; } = string.Empty;
        public string ItemAt { get; set; } = string.Empty;
    }

    private sealed class PeriodRow
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public int Count { get; set; }
    }

    private sealed class LatestRow
    {
        public Guid? MusicSessionId { get; set; }
        public string StartedAt { get; set; } = string.Empty;
        public string? EndedAt { get; set; }
    }

    private sealed class ArtworkRow
    {
        public string Key { get; set; } = string.Empty;
        public byte[] Image { get; set; } = [];
        public string ContentType { get; set; } = string.Empty;
        public int? Width { get; set; }
        public int? Height { get; set; }
    }

    private sealed class EntryRow
    {
        public Guid Id { get; set; }
        public Guid ProfileId { get; set; }
        public string? PersonName { get; set; }
        public string Kind { get; set; } = string.Empty;
        public Guid? TelemetrySessionId { get; set; }
        public Guid? MusicSessionId { get; set; }
        public string StartedAt { get; set; } = string.Empty;
        public string? EndedAt { get; set; }
        public long PlayedSeconds { get; set; }
        public double? ProgressPct { get; set; }
        public long Finished { get; set; }
        public string? DeviceName { get; set; }
        public string? DeviceKind { get; set; }
        public Guid? WorkId { get; set; }
        public Guid? AssetId { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public string? ParentTitle { get; set; }
        public int? Year { get; set; }
        public string? ArtworkKey { get; set; }
        public string? ItemKey { get; set; }
    }
}
