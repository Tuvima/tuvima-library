using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Builds the path of a declared API endpoint from the caller's arguments. Every argument is
/// checked against its declared kind and URI-escaped; a bad argument throws before anything is sent.
/// </summary>
internal static partial class ApiEndpointPath
{
    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex OptionalSegment();

    [GeneratedRegex(@"\{([a-z_][a-z0-9_]*)\}")]
    private static partial Regex Placeholder();

    /// <summary>Optional segments whose arguments are missing are dropped; the rest is filled in.</summary>
    public static string Build(
        string endpointName,
        ApiEndpointConfiguration endpoint,
        IReadOnlyDictionary<string, string>? arguments,
        int? page = null)
    {
        var values = new Dictionary<string, string>(endpoint.Defaults, StringComparer.Ordinal);
        foreach (var (name, value) in arguments ?? new Dictionary<string, string>())
        {
            if (!endpoint.Args.ContainsKey(name))
            {
                throw new ArgumentException($"Endpoint '{endpointName}' has no argument '{name}'.", nameof(arguments));
            }

            values[name] = value;
        }

        var escaped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, declared) in endpoint.Args)
        {
            var optional = declared.EndsWith('?');
            var kind = optional ? declared[..^1] : declared;
            if (!values.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                if (optional)
                {
                    continue;
                }

                throw new ArgumentException($"Endpoint '{endpointName}' needs the argument '{name}'.", nameof(arguments));
            }

            escaped[name] = Check(endpointName, name, kind, raw);
        }

        if (page is { } pageNumber)
        {
            escaped["page"] = Math.Max(0, pageNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var path = OptionalSegment().Replace(endpoint.Path, match =>
            Placeholder().Matches(match.Groups[1].Value).All(placeholder => escaped.ContainsKey(placeholder.Groups[1].Value))
                ? match.Groups[1].Value
                : string.Empty);
        return Placeholder().Replace(path, match =>
            escaped.TryGetValue(match.Groups[1].Value, out var value)
                ? value
                : throw new ArgumentException($"Endpoint '{endpointName}' has no value for '{match.Value}'.", nameof(endpoint)));
    }

    private static string Check(string endpointName, string name, string kind, string value)
    {
        if (kind == ApiArgumentKinds.Digits)
        {
            return value.All(char.IsDigit)
                ? Uri.EscapeDataString(value)
                : throw new ArgumentException($"The argument '{name}' of '{endpointName}' must be numeric.", name);
        }

        if (kind == ApiArgumentKinds.Alpha3)
        {
            return value.Length == 3 && value.All(char.IsLetter)
                ? Uri.EscapeDataString(value.ToLowerInvariant())
                : throw new ArgumentException($"The argument '{name}' of '{endpointName}' must be a three-letter code.", name);
        }

        if (kind.StartsWith(ApiArgumentKinds.OneOfPrefix, StringComparison.Ordinal))
        {
            var allowed = kind[ApiArgumentKinds.OneOfPrefix.Length..].Split(',');
            return allowed.Contains(value, StringComparer.Ordinal)
                ? Uri.EscapeDataString(value)
                : throw new ArgumentOutOfRangeException(name, value, $"The argument '{name}' of '{endpointName}' must be one of {string.Join(", ", allowed)}.");
        }

        return Uri.EscapeDataString(value);
    }
}
