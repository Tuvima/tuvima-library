using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// How a provider signs in before it can be called: one login request that returns a token, how
/// long the token lives, and how it is sent afterwards. Declared in <c>auth</c> of a provider file.
/// </summary>
public sealed class ProviderAuthConfiguration
{
    [JsonPropertyName("login")]
    public ProviderLoginConfiguration Login { get; set; } = new();

    [JsonPropertyName("deliver")]
    public ProviderTokenDeliveryConfiguration Deliver { get; set; } = new();

    /// <summary>How many times a request refused with 401 signs in again and is repeated (0 or 1).</summary>
    [JsonPropertyName("retry_on_unauthorized")]
    public int RetryOnUnauthorized { get; set; } = 1;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class ProviderLoginConfiguration
{
    /// <summary>Only <c>POST</c> with a JSON body is supported.</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "POST";

    /// <summary>Path below the API address.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// JSON body, in property order. Values are templates over the provider's credentials
    /// (<c>{api_key}</c>, <c>{pin}</c>, <c>{username}</c>, <c>{password}</c>, <c>{client_key}</c>,
    /// <c>{access_token}</c>); a value written <c>{pin?}</c> is left out when that credential is blank.
    /// </summary>
    [JsonPropertyName("body")]
    public Dictionary<string, string> Body { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Path of the token in the answer.</summary>
    [JsonPropertyName("token_path")]
    public string TokenPath { get; set; } = string.Empty;

    /// <summary>The token is trusted for this many days after sign-in.</summary>
    [JsonPropertyName("lifetime_days")]
    public double LifetimeDays { get; set; }

    /// <summary>The token is renewed this many minutes before it would expire.</summary>
    [JsonPropertyName("refresh_early_minutes")]
    public double RefreshEarlyMinutes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class ProviderTokenDeliveryConfiguration
{
    /// <summary>Header that carries the token.</summary>
    [JsonPropertyName("header")]
    public string Header { get; set; } = "Authorization";

    /// <summary>Header value with <c>{token}</c>, for example <c>Bearer {token}</c>.</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = "Bearer {token}";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>
/// Named endpoints of a provider's API that the adapter and other parts of the Library call
/// through the general provider client. Declared in <c>api</c> of a provider file.
/// </summary>
public sealed class ProviderApiConfiguration
{
    /// <summary>
    /// Hosts the API address may use. The address in <c>endpoints.api</c> must be HTTPS, on the
    /// default port, without credentials, and on one of these hosts, or no request is made.
    /// </summary>
    [JsonPropertyName("allowed_hosts")]
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Path of the useful part of every answer; the rest (paging links …) is read by paging rules.</summary>
    [JsonPropertyName("data_path")]
    public string? DataPath { get; set; }

    /// <summary>Minutes an answer is remembered in memory unless the endpoint says otherwise.</summary>
    [JsonPropertyName("cache_minutes")]
    public int CacheMinutes { get; set; }

    /// <summary>The memory cache is emptied when it holds this many answers.</summary>
    [JsonPropertyName("cache_max_entries")]
    public int CacheMaxEntries { get; set; } = 1000;

    [JsonPropertyName("endpoints")]
    public Dictionary<string, ApiEndpointConfiguration> Endpoints { get; set; } = new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>Accepted argument kinds of an API endpoint; <c>?</c> after a kind makes the argument optional.</summary>
public static class ApiArgumentKinds
{
    /// <summary>Only digits; anything else is refused.</summary>
    public const string Digits = "digits";

    /// <summary>Exactly three letters, lower-cased.</summary>
    public const string Alpha3 = "alpha3";

    /// <summary>Any text, URI-escaped.</summary>
    public const string Text = "text";

    /// <summary>One of a list: <c>one_of:a,b,c</c>.</summary>
    public const string OneOfPrefix = "one_of:";

    /// <summary>True when the declared kind (with or without the optional marker) is one the client understands.</summary>
    public static bool IsKnown(string declared)
    {
        var kind = declared.EndsWith('?') ? declared[..^1] : declared;
        return kind is Digits or Alpha3 or Text
            || (kind.StartsWith(OneOfPrefix, StringComparison.Ordinal) && kind.Length > OneOfPrefix.Length);
    }
}

public sealed class ApiEndpointConfiguration
{
    /// <summary>
    /// Path below the API address, with <c>{argument}</c> placeholders. A segment in square
    /// brackets, such as <c>[/{lang}]</c>, is left out when its argument is not given. A paged
    /// endpoint uses <c>{page}</c>.
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Argument name to kind (see <see cref="ApiArgumentKinds"/>).</summary>
    [JsonPropertyName("args")]
    public Dictionary<string, string> Args { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Values used for arguments the caller leaves out.</summary>
    [JsonPropertyName("defaults")]
    public Dictionary<string, string> Defaults { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Minutes an answer of this endpoint is remembered; overrides the provider default.</summary>
    [JsonPropertyName("cache_minutes")]
    public int? CacheMinutes { get; set; }

    [JsonPropertyName("paging")]
    public ApiPagingConfiguration? Paging { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class ApiPagingConfiguration
{
    /// <summary>Number of the first page.</summary>
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("max_pages")]
    public int MaxPages { get; set; }

    /// <summary>Path, in the full answer, of the list of items of a page; an empty page ends the read.</summary>
    [JsonPropertyName("items_path")]
    public string ItemsPath { get; set; } = string.Empty;

    /// <summary>Path, in the full answer, of the link to the next page; blank ends the read.</summary>
    [JsonPropertyName("next_path")]
    public string NextPath { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}
