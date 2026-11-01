using MediaEngine.Contracts.Collections;

namespace MediaEngine.Api.Services.Collections.ListImport.Sources;

/// <summary>
/// A web address a list can be read from, once a source has recognised it (Collections and lists spec 4d). The address is kept in
/// its plain form (no tracking parameters, no fragment) so the same list always has the same stored address.
/// </summary>
/// <param name="ProviderName">The provider file that serves it (<c>tmdb</c>, <c>mdblist</c>, <c>openlibrary</c>, <c>musicbrainz</c>).</param>
/// <param name="Kind">See <see cref="ListSourceKinds"/>.</param>
/// <param name="Address">The plain address, stored with the collection and shown to the person.</param>
/// <param name="Parts">What the source needs to ask for it: ids, user names and list names.</param>
public sealed record WebListAddress(string ProviderName, string Kind, string Address, IReadOnlyDictionary<string, string> Parts)
{
    public string Part(string name) => Parts.TryGetValue(name, out var value) ? value : string.Empty;
}

/// <summary>Why a web list could not be read.</summary>
public enum WebListFailure
{
    None = 0,

    /// <summary>The site did not answer, was rate-limited or errored. The import is saved and tried again later.</summary>
    Down,

    /// <summary>The list does not exist, or it is private.</summary>
    NotFound,

    /// <summary>The source needs a key and none is set.</summary>
    KeyMissing,

    /// <summary>The site refused the key.</summary>
    KeyRejected,
}

/// <summary>What asking a site for a list returned: the list, or the reason it could not be read.</summary>
public sealed record WebListFetch(ParsedList? List, WebListFailure Failure)
{
    public static WebListFetch Found(ParsedList list) => new(list, WebListFailure.None);

    public static WebListFetch Failed(WebListFailure failure) => new(null, failure);
}

/// <summary>
/// One web site Library can read lists from (Collections and lists spec D7, D8: a documented public API, never scraping). A source
/// recognises its own addresses, reads the list a page at a time within the site's limits, and serves the refresh job as an
/// <see cref="IListSourceFetcher"/>. It is also a metadata source on the Plugins page (its provider file), whose switch turns the
/// source off and whose key, if it needs one, is the person's own.
/// </summary>
public interface IWebListSource : IListSourceFetcher
{
    /// <summary>The provider file that describes this source on the Plugins page.</summary>
    string ProviderName { get; }

    /// <summary>The source in the words used when it is off: "TMDb lists".</summary>
    string DisplayName { get; }

    /// <summary>True when the source needs the person's own key before it can read a list.</summary>
    bool NeedsKey { get; }

    /// <summary>The address, when it is one of this source's list addresses.</summary>
    WebListAddress? Recognise(Uri address);

    /// <summary>Reads the whole list. Never throws for a site that is down or a list that is missing.</summary>
    Task<WebListFetch> FetchAsync(WebListAddress address, CancellationToken ct);
}
