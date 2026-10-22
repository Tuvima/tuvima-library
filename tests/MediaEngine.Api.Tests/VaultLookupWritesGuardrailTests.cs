using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Looking up a Vault video, and applying what is found, writes only to the Vault's own tables, the file's own claims and values,
/// and the Vault's own image rows. It never creates people, series, tags or collections that the ordinary library would show
/// (Videos spec D17, WP17 part C).
/// </summary>
public sealed class VaultLookupWritesGuardrailTests
{
    private static readonly string Repo = FindRepoRoot();

    // The lookup and apply path, and every Vault repository.
    private static IEnumerable<string> LookupSources()
    {
        var providers = Path.Combine(Repo, "src", "MediaEngine.Providers", "Services");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(providers, "Vault"), "*.cs"))
        {
            yield return file;
        }

        yield return Path.Combine(providers, "StashBox", "VaultVideoDetailsService.cs");
        yield return Path.Combine(providers, "StashBox", "StashBoxVideoLookup.cs");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo, "src", "MediaEngine.Storage"), "Vault*Repository.cs"))
        {
            yield return file;
        }
    }

    // Tables the lookup path may write outside vault_*: the Vault's own image rows (only VaultImageRepository, which removes a
    // Vault library's own images).
    private static readonly Dictionary<string, string[]> OwnRowsOutsideVaultTables = new(StringComparer.Ordinal)
    {
        ["VaultImageRepository.cs"] = ["local_items", "local_files", "local_file_sources"],
        // Putting a restored bookmark back is personal state the person made, not a lookup result; it names no person, series or tag.
        ["VaultBookmarkRelinkRepository.cs"] = ["audiobook_bookmarks"],
        // Removing a Vault library (an administrator action, not the lookup) purges the residue that only the Vault mark made
        // recognisable: the library's excluded-file rows and the file operations about its folders (security review L1).
        ["VaultLibraryRepository.cs"] = ["excluded_source_files", "media_operations", "media_operation_events"],
    };

    private static readonly Regex Writes = new(
        @"\b(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|DELETE\s+FROM|UPDATE)\s+(?<table>[A-Za-z_]\w*)(?=\s+SET\b|\s*\(|\s+(?:WHERE|SELECT|VALUES|AS)\b|\s*$|\s*;|\s*\r?\n)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void TheVaultLookupPath_WritesOnlyVaultTables_AndTheFilesOwnImageRows()
    {
        var offenders = new List<string>();
        var sawVaultWrite = false;
        foreach (var path in LookupSources())
        {
            var name = Path.GetFileName(path);
            var allowed = OwnRowsOutsideVaultTables.GetValueOrDefault(name) ?? [];
            foreach (Match match in Writes.Matches(File.ReadAllText(path)))
            {
                var table = match.Groups["table"].Value;
                if (table.StartsWith("vault_", StringComparison.OrdinalIgnoreCase))
                {
                    sawVaultWrite = true;
                }
                else if (!allowed.Contains(table, StringComparer.OrdinalIgnoreCase))
                {
                    offenders.Add($"{name} writes {table}");
                }
            }
        }

        Assert.True(sawVaultWrite, "The scan found no Vault write at all; the pattern no longer matches the code.");
        Assert.True(offenders.Count == 0, "The Vault lookup path writes outside the Vault tables: " + string.Join("; ", offenders));
    }

    [Fact]
    public void TheVaultLookupPath_NeverReachesPeopleSeriesTagsOrCollections()
    {
        var forbidden = new Regex(
            @"\bI(?:Person|People|Series|Tag|Collection|ParentCollection|Universe|Work|Edition|Hydration|Enrichment|Wikidata|Review)\w*(?:Repository|Service|Writer)\b"
            + @"|\b(?:PersonRepository|SeriesRepository|TagRepository|CollectionRepository|PersonLinker|PersonReconciler)\b",
            RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var path in LookupSources())
        {
            foreach (Match match in forbidden.Matches(File.ReadAllText(path)))
            {
                offenders.Add($"{Path.GetFileName(path)} uses {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0, "The Vault lookup path reaches the ordinary library's records: " + string.Join("; ", offenders.Distinct()));
    }

    [Fact]
    public void TheVaultLookupPath_KeepsTagsAndNamesInTheVaultsOwnTables()
    {
        var applier = File.ReadAllText(Path.Combine(Repo, "src", "MediaEngine.Providers", "Services", "Vault", "VaultRecordApplier.cs"));
        Assert.Contains("_names.EnsureTagAsync", applier);
        Assert.Contains("_names.LinkTagAsync", applier);

        var names = File.ReadAllText(Path.Combine(Repo, "src", "MediaEngine.Storage", "VaultNameRepository.cs"));
        foreach (Match match in Writes.Matches(names))
        {
            Assert.StartsWith("vault_", match.Groups["table"].Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
