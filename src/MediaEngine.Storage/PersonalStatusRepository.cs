using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
namespace MediaEngine.Storage;

public sealed class PersonalStatusRepository(IDatabaseConnection db) : IPersonalStatusRepository
{
    private sealed class AssetState
    {
        public Guid AssetId { get; set; }
        public Guid WorkId { get; set; }
        public string ContentHash { get; set; } = "";
        public double Progress { get; set; }
        public long Revision { get; set; }
        public string? Accessed { get; set; }
        public string? Properties { get; set; }
        public UserState State(Guid profile) => new()
        {
            UserId = profile,
            AssetId = AssetId,
            ContentHash = ContentHash,
            ProgressPct = Progress,
            Revision = Revision,
            LastAccessed = DateTimeOffset.TryParse(Accessed, out var date) ? date : DateTimeOffset.UnixEpoch,
            ExtendedProperties = string.IsNullOrEmpty(Properties) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(Properties)!
        };
    }
    // One row per (file, work): an episode covered by a combined file is its own work sharing the host file,
    // so counts follow works while state reads and writes follow files (see DistinctFiles).
    private static List<AssetState> DistinctFiles(IEnumerable<AssetState> assets) =>
        assets.DistinctBy(a => a.AssetId).ToList();
    private static List<AssetState> Assets(SqliteConnection conn, SqliteTransaction? tx, Guid profile, PersonalStatusTarget target)
    {
        if (target.MediaType == MediaType.Unknown)
        {
            return [];
        }

        return conn.Query<AssetState>("""
            WITH RECURSIVE scope(id) AS (
                SELECT id FROM works WHERE id=@Id
                UNION SELECT work_id FROM collection_items WHERE collection_id=@Id
                UNION SELECT w.id FROM works w JOIN scope s ON w.parent_work_id=s.id
            )
            SELECT DISTINCT ma.id AssetId, w.id WorkId, ma.content_hash ContentHash,
                CAST(COALESCE(us.progress_pct,0) AS REAL) Progress, COALESCE(us.revision,0) Revision,
                us.last_accessed Accessed, us.extended_properties Properties
            FROM scope s JOIN works w ON w.id=s.id JOIN work_owned_assets woa ON woa.work_id=w.id
            JOIN media_assets ma ON ma.id=woa.asset_id
            LEFT JOIN user_states us ON us.asset_id=ma.id AND us.user_id=@profile
            WHERE ma.status='Normal' AND ma.is_orphaned=0
            AND (@unrestricted=1 OR ma.id IN @allowedAssets) AND (
                (@media NOT IN ('Books','Audiobooks') AND w.media_type=@media)
                OR (@media IN ('Books','Audiobooks') AND w.media_type IN ('Books','Audiobooks')
                    AND CASE WHEN lower(ma.file_path_root) LIKE '%.m4b' OR lower(ma.file_path_root) LIKE '%.m4a'
                        OR lower(ma.file_path_root) LIKE '%.mp3' OR lower(ma.file_path_root) LIKE '%.flac'
                        OR lower(ma.file_path_root) LIKE '%.ogg' OR lower(ma.file_path_root) LIKE '%.wav'
                        THEN @media='Audiobooks' ELSE @media='Books' END)
            )
            ORDER BY ma.id;
            """, new
        {
            target.Id,
            profile,
            media = target.MediaType.ToString(),
            unrestricted = target.AuthorizedAssetIds is null ? 1 : 0,
            allowedAssets = (target.AuthorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray()
        }, tx).ToList();
    }
    private static string Revision(IEnumerable<UserState> states) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join(";", states.OrderBy(s => s.AssetId).Select(s => $"{s.AssetId:D}:{s.Revision}")))));
    public Task<PersonalStatusSnapshot> ReadAsync(Guid profileId, PersonalStatusTarget target, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var assets = Assets(conn, null, profileId, target);
        var works = assets.GroupBy(a => a.WorkId).ToList();
        return Task.FromResult(new PersonalStatusSnapshot(Revision(DistinctFiles(assets).Select(a => a.State(profileId))), works.Count,
            works.Count(g => g.OrderByDescending(a => a.Accessed, StringComparer.Ordinal).ThenBy(a => a.AssetId).First().Progress > 0), works.Count(g => g.OrderByDescending(a => a.Accessed, StringComparer.Ordinal).ThenBy(a => a.AssetId).First().Progress >= 99.5),
            assets.Count > 0 && assets.All(a => a.State(profileId).ExtendedProperties.GetValueOrDefault("hide_continue") == "true")));
    }
    public Task<PersonalStatusResult> ExecuteAsync(Guid profileId, PersonalStatusTarget target, PersonalStatusCommand command,
        Guid commandId, string expectedRevision, CancellationToken ct = default) => db.ExecuteWriteAsync((conn, tx, token) =>
        {
            var existing = conn.QueryFirstOrDefault<string>("SELECT snapshot_json FROM personal_status_commands WHERE id=@commandId AND profile_id=@profileId AND target_id=@Id AND media_type=@media AND command=@command",
                new { commandId, profileId, target.Id, media = target.MediaType.ToString(), command = command.ToString() }, tx);
            if (existing is not null)
            {
                var prior = JsonSerializer.Deserialize<PersonalStatusUndo>(existing)!;
                RequireSnapshotAccess(prior, target.AuthorizedAssetIds);
                return new PersonalStatusResult(commandId, prior.OwnedCount, Revision(prior.After));
            }
            if (!Enum.IsDefined(command))
            {
                throw new ArgumentException("Unknown status command.");
            }

            var assets = Assets(conn, tx, profileId, target);
            if (assets.Count == 0)
            {
                throw new InvalidOperationException("No owned media in this scope.");
            }

            var before = DistinctFiles(assets).Select(a => a.State(profileId)).ToList();
            if (Revision(before) != expectedRevision)
            {
                throw new StateRevisionConflictException();
            }

            var after = before.Select(s => JsonSerializer.Deserialize<UserState>(JsonSerializer.Serialize(s))!).ToList();
            foreach (var state in after)
            {
                if (command is PersonalStatusCommand.Complete or PersonalStatusCommand.Reset)
                {
                    // Preserve annotations, bookmarks and history; clear only active experience coordinates.
                    foreach (var key in new[] { "position_seconds", "playback_timestamp_ms", "last_page_read", "last_chapter", "cfi", "epub_cfi", "location", "current_page", "page", "track_index", "track_progress", "completed_tracks", "audiobook_start_kind", "chapter_index", "page_in_chapter", "total_pages_in_chapter" })
                    {
                        state.ExtendedProperties.Remove(key);
                    }

                    var activeSession = conn.QueryFirstOrDefault<Guid?>("""
                    SELECT ps.session_id FROM player_sessions ps JOIN player_queue_items qi ON qi.id=ps.current_queue_item_id
                    WHERE ps.profile_id=@profileId AND qi.asset_id=@AssetId;
                    """, new { profileId, state.AssetId }, tx);
                    if (activeSession.HasValue)
                    {
                        state.ExtendedProperties["player_session_id"] = activeSession.Value.ToString("D");
                    }

                    if (state.ExtendedProperties.TryGetValue("player_session_id", out var session))
                    {
                        state.ExtendedProperties["blocked_player_session_id"] = session;
                    }

                    state.ExtendedProperties["status_changed_at"] = DateTimeOffset.UtcNow.ToString("O");
                    state.ExtendedProperties["manual_completion"] = command == PersonalStatusCommand.Complete ? "true" : "false";
                    state.ProgressPct = command == PersonalStatusCommand.Complete ? 100 : 0;
                }
                else
                {
                    state.ExtendedProperties["hide_continue"] = command == PersonalStatusCommand.HideContinue ? "true" : "false";
                }

                state.Revision++;
                Write(conn, tx, state);
            }
            if (command is PersonalStatusCommand.Complete or PersonalStatusCommand.Reset)
            {
                conn.Execute("""
                UPDATE player_sessions SET session_id=@newSession,playback_state='stopped',position_seconds=0,
                    progress_pct=0,state_version=state_version+1
                WHERE profile_id=@profileId AND current_queue_item_id IN
                    (SELECT id FROM player_queue_items WHERE profile_id=@profileId AND asset_id IN @assetIds);
                """, new { profileId, newSession = Guid.NewGuid(), assetIds = after.Select(s => GuidSql.ToBlob(s.AssetId)).ToArray() }, tx);
            }

            var count = assets.Select(a => a.WorkId).Distinct().Count();
            conn.Execute("""
            INSERT INTO personal_status_commands(id,profile_id,target_id,media_type,command,changed_at,snapshot_json)
            VALUES(@commandId,@profileId,@Id,@media,@command,@date,@snapshot);
            """, new
            {
                commandId,
                profileId,
                target.Id,
                media = target.MediaType.ToString(),
                command = command.ToString(),
                date = DateTimeOffset.UtcNow.ToString("O"),
                snapshot = JsonSerializer.Serialize(new PersonalStatusUndo(before, after, count))
            }, tx);
            return new PersonalStatusResult(commandId, count, Revision(after));
        }, ct);
    private static void Write(SqliteConnection conn, SqliteTransaction tx, UserState state) => conn.Execute("""
        INSERT INTO user_states(user_id,asset_id,content_hash,progress_pct,last_accessed,extended_properties,revision)
        VALUES(@UserId,@AssetId,@ContentHash,@ProgressPct,@date,@json,@Revision)
        ON CONFLICT(user_id,asset_id) DO UPDATE SET progress_pct=excluded.progress_pct,
          extended_properties=excluded.extended_properties,revision=excluded.revision;
        """, new
    {
        state.UserId,
        state.AssetId,
        state.ContentHash,
        state.ProgressPct,
        state.Revision,
        date = state.LastAccessed.ToString("O"),
        json = JsonSerializer.Serialize(state.ExtendedProperties)
    }, tx);
    public Task<PersonalStatusResult> UndoAsync(Guid profileId, Guid commandId, CancellationToken ct = default,
        IReadOnlySet<Guid>? authorizedAssetIds = null) => db.ExecuteWriteAsync((conn, tx, token) =>
    {
        var json = conn.QueryFirstOrDefault<string>("SELECT snapshot_json FROM personal_status_commands WHERE id=@commandId AND profile_id=@profileId AND undone=0", new { commandId, profileId }, tx)
            ?? throw new InvalidOperationException("This change is unavailable or already undone.");
        var snapshot = JsonSerializer.Deserialize<PersonalStatusUndo>(json)!;
        RequireSnapshotAccess(snapshot, authorizedAssetIds);
        foreach (var state in snapshot.After)
        {
            var revision = conn.QuerySingleOrDefault<long>("SELECT revision FROM user_states WHERE user_id=@profileId AND asset_id=@AssetId", new { profileId, state.AssetId }, tx);
            if (revision != state.Revision)
            {
                throw new StateRevisionConflictException();
            }
        }
        foreach (var state in snapshot.Before) { state.Revision += 2; Write(conn, tx, state); }
        conn.Execute("UPDATE personal_status_commands SET undone=1 WHERE id=@commandId", new { commandId }, tx);
        return new PersonalStatusResult(commandId, snapshot.OwnedCount, Revision(snapshot.Before));
    }, ct);
    public Task<IReadOnlyList<PersonalStatusHistory>> HistoryAsync(Guid profileId, PersonalStatusTarget target, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var assetIds = Assets(conn, null, profileId, target).Select(a => GuidSql.ToBlob(a.AssetId)).ToArray();
        var rows = conn.Query<(Guid Id, string Command, string ChangedAt, long Undone, string? Snapshot)>(new CommandDefinition("""
            SELECT id,command,changed_at,undone,snapshot_json FROM personal_status_commands
            WHERE profile_id=@profileId AND target_id=@Id AND media_type=@media
            UNION ALL
            SELECT id,experience,updated_at,0,NULL FROM consumption_history
            WHERE profile_id=@profileId AND asset_id IN @assetIds
            ORDER BY changed_at DESC;
            """, new { profileId, target.Id, media = target.MediaType.ToString(), assetIds }, cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<PersonalStatusHistory>>(rows
            .Where(r => r.Snapshot is null || HasSnapshotAccess(JsonSerializer.Deserialize<PersonalStatusUndo>(r.Snapshot)!, target.AuthorizedAssetIds))
            .Select(r => new PersonalStatusHistory(r.Id, r.Command, DateTimeOffset.Parse(r.ChangedAt), r.Undone != 0)).ToList());
    }

    private static bool HasSnapshotAccess(PersonalStatusUndo snapshot, IReadOnlySet<Guid>? allowed) =>
        allowed is null || snapshot.Before.Concat(snapshot.After).All(state => allowed.Contains(state.AssetId));

    private static void RequireSnapshotAccess(PersonalStatusUndo snapshot, IReadOnlySet<Guid>? allowed)
    {
        if (!HasSnapshotAccess(snapshot, allowed))
        {
            throw new InvalidOperationException("This change is unavailable in the current library scope.");
        }
    }
}
