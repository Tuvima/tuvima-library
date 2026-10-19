using System.Text.RegularExpressions;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// Turns values read from a run's data store into values that mean the same in any run folder: the sample, data
/// and run folders become tokens, the run's own row ids (and ids in Engine stream paths) become a token, and
/// "..._at" stamps stop carrying a clock. A GUID that belongs to a provider (such as one inside an image address)
/// is a real value and is kept. Real value changes are never touched.
/// </summary>
public sealed partial class BaselineNormalizer
{
    public const string GuidToken = "<guid>";
    public const string TimestampToken = "<timestamp>";
    public const string FolderHashToken = "source-folder:<folder-hash>";

    private readonly string _sampleRoot;
    private readonly HashSet<string> _localIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly (Regex Pattern, string Token)[] _roots;

    public BaselineNormalizer(string sampleRoot, params (string Root, string Token)[] otherRoots)
    {
        _sampleRoot = Trim(sampleRoot);
        var roots = new List<(string Root, string Token)> { (sampleRoot, "<samples>") };
        roots.AddRange(otherRoots);
        _roots = roots
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Root))
            .OrderByDescending(entry => entry.Root.Length)
            .Select(entry => (RootPattern(entry.Root), entry.Token))
            .ToArray();
    }

    /// <summary>Registers ids of rows in the run's own data store: wherever they appear in a value they are volatile.</summary>
    public void AddLocalIds(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
        {
            _localIds.Add(id.ToString("D"));
        }
    }

    /// <summary>True for keys whose value is a moment in time (for example <c>artwork_settled_at</c>).</summary>
    public static bool IsVolatileKey(string key) => key.EndsWith("_at", StringComparison.Ordinal);

    /// <summary>The path relative to the sample root with forward slashes; paths elsewhere are tokenised.</summary>
    public string RelativePath(string path)
    {
        var full = path.Replace('\\', '/');
        var root = _sampleRoot.Replace('\\', '/');
        if (root.Length > 0 && full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return full[(root.Length + 1)..];
        }

        return Text(path);
    }

    /// <summary>Folder names, GUIDs and nothing else are replaced.</summary>
    public string Text(string value)
    {
        foreach (var (pattern, token) in _roots)
        {
            value = pattern.Replace(value, token);
        }

        value = StreamIdPattern().Replace(value, GuidToken);
        // Audiobook recordings are keyed by a digest of the absolute folder path, which differs in every run folder.
        value = FolderHashPattern().Replace(value, FolderHashToken);
        return GuidPattern().Replace(value, match => _localIds.Contains(match.Value) ? GuidToken : match.Value);
    }

    /// <summary>A stored value for the given key: stamps become a token, everything else is tokenised text.</summary>
    public string Value(string key, string value) =>
        IsVolatileKey(key) && value.Length > 0 ? TimestampToken : Text(value);

    private static Regex RootPattern(string root)
    {
        var trimmed = Trim(root);
        var variants = new[]
            {
                trimmed,
                trimmed.Replace('\\', '/'),
                trimmed.Replace("\\", "\\\\", StringComparison.Ordinal),
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Regex.Escape);
        return new Regex(string.Join("|", variants), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Trim(string path) => path.TrimEnd('\\', '/');

    // Engine stream addresses name a row of this run: "/stream/artwork/<id>".
    [GeneratedRegex("(?<=/stream/[A-Za-z-]+/)[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex StreamIdPattern();

    [GeneratedRegex("source-folder:[0-9a-fA-F]{64}")]
    private static partial Regex FolderHashPattern();

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
