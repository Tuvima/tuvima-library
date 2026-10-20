using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// The query string a routed page was routed with.
/// While the details overlay is open the address bar names the detail item, but the page under it must keep seeing
/// its own query (its filters, sort, search). A routed page therefore reads its query from this value, cascaded by the
/// overlay-aware route view as the address that page was routed with, and never binds the address bar directly with
/// <c>[SupplyParameterFromQuery]</c> (guarded by a test). A page that is rendered outside the route view falls back to the live address.
/// </summary>
/// <remarks>
/// Read values the way the framework's query binding did: a missing value is null (an empty array for a list, false for a flag)
/// and when a key repeats the first value wins. A value that cannot be parsed as a number, id or flag reads as missing, where the
/// framework binding threw and failed the page.
/// </remarks>
public readonly struct RoutedPageQuery
{
    /// <summary>The name of the cascading value (a string, so an unchanged address never re-renders the page it feeds).</summary>
    public const string CascadingName = "RoutedPageUri";

    private readonly Dictionary<string, StringValues>? _values;

    private RoutedPageQuery(Dictionary<string, StringValues> values) => _values = values;

    /// <summary>Reads the query of <paramref name="routedUri"/> (an absolute URI or an app path with a query).</summary>
    public static RoutedPageQuery From(string? routedUri)
    {
        if (string.IsNullOrEmpty(routedUri))
        {
            return default;
        }

        var start = routedUri.IndexOf('?', StringComparison.Ordinal);
        if (start < 0)
        {
            return default;
        }

        var end = routedUri.IndexOf('#', start);
        var query = end < 0 ? routedUri[start..] : routedUri[start..end];
        return new RoutedPageQuery(QueryHelpers.ParseQuery(query));
    }

    /// <summary>The value of <paramref name="name"/>, or null when absent.</summary>
    public string? String(string name) => First(name);

    /// <summary>Every value of <paramref name="name"/>; an empty array when absent (as the framework binding gave).</summary>
    public string[] Strings(string name) =>
        _values is not null && _values.TryGetValue(name, out var values)
            ? values.Select(static value => value ?? string.Empty).ToArray()
            : [];

    public int? Int(string name) =>
        int.TryParse(First(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public Guid? Guid(string name) => System.Guid.TryParse(First(name), out var value) ? value : null;

    /// <summary>True only for <c>name=true</c> (any case); anything else, including absent, is false.</summary>
    public bool Bool(string name) => bool.TryParse(First(name), out var value) && value;

    private string? First(string name) =>
        _values is not null && _values.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}
