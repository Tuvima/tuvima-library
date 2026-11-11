using System.Net;
using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Services;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Api.Services.Collections.ListImport.Sources;

/// <summary>
/// What every web list source shares (Collections and lists spec D5, D7, D8; plugin architecture spec D7): the provider file read
/// fresh on each call (so the switch and key on the Plugins page apply at once), the source's own rate limit and User-Agent, one
/// place that turns a slow, refusing or missing site into a plain <see cref="WebListFailure"/>, and health reporting. A source
/// reads only the one address it was given and never sends anything but that address and the list's identifiers.
/// </summary>
public abstract class WebListSourceBase : IWebListSource
{
    /// <summary>The most entries one list can bring in; a list longer than this is read up to here.</summary>
    protected const int MaximumEntries = 5000;

    /// <summary>The most pages one list is read over, so a site that never ends cannot hold the refresh forever.</summary>
    protected const int MaximumPages = 60;

    private readonly IConfigurationLoader? _configuration;
    private readonly IHttpClientFactory _http;
    private readonly IProviderRateLimiterCoordinator _limiter;
    private readonly IProviderHealthMonitor? _health;

    /// <param name="configuration">The provider files. Null for a source that is a plugin and supplies its own <see cref="LoadSetupAsync"/>.</param>
    protected WebListSourceBase(
        IConfigurationLoader? configuration,
        IHttpClientFactory http,
        IProviderRateLimiterCoordinator limiter,
        IProviderHealthMonitor? health,
        ILogger logger)
    {
        _configuration = configuration;
        _http = http;
        _limiter = limiter;
        _health = health;
        Logger = logger;
    }

    protected ILogger Logger { get; }

    public abstract string ProviderName { get; }

    public abstract string DisplayName { get; }

    public virtual bool NeedsKey => false;

    /// <summary>The address of the site's API when the provider file does not give one.</summary>
    protected abstract string DefaultApiBase { get; }

    public abstract WebListAddress? Recognise(Uri address);

    /// <summary>Reads the list. Throws <see cref="WebListFailureException"/> for the failures a person can be told about.</summary>
    protected abstract Task<ParsedList> ReadAsync(WebListAddress address, string apiBase, string? key, CancellationToken ct);

    /// <summary>
    /// Reads the list for a profile. A source without sign-in ignores the profile; one with sign-in overrides this to read a private
    /// or the person's own list with that profile's account. <paramref name="accountFirst"/> is true for a refresh of a list that was
    /// read with the profile's account before, so the account is used straight away.
    /// </summary>
    protected virtual Task<ParsedList> ReadAsync(WebListAddress address, string apiBase, string? key, Guid? profileId, bool accountFirst, CancellationToken ct) =>
        ReadAsync(address, apiBase, key, ct);

    public virtual bool CanSignIn => false;

    public virtual string SettingsPath => "/settings/plugins";

    public virtual string SignInPath => SettingsPath;

    public virtual async Task<bool> IsSwitchedOnAsync(CancellationToken ct) =>
        await LoadSetupAsync(ct).ConfigureAwait(false) is { Enabled: true };

    public Task<WebListFetch> FetchAsync(WebListAddress address, CancellationToken ct) => FetchAsync(address, null, ct);

    public Task<WebListFetch> FetchAsync(WebListAddress address, Guid? profileId, CancellationToken ct) =>
        FetchCoreAsync(address, profileId, accountFirst: false, ct);

    private async Task<WebListFetch> FetchCoreAsync(WebListAddress address, Guid? profileId, bool accountFirst, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        var setup = await LoadSetupAsync(ct).ConfigureAwait(false);
        if (setup is null)
        {
            Logger.LogWarning("The {Provider} source is not set up, so its lists cannot be read", ProviderName);
            return WebListFetch.Failed(WebListFailure.Down);
        }

        string? key = null;
        if (NeedsKey)
        {
            key = setup.Key;
            if (string.IsNullOrWhiteSpace(key))
            {
                return WebListFetch.Failed(WebListFailure.KeyMissing);
            }
        }

        try
        {
            var list = await ReadAsync(address, setup.ApiBase, key, profileId, accountFirst, ct).ConfigureAwait(false);
            await ReportAsync(success: true, null, ct).ConfigureAwait(false);
            return WebListFetch.Found(list with { SourceKind = address.Kind, SourceAddress = address.Address });
        }
        catch (WebListFailureException ex)
        {
            Logger.LogInformation("{Source} could not read {Address}: {Failure}", DisplayName, address.Address, ex.Failure);
            if (ex.Failure == WebListFailure.Down)
            {
                await ReportAsync(success: false, ex.Message, ct).ConfigureAwait(false);
            }

            return WebListFetch.Failed(ex.Failure);
        }
    }

    // ── The refresh job ──────────────────────────────────────────────────────────────────────────────────────────────

    public bool CanFetch(CollectionListSource source) =>
        ListSourceKinds.IsWeb(source.SourceKind)
        && Uri.TryCreate(source.SourceAddress, UriKind.Absolute, out var uri)
        && Recognise(uri) is not null;

    public async Task<ParsedList?> FetchAsync(CollectionListSource source, CancellationToken ct)
    {
        if (!Uri.TryCreate(source.SourceAddress, UriKind.Absolute, out var uri) || Recognise(uri) is not { } address)
        {
            return null;
        }

        // A source that was switched off keeps what it brought in; the refresh waits until it is switched on again.
        if (!await IsSwitchedOnAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        // A list that was read with a profile's account is read with that profile's account again, and with no one else's.
        var fetch = await FetchCoreAsync(address, source.ConnectionProfileId, accountFirst: source.ConnectionProfileId is not null, ct).ConfigureAwait(false);
        return fetch.List;
    }

    public virtual Task<bool> NeedsSignInAsync(CollectionListSource source, CancellationToken ct) => Task.FromResult(false);

    // ── Setup ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the source needs to read a list: whether it is switched on, where its API is, the key, its rate limit and User-Agent.
    /// Read fresh on every use. By default it comes from the source's provider file; a source that is a plugin supplies its own.
    /// Null when the source has nothing to read from.
    /// </summary>
    protected virtual Task<WebSourceSetup?> LoadSetupAsync(CancellationToken ct)
    {
        var provider = _configuration?.LoadProvider(ProviderName);
        return Task.FromResult<WebSourceSetup?>(provider is null
            ? null
            : new WebSourceSetup(
                provider.Enabled,
                ApiBaseOf(provider),
                KeyOf(provider),
                provider.RateLimit,
                provider.HttpClient?.UserAgent));
    }

    /// <summary>The key the person set for this source, preferring an administrator override. Null when none is set.</summary>
    protected virtual string? KeyOf(ProviderConfiguration provider) =>
        !string.IsNullOrWhiteSpace(provider.HttpClient?.ApiKeyOverride) ? provider.HttpClient.ApiKeyOverride : provider.HttpClient?.ApiKey;

    private string ApiBaseOf(ProviderConfiguration provider) =>
        provider.Endpoints.TryGetValue("api", out var api) && !string.IsNullOrWhiteSpace(api) ? api.TrimEnd('/') : DefaultApiBase;

    // ── Reading JSON ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Gets one JSON document within the source's rate limit. <paramref name="decorate"/> adds headers a site wants. The caller disposes it.</summary>
    protected async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? decorate = null)
    {
        var setup = await LoadSetupAsync(ct).ConfigureAwait(false);
        var client = _http.CreateClient(HttpClientName);
        var userAgent = setup?.UserAgent;
        try
        {
            return await _limiter.ExecuteAsync(
                ProviderName,
                setup?.RateLimit,
                async token =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Accept.ParseAdd("application/json");
                    decorate?.Invoke(request);
                    if (!string.IsNullOrWhiteSpace(userAgent))
                    {
                        request.Headers.UserAgent.ParseAdd(userAgent);
                    }

                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    var failure = FailureFor(response.StatusCode);
                    if (failure is not null)
                    {
                        throw new WebListFailureException(failure.Value, $"{DisplayName} answered {(int)response.StatusCode}");
                    }

                    await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    return await JsonDocument.ParseAsync(body, cancellationToken: token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new WebListFailureException(WebListFailure.Down, $"{DisplayName} could not be reached: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new WebListFailureException(WebListFailure.Down, $"{DisplayName} took too long to answer: {ex.Message}");
        }
        catch (JsonException ex)
        {
            throw new WebListFailureException(WebListFailure.Down, $"{DisplayName} sent something that was not JSON: {ex.Message}");
        }
    }

    /// <summary>The name of the HTTP client the source uses, registered with the provider's User-Agent.</summary>
    protected virtual string HttpClientName => $"list_source_{ProviderName}";

    /// <summary>Null for a success; the failure a status code means otherwise.</summary>
    protected virtual WebListFailure? FailureFor(HttpStatusCode status)
    {
        if ((int)status is >= 200 and < 300)
        {
            return null;
        }

        return status switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => WebListFailure.NotFound,
            HttpStatusCode.Unauthorized => NeedsKey ? WebListFailure.KeyRejected : WebListFailure.NotFound,
            HttpStatusCode.Forbidden => NeedsKey ? WebListFailure.KeyRejected : WebListFailure.NotFound,
            _ => WebListFailure.Down,
        };
    }

    private async Task ReportAsync(bool success, string? reason, CancellationToken ct)
    {
        if (_health is null)
        {
            return;
        }

        if (success)
        {
            await _health.ReportSuccessAsync(ProviderName, ct).ConfigureAwait(false);
        }
        else
        {
            await _health.ReportFailureAsync(ProviderName, reason ?? "list could not be read", ct).ConfigureAwait(false);
        }
    }

    // ── Helpers for the sources ──────────────────────────────────────────────────────────────────────────────────────

    protected static string Query(string url, params (string Name, string? Value)[] pairs)
    {
        var parts = pairs
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Name)}={Uri.EscapeDataString(pair.Value!)}");
        var joined = string.Join('&', parts);
        if (joined.Length == 0)
        {
            return url;
        }

        return url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + joined;
    }

    protected static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : null;

    protected static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var year) && year is > 1000 and < 3000 ? year : null;

    /// <summary>The trimmed path segments of an address, without the empty ones.</summary>
    protected static string[] Segments(Uri address) =>
        address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    protected static bool IsHost(Uri address, string host) =>
        address.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
        || address.Host.Equals("www." + host, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a web source needs to read a list (see <see cref="WebListSourceBase"/>).</summary>
/// <param name="Enabled">True when the source is switched on.</param>
/// <param name="ApiBase">The site's API address, without a trailing slash.</param>
/// <param name="Key">The person's own key or client ID, or null when none is set.</param>
/// <param name="RateLimit">How fast the source may ask, or null for the default.</param>
/// <param name="UserAgent">The User-Agent to send, or null for none.</param>
public sealed record WebSourceSetup(bool Enabled, string ApiBase, string? Key, ProviderRateLimitConfiguration? RateLimit, string? UserAgent);

/// <summary>A web list could not be read for a reason the person can be told about. Raised and caught inside the sources.</summary>
public sealed class WebListFailureException(WebListFailure failure, string message) : Exception(message)
{
    public WebListFailure Failure { get; } = failure;
}
