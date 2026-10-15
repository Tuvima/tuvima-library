using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Security;

public sealed record ClientAccessIdentity(ClientToken Token, ClientDevice Device, Account Account, AccountProfileGrant Grant, MediaEngine.Domain.Entities.Application Application);

public sealed record ClientTokenResult(OAuthTokenResponse? Success, OAuthErrorResponse? Error)
{
    public static ClientTokenResult Failed(string error, string description, int? interval = null) =>
        new(null, new OAuthErrorResponse { Error = error, ErrorDescription = description, Interval = interval });
}

public sealed class ClientAuthorizationService(
    IClientAuthorizationRepository repository,
    IAccountRepository accounts,
    IApplicationRepository applications,
    IPermissionRegistry permissions,
    TimeProvider timeProvider)
{
    public const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    public const string RefreshGrantType = "refresh_token";
    public static readonly TimeSpan DeviceCodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);
    public const string ProfileNotAllowedError = "profile_not_allowed";
    private const int InitialPollIntervalSeconds = 5;
    private const string UserCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly HashSet<string> ValidDeviceClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "web", "mobile", "television", "automotive",
    };

    public async Task<DeviceAuthorizationResponse> BeginAsync(
        DeviceAuthorizationRequest request,
        string verificationBaseUri,
        CancellationToken ct = default)
    {
        var clientId = RequiredText(request.ClientId, nameof(request.ClientId), 100);
        var application = await applications.GetApplicationByClientIdAsync(clientId, ct).ConfigureAwait(false);
        if (application?.IsEnabled != true || application.ApplicationType != ApplicationType.UserClient)
        {
            throw new ArgumentException("The client_id is not registered for an enabled user-client Application.", nameof(request.ClientId));
        }

        var now = timeProvider.GetUtcNow();
        var deviceCode = RandomToken(32);
        var userCode = RandomUserCode();
        var scopes = NormalizeScopes(
            request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            useDefaultsWhenEmpty: true);
        var deviceClass = NormalizeDeviceClass(request.DeviceClass);
        var capabilities = NormalizeCapabilities(request.Capabilities);

        await repository.InsertPairingAsync(new DevicePairingRequest
        {
            Id = Guid.NewGuid(),
            ApplicationId = application.Id,
            DeviceCodeHash = Hash(deviceCode),
            UserCodeHash = Hash(NormalizeUserCode(userCode)),
            ClientId = clientId,
            ClientName = Text(request.ClientName, 100, clientId),
            ClientVersion = Text(request.ClientVersion, 50, "unknown"),
            DeviceName = Text(request.DeviceName, 100, "Unknown device"),
            DeviceClass = deviceClass,
            RequestedScopes = JoinScopes(scopes),
            CapabilitiesJson = JsonSerializer.Serialize(capabilities),
            Status = "pending",
            PollIntervalSeconds = InitialPollIntervalSeconds,
            CreatedAt = now,
            ExpiresAt = now.Add(DeviceCodeLifetime),
        }, ct).ConfigureAwait(false);

        var verificationUri = verificationBaseUri.TrimEnd('/') + "/pair";
        return new DeviceAuthorizationResponse
        {
            DeviceCode = deviceCode,
            UserCode = userCode,
            VerificationUri = verificationUri,
            VerificationUriComplete = $"{verificationUri}?user_code={Uri.EscapeDataString(userCode)}",
            ExpiresIn = checked((int)DeviceCodeLifetime.TotalSeconds),
            Interval = InitialPollIntervalSeconds,
        };
    }

    public async Task<PairingReviewResponse?> ReviewAsync(string userCode, CancellationToken ct = default)
    {
        var pairing = await repository.GetPairingByUserCodeHashAsync(Hash(NormalizeUserCode(userCode)), ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (pairing is null || pairing.Status != "pending" || pairing.ExpiresAt <= now)
        {
            return null;
        }

        // The photo item is only worth offering when the Application can actually carry the photo scopes to a token.
        var application = await applications.GetApplicationByClientIdAsync(pairing.ClientId, ct).ConfigureAwait(false);
        var offersPhotoScopes = application?.IsEnabled == true
            && application.Id == pairing.ApplicationId
            && application.ApplicationType == ApplicationType.UserClient
            && await HasEffectivePermissionsAsync(application, ClientApiScopes.Photos, ct).ConfigureAwait(false);

        return new PairingReviewResponse
        {
            RequestId = pairing.Id,
            ClientId = pairing.ClientId,
            ClientName = pairing.ClientName,
            ClientVersion = pairing.ClientVersion,
            DeviceName = pairing.DeviceName,
            DeviceClass = pairing.DeviceClass,
            RequestedScopes = SplitScopes(pairing.RequestedScopes),
            ExpiresAt = pairing.ExpiresAt,
            OffersPhotoScopes = offersPhotoScopes,
        };
    }

    /// <summary>
    /// Records the person's decision. Approving grants exactly what the decision lists, within what the app asked for:
    /// <c>Scopes</c> omitted (null) means every requested scope except the photo scopes, an explicit list means those scopes,
    /// and photo scopes are only ever granted when listed. Approving with nothing left to grant, or granting photo scopes the
    /// Application does not hold, throws <see cref="ArgumentException"/> before anything is stored, so no useless or
    /// half-working device is created.
    /// </summary>
    public async Task<bool> DecideAsync(
        PairingDecisionRequest request,
        Guid accountId,
        Guid profileId,
        Guid approvedByProfileId,
        CancellationToken ct = default)
    {
        var pairing = await repository.GetPairingByUserCodeHashAsync(Hash(NormalizeUserCode(request.UserCode)), ct).ConfigureAwait(false);
        if (pairing is null)
        {
            return false;
        }

        var requested = SplitScopes(pairing.RequestedScopes);

        // A decision that omits the scopes keeps meaning "everything asked for", except the photo scopes: those are
        // granted only when the person's decision lists them, so they can never be granted silently. An explicit list
        // (even an empty one) means exactly what it names, so unticking everything on the pairing screen grants nothing.
        var approved = request.Scopes is null
            ? requested.Where(scope => !ClientApiScopes.Photos.Contains(scope, StringComparer.Ordinal)).ToArray()
            : NormalizeScopes(request.Scopes).Where(requested.Contains).ToArray();
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var grant = await accounts.GetGrantAsync(accountId, profileId, ct).ConfigureAwait(false);
        var boundApplication = await applications.GetApplicationByClientIdAsync(pairing.ClientId, ct).ConfigureAwait(false);
        if (account?.IsEnabled != true || grant?.IsEnabled != true ||
            boundApplication?.IsEnabled != true || boundApplication.Id != pairing.ApplicationId ||
            boundApplication.ApplicationType != ApplicationType.UserClient)
        {
            return false;
        }

        if (request.Approved)
        {
            if (approved.Length == 0)
            {
                throw new ArgumentException("Choose at least one thing this device may do, or deny the request.");
            }

            // Photo access needs the Application to hold the photo permissions too; otherwise the device would show as paired
            // and then fail at token exchange.
            var photoScopes = approved.Where(scope => ClientApiScopes.Photos.Contains(scope, StringComparer.Ordinal)).ToArray();
            if (photoScopes.Length > 0
                && !await HasEffectivePermissionsAsync(boundApplication, photoScopes, ct).ConfigureAwait(false))
            {
                throw new ArgumentException("This app is not allowed to use photos and videos, so that option cannot be approved.");
            }
        }

        return await repository.DecidePairingAsync(
            pairing.Id,
            request.Approved,
            accountId,
            profileId,
            approvedByProfileId,
            JoinScopes(request.Approved ? approved : []),
            timeProvider.GetUtcNow(),
            ct).ConfigureAwait(false);
    }

    public Task<ClientTokenResult> ExchangeAsync(OAuthTokenRequest request, CancellationToken ct = default) =>
        request.GrantType switch
        {
            DeviceGrantType => ExchangeDeviceCodeAsync(request, ct),
            RefreshGrantType => RefreshAsync(request, ct),
            _ => Task.FromResult(ClientTokenResult.Failed("unsupported_grant_type", "The requested grant type is not supported.")),
        };

    public async Task<ClientAccessIdentity?> ValidateAccessTokenAsync(string plaintextToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken) || plaintextToken.Length > 512)
        {
            return null;
        }

        var match = await repository.FindActiveAccessTokenAsync(Hash(plaintextToken), timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        if (match is null)
        {
            return null;
        }

        var live = await ValidateBindingAsync(match.Value.Token, match.Value.Device, ct).ConfigureAwait(false);
        return live;
    }

    public Task<IReadOnlyList<ClientDevice>> GetDevicesAsync(Guid profileId, CancellationToken ct = default) =>
        repository.GetDevicesAsync(profileId, ct);

    public Task<ClientDevice?> GetDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        repository.GetDeviceAsync(deviceId, ct);

    public Task<bool> UpdateCapabilitiesAsync(Guid deviceId, ClientCapabilitiesDto capabilities, CancellationToken ct = default) =>
        repository.UpdateCapabilitiesAsync(
            deviceId,
            JsonSerializer.Serialize(NormalizeCapabilities(capabilities)),
            timeProvider.GetUtcNow(),
            ct);

    public Task<bool> RevokeDeviceAsync(Guid deviceId, Guid profileId, CancellationToken ct = default) =>
        repository.RevokeDeviceAsync(deviceId, profileId, timeProvider.GetUtcNow(), "user_revoked", ct);

    /// <summary>
    /// Re-binds a paired device to another profile of the same account and issues a fresh token pair for it. The caller
    /// has already decided the person may use the profile and has checked its PIN; this re-checks everything a refresh
    /// checks (account, the new profile's grant, Application and its scopes), revokes every earlier token of the device,
    /// and keeps the device id and approved scopes. Only the profile changes.
    /// <paramref name="expectedProfileId"/> is the profile the caller authenticated as and <paramref name="callerTokenId"/>
    /// its access token: the switch only happens if the device is still on that profile and that token is still live, so
    /// of two switches made at once only one succeeds. <paramref name="audit"/> is stored in the same transaction.
    /// </summary>
    public async Task<ClientTokenResult> SwitchDeviceProfileAsync(
        Guid deviceId,
        Guid accountId,
        Guid expectedProfileId,
        Guid callerTokenId,
        Guid targetProfileId,
        AuthorizationAuditEvent audit,
        CancellationToken ct = default)
    {
        var device = await repository.GetDeviceAsync(deviceId, ct).ConfigureAwait(false);
        if (device is null || !device.IsActive || device.AccountId != accountId || device.ProfileId != expectedProfileId)
        {
            return ClientTokenResult.Failed("invalid_grant", "The device is no longer paired or has already switched profile.");
        }

        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var grant = await accounts.GetGrantAsync(accountId, targetProfileId, ct).ConfigureAwait(false);
        var application = await applications.GetApplicationAsync(device.ApplicationId, ct).ConfigureAwait(false);
        var boundApplication = await applications.GetApplicationByClientIdAsync(device.ClientId, ct).ConfigureAwait(false);
        if (account?.IsEnabled != true || application?.IsEnabled != true ||
            boundApplication?.Id != application.Id || boundApplication.ApplicationType != ApplicationType.UserClient)
        {
            return ClientTokenResult.Failed("invalid_grant", "The account or Application is unavailable.");
        }

        if (grant?.IsEnabled != true)
        {
            return ClientTokenResult.Failed(ProfileNotAllowedError, "This account cannot use that profile.");
        }

        if (!await HasEffectivePermissionsAsync(application, SplitScopes(device.Scopes), ct).ConfigureAwait(false))
        {
            return ClientTokenResult.Failed("invalid_grant", "The device's scopes are no longer granted to the Application.");
        }

        var now = timeProvider.GetUtcNow();
        var familyId = Guid.NewGuid();
        var access = NewToken(application.Id, accountId, deviceId, targetProfileId, familyId, "access", device.Scopes, account.AuthorizationVersion, grant.AuthorizationVersion, application.AuthorizationVersion, 0, AccessTokenLifetime, now);
        var refresh = NewToken(application.Id, accountId, deviceId, targetProfileId, familyId, "refresh", device.Scopes, account.AuthorizationVersion, grant.AuthorizationVersion, application.AuthorizationVersion, 0, RefreshTokenLifetime, now);
        if (!await repository.SwitchDeviceProfileAsync(
                deviceId, accountId, expectedProfileId, callerTokenId, targetProfileId, access.Entity, refresh.Entity, now, "profile_switched", audit, ct).ConfigureAwait(false))
        {
            return ClientTokenResult.Failed("invalid_grant", "The device is no longer paired or has already switched profile.");
        }

        return Success(access.Plaintext, refresh.Plaintext, deviceId, targetProfileId, device.Scopes);
    }

    private async Task<ClientTokenResult> ExchangeDeviceCodeAsync(OAuthTokenRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceCode))
        {
            return ClientTokenResult.Failed("invalid_request", "device_code is required.");
        }

        var pairing = await repository.GetPairingByDeviceCodeHashAsync(Hash(request.DeviceCode), ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (pairing is null || !string.Equals(pairing.ClientId, request.ClientId, StringComparison.Ordinal))
        {
            return ClientTokenResult.Failed("invalid_grant", "The device code is invalid.");
        }
        if (pairing.ExpiresAt <= now)
        {
            return ClientTokenResult.Failed("expired_token", "The device code has expired.");
        }

        if (pairing.LastPolledAt is { } lastPoll
            && now < lastPoll.AddSeconds(pairing.PollIntervalSeconds))
        {
            var slower = Math.Min(60, pairing.PollIntervalSeconds + 5);
            await repository.RecordPairingPollAsync(pairing.Id, now, slower, ct).ConfigureAwait(false);
            return ClientTokenResult.Failed("slow_down", "Polling is faster than the permitted interval.", slower);
        }

        await repository.RecordPairingPollAsync(pairing.Id, now, pairing.PollIntervalSeconds, ct).ConfigureAwait(false);
        if (pairing.Status == "pending")
        {
            return ClientTokenResult.Failed("authorization_pending", "The device is waiting for approval.", pairing.PollIntervalSeconds);
        }
        if (pairing.Status == "denied")
        {
            return ClientTokenResult.Failed("access_denied", "The device request was denied.");
        }
        if (pairing.Status != "approved" || pairing.ProfileId is not Guid profileId || pairing.AccountId is not Guid accountId)
        {
            return ClientTokenResult.Failed("expired_token", "The device authorization has already been consumed.");
        }

        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var grant = await accounts.GetGrantAsync(accountId, profileId, ct).ConfigureAwait(false);
        var application = await applications.GetApplicationByClientIdAsync(pairing.ClientId, ct).ConfigureAwait(false);
        if (account?.IsEnabled != true || grant?.IsEnabled != true || application?.IsEnabled != true ||
            application.Id != pairing.ApplicationId || application.ApplicationType != ApplicationType.UserClient)
        {
            return ClientTokenResult.Failed("access_denied", "The approved account, profile grant, or Application is unavailable.");
        }

        if (!await HasEffectivePermissionsAsync(application, SplitScopes(pairing.ApprovedScopes), ct).ConfigureAwait(false))
        {
            return ClientTokenResult.Failed("access_denied", "The approved scopes are no longer granted to the Application.");
        }

        var deviceId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var scopes = pairing.ApprovedScopes;
        var access = NewToken(application.Id, accountId, deviceId, profileId, familyId, "access", scopes, account.AuthorizationVersion, grant.AuthorizationVersion, application.AuthorizationVersion, 0, AccessTokenLifetime, now);
        var refresh = NewToken(application.Id, accountId, deviceId, profileId, familyId, "refresh", scopes, account.AuthorizationVersion, grant.AuthorizationVersion, application.AuthorizationVersion, 0, RefreshTokenLifetime, now);
        var device = new ClientDevice
        {
            Id = deviceId,
            ApplicationId = application.Id,
            AccountId = accountId,
            ProfileId = profileId,
            DeviceName = pairing.DeviceName,
            DeviceClass = pairing.DeviceClass,
            ClientId = pairing.ClientId,
            ClientName = pairing.ClientName,
            ClientVersion = pairing.ClientVersion,
            Scopes = scopes,
            CapabilitiesJson = pairing.CapabilitiesJson,
            CreatedAt = now,
            LastSeenAt = now,
        };

        if (!await repository.ConsumePairingAsync(pairing, device, access.Entity, refresh.Entity, now, ct).ConfigureAwait(false))
        {
            return ClientTokenResult.Failed("expired_token", "The device authorization has already been consumed.");
        }

        return Success(access.Plaintext, refresh.Plaintext, deviceId, profileId, scopes);
    }

    private async Task<ClientTokenResult> RefreshAsync(OAuthTokenRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return ClientTokenResult.Failed("invalid_request", "refresh_token is required.");
        }

        var match = await repository.FindRefreshTokenAsync(Hash(request.RefreshToken), ct).ConfigureAwait(false);
        if (match is null || !string.Equals(match.Value.Device.ClientId, request.ClientId, StringComparison.Ordinal))
        {
            return ClientTokenResult.Failed("invalid_grant", "The refresh token is invalid.");
        }

        var current = match.Value.Token;
        var now = timeProvider.GetUtcNow();
        if (current.ConsumedAt is not null || current.RevokedAt is not null)
        {
            await repository.RevokeTokenFamilyAsync(current.TokenFamilyId, now, "refresh_token_replay", ct).ConfigureAwait(false);
            return ClientTokenResult.Failed("invalid_grant", "Refresh token replay was detected and the device grant was revoked.");
        }
        if (current.ExpiresAt <= now || !match.Value.Device.IsActive)
        {
            return ClientTokenResult.Failed("invalid_grant", "The refresh token is expired or revoked.");
        }

        var live = await ValidateBindingAsync(current, match.Value.Device, ct).ConfigureAwait(false);
        if (live is null)
        {
            return ClientTokenResult.Failed("invalid_grant", "The account, profile grant, Application, or scope grant changed.");
        }

        var nextGeneration = checked(current.Generation + 1);
        var access = NewToken(current.ApplicationId, current.AccountId, current.DeviceId, current.ProfileId, current.TokenFamilyId, "access", current.Scopes, live.Account.AuthorizationVersion, live.Grant.AuthorizationVersion, live.Application.AuthorizationVersion, nextGeneration, AccessTokenLifetime, now);
        var refresh = NewToken(current.ApplicationId, current.AccountId, current.DeviceId, current.ProfileId, current.TokenFamilyId, "refresh", current.Scopes, live.Account.AuthorizationVersion, live.Grant.AuthorizationVersion, live.Application.AuthorizationVersion, nextGeneration, RefreshTokenLifetime, now);
        if (!await repository.RotateRefreshTokenAsync(current, access.Entity, refresh.Entity, now, ct).ConfigureAwait(false))
        {
            await repository.RevokeTokenFamilyAsync(current.TokenFamilyId, now, "refresh_token_replay", ct).ConfigureAwait(false);
            return ClientTokenResult.Failed("invalid_grant", "Refresh token replay was detected and the device grant was revoked.");
        }

        return Success(access.Plaintext, refresh.Plaintext, current.DeviceId, current.ProfileId, current.Scopes);
    }

    private static ClientTokenResult Success(string access, string refresh, Guid deviceId, Guid profileId, string scopes) =>
        new(new OAuthTokenResponse
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresIn = checked((int)AccessTokenLifetime.TotalSeconds),
            Scope = scopes,
            DeviceId = deviceId,
            ProfileId = profileId,
        }, null);

    private static (ClientToken Entity, string Plaintext) NewToken(
        Guid applicationId,
        Guid accountId,
        Guid deviceId,
        Guid profileId,
        Guid familyId,
        string kind,
        string scopes,
        long accountVersion,
        long grantVersion,
        long applicationVersion,
        int generation,
        TimeSpan lifetime,
        DateTimeOffset now)
    {
        var plaintext = RandomToken(32);
        return (new ClientToken
        {
            Id = Guid.NewGuid(),
            ApplicationId = applicationId,
            AccountId = accountId,
            DeviceId = deviceId,
            ProfileId = profileId,
            TokenFamilyId = familyId,
            Kind = kind,
            TokenHash = Hash(plaintext),
            Scopes = scopes,
            AccountAuthorizationVersion = accountVersion,
            GrantAuthorizationVersion = grantVersion,
            ApplicationAuthorizationVersion = applicationVersion,
            Generation = generation,
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime),
        }, plaintext);
    }

    private async Task<ClientAccessIdentity?> ValidateBindingAsync(ClientToken token, ClientDevice device, CancellationToken ct)
    {
        if (token.ApplicationId != device.ApplicationId || token.AccountId != device.AccountId || token.ProfileId != device.ProfileId)
        {
            return null;
        }

        var account = await accounts.GetByIdAsync(token.AccountId, ct).ConfigureAwait(false);
        var grant = await accounts.GetGrantAsync(token.AccountId, token.ProfileId, ct).ConfigureAwait(false);
        var application = await applications.GetApplicationAsync(token.ApplicationId, ct).ConfigureAwait(false);
        if (account?.IsEnabled != true || grant?.IsEnabled != true || application?.IsEnabled != true)
        {
            return null;
        }

        var boundApplication = await applications.GetApplicationByClientIdAsync(device.ClientId, ct).ConfigureAwait(false);
        if (boundApplication?.Id != application.Id || boundApplication.ApplicationType != ApplicationType.UserClient)
        {
            return null;
        }

        if (account.AuthorizationVersion != token.AccountAuthorizationVersion || grant.AuthorizationVersion != token.GrantAuthorizationVersion || application.AuthorizationVersion != token.ApplicationAuthorizationVersion)
        {
            return null;
        }

        if (!await HasEffectivePermissionsAsync(application, SplitScopes(token.Scopes), ct).ConfigureAwait(false))
        {
            return null;
        }

        return new(token, device, account, grant, application);
    }

    private async Task<bool> HasEffectivePermissionsAsync(
        MediaEngine.Domain.Entities.Application application,
        IEnumerable<string> scopes,
        CancellationToken ct)
    {
        var grants = application.IsAdministrator
            ? null
            : await applications.GetApplicationPermissionsAsync(application.Id, ct).ConfigureAwait(false);
        foreach (var scope in scopes)
        {
            var permission = new ApplicationPermissionId(scope);
            if (!permissions.TryGet(permission, out var definition) || !definition.IsAvailable ||
                !definition.ApplicationTypes.Contains(application.ApplicationType) ||
                (!application.IsAdministrator && !grants!.Contains(permission)))
            {
                return false;
            }
        }
        return true;
    }

    public static IReadOnlyList<string> SplitScopes(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<string> NormalizeScopes(IEnumerable<string> scopes, bool useDefaultsWhenEmpty = false)
    {
        var supplied = scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unknown = supplied.Where(scope => !ClientApiScopes.Consumer.Contains(scope, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException($"Unsupported scope: {string.Join(", ", unknown)}.");
        }

        var requested = supplied
            .Order(StringComparer.Ordinal)
            .ToArray();
        return requested.Length == 0 && useDefaultsWhenEmpty ? ClientApiScopes.Default : requested;
    }

    private static string JoinScopes(IEnumerable<string> scopes) => string.Join(' ', scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static ClientCapabilitiesDto NormalizeCapabilities(ClientCapabilitiesDto source) => new()
    {
        SchemaVersion = 1,
        Containers = NormalizeValues(source.Containers, 20),
        VideoCodecs = NormalizeValues(source.VideoCodecs, 20),
        AudioCodecs = NormalizeValues(source.AudioCodecs, 20),
        SubtitleFormats = NormalizeValues(source.SubtitleFormats, 20),
        Protocols = NormalizeValues(source.Protocols, 10),
        MaxWidth = Clamp(source.MaxWidth, 320, 16384),
        MaxHeight = Clamp(source.MaxHeight, 240, 8640),
        MaxBitrateKbps = Clamp(source.MaxBitrateKbps, 64, 500_000),
        MaxAudioChannels = Clamp(source.MaxAudioChannels, 1, 32),
        SupportsHdr = source.SupportsHdr,
        SupportsPlaybackSpeed = source.SupportsPlaybackSpeed,
        SupportsOfflineDownloads = source.SupportsOfflineDownloads,
    };

    private static IReadOnlyList<string> NormalizeValues(IEnumerable<string> values, int maximum) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length <= 40)
            .Distinct(StringComparer.Ordinal)
            .Take(maximum)
            .ToArray();

    private static int? Clamp(int? value, int minimum, int maximum) => value.HasValue ? Math.Clamp(value.Value, minimum, maximum) : null;

    private static string NormalizeDeviceClass(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not null && ValidDeviceClasses.Contains(normalized) ? normalized : "television";
    }

    private static string NormalizeUserCode(string value) =>
        new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string RequiredText(string value, string name, int maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return Text(value, maximum, string.Empty);
    }

    private static string Text(string? value, int maximum, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }

    private static string RandomUserCode()
    {
        Span<char> characters = stackalloc char[8];
        for (var i = 0; i < characters.Length; i++)
        {
            characters[i] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        }
        return $"{new string(characters[..4])}-{new string(characters[4..])}";
    }

    private static string RandomToken(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
}
