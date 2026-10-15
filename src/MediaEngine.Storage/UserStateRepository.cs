using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IUserStateStore"/>.
///
/// Tracks user progress (reading position, playback timestamp, completion %)
/// for each media asset.  Extended properties are serialised as a JSON blob.
///
/// Spec: Phase 2 – IUserStateStore.
/// </summary>
public sealed class UserStateRepository : IUserStateStore
{
    private readonly IDatabaseConnection _db;

    public UserStateRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<UserState?> GetAsync(Guid userId, Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<UserStateRow>("""
            SELECT user_id AS UserId, asset_id AS AssetId, content_hash AS ContentHash,
                   progress_pct AS ProgressPct, last_accessed AS LastAccessed,
                   extended_properties AS ExtendedProperties, revision AS Revision
            FROM   user_states
            WHERE  user_id  = @userId
              AND  asset_id = @assetId
            """, new { userId, assetId });

        return Task.FromResult(row is null ? null : (UserState?)MapRow(row));
    }

    /// <inheritdoc/>
    public Task SaveAsync(UserState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return _db.ExecuteWriteAsync((conn, tx, token) =>
        {
            var priorJson = conn.QueryFirstOrDefault<string>("SELECT extended_properties FROM user_states WHERE user_id=@UserId AND asset_id=@AssetId",
                new { state.UserId, state.AssetId }, tx);
            var prior = string.IsNullOrEmpty(priorJson) ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(priorJson)!;
            foreach (var key in new[] { "hide_continue", "blocked_player_session_id", "status_changed_at" })
            {
                if (prior.TryGetValue(key, out var value))
                {
                    state.ExtendedProperties[key] = value;
                }
            }

            var changed = conn.Execute("""
                INSERT INTO user_states(user_id, asset_id, content_hash, progress_pct, last_accessed, extended_properties, revision)
                SELECT @UserId, @AssetId, @ContentHash, @ProgressPct, @accessed, @properties, 1
                WHERE @Revision=0 OR EXISTS(SELECT 1 FROM user_states WHERE user_id=@UserId AND asset_id=@AssetId)
                ON CONFLICT(user_id,asset_id) DO UPDATE SET content_hash=excluded.content_hash,
                  progress_pct=excluded.progress_pct, last_accessed=excluded.last_accessed,
                  extended_properties=excluded.extended_properties, revision=user_states.revision+1
                WHERE user_states.revision=@Revision;
                """, new
            {
                state.UserId,
                state.AssetId,
                state.ContentHash,
                state.ProgressPct,
                state.Revision,
                accessed = state.LastAccessed.ToUniversalTime().ToString("O"),
                properties = JsonSerializer.Serialize(state.ExtendedProperties)
            }, tx);
            if (changed != 1)
            {
                throw new StateRevisionConflictException();
            }

            state.Revision++;
            var sessionKey = state.ExtendedProperties.GetValueOrDefault("player_session_id")
                ?? state.ExtendedProperties.GetValueOrDefault("reader_session_id");
            if (state.ProgressPct > 0 && !string.IsNullOrWhiteSpace(sessionKey))
            {
                conn.Execute("""
                    INSERT INTO consumption_history(id,profile_id,asset_id,session_key,experience,started_at,updated_at,progress_pct)
                    VALUES(@id,@UserId,@AssetId,@sessionKey,@experience,@date,@date,@ProgressPct)
                    ON CONFLICT(profile_id,asset_id,session_key) DO UPDATE SET updated_at=excluded.updated_at,progress_pct=excluded.progress_pct;
                    """, new
                {
                    id = Guid.NewGuid(),
                    state.UserId,
                    state.AssetId,
                    sessionKey,
                    state.ProgressPct,
                    experience = state.ExtendedProperties.ContainsKey("reader_session_id") ? "Read" : "Played",
                    date = state.LastAccessed.ToUniversalTime().ToString("O")
                }, tx);
            }
        }, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<UserState>> FindByContentHashAsync(
        string contentHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        using var conn = _db.CreateConnection();
        var rows = conn.Query<UserStateRow>("""
            SELECT user_id AS UserId, asset_id AS AssetId, content_hash AS ContentHash,
                   progress_pct AS ProgressPct, last_accessed AS LastAccessed,
                   extended_properties AS ExtendedProperties, revision AS Revision
            FROM   user_states
            WHERE  content_hash = @contentHash
            """, new { contentHash }).AsList();

        return Task.FromResult<IReadOnlyList<UserState>>(rows.Select(MapRow).ToList());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<UserState>> GetRecentAsync(
        Guid userId, int limit = 50, CancellationToken ct = default) => GetRecentAuthorizedAsync(userId, null, limit, ct);

    public Task<IReadOnlyList<UserState>> GetRecentAuthorizedAsync(
        Guid userId, IReadOnlySet<Guid>? authorizedAssetIds, int limit = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<UserStateRow>("""
            SELECT user_id AS UserId, asset_id AS AssetId, content_hash AS ContentHash,
                   progress_pct AS ProgressPct, last_accessed AS LastAccessed,
                   extended_properties AS ExtendedProperties, revision AS Revision
            FROM   user_states
            WHERE  user_id = @userId
              AND (@unrestricted=1 OR asset_id IN @allowedAssets)
            ORDER BY last_accessed DESC
            LIMIT @limit
            """, new
        {
            userId,
            limit,
            unrestricted = authorizedAssetIds is null ? 1 : 0,
            allowedAssets = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray()
        }).AsList();

        return Task.FromResult<IReadOnlyList<UserState>>(rows.Select(MapRow).ToList());
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private sealed class UserStateRow
    {
        public long Revision { get; set; }
        public Guid UserId { get; set; }
        public Guid AssetId { get; set; }
        public string? ContentHash { get; set; }
        public double ProgressPct { get; set; }
        public string LastAccessed { get; set; } = "";
        public string? ExtendedProperties { get; set; }
    }

    private static UserState MapRow(UserStateRow r) => new()
    {
        Revision = r.Revision,
        UserId = r.UserId,
        AssetId = r.AssetId,
        ContentHash = r.ContentHash ?? string.Empty,
        ProgressPct = r.ProgressPct,
        LastAccessed = DateTimeOffset.Parse(r.LastAccessed),
        ExtendedProperties = string.IsNullOrEmpty(r.ExtendedProperties)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(r.ExtendedProperties) ?? [],
    };
}
