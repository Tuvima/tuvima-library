using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// "Approve on another device" sign-in for a plugin's personal credential (Plugin architecture spec C13). The person asks to sign in,
/// the Library gets a short code from the site and shows it, the person approves it on a phone or computer, and the Library
/// keeps the token that comes back as that profile's own credential, through <see cref="PluginProfileConnectionService"/> (encrypted,
/// never readable by another profile or an administrator). The app's own client id and secret are the plugin's household secrets.
/// Nothing else about the person is sent: only the app's client id goes out to ask for a code, and the code and client secret to
/// collect the token.
/// </summary>
public sealed class PluginDeviceSignInService(
    PluginCatalog catalog,
    PluginProfileConnectionService connections,
    IConfigSecretsStore secrets,
    IHttpClientFactory http,
    TimeProvider clock,
    ILogger<PluginDeviceSignInService> logger)
{
    public const string HttpClientName = "plugin_device_sign_in";

    /// <summary>A token this close to running out (or past it) is renewed before it is used.</summary>
    private static readonly TimeSpan RenewWithin = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan DefaultCodeLife = TimeSpan.FromMinutes(10);
    private const int DefaultIntervalSeconds = 5;
    private const int MaximumResponseCharacters = 16 * 1024;

    private readonly ConcurrentDictionary<string, PendingCode> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _renewals = new(StringComparer.Ordinal);

    /// <summary>The sign-in a personal credential offers, or null when it is entered by hand (or the plugin is not installed).</summary>
    public PluginDeviceSignIn? SignInFor(string pluginId, string name) =>
        connections.PersonalCredentials(pluginId)
            .FirstOrDefault(secret => string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase))?.SignIn;

    /// <summary>True when the credential can be signed in to but the app's client id has not been entered yet.</summary>
    public async Task<bool> NeedsSetupAsync(string pluginId, string name, CancellationToken ct)
    {
        var signIn = SignInFor(pluginId, name);
        if (signIn is null || catalog.Get(pluginId) is not { } registration)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(await ClientValueAsync(registration.Manifest.Id, signIn.ClientIdSecret, ct).ConfigureAwait(false));
    }

    // ── Start ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Asks the site for a code and remembers it for this profile. The caller has checked the person agreed.</summary>
    public async Task<DeviceSignInStarted> StartAsync(string pluginId, Guid profileId, string name, CancellationToken ct)
    {
        if (SignInFor(pluginId, name) is not { } signIn || catalog.Get(pluginId) is not { } registration)
        {
            return DeviceSignInStarted.NotAsked;
        }

        var canonicalId = registration.Manifest.Id;
        var clientId = await ClientValueAsync(canonicalId, signIn.ClientIdSecret, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return DeviceSignInStarted.NeedsSetup;
        }

        var answer = await PostAsync(registration, signIn, signIn.CodePath, new Dictionary<string, string> { ["client_id"] = clientId }, ct).ConfigureAwait(false);
        if (answer.Status != HttpStatusCode.OK || answer.Body is not { } body)
        {
            logger.LogInformation("Plugin {PluginId} sign-in could not get a code ({Status})", canonicalId, (int)answer.Status);
            return DeviceSignInStarted.Unavailable;
        }

        var deviceCode = Text(body, "device_code");
        var userCode = Text(body, "user_code");
        var url = Text(body, "verification_url") ?? Text(body, "verification_uri");
        if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode) || !IsHttps(url))
        {
            logger.LogInformation("Plugin {PluginId} sign-in got an answer that was not a code", canonicalId);
            return DeviceSignInStarted.Unavailable;
        }

        var life = Number(body, "expires_in") is { } seconds and > 0 ? TimeSpan.FromSeconds(Math.Min(seconds, 3600)) : DefaultCodeLife;
        var interval = Math.Clamp(Number(body, "interval") ?? DefaultIntervalSeconds, 1, 60);
        var now = clock.GetUtcNow();
        _pending[KeyOf(canonicalId, profileId, name)] = new PendingCode(deviceCode, now + life, interval, now + TimeSpan.FromSeconds(interval));
        return DeviceSignInStarted.Started(new PluginDeviceSignInStartResponse
        {
            UserCode = userCode,
            VerificationUrl = url!,
            ExpiresInSeconds = (int)life.TotalSeconds,
            IntervalSeconds = interval,
        });
    }

    // ── Check ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One look at whether the person has approved the code yet. Asks the site no more often than it allowed.</summary>
    public async Task<PluginDeviceSignInCheckResponse> CheckAsync(string pluginId, Guid profileId, string name, CancellationToken ct)
    {
        if (SignInFor(pluginId, name) is not { } signIn || catalog.Get(pluginId) is not { } registration)
        {
            return Failed("This plugin does not sign in that way.");
        }

        var key = KeyOf(registration.Manifest.Id, profileId, name);
        if (!_pending.TryGetValue(key, out var pending))
        {
            return new PluginDeviceSignInCheckResponse { Status = PluginDeviceSignInStatuses.Expired, Message = "That code is no longer waiting. Start again to get a new one." };
        }

        var now = clock.GetUtcNow();
        if (now >= pending.ExpiresAt)
        {
            _pending.TryRemove(key, out _);
            return new PluginDeviceSignInCheckResponse { Status = PluginDeviceSignInStatuses.Expired, Message = "That code ran out. Start again to get a new one." };
        }

        if (now < pending.NextPollAt)
        {
            return Waiting(pending.IntervalSeconds);
        }

        var clientId = await ClientValueAsync(registration.Manifest.Id, signIn.ClientIdSecret, ct).ConfigureAwait(false);
        var clientSecret = signIn.ClientSecretSecret is null ? null : await ClientValueAsync(registration.Manifest.Id, signIn.ClientSecretSecret, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            _pending.TryRemove(key, out _);
            return Failed("The app's client id was removed while you were signing in.");
        }

        var form = new Dictionary<string, string> { ["code"] = pending.DeviceCode, ["client_id"] = clientId };
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            form["client_secret"] = clientSecret;
        }

        var answer = await PostAsync(registration, signIn, signIn.TokenPath, form, ct).ConfigureAwait(false);
        switch (answer.Status)
        {
            case HttpStatusCode.OK when answer.Body is { } body && ReadToken(body, clock.GetUtcNow(), previousRefresh: null) is { } token:
                _pending.TryRemove(key, out _);
                await connections.LinkAsync(registration.Manifest.Id, profileId, name, token.ToJson(), ct).ConfigureAwait(false);
                return new PluginDeviceSignInCheckResponse { Status = PluginDeviceSignInStatuses.SignedIn };
            case HttpStatusCode.BadRequest:
                // The site's way of saying "not approved yet".
                _pending[key] = pending with { NextPollAt = clock.GetUtcNow() + TimeSpan.FromSeconds(pending.IntervalSeconds) };
                return Waiting(pending.IntervalSeconds);
            case HttpStatusCode.TooManyRequests:
                var slower = Math.Min(pending.IntervalSeconds + 1, 60);
                _pending[key] = pending with { IntervalSeconds = slower, NextPollAt = clock.GetUtcNow() + TimeSpan.FromSeconds(slower) };
                return Waiting(slower);
            case HttpStatusCode.Gone:
                _pending.TryRemove(key, out _);
                return new PluginDeviceSignInCheckResponse { Status = PluginDeviceSignInStatuses.Expired, Message = "That code ran out. Start again to get a new one." };
            case (HttpStatusCode)418:
                _pending.TryRemove(key, out _);
                return new PluginDeviceSignInCheckResponse { Status = PluginDeviceSignInStatuses.Denied, Message = "The sign-in was turned down on the other device." };
            case HttpStatusCode.NotFound or HttpStatusCode.Conflict:
                _pending.TryRemove(key, out _);
                return Failed("That code was not accepted. Start again to get a new one.");
            default:
                // The site did not answer or answered oddly: keep the code and look again, until it runs out.
                _pending[key] = pending with { NextPollAt = clock.GetUtcNow() + TimeSpan.FromSeconds(pending.IntervalSeconds) };
                return Waiting(pending.IntervalSeconds);
        }
    }

    // ── Using the token ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// For the host only: this profile's access token, renewed first when it has run out. Null when the profile has not signed in,
    /// or when the site no longer accepts its token (the sign-in is then removed, so the person is asked to sign in again).
    /// </summary>
    internal async Task<string?> AccessTokenAsync(string pluginId, Guid profileId, string name, CancellationToken ct)
    {
        if (SignInFor(pluginId, name) is not { } signIn || catalog.Get(pluginId) is not { } registration)
        {
            return null;
        }

        var canonicalId = registration.Manifest.Id;
        var stored = DeviceToken.FromJson(await connections.ReadForHostAsync(canonicalId, profileId, name, ct).ConfigureAwait(false));
        if (stored is null)
        {
            return null;
        }

        if (!stored.NeedsRenewal(clock.GetUtcNow(), RenewWithin))
        {
            return stored.AccessToken;
        }

        // A refresh token works once, so two callers must not renew at the same time.
        var gate = _renewals.GetOrAdd(KeyOf(canonicalId, profileId, name), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            stored = DeviceToken.FromJson(await connections.ReadForHostAsync(canonicalId, profileId, name, ct).ConfigureAwait(false));
            if (stored is null)
            {
                return null;
            }

            if (!stored.NeedsRenewal(clock.GetUtcNow(), RenewWithin))
            {
                return stored.AccessToken;
            }

            return await RenewAsync(registration, signIn, profileId, name, stored, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>True when this profile has a token for the credential. Never the token.</summary>
    internal Task<bool> HasTokenAsync(string pluginId, Guid profileId, string name, CancellationToken ct) =>
        connections.IsLinkedAsync(catalog.Get(pluginId)?.Manifest.Id ?? pluginId, profileId, name, ct);

    private async Task<string?> RenewAsync(
        PluginRegistration registration, PluginDeviceSignIn signIn, Guid profileId, string name, DeviceToken stored, CancellationToken ct)
    {
        var canonicalId = registration.Manifest.Id;
        var stillValid = stored.IsStillValid(clock.GetUtcNow());
        if (string.IsNullOrWhiteSpace(signIn.RefreshPath) || string.IsNullOrWhiteSpace(stored.RefreshToken))
        {
            return await DropIfDeadAsync(canonicalId, profileId, name, stillValid, stored, ct).ConfigureAwait(false);
        }

        var clientId = await ClientValueAsync(canonicalId, signIn.ClientIdSecret, ct).ConfigureAwait(false);
        var clientSecret = signIn.ClientSecretSecret is null ? null : await ClientValueAsync(canonicalId, signIn.ClientSecretSecret, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return stillValid ? stored.AccessToken : null;
        }

        var form = new Dictionary<string, string>
        {
            ["refresh_token"] = stored.RefreshToken,
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["redirect_uri"] = "urn:ietf:wg:oauth:2.0:oob",
        };
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            form["client_secret"] = clientSecret;
        }

        var answer = await PostAsync(registration, signIn, signIn.RefreshPath!, form, ct).ConfigureAwait(false);
        if (answer.Status == HttpStatusCode.OK && answer.Body is { } body && ReadToken(body, clock.GetUtcNow(), stored.RefreshToken) is { } renewed)
        {
            await connections.LinkAsync(canonicalId, profileId, name, renewed.ToJson(), ct).ConfigureAwait(false);
            return renewed.AccessToken;
        }

        if (answer.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The site no longer honours this sign-in (the person removed the app there, or the refresh token ran out).
            logger.LogInformation("Plugin {PluginId} sign-in for a profile was refused on renewal; removing it", canonicalId);
            await connections.UnlinkAsync(canonicalId, profileId, name, ct).ConfigureAwait(false);
            return null;
        }

        // The site could not be reached: use the token while it still works, and try renewing again next time.
        return stillValid ? stored.AccessToken : null;
    }

    private async Task<string?> DropIfDeadAsync(string pluginId, Guid profileId, string name, bool stillValid, DeviceToken stored, CancellationToken ct)
    {
        if (stillValid)
        {
            return stored.AccessToken;
        }

        await connections.UnlinkAsync(pluginId, profileId, name, ct).ConfigureAwait(false);
        return null;
    }

    // ── Talking to the site ──────────────────────────────────────────────────────────────────────────────────────────

    private async Task<SiteAnswer> PostAsync(
        PluginRegistration registration, PluginDeviceSignIn signIn, string path, Dictionary<string, string> form, CancellationToken ct)
    {
        var destination = registration.Manifest.Network.FirstOrDefault(candidate => string.Equals(candidate.Host, signIn.Host, StringComparison.OrdinalIgnoreCase));
        if (destination is null)
        {
            return new SiteAnswer(HttpStatusCode.ServiceUnavailable, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{signIn.Host}{path}"))
            {
                Content = new StringContent(JsonSerializer.Serialize(form), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            foreach (var (header, value) in signIn.Headers)
            {
                request.Headers.TryAddWithoutValidation(header, value);
            }

            if (!string.IsNullOrWhiteSpace(signIn.ClientIdHeader) && form.TryGetValue("client_id", out var clientId))
            {
                request.Headers.TryAddWithoutValidation(signIn.ClientIdHeader, clientId);
            }

            using var response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Length > MaximumResponseCharacters)
            {
                return new SiteAnswer(response.StatusCode, null);
            }

            return new SiteAnswer(response.StatusCode, TryParse(text));
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("Plugin {PluginId} sign-in could not reach {Host}: {Reason}", registration.Manifest.Id, signIn.Host, ex.GetType().Name);
            return new SiteAnswer(HttpStatusCode.ServiceUnavailable, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SiteAnswer(HttpStatusCode.ServiceUnavailable, null);
        }
    }

    private async Task<string?> ClientValueAsync(string canonicalPluginId, string? secretName, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(secretName)
            ? null
            : await secrets.GetAsync(SecretOwner.Plugin(canonicalPluginId), secretName, ct).ConfigureAwait(false);

    private static JsonElement? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static DeviceToken? ReadToken(JsonElement body, DateTimeOffset now, string? previousRefresh)
    {
        var access = Text(body, "access_token");
        if (string.IsNullOrWhiteSpace(access))
        {
            return null;
        }

        var refresh = Text(body, "refresh_token") ?? previousRefresh;
        var expiresAt = Number(body, "expires_in") is { } seconds and > 0 ? now + TimeSpan.FromSeconds(seconds) : (DateTimeOffset?)null;
        return new DeviceToken(access, refresh, expiresAt);
    }

    private static PluginDeviceSignInCheckResponse Waiting(int interval) =>
        new() { Status = PluginDeviceSignInStatuses.Waiting, IntervalSeconds = interval };

    private static PluginDeviceSignInCheckResponse Failed(string message) =>
        new() { Status = PluginDeviceSignInStatuses.Failed, Message = message };

    private static string KeyOf(string pluginId, Guid profileId, string name) =>
        string.Create(CultureInfo.InvariantCulture, $"{pluginId.ToLowerInvariant()}|{profileId:N}|{name.ToLowerInvariant()}");

    private sealed record PendingCode(string DeviceCode, DateTimeOffset ExpiresAt, int IntervalSeconds, DateTimeOffset NextPollAt);

    private sealed record SiteAnswer(HttpStatusCode Status, JsonElement? Body);

    /// <summary>What is kept for a signed-in profile: the token, the one that renews it, and when it runs out.</summary>
    private sealed record DeviceToken(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt)
    {
        public bool NeedsRenewal(DateTimeOffset now, TimeSpan within) => ExpiresAt is { } end && now >= end - within;

        public bool IsStillValid(DateTimeOffset now) => ExpiresAt is null || now < ExpiresAt;

        public string ToJson() => JsonSerializer.Serialize(this);

        public static DeviceToken? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var token = JsonSerializer.Deserialize<DeviceToken>(json);
                return token is { AccessToken.Length: > 0 } ? token : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>How asking for a code ended.</summary>
public sealed record DeviceSignInStarted(DeviceSignInOutcome Outcome, PluginDeviceSignInStartResponse? Code = null)
{
    public static DeviceSignInStarted NotAsked { get; } = new(DeviceSignInOutcome.NotAsked);

    public static DeviceSignInStarted NeedsSetup { get; } = new(DeviceSignInOutcome.NeedsSetup);

    public static DeviceSignInStarted Unavailable { get; } = new(DeviceSignInOutcome.Unavailable);

    public static DeviceSignInStarted Started(PluginDeviceSignInStartResponse code) => new(DeviceSignInOutcome.Started, code);
}

public enum DeviceSignInOutcome
{
    Started,

    /// <summary>The plugin does not sign in to that credential.</summary>
    NotAsked,

    /// <summary>The app's client id has not been entered.</summary>
    NeedsSetup,

    /// <summary>The site did not give a code.</summary>
    Unavailable,
}
