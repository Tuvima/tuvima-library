using System.Text;
using System.Text.RegularExpressions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Providers are plugins: no provider adapter, provider client or provider registration refers to
/// TVDB, TMDB, ComicVine, SubDL or LRCLIB by name. What those five do is described by their files in
/// <c>config/providers</c>. References to the shared catalogues of identifier and claim keys, and the
/// retail-match services listed for the Plugins work (WP20), are the only exceptions.
/// </summary>
public sealed class ProviderNamingGuardrailTests
{
    private static readonly Regex ProviderName = new("tvdb|tmdb|comic_?vine|subdl|lrclib", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Key catalogues and identifier placeholders are data about identifiers, not branches on a provider.
    private static readonly Regex[] CatalogueReferences =
    [
        new(@"\b(?:BridgeIdKeys|WellKnownProviders|MetadataFieldConstants|ProviderRateLimitDefaults)\.\w+", RegexOptions.Compiled),
        new(@"\{(?:tvdb|tmdb|comic_vine)_id\}", RegexOptions.Compiled),
        new(@"\b(?:request|source)\.TmdbId\b|\bTmdbId = ", RegexOptions.Compiled),
    ];

    // Retail-match services that still name TMDB. They move with the Plugins work (WP20).
    private static readonly Regex RetailMatchRegistrations = new(
        @"\b(?:TmdbRetailClient|TmdbTvSuggestionService|ITmdbSeasonEpisodeCatalogue|TmdbSeasonEpisodeCatalogue)\b", RegexOptions.Compiled);

    private static readonly string[] Scanned =
    [
        "src/MediaEngine.Providers/Adapters",
        "src/MediaEngine.Providers/Providers",
        "src/MediaEngine.Providers/Services/ProviderApiClient.cs",
        "src/MediaEngine.Providers/Services/ProviderSession.cs",
        "src/MediaEngine.Providers/Services/ApiEndpointPath.cs",
        "src/MediaEngine.Api/DependencyInjection/TuvimaProviderServiceCollectionExtensions.cs",
        "src/MediaEngine.Api/DependencyInjection/TuvimaHttpClientRegistration.cs",
    ];

    [Fact]
    public void AdapterClientAndRegistrationCode_NamesNoneOfTheFiveProviders()
    {
        var root = FindRepoRoot();
        var offences = new List<string>();
        foreach (var file in ScannedFiles(root))
        {
            var code = StripComments(File.ReadAllText(file));
            foreach (var catalogue in CatalogueReferences)
            {
                code = catalogue.Replace(code, string.Empty);
            }

            if (file.EndsWith("TuvimaProviderServiceCollectionExtensions.cs", StringComparison.Ordinal))
            {
                code = RetailMatchRegistrations.Replace(code, string.Empty);
            }

            offences.AddRange(
                code.Split('\n')
                    .Select((line, index) => (line, index))
                    .Where(entry => ProviderName.IsMatch(entry.line))
                    .Select(entry => $"{Path.GetRelativePath(root, file)}: {entry.line.Trim()}"));
        }

        Assert.True(offences.Count == 0, "Provider names found in adapter, client or registration code:\n" + string.Join('\n', offences));
    }

    [Fact]
    public void TheRetailMatchRegistrationsThatNameTmdb_AreMarkedForThePluginsWork()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "MediaEngine.Api", "DependencyInjection", "TuvimaProviderServiceCollectionExtensions.cs"));

        Assert.Contains("(WP20)", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tvdb", "config_driven")]
    [InlineData("tmdb", "config_driven")]
    [InlineData("comicvine", "config_driven")]
    [InlineData("subdl", "text_track")]
    [InlineData("lrclib", "text_track")]
    public void TheFiveProviders_RunFromTheirFiles(string name, string adapterType)
    {
        var file = File.ReadAllText(Path.Combine(FindRepoRoot(), "config", "providers", $"{name}.json"));

        Assert.Contains($"\"adapter_type\": \"{adapterType}\"", file, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScanner_IgnoresCommentsButSeesNamesInStringsAndIdentifiers()
    {
        var code = StripComments("// tvdb here\nvar a = \"https://x.test/tmdb\"; /* subdl */ class TvdbThing { }\n/// lrclib");

        Assert.Contains("https://x.test/tmdb", code, StringComparison.Ordinal);
        Assert.Contains("TvdbThing", code, StringComparison.Ordinal);
        Assert.DoesNotContain("subdl", code, StringComparison.Ordinal);
        Assert.DoesNotContain("lrclib", code, StringComparison.Ordinal);
        Assert.Matches(ProviderName, code);
    }

    private static IEnumerable<string> ScannedFiles(string root) =>
        Scanned.SelectMany(entry =>
        {
            var path = Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                : [path];
        });

    /// <summary>Removes comments but keeps string contents, so addresses such as https:// survive.</summary>
    internal static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                result.Append('\n');
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }

                i++;
                continue;
            }

            if (c == '"')
            {
                var verbatim = i > 0 && source[i - 1] == '@';
                result.Append(c);
                i++;
                while (i < source.Length)
                {
                    result.Append(source[i]);
                    if (source[i] == '\\' && !verbatim && i + 1 < source.Length)
                    {
                        result.Append(source[++i]);
                    }
                    else if (source[i] == '"')
                    {
                        break;
                    }

                    i++;
                }

                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
