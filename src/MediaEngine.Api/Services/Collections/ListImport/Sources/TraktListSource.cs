using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Collections.ListImport.Sources;

/// <summary>
/// Where a signed-in Trakt account's access token comes from (Collections and lists spec D7, WP6e; plugin architecture spec C13).
/// A person's own Trakt lists and watchlist can be read only with a token that person approved on another device. Library does
/// not hold such a token yet, so the shipped answer is <see cref="UnlinkedTraktAccessTokens"/>; the per-profile connection that
/// links the account plugs in here, and nothing else in the source changes.
/// </summary>
public interface ITraktAccessTokens
{
    /// <summary>The token of the person whose collection is being read, or null when no Trakt account is linked.</summary>
    Task<string?> TokenAsync(CancellationToken ct);
}

/// <summary>No Trakt account can be linked yet, so there is never a token.</summary>
public sealed class UnlinkedTraktAccessTokens : ITraktAccessTokens
{
    public Task<string?> TokenAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>
/// Trakt lists (Collections and lists spec D7, 4d): a public list (<c>trakt.tv/users/{user}/lists/{list}</c>, an official list
/// <c>trakt.tv/lists/official/{list}</c> or <c>trakt.tv/lists/{id}</c>) or a public watchlist (<c>trakt.tv/users/{user}/watchlist</c>),
/// read through Trakt's documented API with the person's own Trakt client id. Entries carry IMDb, TMDB and TVDB ids. Only the
/// list's address and the client id go to Trakt. A private list, or one's own (<c>users/me</c>), needs a signed-in account; with
/// no account linked it is refused in plain words.
/// </summary>
public sealed class TraktListSource(
    IConfigurationLoader configuration,
    IHttpClientFactory http,
    IProviderRateLimiterCoordinator limiter,
    ILogger<TraktListSource> logger,
    ITraktAccessTokens? tokens = null,
    IProviderHealthMonitor? health = null)
    : WebListSourceBase(configuration, http, limiter, health, logger)
{
    private const int PageSize = 100;
    private const string OwnerAlias = "me";

    private readonly ITraktAccessTokens _tokens = tokens ?? new UnlinkedTraktAccessTokens();

    public override string ProviderName => "trakt";

    public override string DisplayName => "Trakt lists";

    public override bool NeedsKey => true;

    protected override string DefaultApiBase => "https://api.trakt.tv";

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

    protected override async Task<ParsedList> ReadAsync(WebListAddress address, string apiBase, string? key, CancellationToken ct)
    {
        var user = address.Part("user");
        var token = await _tokens.TokenAsync(ct).ConfigureAwait(false);
        if (user.Equals(OwnerAlias, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(token))
        {
            // "me" is the signed-in account; there is nobody it could mean without one.
            throw new WebListFailureException(WebListFailure.SignInNeeded, "A Trakt list of your own needs you to be signed in to Trakt");
        }

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
