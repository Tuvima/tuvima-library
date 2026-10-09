using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class ClientAuthorizationRepository(IDatabaseConnection db) : IClientAuthorizationRepository
{
    public Task InsertPairingAsync(DevicePairingRequest request, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            conn.Execute("""
                INSERT INTO device_pairing_requests
                    (id, application_id, device_code_hash, user_code_hash, client_id, client_name, client_version,
                     device_name, device_class, requested_scopes, approved_scopes, capabilities_json,
                     status, poll_interval_seconds, created_at, expires_at)
                VALUES
                    (@Id, @ApplicationId, @DeviceCodeHash, @UserCodeHash, @ClientId, @ClientName, @ClientVersion,
                     @DeviceName, @DeviceClass, @RequestedScopes, @ApprovedScopes, @CapabilitiesJson,
                     @Status, @PollIntervalSeconds, @CreatedAt, @ExpiresAt);
                """, PairingParameters(request), tx);
        }, ct);

    public Task<DevicePairingRequest?> GetPairingByDeviceCodeHashAsync(string hash, CancellationToken ct = default) =>
        GetPairingAsync("device_code_hash = @hash", hash, ct);

    public Task<DevicePairingRequest?> GetPairingByUserCodeHashAsync(string hash, CancellationToken ct = default) =>
        GetPairingAsync("user_code_hash = @hash", hash, ct);

    public Task RecordPairingPollAsync(Guid requestId, DateTimeOffset polledAt, int intervalSeconds, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            conn.Execute("""
                UPDATE device_pairing_requests
                SET last_polled_at = @polledAt,
                    poll_interval_seconds = @intervalSeconds
                WHERE id = @requestId AND status IN ('pending','approved');
                """, new { requestId, polledAt = Iso(polledAt), intervalSeconds }, tx);
        }, ct);

    public Task<bool> DecidePairingAsync(
        Guid requestId,
        bool approved,
        Guid accountId,
        Guid profileId,
        Guid approvedByProfileId,
        string scopes,
        DateTimeOffset now,
        CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            return conn.Execute("""
                UPDATE device_pairing_requests
                SET status = @status,
                    profile_id = @profileId,
                    account_id = @accountId,
                    approved_by_profile_id = @approvedByProfileId,
                    approved_scopes = @scopes,
                    decided_at = @now
                WHERE id = @requestId
                  AND status = 'pending'
                  AND expires_at > @now;
                """, new
            {
                requestId,
                status = approved ? "approved" : "denied",
                profileId,
                accountId,
                approvedByProfileId,
                scopes,
                now = Iso(now),
            }, tx) == 1;
        }, ct);

    public Task<bool> ConsumePairingAsync(
        DevicePairingRequest pairing,
        ClientDevice device,
        ClientToken accessToken,
        ClientToken refreshToken,
        DateTimeOffset now,
        CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters();
            parameters.Add("id", pairing.Id);
            parameters.Add("now", Iso(now));
            var consumed = conn.Execute("""
                UPDATE device_pairing_requests
                SET status = 'consumed', consumed_at = @now
                WHERE id = @id
                  AND status = 'approved'
                  AND consumed_at IS NULL
                  AND expires_at > @now;
                """, parameters, tx);
            if (consumed != 1)
            {
                return false;
            }

            InsertDevice(conn, tx, device);
            InsertToken(conn, tx, accessToken);
            InsertToken(conn, tx, refreshToken);
            return true;
        }, ct);

    public Task<(ClientToken Token, ClientDevice Device)?> FindActiveAccessTokenAsync(
        string hash,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<TokenDeviceRow>(TokenDeviceSelect + "\n" + """
            WHERE t.token_hash = @hash
              AND t.token_kind = 'access'
              AND t.revoked_at IS NULL
              AND t.expires_at > @now
              AND d.revoked_at IS NULL
              AND t.application_id = d.application_id
              AND t.account_id = d.account_id
              AND t.profile_id = d.profile_id
            LIMIT 1;
            """, new { hash, now = Iso(now) });
        return Task.FromResult(row is null ? null : (MapToken(row), MapDevice(row)) as (ClientToken, ClientDevice)?);
    }

    public Task<(ClientToken Token, ClientDevice Device)?> FindRefreshTokenAsync(string hash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<TokenDeviceRow>(TokenDeviceSelect + "\n" + """
            WHERE t.token_hash = @hash
              AND t.token_kind = 'refresh'
              AND t.application_id = d.application_id
              AND t.account_id = d.account_id
              AND t.profile_id = d.profile_id
            LIMIT 1;
            """, new { hash });
        return Task.FromResult(row is null ? null : (MapToken(row), MapDevice(row)) as (ClientToken, ClientDevice)?);
    }

    public Task<bool> RotateRefreshTokenAsync(
        ClientToken current,
        ClientToken nextAccess,
        ClientToken nextRefresh,
        DateTimeOffset now,
        CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters();
            parameters.Add("id", current.Id);
            parameters.Add("now", Iso(now));
            var consumed = conn.Execute("""
                UPDATE client_tokens
                SET consumed_at = @now
                WHERE id = @id
                  AND token_kind = 'refresh'
                  AND consumed_at IS NULL
                  AND revoked_at IS NULL
                  AND expires_at > @now;
                """, parameters, tx);
            if (consumed != 1)
            {
                return false;
            }

            InsertToken(conn, tx, nextAccess);
            InsertToken(conn, tx, nextRefresh);
            conn.Execute("UPDATE client_devices SET last_seen_at = @now WHERE id = @deviceId AND revoked_at IS NULL;",
                new { now = Iso(now), deviceId = current.DeviceId }, tx);
            return true;
        }, ct);

    public Task RevokeTokenFamilyAsync(Guid tokenFamilyId, DateTimeOffset now, string reason, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            conn.Execute("""
                UPDATE client_tokens
                SET revoked_at = COALESCE(revoked_at, @now), revoked_reason = COALESCE(revoked_reason, @reason)
                WHERE token_family_id = @tokenFamilyId;
                """, new { tokenFamilyId, now = Iso(now), reason }, tx);
        }, ct);

    public Task<IReadOnlyList<ClientDevice>> GetDevicesAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<DeviceRow>(DeviceSelect + " WHERE profile_id = @profileId ORDER BY last_seen_at DESC;", new { profileId });
        return Task.FromResult<IReadOnlyList<ClientDevice>>(rows.Select(MapDevice).ToList());
    }

    public Task<ClientDevice?> GetDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<DeviceRow>(DeviceSelect + " WHERE id = @deviceId LIMIT 1;", new { deviceId });
        return Task.FromResult(row is null ? null : MapDevice(row));
    }

    public Task<ClientToken?> GetTokenByIdAsync(Guid tokenId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<TokenDeviceRow>(
            TokenDeviceSelect + " WHERE t.id = @tokenId LIMIT 1;",
            new { tokenId });
        return Task.FromResult(row is null ? null : MapToken(row));
    }

    public Task<bool> UpdateCapabilitiesAsync(Guid deviceId, string capabilitiesJson, DateTimeOffset now, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            return conn.Execute("""
                UPDATE client_devices
                SET capabilities_json = @capabilitiesJson, last_seen_at = @now
                WHERE id = @deviceId AND revoked_at IS NULL;
                """, new { deviceId, capabilitiesJson, now = Iso(now) }, tx) == 1;
        }, ct);

    public Task<IReadOnlyList<ClientDevice>> GetActiveDevicesAsync(Guid? accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<DeviceRow>(
            DeviceSelect + " WHERE revoked_at IS NULL AND (@accountId IS NULL OR account_id = @accountId) ORDER BY last_seen_at DESC;",
            new { accountId });
        return Task.FromResult<IReadOnlyList<ClientDevice>>(rows.Select(MapDevice).ToList());
    }

    public Task<bool> RevokeDeviceAsync(Guid deviceId, Guid profileId, DateTimeOffset now, string reason, CancellationToken ct = default) =>
        RevokeDeviceCoreAsync(deviceId, profileId, now, reason, ct);

    public Task<bool> RevokeDeviceByIdAsync(Guid deviceId, DateTimeOffset now, string reason, CancellationToken ct = default) =>
        RevokeDeviceCoreAsync(deviceId, null, now, reason, ct);

    private Task<bool> RevokeDeviceCoreAsync(Guid deviceId, Guid? profileId, DateTimeOffset now, string reason, CancellationToken ct) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var changed = conn.Execute("""
                UPDATE client_devices
                SET revoked_at = @now, revoked_reason = @reason
                WHERE id = @deviceId AND (@profileId IS NULL OR profile_id = @profileId) AND revoked_at IS NULL;
                """, new { deviceId, profileId, now = Iso(now), reason }, tx);
            if (changed == 1)
            {
                conn.Execute("""
                    UPDATE client_tokens
                    SET revoked_at = COALESCE(revoked_at, @now), revoked_reason = COALESCE(revoked_reason, @reason)
                    WHERE device_id = @deviceId;
                    """, new { deviceId, now = Iso(now), reason }, tx);
            }
            return changed == 1;
        }, ct);

    private Task<DevicePairingRequest?> GetPairingAsync(string predicate, string hash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<PairingRow>(PairingSelect + $" WHERE {predicate} LIMIT 1;", new { hash });
        return Task.FromResult(row is null ? null : MapPairing(row));
    }

    private static void InsertDevice(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, ClientDevice value) =>
        conn.Execute("""
            INSERT INTO client_devices
                (id, application_id, account_id, profile_id, device_name, device_class, client_id, client_name, client_version,
                 scopes, capabilities_json, created_at, last_seen_at, revoked_at, revoked_reason)
            VALUES
                (@Id, @ApplicationId, @AccountId, @ProfileId, @DeviceName, @DeviceClass, @ClientId, @ClientName, @ClientVersion,
                 @Scopes, @CapabilitiesJson, @CreatedAt, @LastSeenAt, @RevokedAt, @RevokedReason);
            """, new
        {
            value.Id,
            value.ApplicationId,
            value.AccountId,
            value.ProfileId,
            value.DeviceName,
            value.DeviceClass,
            value.ClientId,
            value.ClientName,
            value.ClientVersion,
            value.Scopes,
            value.CapabilitiesJson,
            CreatedAt = Iso(value.CreatedAt),
            LastSeenAt = Iso(value.LastSeenAt),
            RevokedAt = value.RevokedAt is null ? null : Iso(value.RevokedAt.Value),
            value.RevokedReason,
        }, tx);

    private static void InsertToken(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, ClientToken value) =>
        conn.Execute("""
            INSERT INTO client_tokens
                (id, application_id, account_id, device_id, profile_id, token_family_id, token_kind, token_hash, scopes,
                 account_authorization_version, grant_authorization_version, application_authorization_version,
                 generation, created_at, expires_at, consumed_at, revoked_at, revoked_reason)
            VALUES
                (@Id, @ApplicationId, @AccountId, @DeviceId, @ProfileId, @TokenFamilyId, @Kind, @TokenHash, @Scopes,
                 @AccountAuthorizationVersion, @GrantAuthorizationVersion, @ApplicationAuthorizationVersion,
                 @Generation, @CreatedAt, @ExpiresAt, @ConsumedAt, @RevokedAt, @RevokedReason);
            """, new
        {
            value.Id,
            value.ApplicationId,
            value.AccountId,
            value.DeviceId,
            value.ProfileId,
            value.TokenFamilyId,
            value.Kind,
            value.TokenHash,
            value.Scopes,
            value.AccountAuthorizationVersion,
            value.GrantAuthorizationVersion,
            value.ApplicationAuthorizationVersion,
            value.Generation,
            CreatedAt = Iso(value.CreatedAt),
            ExpiresAt = Iso(value.ExpiresAt),
            ConsumedAt = value.ConsumedAt is null ? null : Iso(value.ConsumedAt.Value),
            RevokedAt = value.RevokedAt is null ? null : Iso(value.RevokedAt.Value),
            value.RevokedReason,
        }, tx);

    private const string PairingSelect = """
        SELECT id AS Id, application_id AS ApplicationId, device_code_hash AS DeviceCodeHash, user_code_hash AS UserCodeHash,
               client_id AS ClientId, client_name AS ClientName, client_version AS ClientVersion,
               device_name AS DeviceName, device_class AS DeviceClass,
               requested_scopes AS RequestedScopes, approved_scopes AS ApprovedScopes,
               capabilities_json AS CapabilitiesJson, status AS Status,
               poll_interval_seconds AS PollIntervalSeconds, last_polled_at AS LastPolledAt,
               profile_id AS ProfileId, account_id AS AccountId, approved_by_profile_id AS ApprovedByProfileId,
               created_at AS CreatedAt, expires_at AS ExpiresAt, decided_at AS DecidedAt,
               consumed_at AS ConsumedAt
        FROM device_pairing_requests
        """;

    private const string DeviceSelect = """
        SELECT id AS Id, application_id AS ApplicationId, account_id AS AccountId, profile_id AS ProfileId, device_name AS DeviceName, device_class AS DeviceClass,
               client_id AS ClientId, client_name AS ClientName, client_version AS ClientVersion,
               scopes AS Scopes, capabilities_json AS CapabilitiesJson, created_at AS CreatedAt,
               last_seen_at AS LastSeenAt, revoked_at AS RevokedAt, revoked_reason AS RevokedReason
        FROM client_devices
        """;

    private const string TokenDeviceSelect = """
        SELECT t.id AS TokenId, t.application_id AS TokenApplicationId, t.account_id AS TokenAccountId, t.device_id AS TokenDeviceId, t.profile_id AS TokenProfileId,
               t.token_family_id AS TokenFamilyId, t.token_kind AS TokenKind, t.token_hash AS TokenHash,
               t.scopes AS TokenScopes, t.account_authorization_version AS TokenAccountAuthorizationVersion,
               t.grant_authorization_version AS TokenGrantAuthorizationVersion,
               t.application_authorization_version AS TokenApplicationAuthorizationVersion,
               t.generation AS TokenGeneration, t.created_at AS TokenCreatedAt,
               t.expires_at AS TokenExpiresAt, t.consumed_at AS TokenConsumedAt,
               t.revoked_at AS TokenRevokedAt, t.revoked_reason AS TokenRevokedReason,
               d.id AS DeviceId, d.application_id AS DeviceApplicationId, d.account_id AS DeviceAccountId, d.profile_id AS DeviceProfileId, d.device_name AS DeviceName,
               d.device_class AS DeviceClass, d.client_id AS ClientId, d.client_name AS ClientName,
               d.client_version AS ClientVersion, d.scopes AS DeviceScopes,
               d.capabilities_json AS CapabilitiesJson, d.created_at AS DeviceCreatedAt,
               d.last_seen_at AS DeviceLastSeenAt, d.revoked_at AS DeviceRevokedAt,
               d.revoked_reason AS DeviceRevokedReason
        FROM client_tokens t
        JOIN client_devices d ON d.id = t.device_id
        """;

    private static object PairingParameters(DevicePairingRequest value) => new
    {
        value.Id,
        value.ApplicationId,
        value.DeviceCodeHash,
        value.UserCodeHash,
        value.ClientId,
        value.ClientName,
        value.ClientVersion,
        value.DeviceName,
        value.DeviceClass,
        value.RequestedScopes,
        value.ApprovedScopes,
        value.CapabilitiesJson,
        value.Status,
        value.PollIntervalSeconds,
        CreatedAt = Iso(value.CreatedAt),
        ExpiresAt = Iso(value.ExpiresAt),
    };

    private static DevicePairingRequest MapPairing(PairingRow row) => new()
    {
        Id = row.Id,
        ApplicationId = row.ApplicationId,
        DeviceCodeHash = row.DeviceCodeHash,
        UserCodeHash = row.UserCodeHash,
        ClientId = row.ClientId,
        ClientName = row.ClientName,
        ClientVersion = row.ClientVersion,
        DeviceName = row.DeviceName,
        DeviceClass = row.DeviceClass,
        RequestedScopes = row.RequestedScopes,
        ApprovedScopes = row.ApprovedScopes,
        CapabilitiesJson = row.CapabilitiesJson,
        Status = row.Status,
        PollIntervalSeconds = row.PollIntervalSeconds,
        LastPolledAt = Parse(row.LastPolledAt),
        ProfileId = row.ProfileId,
        AccountId = row.AccountId,
        ApprovedByProfileId = row.ApprovedByProfileId,
        CreatedAt = ParseRequired(row.CreatedAt),
        ExpiresAt = ParseRequired(row.ExpiresAt),
        DecidedAt = Parse(row.DecidedAt),
        ConsumedAt = Parse(row.ConsumedAt),
    };

    private static ClientToken MapToken(TokenDeviceRow row) => new()
    {
        Id = row.TokenId,
        ApplicationId = row.TokenApplicationId,
        AccountId = row.TokenAccountId,
        DeviceId = row.TokenDeviceId,
        ProfileId = row.TokenProfileId,
        TokenFamilyId = row.TokenFamilyId,
        Kind = row.TokenKind,
        TokenHash = row.TokenHash,
        Scopes = row.TokenScopes,
        AccountAuthorizationVersion = row.TokenAccountAuthorizationVersion,
        GrantAuthorizationVersion = row.TokenGrantAuthorizationVersion,
        ApplicationAuthorizationVersion = row.TokenApplicationAuthorizationVersion,
        Generation = row.TokenGeneration,
        CreatedAt = ParseRequired(row.TokenCreatedAt),
        ExpiresAt = ParseRequired(row.TokenExpiresAt),
        ConsumedAt = Parse(row.TokenConsumedAt),
        RevokedAt = Parse(row.TokenRevokedAt),
        RevokedReason = row.TokenRevokedReason,
    };

    private static ClientDevice MapDevice(DeviceRow row) => new()
    {
        Id = row.Id,
        ApplicationId = row.ApplicationId,
        AccountId = row.AccountId,
        ProfileId = row.ProfileId,
        DeviceName = row.DeviceName,
        DeviceClass = row.DeviceClass,
        ClientId = row.ClientId,
        ClientName = row.ClientName,
        ClientVersion = row.ClientVersion,
        Scopes = row.Scopes,
        CapabilitiesJson = row.CapabilitiesJson,
        CreatedAt = ParseRequired(row.CreatedAt),
        LastSeenAt = ParseRequired(row.LastSeenAt),
        RevokedAt = Parse(row.RevokedAt),
        RevokedReason = row.RevokedReason,
    };

    private static ClientDevice MapDevice(TokenDeviceRow row) => new()
    {
        Id = row.DeviceId,
        ApplicationId = row.DeviceApplicationId,
        AccountId = row.DeviceAccountId,
        ProfileId = row.DeviceProfileId,
        DeviceName = row.DeviceName,
        DeviceClass = row.DeviceClass,
        ClientId = row.ClientId,
        ClientName = row.ClientName,
        ClientVersion = row.ClientVersion,
        Scopes = row.DeviceScopes,
        CapabilitiesJson = row.CapabilitiesJson,
        CreatedAt = ParseRequired(row.DeviceCreatedAt),
        LastSeenAt = ParseRequired(row.DeviceLastSeenAt),
        RevokedAt = Parse(row.DeviceRevokedAt),
        RevokedReason = row.DeviceRevokedReason,
    };

    private static string Iso(DateTimeOffset value) => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseRequired(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static DateTimeOffset? Parse(string? value) => string.IsNullOrWhiteSpace(value) ? null : ParseRequired(value);

    private sealed class PairingRow
    {
        public Guid Id { get; init; }
        public Guid ApplicationId { get; init; }
        public string DeviceCodeHash { get; init; } = "";
        public string UserCodeHash { get; init; } = "";
        public string ClientId { get; init; } = "";
        public string ClientName { get; init; } = "";
        public string ClientVersion { get; init; } = "";
        public string DeviceName { get; init; } = "";
        public string DeviceClass { get; init; } = "";
        public string RequestedScopes { get; init; } = "";
        public string ApprovedScopes { get; init; } = "";
        public string CapabilitiesJson { get; init; } = "{}";
        public string Status { get; init; } = "pending";
        public int PollIntervalSeconds { get; init; }
        public string? LastPolledAt { get; init; }
        public Guid? ProfileId { get; init; }
        public Guid? AccountId { get; init; }
        public Guid? ApprovedByProfileId { get; init; }
        public string CreatedAt { get; init; } = "";
        public string ExpiresAt { get; init; } = "";
        public string? DecidedAt { get; init; }
        public string? ConsumedAt { get; init; }
    }

    private class DeviceRow
    {
        public Guid Id { get; init; }
        public Guid ApplicationId { get; init; }
        public Guid AccountId { get; init; }
        public Guid ProfileId { get; init; }
        public string DeviceName { get; init; } = "";
        public string DeviceClass { get; init; } = "";
        public string ClientId { get; init; } = "";
        public string ClientName { get; init; } = "";
        public string ClientVersion { get; init; } = "";
        public string Scopes { get; init; } = "";
        public string CapabilitiesJson { get; init; } = "{}";
        public string CreatedAt { get; init; } = "";
        public string LastSeenAt { get; init; } = "";
        public string? RevokedAt { get; init; }
        public string? RevokedReason { get; init; }
    }

    private sealed class TokenDeviceRow
    {
        public Guid TokenId { get; init; }
        public Guid TokenApplicationId { get; init; }
        public Guid TokenAccountId { get; init; }
        public Guid TokenDeviceId { get; init; }
        public Guid TokenProfileId { get; init; }
        public Guid TokenFamilyId { get; init; }
        public string TokenKind { get; init; } = "";
        public string TokenHash { get; init; } = "";
        public string TokenScopes { get; init; } = "";
        public long TokenAccountAuthorizationVersion { get; init; }
        public long TokenGrantAuthorizationVersion { get; init; }
        public long TokenApplicationAuthorizationVersion { get; init; }
        public int TokenGeneration { get; init; }
        public string TokenCreatedAt { get; init; } = "";
        public string TokenExpiresAt { get; init; } = "";
        public string? TokenConsumedAt { get; init; }
        public string? TokenRevokedAt { get; init; }
        public string? TokenRevokedReason { get; init; }
        public Guid DeviceId { get; init; }
        public Guid DeviceApplicationId { get; init; }
        public Guid DeviceAccountId { get; init; }
        public Guid DeviceProfileId { get; init; }
        public string DeviceName { get; init; } = "";
        public string DeviceClass { get; init; } = "";
        public string ClientId { get; init; } = "";
        public string ClientName { get; init; } = "";
        public string ClientVersion { get; init; } = "";
        public string DeviceScopes { get; init; } = "";
        public string CapabilitiesJson { get; init; } = "{}";
        public string DeviceCreatedAt { get; init; } = "";
        public string DeviceLastSeenAt { get; init; } = "";
        public string? DeviceRevokedAt { get; init; }
        public string? DeviceRevokedReason { get; init; }
    }
}
