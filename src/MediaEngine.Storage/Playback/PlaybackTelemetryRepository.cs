using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Events;
using MediaEngine.Domain.Playback;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

public sealed class PlaybackTelemetryRepository(
    IDatabaseConnection database,
    IApplicationEventOutboxWriter eventOutbox) : IPlaybackTelemetryRepository
{
    private const string TelemetrySelect = """
        id AS Id,player_session_id AS PlayerSessionId,authority_session_id AS AuthoritySessionId,
        account_id AS AccountId,profile_id AS ProfileId,application_id AS ApplicationId,device_id AS DeviceId,
        asset_id AS AssetId,library_id AS LibraryId,feature_id AS FeatureId,media_type AS MediaType,state AS State,
        started_at AS StartedAt,last_observed_at AS LastObservedAt,ended_at AS EndedAt,
        started_position_seconds AS StartedPositionSeconds,last_position_seconds AS LastPositionSeconds,
        duration_seconds AS DurationSeconds,played_duration_seconds AS PlayedDurationSeconds,last_sequence AS LastSequence,
        completion_reason AS CompletionReason,delivery_mode AS DeliveryMode,container AS Container,
        video_codec AS VideoCodec,audio_codec AS AudioCodec,width AS Width,height AS Height,
        bitrate_kbps AS BitrateKbps,connection_type AS ConnectionType,client_name AS ClientName,client_version AS ClientVersion
        """;
    private const double MotionToleranceSeconds = 3d;
    private const double MaximumCreditedWallSeconds = 30d;

    public Task<IReadOnlyList<PlaybackTelemetryTransition>> ObserveAsync(
        PlaybackTelemetryObservation observation,
        CancellationToken ct = default)
    {
        Validate(observation);
        return database.ExecuteWriteAsync<IReadOnlyList<PlaybackTelemetryTransition>>((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var transitions = new List<PlaybackTelemetryTransition>(2);

            // Watching a Vault item leaves no trace outside Vault mode (Videos spec D33): no session row, no event for apps,
            // webhooks or plugins, nothing for an administrator's "who is watching" or analytics views. Prevented here, at the
            // one place playback is recorded, so no caller can forget the rule.
            if (connection.ExecuteScalar<long>(
                    $"SELECT CASE WHEN {VaultSql.NotVaultAsset("@assetId")} THEN 0 ELSE 1 END;",
                    new { assetId = observation.AssetId }, transaction) == 1)
            {
                return transitions;
            }

            var active = ReadActive(connection, transaction, observation.PlayerSessionId);

            if (active is not null && IsDuplicateOrOutOfOrder(active, observation))
            {
                transitions.Add(new PlaybackTelemetryTransition(active, active.State, false, true));
                return transitions;
            }

            if (active is not null && active.AssetId != observation.AssetId)
            {
                var replaced = Close(connection, transaction, active, observation.ObservedAt,
                    PlaybackCompletionReasons.Replaced, PlaybackTelemetryStates.Stopped);
                var replacedTransition = new PlaybackTelemetryTransition(replaced, active.State, false, false);
                transitions.Add(replacedTransition);
                AppendEvent(connection, transaction, replacedTransition, token);
                active = null;
            }

            if (active is null)
            {
                if (observation.State != PlaybackTelemetryStates.Playing || observation.HasPlaybackEnded)
                {
                    return transitions;
                }

                var created = Create(connection, transaction, observation);
                var createdTransition = new PlaybackTelemetryTransition(created, null, true, false);
                transitions.Add(createdTransition);
                AppendEvent(connection, transaction, createdTransition, token);
                return transitions;
            }

            var updated = Update(connection, transaction, active, observation);
            var updatedTransition = new PlaybackTelemetryTransition(updated, active.State, false, false);
            transitions.Add(updatedTransition);
            AppendEvent(connection, transaction, updatedTransition, token);
            return transitions;
        }, ct);
    }

    public Task<IReadOnlyList<PlaybackTelemetryTransition>> CloseStaleAsync(
        DateTimeOffset staleBefore,
        CancellationToken ct = default) =>
        database.ExecuteWriteAsync<IReadOnlyList<PlaybackTelemetryTransition>>((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var active = connection.Query<TelemetryRow>($"""
                SELECT {TelemetrySelect} FROM playback_telemetry_sessions
                WHERE ended_at IS NULL AND last_observed_at < @staleBefore
                ORDER BY last_observed_at, id;
                """, new { staleBefore = Iso(staleBefore) }, transaction).Select(Map).ToList();
            var results = new List<PlaybackTelemetryTransition>(active.Count);
            foreach (var session in active)
            {
                var closed = Close(connection, transaction, session, session.LastObservedAt,
                    PlaybackCompletionReasons.Stale, PlaybackTelemetryStates.Stopped);
                var transition = new PlaybackTelemetryTransition(closed, session.State, false, false);
                results.Add(transition);
                AppendEvent(connection, transaction, transition, token);
            }
            return results;
        }, ct);

    public Task<PlaybackTelemetryTransition?> CloseAsync(
        Guid playerSessionId,
        DateTimeOffset endedAt,
        string reason,
        CancellationToken ct = default)
    {
        if (playerSessionId == Guid.Empty)
        {
            throw new ArgumentException("A player session id is required.", nameof(playerSessionId));
        }

        reason = NormalizeCompletionReason(reason);
        return database.ExecuteWriteAsync<PlaybackTelemetryTransition?>((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var active = ReadActive(connection, transaction, playerSessionId);
            if (active is null)
            {
                return null;
            }

            var closed = Close(connection, transaction, active,
                endedAt < active.LastObservedAt ? active.LastObservedAt : endedAt,
                reason, PlaybackTelemetryStates.Stopped);
            var transition = new PlaybackTelemetryTransition(closed, active.State, false, false);
            AppendEvent(connection, transaction, transition, token);
            return transition;
        }, ct);
    }

    public Task<PlaybackTelemetryPage> GetActiveAsync(
        PlaybackTelemetryReadScope scope,
        int limit,
        string? cursor,
        CancellationToken ct = default) => Task.FromResult(ReadPage(scope, true, null, null, limit, cursor, ct));

    public Task<PlaybackTelemetryPage> GetHistoryAsync(
        PlaybackTelemetryReadScope scope,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        string? cursor,
        CancellationToken ct = default) => Task.FromResult(ReadPage(scope, false, from, to, limit, cursor, ct));

    public Task<PlaybackTelemetryAggregate> GetPlaybackAggregateAsync(
        PlaybackTelemetryReadScope scope,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanRead(scope))
        {
            return Task.FromResult(new PlaybackTelemetryAggregate(0, 0, 0, 0, 0, 0, 0, 0, 0));
        }

        using var connection = database.CreateConnection();
        var command = ScopeCommand(scope, from, to);
        var row = connection.QuerySingle<AggregateRow>($"""
            SELECT COUNT(*) AS SessionCount,
                   SUM(CASE WHEN state='completed' THEN 1 ELSE 0 END) AS CompletedCount,
                   COALESCE(SUM(played_duration_seconds),0) AS PlayedDurationSeconds,
                   COUNT(DISTINCT account_id) AS UniqueAccounts,
                   COUNT(DISTINCT profile_id) AS UniqueProfiles,
                   COUNT(DISTINCT asset_id) AS UniqueAssets,
                   SUM(CASE WHEN delivery_mode='direct-play' THEN 1 ELSE 0 END) AS DirectPlayCount,
                   SUM(CASE WHEN delivery_mode='remux' THEN 1 ELSE 0 END) AS RemuxCount,
                   SUM(CASE WHEN delivery_mode='transcode' THEN 1 ELSE 0 END) AS TranscodeCount
            FROM playback_telemetry_sessions
            WHERE {command.Where};
            """, command.Parameters);
        return Task.FromResult(new PlaybackTelemetryAggregate(
            row.SessionCount, row.CompletedCount, row.PlayedDurationSeconds, row.UniqueAccounts,
            row.UniqueProfiles, row.UniqueAssets, row.DirectPlayCount, row.RemuxCount, row.TranscodeCount));
    }

    public Task<IReadOnlyList<PlaybackTelemetryGroupAggregate>> GetGroupAggregatesAsync(
        PlaybackTelemetryReadScope scope,
        string dimension,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanRead(scope))
        {
            return Task.FromResult<IReadOnlyList<PlaybackTelemetryGroupAggregate>>([]);
        }

        var columns = dimension switch
        {
            "users" => (Select: "account_id AS AccountId, profile_id AS ProfileId, NULL AS LibraryId, NULL AS DeviceId", Group: "account_id,profile_id"),
            "libraries" => (Select: "NULL AS AccountId, NULL AS ProfileId, library_id AS LibraryId, NULL AS DeviceId", Group: "library_id"),
            "devices" => (Select: "NULL AS AccountId, NULL AS ProfileId, NULL AS LibraryId, device_id AS DeviceId", Group: "device_id"),
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        };
        using var connection = database.CreateConnection();
        var command = ScopeCommand(scope, from, to);
        command.Parameters.Add("limit", Math.Clamp(limit, 1, 200));
        var rows = connection.Query<GroupRow>($"""
            SELECT {columns.Select}, COUNT(*) AS SessionCount,
                   SUM(CASE WHEN state='completed' THEN 1 ELSE 0 END) AS CompletedCount,
                   COALESCE(SUM(played_duration_seconds),0) AS PlayedDurationSeconds,
                   MAX(COALESCE(ended_at,last_observed_at)) AS LastPlayedAt
            FROM playback_telemetry_sessions
            WHERE {command.Where}
            GROUP BY {columns.Group}
            ORDER BY PlayedDurationSeconds DESC, LastPlayedAt DESC
            LIMIT @limit;
            """, command.Parameters).Select(row => new PlaybackTelemetryGroupAggregate(
                row.AccountId, row.ProfileId, row.LibraryId, row.DeviceId, row.SessionCount,
                row.CompletedCount, row.PlayedDurationSeconds, Parse(row.LastPlayedAt))).ToList();
        return Task.FromResult<IReadOnlyList<PlaybackTelemetryGroupAggregate>>(rows);
    }

    private PlaybackTelemetryPage ReadPage(
        PlaybackTelemetryReadScope scope,
        bool active,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int requestedLimit,
        string? cursor,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanRead(scope))
        {
            return new([], null);
        }

        var limit = Math.Clamp(requestedLimit, 1, 200);
        var decoded = DecodeCursor(cursor);
        var command = ScopeCommand(scope, from, to);
        command.Parameters.Add("limit", limit + 1);
        command.Parameters.Add("cursorAt", decoded?.At);
        command.Parameters.Add("cursorId", decoded?.Id);
        var orderColumn = active ? "last_observed_at" : "ended_at";
        var cursorClause = decoded is null ? "1=1" :
            $"({orderColumn} < @cursorAt OR ({orderColumn} = @cursorAt AND id < @cursorId))";
        using var connection = database.CreateConnection();
        var rows = connection.Query<TelemetryRow>($"""
            SELECT {TelemetrySelect} FROM playback_telemetry_sessions
            WHERE {(active ? "ended_at IS NULL" : "ended_at IS NOT NULL")}
              AND {command.Where}
              AND {cursorClause}
            ORDER BY {orderColumn} DESC,id DESC
            LIMIT @limit;
            """, command.Parameters).Select(Map).ToList();
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = hasMore && rows.Count > 0
            ? EncodeCursor(active ? rows[^1].LastObservedAt : rows[^1].EndedAt!.Value, rows[^1].Id)
            : null;
        return new(rows, next);
    }

    private static PlaybackTelemetrySession Create(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        PlaybackTelemetryObservation observation)
    {
        var session = new PlaybackTelemetrySession
        {
            Id = Guid.CreateVersion7(),
            PlayerSessionId = observation.PlayerSessionId,
            AuthoritySessionId = observation.AuthoritySessionId,
            AccountId = observation.AccountId,
            ProfileId = observation.ProfileId,
            ApplicationId = observation.ApplicationId,
            DeviceId = observation.DeviceId,
            AssetId = observation.AssetId,
            LibraryId = observation.LibraryId,
            Feature = observation.Feature,
            MediaType = Bound(observation.MediaType, 64)!,
            State = PlaybackTelemetryStates.Playing,
            StartedAt = observation.ObservedAt,
            LastObservedAt = observation.ObservedAt,
            StartedPositionSeconds = observation.PositionSeconds,
            LastPositionSeconds = observation.PositionSeconds,
            DurationSeconds = observation.DurationSeconds,
            LastSequence = observation.Sequence ?? -1,
            Delivery = Normalize(observation.Delivery),
            Client = new(Bound(observation.Client.Name, 128), Bound(observation.Client.Version, 64)),
        };
        connection.Execute("""
            INSERT INTO playback_telemetry_sessions
              (id,player_session_id,authority_session_id,account_id,profile_id,application_id,device_id,
               asset_id,library_id,feature_id,media_type,state,started_at,last_observed_at,
               started_position_seconds,last_position_seconds,duration_seconds,played_duration_seconds,last_sequence,
               delivery_mode,container,video_codec,audio_codec,width,height,bitrate_kbps,connection_type,client_name,client_version)
            VALUES
              (@Id,@PlayerSessionId,@AuthoritySessionId,@AccountId,@ProfileId,@ApplicationId,@DeviceId,
               @AssetId,@LibraryId,@FeatureId,@MediaType,@State,@StartedAt,@LastObservedAt,
               @StartedPositionSeconds,@LastPositionSeconds,@DurationSeconds,0,@LastSequence,
               @DeliveryMode,@Container,@VideoCodec,@AudioCodec,@Width,@Height,@BitrateKbps,@ConnectionType,@ClientName,@ClientVersion);
            """, Parameters(session), transaction);
        return session;
    }

    private static PlaybackTelemetrySession Update(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        PlaybackTelemetrySession active,
        PlaybackTelemetryObservation observation)
    {
        var played = active.PlayedDurationSeconds + CreditedDuration(active, observation);
        var completed = observation.HasPlaybackEnded && observation.DurationSeconds is > 0 &&
            observation.PositionSeconds + 5d >= observation.DurationSeconds.Value;
        var shouldClose = completed || observation.CompletionReason is not null ||
            observation.State == PlaybackTelemetryStates.Stopped;
        var state = completed ? PlaybackTelemetryStates.Completed :
            shouldClose ? PlaybackTelemetryStates.Stopped : observation.State;
        var reason = completed ? PlaybackCompletionReasons.Completed :
            shouldClose ? NormalizeCompletionReason(observation.CompletionReason) : null;
        var delivery = Merge(active.Delivery, Normalize(observation.Delivery));
        var client = new PlaybackClientFacts(
            Bound(observation.Client.Name, 128) ?? active.Client.Name,
            Bound(observation.Client.Version, 64) ?? active.Client.Version);
        var updated = active with
        {
            State = state,
            LastObservedAt = observation.ObservedAt,
            EndedAt = shouldClose ? observation.ObservedAt : null,
            LastPositionSeconds = observation.PositionSeconds,
            DurationSeconds = observation.DurationSeconds ?? active.DurationSeconds,
            PlayedDurationSeconds = played,
            LastSequence = observation.Sequence ?? active.LastSequence,
            CompletionReason = reason,
            Delivery = delivery,
            Client = client,
        };
        connection.Execute("""
            UPDATE playback_telemetry_sessions SET
              state=@State,last_observed_at=@LastObservedAt,ended_at=@EndedAt,
              last_position_seconds=@LastPositionSeconds,duration_seconds=@DurationSeconds,
              played_duration_seconds=@PlayedDurationSeconds,last_sequence=@LastSequence,
              completion_reason=@CompletionReason,delivery_mode=@DeliveryMode,container=@Container,
              video_codec=@VideoCodec,audio_codec=@AudioCodec,width=@Width,height=@Height,
              bitrate_kbps=@BitrateKbps,connection_type=@ConnectionType,client_name=@ClientName,client_version=@ClientVersion
            WHERE id=@Id;
            """, Parameters(updated), transaction);
        return updated;
    }

    private static PlaybackTelemetrySession Close(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        PlaybackTelemetrySession active,
        DateTimeOffset endedAt,
        string reason,
        string state)
    {
        var closed = active with { State = state, EndedAt = endedAt, LastObservedAt = endedAt, CompletionReason = reason };
        connection.Execute("""
            UPDATE playback_telemetry_sessions
            SET state=@state,ended_at=@endedAt,last_observed_at=@endedAt,completion_reason=@reason
            WHERE id=@id AND ended_at IS NULL;
            """, new { id = active.Id, state, endedAt = Iso(endedAt), reason }, transaction);
        return closed;
    }

    private static double CreditedDuration(PlaybackTelemetrySession active, PlaybackTelemetryObservation observation)
    {
        if (active.State != PlaybackTelemetryStates.Playing || observation.IsExplicitSeek)
        {
            return 0;
        }

        var elapsed = (observation.ObservedAt - active.LastObservedAt).TotalSeconds;
        var positionDelta = observation.PositionSeconds - active.LastPositionSeconds;
        if (elapsed <= 0 || positionDelta <= 0)
        {
            return 0;
        }

        var rate = Math.Clamp(observation.PlaybackRate, 0.5d, 4d);
        if (positionDelta > elapsed * rate + MotionToleranceSeconds)
        {
            return 0;
        }

        return Math.Min(MaximumCreditedWallSeconds, Math.Min(elapsed, positionDelta / rate));
    }

    private static bool IsDuplicateOrOutOfOrder(PlaybackTelemetrySession active, PlaybackTelemetryObservation observation) =>
        observation.Sequence is { } sequence
            ? sequence <= active.LastSequence
            : observation.ObservedAt <= active.LastObservedAt;

    private static PlaybackTelemetrySession? ReadActive(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        Guid playerSessionId) => connection.QueryFirstOrDefault<TelemetryRow>($"""
            SELECT {TelemetrySelect} FROM playback_telemetry_sessions
            WHERE player_session_id=@playerSessionId AND ended_at IS NULL LIMIT 1;
            """, new { playerSessionId }, transaction) is { } row ? Map(row) : null;

    private void AppendEvent(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        PlaybackTelemetryTransition transition,
        CancellationToken ct)
    {
        if (!transition.StateChanged)
        {
            return;
        }

        var eventType = transition.Session.State switch
        {
            PlaybackTelemetryStates.Playing => "playback.started",
            PlaybackTelemetryStates.Paused => "playback.paused",
            PlaybackTelemetryStates.Completed => "playback.completed",
            PlaybackTelemetryStates.Stopped => "playback.stopped",
            _ => null,
        };
        if (eventType is null)
        {
            return;
        }

        var session = transition.Session;
        var payload = JsonSerializer.SerializeToElement(new
        {
            session_id = session.Id,
            asset_id = session.AssetId,
            state = session.State,
            reason = session.CompletionReason,
            position_seconds = session.LastPositionSeconds,
            duration_seconds = session.DurationSeconds,
            played_duration_seconds = session.PlayedDurationSeconds,
            delivery_mode = session.Delivery.Mode ?? PlaybackTelemetryDeliveryModes.Unknown,
            media_type = session.MediaType,
        });
        eventOutbox.Append(connection, transaction, new ApplicationEventDraft(
            eventType, 1, session.LastObservedAt,
            new ApplicationEventSubject(
                "playback-session",
                session.Id.ToString("D"),
                session.LibraryId,
                session.ProfileId,
                FeatureId: session.Feature),
            payload), ct);
    }

    private static ScopeSql ScopeCommand(PlaybackTelemetryReadScope scope, DateTimeOffset? from, DateTimeOffset? to)
    {
        var parameters = new DynamicParameters();
        parameters.Add("allAccounts", scope.AllAccounts);
        parameters.Add("accountId", scope.AccountId);
        parameters.Add("profileId", scope.ProfileId);
        parameters.Add("allLibraries", scope.AllLibraries);
        parameters.Add("libraryHex", scope.LibraryIds.Select(id => Convert.ToHexString(GuidSql.ToBlob(id))).ToArray());
        parameters.Add("features", scope.Features.Select(feature => feature.Value).ToArray());
        parameters.Add("from", from.HasValue ? Iso(from.Value) : null);
        parameters.Add("to", to.HasValue ? Iso(to.Value) : null);
        return new("""
            (@allAccounts=1 OR account_id=@accountId)
            AND (@profileId IS NULL OR profile_id=@profileId)
            AND (@allLibraries=1 OR HEX(library_id) IN @libraryHex)
            AND HEX(library_id) NOT IN (SELECT UPPER(REPLACE(library_id, '-', '')) FROM vault_libraries)
            AND feature_id IN @features
            AND (@from IS NULL OR started_at>=@from)
            AND (@to IS NULL OR started_at<@to)
            """, parameters);
    }

    private static bool CanRead(PlaybackTelemetryReadScope scope) =>
        scope.IsAllowed && scope.Features.Count > 0 && (scope.AllLibraries || scope.LibraryIds.Count > 0) &&
        (scope.AllAccounts || scope.AccountId.HasValue);

    private static void Validate(PlaybackTelemetryObservation value)
    {
        if (value.PlayerSessionId == Guid.Empty || value.AccountId == Guid.Empty || value.ProfileId == Guid.Empty ||
            value.AssetId == Guid.Empty || value.LibraryId == Guid.Empty)
        {
            throw new ArgumentException("Playback telemetry identities must be non-empty.", nameof(value));
        }

        if (value.Feature == AccountFeatureId.View)
        {
            throw new ArgumentException("View assets do not enter catalogue playback telemetry.", nameof(value));
        }

        Finite(value.PositionSeconds, nameof(value.PositionSeconds), allowZero: true);
        if (value.DurationSeconds.HasValue)
        {
            Finite(value.DurationSeconds.Value, nameof(value.DurationSeconds), allowZero: true);
        }

        Finite(value.PlaybackRate, nameof(value.PlaybackRate), allowZero: false);
        if (value.Sequence is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value.Sequence));
        }

        if (value.ObservedAt == default)
        {
            throw new ArgumentException("Observation time is required.", nameof(value));
        }

        if (value.State is not (PlaybackTelemetryStates.Playing or PlaybackTelemetryStates.Paused or PlaybackTelemetryStates.Stopped))
        {
            throw new ArgumentException("Unsupported playback state.", nameof(value));
        }
    }

    private static void Finite(double value, string name, bool allowZero)
    {
        if (!double.IsFinite(value) || value < 0 || (!allowZero && value == 0))
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static PlaybackDeliveryFacts Normalize(PlaybackDeliveryFacts value) => new(
        NormalizeDeliveryMode(value.Mode), Bound(value.Container, 64), Bound(value.VideoCodec, 64),
        Bound(value.AudioCodec, 64), Positive(value.Width), Positive(value.Height), Positive(value.BitrateKbps),
        NormalizeConnectionType(value.ConnectionType));

    private static PlaybackDeliveryFacts Merge(PlaybackDeliveryFacts current, PlaybackDeliveryFacts next) => new(
        next.Mode ?? current.Mode, next.Container ?? current.Container, next.VideoCodec ?? current.VideoCodec,
        next.AudioCodec ?? current.AudioCodec, next.Width ?? current.Width, next.Height ?? current.Height,
        next.BitrateKbps ?? current.BitrateKbps, next.ConnectionType ?? current.ConnectionType);

    private static string? NormalizeDeliveryMode(string? value) => value switch
    {
        PlaybackTelemetryDeliveryModes.DirectPlay or PlaybackTelemetryDeliveryModes.Remux or
        PlaybackTelemetryDeliveryModes.Transcode or PlaybackTelemetryDeliveryModes.Reader or
        PlaybackTelemetryDeliveryModes.Offline or PlaybackTelemetryDeliveryModes.Unknown => value,
        _ => null,
    };

    private static string? NormalizeConnectionType(string? value) => value switch
    {
        PlaybackConnectionTypes.Local or PlaybackConnectionTypes.Remote or PlaybackConnectionTypes.Unknown => value,
        _ => null,
    };

    private static string NormalizeCompletionReason(string? value) => value switch
    {
        PlaybackCompletionReasons.Replaced or PlaybackCompletionReasons.Takeover or PlaybackCompletionReasons.Stale or
        PlaybackCompletionReasons.Error => value,
        _ => PlaybackCompletionReasons.Stopped,
    };

    private static int? Positive(int? value) => value is > 0 ? value : null;
    private static string? Bound(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string EncodeCursor(DateTimeOffset at, Guid id) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes($"{Iso(at)}|{id:D}"));

    private static Cursor? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var pieces = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return pieces.Length == 2 && Guid.TryParse(pieces[1], out var id)
                ? new(Iso(Parse(pieces[0])), id)
                : throw new FormatException();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new ArgumentException("Invalid playback telemetry cursor.", nameof(cursor));
        }
    }

    private static object Parameters(PlaybackTelemetrySession session) => new
    {
        session.Id,
        session.PlayerSessionId,
        session.AuthoritySessionId,
        session.AccountId,
        session.ProfileId,
        session.ApplicationId,
        session.DeviceId,
        session.AssetId,
        session.LibraryId,
        FeatureId = session.Feature.Value,
        session.MediaType,
        session.State,
        StartedAt = Iso(session.StartedAt),
        LastObservedAt = Iso(session.LastObservedAt),
        EndedAt = session.EndedAt.HasValue ? Iso(session.EndedAt.Value) : null,
        session.StartedPositionSeconds,
        session.LastPositionSeconds,
        session.DurationSeconds,
        session.PlayedDurationSeconds,
        session.LastSequence,
        session.CompletionReason,
        DeliveryMode = session.Delivery.Mode,
        session.Delivery.Container,
        session.Delivery.VideoCodec,
        session.Delivery.AudioCodec,
        session.Delivery.Width,
        session.Delivery.Height,
        session.Delivery.BitrateKbps,
        ConnectionType = session.Delivery.ConnectionType,
        ClientName = session.Client.Name,
        ClientVersion = session.Client.Version,
    };

    private static PlaybackTelemetrySession Map(TelemetryRow row) => new()
    {
        Id = row.Id,
        PlayerSessionId = row.PlayerSessionId,
        AuthoritySessionId = row.AuthoritySessionId,
        AccountId = row.AccountId,
        ProfileId = row.ProfileId,
        ApplicationId = row.ApplicationId,
        DeviceId = row.DeviceId,
        AssetId = row.AssetId,
        LibraryId = row.LibraryId,
        Feature = new AccountFeatureId(row.FeatureId),
        MediaType = row.MediaType,
        State = row.State,
        StartedAt = Parse(row.StartedAt),
        LastObservedAt = Parse(row.LastObservedAt),
        EndedAt = row.EndedAt is null ? null : Parse(row.EndedAt),
        StartedPositionSeconds = row.StartedPositionSeconds,
        LastPositionSeconds = row.LastPositionSeconds,
        DurationSeconds = row.DurationSeconds,
        PlayedDurationSeconds = row.PlayedDurationSeconds,
        LastSequence = row.LastSequence,
        CompletionReason = row.CompletionReason,
        Delivery = new(row.DeliveryMode, row.Container, row.VideoCodec, row.AudioCodec, row.Width, row.Height,
            row.BitrateKbps, row.ConnectionType),
        Client = new(row.ClientName, row.ClientVersion),
    };

    private sealed record ScopeSql(string Where, DynamicParameters Parameters);
    private sealed record Cursor(string At, Guid Id);
    private sealed class AggregateRow
    {
        public long SessionCount { get; init; }
        public long CompletedCount { get; init; }
        public double PlayedDurationSeconds { get; init; }
        public long UniqueAccounts { get; init; }
        public long UniqueProfiles { get; init; }
        public long UniqueAssets { get; init; }
        public long DirectPlayCount { get; init; }
        public long RemuxCount { get; init; }
        public long TranscodeCount { get; init; }
    }
    private sealed class GroupRow
    {
        public Guid? AccountId { get; init; }
        public Guid? ProfileId { get; init; }
        public Guid? LibraryId { get; init; }
        public Guid? DeviceId { get; init; }
        public long SessionCount { get; init; }
        public long CompletedCount { get; init; }
        public double PlayedDurationSeconds { get; init; }
        public string LastPlayedAt { get; init; } = string.Empty;
    }
    private sealed class TelemetryRow
    {
        public Guid Id { get; init; }
        public Guid PlayerSessionId { get; init; }
        public Guid? AuthoritySessionId { get; init; }
        public Guid AccountId { get; init; }
        public Guid ProfileId { get; init; }
        public Guid? ApplicationId { get; init; }
        public Guid? DeviceId { get; init; }
        public Guid? AssetId { get; init; }
        public Guid LibraryId { get; init; }
        public string FeatureId { get; init; } = string.Empty;
        public string MediaType { get; init; } = string.Empty; public string State { get; init; } = string.Empty;
        public string StartedAt { get; init; } = string.Empty; public string LastObservedAt { get; init; } = string.Empty;
        public string? EndedAt { get; init; }
        public double StartedPositionSeconds { get; init; }
        public double LastPositionSeconds { get; init; }
        public double? DurationSeconds { get; init; }
        public double PlayedDurationSeconds { get; init; }
        public long LastSequence { get; init; }
        public string? CompletionReason { get; init; }
        public string? DeliveryMode { get; init; }
        public string? Container { get; init; }
        public string? VideoCodec { get; init; }
        public string? AudioCodec { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public int? BitrateKbps { get; init; }
        public string? ConnectionType { get; init; }
        public string? ClientName { get; init; }
        public string? ClientVersion { get; init; }
    }
}
