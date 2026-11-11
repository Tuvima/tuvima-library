using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MediaEngine.Api.Services.Plugins.Trakt;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Collections.ListImport.Sources;

/// <summary>
/// Where a signed-in Trakt account's access token comes from (Collections and lists spec D7, WP6e; plugin architecture spec C13).
/// A person's own Trakt lists and watchlist can be read only with a token that person approved on another device. The token is
/// that profile's own credential in the Trakt plugin; a profile can only ever reach its own.
/// </summary>
public interface ITraktAccessTokens
{
    /// <summary>
    /// The token of the profile whose collection is being read, renewed first if it ran out. Null when no profile is given, the
    /// profile has not signed in to Trakt, or Trakt no longer accepts its sign-in.
    /// </summary>
    Task<string?> TokenAsync(Guid? profileId, CancellationToken ct);

    /// <summary>True when the profile has a Trakt sign-in kept. Never the token.</summary>
    Task<bool> HasAccountAsync(Guid profileId, CancellationToken ct);
}

/// <summary>No Trakt account is linked to anyone: there is never a token. Used where the Trakt plugin is not part of the app.</summary>
public sealed class UnlinkedTraktAccessTokens : ITraktAccessTokens
{
    public Task<string?> TokenAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<bool> HasAccountAsync(Guid profileId, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>Whether the Trakt plugin is switched on, and the Trakt app's client ID the household entered. Read fresh each time.</summary>
public interface ITraktSettings
{
    Task<TraktSetup> LoadAsync(CancellationToken ct);
}

/// <param name="PluginOn">True when the Trakt plugin is switched on.</param>
/// <param name="ClientId">The Trakt app's client ID, or null when none is set.</param>
public sealed record TraktSetup(bool PluginOn, string? ClientId);

/// <summary>
/// Trakt lists (Collections and lists spec D7, 4d): a public list (<c>trakt.tv/users/{user}/lists/{list}</c>, an official list
/// <c>trakt.tv/lists/official/{list}</c> or <c>trakt.tv/lists/{id}</c>) or a watchlist (<c>trakt.tv/users/{user}/watchlist</c>),
/// read through Trakt's documented API with the household's own Trakt client id. Entries carry IMDb, TMDB and TVDB ids. A public
/// list sends Trakt only the list's address and the client id. A private list, or one's own (<c>users/me</c>), is read with the
/// token of the profile that signed in to Trakt (the Trakt plugin's per-profile connection), sent to Trakt only; with no sign-in
/// it is refused in plain words.
/// </summary>
public sealed class TraktListSource(
    ITraktSettings settings,
    IHttpClientFactory http,
    IProviderRateLimiterCoordinator limiter,
    ILogger<TraktListSource> logger,
    ITraktAccessTokens? tokens = null,
    IProviderHealthMonitor? health = null)
    : WebListSourceBase(null, http, limiter, health, logger)
{
    private const int PageSize = 100;
    private const string OwnerAlias = "me";
    private const string UserAgent = "Tuvima Library/1.0 (https://github.com/Tuvima/tuvima-library)";

    private static readonly ProviderRateLimitConfiguration RateLimit = new() { RequestsPerSecond = 2, Burst = 2, MaxConcurrency = 1 };

    private readonly ITraktAccessTokens _tokens = tokens ?? new UnlinkedTraktAccessTokens();

    public override string ProviderName => "trakt";

    public override string DisplayName => "Trakt lists";

    public override bool NeedsKey => true;

    public override bool CanSignIn => true;

    public override string SettingsPath => $"/settings/plugins/{TraktPlugin.PluginId}/settings";

    public override string SignInPath => TraktPlugin.SignInPagePath;

    protected override string DefaultApiBase => "https://api.trakt.tv";

    protected override async Task<WebSourceSetup?> LoadSetupAsync(CancellationToken ct)
    {
        var current = await settings.LoadAsync(ct).ConfigureAwait(false);
        return new WebSourceSetup(current.PluginOn, DefaultApiBase, current.ClientId, RateLimit, UserAgent);
    }

    public override async Task<bool> NeedsSignInAsync(CollectionListSource source, CancellationToken ct) =>
        source.ConnectionProfileId is { } profile
        && CanFetch(source)
        && !await _tokens.HasAccountAsync(profile, ct).ConfigureAwait(false);

    public override WebListAddress? Recognise(Uri address)
    {
        // trakt.me is Trakt's short-link site; its list links have the same shape as trakt.tv's.
        if (!IsHost(address, "trakt.tv") && !IsHost(address, "trakt.me"))
        {
            return null;
        }

        var segments = Segments(address);
        if (segments.Length >= 4
            && segments[0].Equals("users", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("lists", StringComparison.OrdinalIgnoreCase))
        {
            var user = segments[1].ToLowerInvariant();
            var list = segments[3].ToLowerInvariant();
            return new WebListAddress(
                ProviderName,
                ListSourceKinds.TraktList,
                $"https://trakt.tv/users/{user}/lists/{list}",
                new Dictionary<string, string> { ["user"] = user, ["list"] = list });
        }

        if (segments.Length >= 3
            && segments[0].Equals("users", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("watchlist", StringComparison.OrdinalIgnoreCase))
        {
            var user = segments[1].ToLowerInvariant();
            return new WebListAddress(
                ProviderName,
                ListSourceKinds.TraktWatchlist,
                $"https://trakt.tv/users/{user}/watchlist",
                new Dictionary<string, string> { ["user"] = user });
        }

        if (segments.Length >= 3
            && segments[0].Equals("lists", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("official", StringComparison.OrdinalIgnoreCase))
        {
            var list = segments[2].ToLowerInvariant();
            return new WebListAddress(
                ProviderName,
                ListSourceKinds.TraktList,
                $"https://trakt.tv/lists/official/{list}",
                new Dictionary<string, string> { ["list"] = list });
        }

        if (segments.Length >= 2
            && segments[0].Equals("lists", StringComparison.OrdinalIgnoreCase)
            && segments[1].All(char.IsAsciiDigit))
        {
            return new WebListAddress(
                ProviderName,
                ListSourceKinds.TraktList,
                $"https://trakt.tv/lists/{segments[1]}",
                new Dictionary<string, string> { ["list"] = segments[1] });
        }

        return null;
    }

    protected override WebListFailure? FailureFor(HttpStatusCode status)
    {
        if ((int)status is >= 200 and < 300)
        {
            return null;
        }

        return status switch
        {
            // Trakt answers 401 for a private list or one that needs the signed-in account, and 403 for a client id it does not accept.
            HttpStatusCode.Unauthorized => WebListFailure.SignInNeeded,
            HttpStatusCode.Forbidden => WebListFailure.KeyRejected,
            _ => base.FailureFor(status),
        };
    }

    protected override Task<ParsedList> ReadAsync(WebListAddress address, string apiBase, string? key, CancellationToken ct) =>
        ReadAsync(address, apiBase, key, profileId: null, accountFirst: false, ct);

    protected override async Task<ParsedList> ReadAsync(
        WebListAddress address, string apiBase, string? key, Guid? profileId, bool accountFirst, CancellationToken ct)
    {
        var isOwn = address.Part("user").Equals(OwnerAlias, StringComparison.Ordinal);

        // A public list needs no account, so none is sent. "me" is the signed-in account, and a list that was read with an account
        // before is read with it again. Anything else is tried plainly first and signed in for only if Trakt says it must be.
        string? token = null;
        if (profileId is { } profile && (isOwn || accountFirst))
        {
            token = await _tokens.TokenAsync(profile, ct).ConfigureAwait(false);
        }

        if (isOwn && string.IsNullOrWhiteSpace(token))
        {
            // "me" is the signed-in account; there is nobody it could mean without one.
            throw new WebListFailureException(WebListFailure.SignInNeeded, "A Trakt list of your own needs you to be signed in to Trakt");
        }

        try
        {
            var list = await ReadWithAsync(address, apiBase, key, token, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(token) ? list : list with { ConnectionProfileId = profileId };
        }
        catch (WebListFailureException ex) when (ex.Failure == WebListFailure.SignInNeeded && string.IsNullOrWhiteSpace(token) && profileId is not null)
        {
            // Trakt said this list is private. If this profile signed in, it is read as that person.
            token = await _tokens.TokenAsync(profileId, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw;
            }

            return (await ReadWithAsync(address, apiBase, key, token, ct).ConfigureAwait(false)) with { ConnectionProfileId = profileId };
        }
    }

    private async Task<ParsedList> ReadWithAsync(WebListAddress address, string apiBase, string? key, string? token, CancellationToken ct)
    {
        var user = address.Part("user");
        void Decorate(HttpRequestMessage request)
        {
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", key);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        var isWatchlist = address.Kind == ListSourceKinds.TraktWatchlist;
        var root = isWatchlist
            ? $"{apiBase}/users/{Uri.EscapeDataString(user)}/watchlist"
            : user.Length > 0
                ? $"{apiBase}/users/{Uri.EscapeDataString(user)}/lists/{Uri.EscapeDataString(address.Part("list"))}"
                : $"{apiBase}/lists/{Uri.EscapeDataString(address.Part("list"))}";
        var itemsUrl = isWatchlist ? root : root + "/items";

        var entries = new List<ParsedListEntry>();
        for (var page = 1; page <= MaximumPages && entries.Count < MaximumEntries; page++)
        {
            using var document = await GetJsonAsync(
                Query(
                    itemsUrl,
                    ("page", page.ToString(CultureInfo.InvariantCulture)),
                    ("limit", PageSize.ToString(CultureInfo.InvariantCulture))),
                ct,
                Decorate).ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var received = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                received++;
                if (EntryOf(item) is { } entry)
                {
                    entries.Add(entry with { Position = entries.Count + 1 });
                }
            }

            if (received < PageSize)
            {
                break;
            }
        }

        var name = isWatchlist
            ? (user.Equals(OwnerAlias, StringComparison.Ordinal) ? "Trakt watchlist" : $"{user}'s Trakt watchlist")
            : await ReadNameAsync(root, Decorate, address.Part("list"), ct).ConfigureAwait(false);
        return new ParsedList { Name = name, Entries = entries.Take(MaximumEntries).ToList() };
    }

    // The list's own name is a nicety: a failure here must not stop the entries from being read.
    private async Task<string> ReadNameAsync(string url, Action<HttpRequestMessage> decorate, string fallback, CancellationToken ct)
    {
        try
        {
            using var document = await GetJsonAsync(url, ct, decorate).ConfigureAwait(false);
            if (StringOf(document.RootElement, "name")?.Trim() is { Length: > 0 } name)
            {
                return name;
            }
        }
        catch (WebListFailureException ex) when (ex.Failure == WebListFailure.Down)
        {
            Logger.LogDebug("Trakt did not give the list's name: {Message}", ex.Message);
        }

        return fallback.Replace('-', ' ');
    }

    private static ParsedListEntry? EntryOf(JsonElement item)
    {
        var type = StringOf(item, "type");
        var (property, mediaType) = type switch
        {
            "movie" => ("movie", "Movies"),
            "show" => ("show", "TV"),
            _ => (string.Empty, string.Empty),
        };

        // Seasons, episodes and people are not titles a collection holds.
        if (property.Length == 0 || !item.TryGetProperty(property, out var media) || media.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var title = StringOf(media, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var ids = new Dictionary<string, string>();
        if (media.TryGetProperty("ids", out var idObject))
        {
            Add(ids, ListIdentifierKeys.Imdb, StringOf(idObject, "imdb"));
            Add(ids, ListIdentifierKeys.Tmdb, StringOf(idObject, "tmdb"));
            Add(ids, ListIdentifierKeys.Tvdb, StringOf(idObject, "tvdb"));
        }

        int? year = int.TryParse(StringOf(media, "year"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
        return new ParsedListEntry { Title = title.Trim(), Year = year, Ids = ids, MediaTypes = [mediaType] };
    }

    private static void Add(Dictionary<string, string> ids, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != "0")
        {
            ids[key] = value.Trim();
        }
    }
}
