using System.Text.RegularExpressions;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// What a kind's entry may refer to that lives outside the kinds file: the provider names that exist and the
/// <c>organization_templates</c> keys in <c>core.json</c>. Left out, those two checks are skipped.
/// </summary>
public sealed record LibraryKindKnowledge(IReadOnlySet<string> Providers, IReadOnlySet<string> NamingTemplates);

/// <summary>
/// The one set of rules for a library kind, used for the shipped file, for kinds a plugin supplies, and for the link between a
/// library and its kind. Every problem is one plain sentence naming the entry and saying what to change.
/// </summary>
public static partial class LibraryKindRules
{
    private const int MaxLabelLength = 80;
    private const int MaxTooltipLength = 300;
    private const int MaxProviders = 12;

    /// <summary>How many named libraries a plain kind (Other videos, Other audio, Other reading) allows.</summary>
    public const int MaxPlainLibraries = 5;

    private const int MaxLibrariesCeiling = 20;

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$")]
    private static partial Regex KeyPattern();

    public static bool IsValidKey(string? key) => !string.IsNullOrEmpty(key) && key.Length <= 64 && KeyPattern().IsMatch(key);

    /// <summary>The first problem with one kind, or null when it is fine. <paramref name="name"/> is how the entry is called in the sentence.</summary>
    public static string? Problem(LibraryKindDefinition kind, string name, LibraryKindKnowledge? knowledge = null)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!IsValidKey(kind.Key))
        {
            return $"{name} needs a \"key\" of lower-case letters, digits and underscores, starting with a letter (for example podcasts or recorded_tv), up to 64 characters.";
        }

        name = $"The library kind \"{kind.Key}\"";
        if (!LibraryAreas.IsValid(kind.Area))
        {
            return $"{name} has an \"area\" that does not exist. Use watch, listen, read, view or vault.";
        }

        if (string.IsNullOrWhiteSpace(kind.Label) || kind.Label.Length > MaxLabelLength)
        {
            return $"{name} needs a \"label\" of 1 to {MaxLabelLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(kind.Icon))
        {
            return $"{name} needs an \"icon\" (an AppMaterialIcons name such as Podcasts).";
        }

        if (kind.Format is null)
        {
            if (kind.Area != LibraryAreas.View)
            {
                return $"{name} needs a \"format\" (Movies, TV, Videos, Music, Audiobooks, Books or Comics); only View kinds have none.";
            }
        }
        else if (!LibraryKindFormats.IsValid(kind.Format))
        {
            return $"{name} has a \"format\" of \"{kind.Format}\". Use Movies, TV, Videos, Music, Audiobooks, Books or Comics.";
        }

        if (kind.Lookup is null)
        {
            return $"{name} needs a \"lookup\": \"none\" or an object with a providers list.";
        }

        if (kind.Lookup.Providers.Count > MaxProviders)
        {
            return $"{name} may name at most {MaxProviders} lookup providers.";
        }

        if (knowledge is not null)
        {
            var unknown = kind.Lookup.Providers.FirstOrDefault(provider => !knowledge.Providers.Contains(provider));
            if (unknown is not null)
            {
                return $"{name} looks details up from \"{unknown}\", which is not a metadata source of this Library or of the same plugin.";
            }
        }

        if (!LibraryKindGroupings.IsValid(kind.Grouping))
        {
            return $"{name} has a \"grouping\" of \"{kind.Grouping}\". Use one of {string.Join(", ", LibraryKindGroupings.All)}.";
        }

        if (kind.Format is not null)
        {
            if (string.IsNullOrWhiteSpace(kind.NamingTemplate))
            {
                return $"{name} needs a \"naming_template\" (a key under organization_templates in core.json).";
            }

            if (knowledge is not null && !knowledge.NamingTemplates.Contains(kind.NamingTemplate))
            {
                return $"{name} has a \"naming_template\" of \"{kind.NamingTemplate}\", which is not one of the Library's naming templates ({string.Join(", ", knowledge.NamingTemplates.Order(StringComparer.Ordinal))}).";
            }
        }

        if (!LibraryKindMisfits.IsValid(kind.Misfit))
        {
            return $"{name} has a \"misfit\" of \"{kind.Misfit}\". Use unmatched or keep.";
        }

        if (kind.Misfit == LibraryKindMisfits.Unmatched && kind.Lookup.IsNone)
        {
            return $"{name} sends files that do not fit to Unmatched, but it has no lookup. Use \"misfit\": \"keep\" or name a lookup provider.";
        }

        if (!LibraryKindHandling.IsValid(kind.DefaultHandling))
        {
            return $"{name} has a \"default_handling\" of \"{kind.DefaultHandling}\". Use read_only or managed.";
        }

        if (kind.Tooltip is null || kind.Tooltip.Length > MaxTooltipLength)
        {
            return $"{name} needs a \"tooltip\" of up to {MaxTooltipLength} characters.";
        }

        if (kind.Order < 0)
        {
            return $"{name} needs an \"order\" of 0 or more.";
        }

        if (kind.MaxLibraries < 1 || kind.MaxLibraries > MaxLibrariesCeiling)
        {
            return $"{name} needs a \"max_libraries\" between 1 and {MaxLibrariesCeiling}.";
        }

        if (!kind.AllowsMultiple && kind.MaxLibraries != 1)
        {
            return $"{name} has a \"max_libraries\" above 1 but \"allows_multiple\" is not true. Set \"allows_multiple\": true or use 1.";
        }

        return null;
    }

    /// <summary>Every problem in a list of kinds: each entry's own, and keys used more than once.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<LibraryKindDefinition> kinds, LibraryKindKnowledge? knowledge = null)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < kinds.Count; index++)
        {
            var kind = kinds[index];
            if (Problem(kind, $"kinds[{index}]", knowledge) is { } problem)
            {
                problems.Add(problem);
            }
            else if (!seen.Add(kind.Key))
            {
                problems.Add($"The library kind \"{kind.Key}\" is listed more than once.");
            }
        }

        return problems;
    }

    /// <summary>
    /// What is wrong with the link between catalogued libraries and their kinds: a missing <c>kind_key</c>, one that names no
    /// kind (unless <paramref name="allowUnknownKeys"/>, for a file read without the plugins' kinds), and a library whose media
    /// types do not include the kind's format.
    /// </summary>
    public static IReadOnlyList<string> LibraryProblems(
        LibrariesConfiguration libraries,
        IReadOnlyList<LibraryKindDefinition> kinds,
        bool allowUnknownKeys = false,
        bool allowMissingKeys = false)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(kinds);
        var problems = new List<string>();
        for (var index = 0; index < libraries.Libraries.Count; index++)
        {
            var library = libraries.Libraries[index];
            if (!string.Equals(library.Kind, LibraryKinds.Catalogued, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A library with no kind (the Vault's own entries, which no library list shows: Videos spec D45) is not one of
            // the library kinds. The whole-file check leaves it alone; a save from Settings or setup must name its kind.
            if (allowMissingKeys && string.IsNullOrEmpty(library.KindKey))
            {
                continue;
            }

            var prefix = $"libraries[{index}]";
            if (!IsValidKey(library.KindKey))
            {
                problems.Add($"{prefix}.kind_key must name a library kind (lower-case letters, digits and underscores).");
                continue;
            }

            var kind = kinds.FirstOrDefault(candidate => string.Equals(candidate.Key, library.KindKey, StringComparison.Ordinal));
            if (kind is null)
            {
                if (!allowUnknownKeys)
                {
                    problems.Add($"{prefix}.kind_key \"{library.KindKey}\" is not a library kind in library_kinds.json or an enabled plugin.");
                }

                continue;
            }

            if (kind.Format is null || !library.MediaTypes.Any(token => TokenMatchesFormat(token, kind.Format)))
            {
                problems.Add($"{prefix}.media_types must include {kind.Format ?? "a format"}, the format of the library kind \"{kind.Key}\".");
            }

            AddLookupContradiction(library, kind, prefix, problems);
        }

        AddCountAndNameProblems(libraries, kinds, problems);

        return problems;
    }

    // The kind decides what a library may look up (LibraryKindLookupPolicy), so a library whose own metadata_policy or youtube_lookup
    // says otherwise is reported in plain words. Ingestion follows the kind either way; the message is so nobody is surprised.
    private static void AddLookupContradiction(LibraryFolderConfig library, LibraryKindDefinition kind, string prefix, List<string> problems)
    {
        var mode = LibraryKindLookupPolicy.ModeOf(kind.Key, [kind]);
        if (mode == LibraryLookupMode.Plain)
        {
            var never = $"{prefix} is {Article(kind.Label)} {kind.Label} library, which never looks anything up";
            if (!LibraryMetadataPolicies.BypassesExternalIdentity(library.MetadataPolicy))
            {
                problems.Add($"{never}, but metadata_policy is \"{library.MetadataPolicy}\". Use local_only.");
            }

            if (library.YoutubeLookup)
            {
                problems.Add($"{never}, but youtube_lookup is true. Set youtube_lookup to false.");
            }
        }
        else if (mode == LibraryLookupMode.YouTube)
        {
            var youtube = $"{prefix} is {Article(kind.Label)} {kind.Label} library, which looks videos up on YouTube by their ID and nowhere else";
            if (!LibraryMetadataPolicies.BypassesExternalIdentity(library.MetadataPolicy))
            {
                problems.Add($"{youtube}, but metadata_policy is \"{library.MetadataPolicy}\". Use local_only.");
            }

            if (!library.YoutubeLookup)
            {
                problems.Add($"{youtube}, but youtube_lookup is false. Set youtube_lookup to true.");
            }
        }
    }

    private static string Article(string label) =>
        label.Length > 0 && "AEIOUaeiou".Contains(label[0]) ? "an" : "a";

    // A kind without allows_multiple holds one library; a plain kind holds up to max_libraries, each with its own name. Names are unique
    // (ignoring case) among the catalogued libraries of an area and never read as another kind's label (a library may keep its own
    // kind's label: that is the default name of the first one).
    private static void AddCountAndNameProblems(LibrariesConfiguration libraries, IReadOnlyList<LibraryKindDefinition> kinds, List<string> problems)
    {
        var catalogued = libraries.Libraries
            .Select((library, index) => (library, index))
            .Where(entry => string.Equals(entry.library.Kind, LibraryKinds.Catalogued, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(entry.library.KindKey))
            .ToList();

        foreach (var group in catalogued.Where(entry => IsValidKey(entry.library.KindKey)).GroupBy(entry => entry.library.KindKey, StringComparer.Ordinal))
        {
            var kind = kinds.FirstOrDefault(candidate => string.Equals(candidate.Key, group.Key, StringComparison.Ordinal));
            if (kind is null)
            {
                continue;
            }

            var allowed = kind.AllowsMultiple ? Math.Max(1, kind.MaxLibraries) : 1;
            if (group.Count() > allowed)
            {
                var extra = group.ElementAt(allowed);
                problems.Add(kind.AllowsMultiple
                    ? $"libraries[{extra.index}] is library number {allowed + 1} of the kind \"{kind.Key}\", which allows at most {allowed}."
                    : $"libraries[{extra.index}] is a second library of the kind \"{kind.Key}\", which holds one library.");
            }
        }

        foreach (var areaGroup in catalogued.Where(entry => !string.IsNullOrWhiteSpace(entry.library.Name)).GroupBy(entry => entry.library.Area, StringComparer.OrdinalIgnoreCase))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (library, index) in areaGroup)
            {
                var name = library.Name.Trim();
                if (!seen.Add(name))
                {
                    problems.Add($"libraries[{index}].name \"{name}\" is already used by another library in {areaGroup.Key}. Give each library its own name.");
                }
            }
        }

        foreach (var (library, index) in catalogued.Where(entry => !string.IsNullOrWhiteSpace(entry.library.Name)))
        {
            var kind = kinds.FirstOrDefault(candidate => string.Equals(candidate.Key, library.KindKey, StringComparison.Ordinal));
            if (kind is not { AllowsMultiple: true })
            {
                continue;
            }

            var name = library.Name.Trim();
            var clash = kinds.FirstOrDefault(other => !string.Equals(other.Key, kind.Key, StringComparison.Ordinal)
                && string.Equals(other.Label, name, StringComparison.OrdinalIgnoreCase));
            if (clash is not null)
            {
                problems.Add($"libraries[{index}].name \"{name}\" is the name of the library kind \"{clash.Label}\". Choose a different name.");
            }
        }
    }

    /// <summary>True when a library's media type token (Movies, TV, and the older TVShows spelling) is the kind's format.</summary>
    public static bool TokenMatchesFormat(string? token, string? format) =>
        !string.IsNullOrWhiteSpace(token)
        && !string.IsNullOrWhiteSpace(format)
        && (string.Equals(token.Trim(), format, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(format, nameof(MediaType.TV), StringComparison.OrdinalIgnoreCase)
                && string.Equals(token.Trim(), "TVShows", StringComparison.OrdinalIgnoreCase)));
}
