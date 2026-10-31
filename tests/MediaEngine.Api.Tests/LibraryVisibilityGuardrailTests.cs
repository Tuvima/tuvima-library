using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A library is on or off per household and nowhere else (libraries redesign D16 and D19, library access v4). One rule answers "may this
/// profile see this library": a profile sees every library in the areas it has unless the profile's household turned it off, read through
/// <c>LibraryVisibilityRule</c>. These tests fail the build when any per-profile library rule exists (a per-profile hidden table, a
/// per-profile area switch table, a repository member that reads one, or a wire field that carries one), when anything brings back the
/// earlier per-library grants ("Selected profiles"), or when a new file starts asking for a household's hidden set without being reviewed here.
/// </summary>
public sealed class LibraryVisibilityGuardrailTests
{
    private const string RuleType = "src/MediaEngine.Api/Security/LibraryVisibilityRule.cs";

    // The per-profile library list and the per-profile area switches are gone (step 0069). Only the steps that created or dropped them,
    // or that say what they add to, may spell them. Nothing else, including the generated snapshot, may.
    private static readonly Dictionary<string, string> MayNameThePerProfileTables = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Storage/Schema/Upgrades/0046_profile_held_access.sql"] = "the released step that created them",
        ["src/MediaEngine.Storage/Schema/Upgrades/0067_household_library_hidden.sql"] = "the step that creates the household table, which says what it adds to",
        ["src/MediaEngine.Storage/Schema/Upgrades/0069_remove_per_profile_library_and_area_access.sql"] = "the step that drops them",
    };

    private static readonly string[] PerProfileTables = ["profile_library_hidden", "profile_feature_grants"];

    // Wire names that carried a per-profile library list or per-profile area switches. (The household's own list is "hidden_library_ids"
    // on the household contracts and is the one rule that stays.)
    private static readonly string[] PerProfileWireNames = ["household_hidden_library_ids", "feature_ids", "FeatureIds", "ReplaceProfileAccessRequest"];

    // The only files that may name the household table: the repository that owns it and the places that list every table by name.
    private static readonly Dictionary<string, string> MayNameTheHouseholdTable = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Storage/HouseholdLibraryAccessRepository.cs"] = "the repository that owns the table",
        ["src/MediaEngine.Storage/Backup/BackupTableClassification.cs"] = "every table is classified for backup by name",
        ["src/MediaEngine.Api/DevSupport/DevHarnessResetService.cs"] = "the development reset lists every table it empties",
        ["src/MediaEngine.Storage/Schema/schema.snapshot.sql"] = "the generated data structure",
        ["src/MediaEngine.Storage/Schema/Upgrades/0067_household_library_hidden.sql"] = "the step that creates the table",
        ["src/MediaEngine.Storage/Schema/Upgrades/0069_remove_per_profile_library_and_area_access.sql"] = "the step that drops the per-profile tables, which says the household table is what stays",
    };

    // The earlier per-library grants, the per-account hidden table and the library-side "Selected profiles" choice are gone; only the steps
    // that created and dropped them may spell them.
    private static readonly HashSet<string> MayNameTheOldTables = new(StringComparer.Ordinal)
    {
        "src/MediaEngine.Storage/Schema/Upgrades/0001_baseline.sql",
        "src/MediaEngine.Storage/Schema/Upgrades/0045_simpler_access_hidden_libraries.sql",
        "src/MediaEngine.Storage/Schema/Upgrades/0048_drop_account_held_access.sql",
        "src/MediaEngine.Storage/Schema/Upgrades/0057_library_profile_grants.sql",
        "src/MediaEngine.Storage/Schema/Upgrades/0067_household_library_hidden.sql",
    };

    private static readonly string[] OldNames =
    [
        "account_library_grants", "profile_library_grants", "account_library_hidden", "library_profile_grants",
        "GetLibraryGrantsAsync", "HasLibraryGrantAsync", "GetGrantedLibrariesAsync", "GetGrantedProfilesAsync",
        "GetSelectedLibraryIdsAsync", "ILibraryProfileGrantRepository", "ILibraryAccessCatalog", "LibraryAccessLevels",
    ];

    // The files that may ask for a household's hidden libraries, each a reviewed enforcement or administration site that uses the rule's
    // answer (or its pure MaySee) rather than keeping its own copy of it.
    private static readonly Dictionary<string, string> MayReadTheHiddenSet = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Domain/Contracts/IHouseholdLibraryAccessRepository.cs"] = "the household repository contract",
        ["src/MediaEngine.Storage/HouseholdLibraryAccessRepository.cs"] = "the repository that owns the household table",
        [RuleType] = "the one visibility rule",
        ["src/MediaEngine.Api/Security/HouseholdLibraryAccessService.cs"] = "the server administrator's read and save of a household's hidden libraries",
        ["src/MediaEngine.Api/Endpoints/AccountEndpoints.cs"] = "the household list and the library picker",
    };

    private static readonly string[] ScannedExtensions = [".cs", ".razor", ".sql", ".json", ".cshtml", ".js"];

    [Fact]
    public void NoSourceFile_ReadsTheHiddenLibraryTables_OutsideTheRepositoriesAndTheRule()
    {
        var offences = FindOffences(ReadSourceFiles());

        Assert.True(offences.Count == 0, "A library is on or off per household and nowhere else (libraries redesign D16). Fix these, do not widen the lists: "
            + string.Join("; ", offences));
    }

    [Fact]
    public void NoPerProfileLibraryRule_ExistsInTheSourceTheSchemaOrTheRepositoryMembers()
    {
        // A seeded or stored per-profile library row cannot exist when the table is not in the data structure.
        var snapshot = ReadSourceFiles().Single(file => file.Path == "src/MediaEngine.Storage/Schema/schema.snapshot.sql").Text;
        foreach (var table in PerProfileTables)
        {
            Assert.DoesNotContain(table, snapshot, StringComparison.Ordinal);
        }

        // No repository member reads a per-profile library or area rule: the profile repository holds roles, derived areas and the PIN only.
        foreach (var type in new[] { typeof(IProfileAccessRepository), typeof(ProfileAccessRepository) })
        {
            var members = type.GetMethods().Select(method => method.Name)
                .Where(name => name.Contains("Hidden", StringComparison.Ordinal) || name.Contains("Librar", StringComparison.Ordinal)
                    || name.Contains("FeatureGrant", StringComparison.Ordinal) || name.StartsWith("ReplaceAccess", StringComparison.Ordinal))
                .ToArray();
            Assert.True(members.Length == 0, $"{type.Name} must not read or write a per-profile library or area rule: {string.Join(", ", members)}");
        }
    }

    [Fact]
    public void ARealDataStore_HasNoPerProfileLibraryTable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-visibility-guardrail-{Guid.NewGuid():N}.db");
        try
        {
            var database = new DatabaseConnection(path);
            database.InitializeSchema();
            using var connection = database.CreateConnection();
            var names = connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table';").ToHashSet(StringComparer.Ordinal);

            Assert.DoesNotContain("profile_library_hidden", names);
            Assert.DoesNotContain("profile_feature_grants", names);
            Assert.Contains("household_library_hidden", names);
        }
        finally
        {
            TestTemp.DeleteDatabase(path);
        }
    }

    [Fact]
    public void TheScanner_FlagsASeededPerProfileLibraryRule()
    {
        var offences = FindOffences(
        [
            ("src/MediaEngine.Api/Services/Foo.cs", "conn.Query(\"SELECT library_id FROM profile_library_hidden\");"),
            ("src/MediaEngine.Api/Services/Bar.cs", "conn.Query(\"SELECT feature_id FROM profile_feature_grants\");"),
            ("src/MediaEngine.Api/Services/Baz.cs", "var gone = await profileAccess.IsLibraryHiddenAsync(id, library, ct);"),
            ("src/MediaEngine.Contracts/Authentication/Qux.cs", "[property: JsonPropertyName(\"feature_ids\")] IReadOnlyList<string> FeatureIds"),
            ("src/MediaEngine.Storage/Schema/schema.snapshot.sql", "CREATE TABLE profile_library_hidden ("),
        ]);

        Assert.Equal(6, offences.Count);
        Assert.Contains(offences, offence => offence.Contains("Foo.cs", StringComparison.Ordinal) && offence.Contains("profile_library_hidden", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("Bar.cs", StringComparison.Ordinal) && offence.Contains("profile_feature_grants", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("Baz.cs", StringComparison.Ordinal) && offence.Contains("IsLibraryHiddenAsync", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("Qux.cs", StringComparison.Ordinal) && offence.Contains("feature_ids", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScanner_FlagsASeededReadOfTheOldGrantTables()
    {
        var offences = FindOffences(
        [
            ("src/MediaEngine.Api/Services/Foo.cs", "var rows = conn.Query(\"SELECT library_id FROM profile_library_grants\");"),
            ("src/MediaEngine.Api/Services/Bar.cs", "var set = await accounts.GetLibraryGrantsAsync(id, ct);"),
            ("src/MediaEngine.Api/Services/Baz.cs", "if (await accounts.HasLibraryGrantAsync(id, library, ct)) { }"),
            ("src/MediaEngine.Api/Services/Qux.cs", "var rows = conn.Query(\"SELECT library_id FROM account_library_hidden\");"),
        ]);

        Assert.Equal(4, offences.Count);
        Assert.All(offences, offence => Assert.Contains("earlier per-library grants", offence));
    }

    [Fact]
    public void TheScanner_FlagsASecondRuleThatReadsTheHouseholdListOutsideTheRule()
    {
        var offences = FindOffences(
        [
            ("src/MediaEngine.Api/Services/Foo.cs", "conn.Query(\"SELECT library_id FROM household_library_hidden\");"),
            ("src/MediaEngine.Api/Services/Bar.cs", "var gone = await householdLibraries.GetHiddenLibrariesForProfileAsync(id, ct);"),
            ("src/MediaEngine.Api/Services/Baz.cs", "var hidden = await householdLibraries.GetHiddenLibrariesAsync(householdId, ct);"),
        ]);

        Assert.Equal(3, offences.Count);
        Assert.Contains(offences, offence => offence.Contains("Services/Foo.cs", StringComparison.Ordinal) && offence.Contains("household_library_hidden", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("Services/Bar.cs", StringComparison.Ordinal) && offence.Contains("GetHiddenLibrariesForProfileAsync", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("Services/Baz.cs", StringComparison.Ordinal) && offence.Contains("GetHiddenLibrariesAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScanner_FlagsABroughtBackSelectedProfilesLibraryChoice()
    {
        var offences = FindOffences(
        [
            ("src/MediaEngine.Api/Services/Foo.cs", "var granted = await grants.GetGrantedLibrariesAsync(id, ct);"),
            ("src/MediaEngine.Api/Services/Bar.cs", "var selected = await catalog.GetSelectedLibraryIdsAsync(ct);"),
            ("src/MediaEngine.Api/Services/Baz.cs", "if (library.Access == LibraryAccessLevels.Selected) { }"),
            ("src/MediaEngine.Api/Services/Qux.cs", "conn.Query(\"SELECT library_id FROM library_profile_grants\");"),
        ]);

        Assert.Equal(4, offences.Count);
        Assert.All(offences, offence => Assert.Contains("earlier per-library grants", offence));
    }

    [Fact]
    public void TheScanner_AcceptsTheHouseholdRepositoryTheRuleAndTheReviewedReaders()
    {
        var offences = FindOffences(
        [
            ("src/MediaEngine.Storage/HouseholdLibraryAccessRepository.cs", "SELECT library_id FROM household_library_hidden; GetHiddenLibrariesAsync GetHiddenLibrariesForProfileAsync"),
            (RuleType, "householdLibraries.GetHiddenLibrariesForProfileAsync(profileId, ct);"),
            ("src/MediaEngine.Storage/Schema/Upgrades/0069_remove_per_profile_library_and_area_access.sql", "DROP TABLE profile_library_hidden; DROP TABLE profile_feature_grants;"),
        ]);

        Assert.Empty(offences);
    }

    [Fact]
    public void TheRealSourceTree_IsScanned()
    {
        var files = ReadSourceFiles();

        Assert.Contains(files, file => file.Path == RuleType);
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Storage/ProfileAccessRepository.cs");
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Storage/Schema/schema.snapshot.sql");
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Contracts/Authentication/AccessContracts.cs");
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Storage/HouseholdLibraryAccessRepository.cs");
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Storage/Schema/Upgrades/0001_baseline.sql");
    }

    private static List<string> FindOffences(IEnumerable<(string Path, string Text)> files)
    {
        var offences = new List<string>();
        foreach (var (path, text) in files)
        {
            if (!MayNameTheOldTables.Contains(path))
            {
                foreach (var name in OldNames)
                {
                    if (text.Contains(name, StringComparison.Ordinal))
                    {
                        offences.Add($"{path} mentions the earlier per-library grants ({name}); the library-side Selected-profiles choice is gone, who sees a library is set on the household and on the person");
                    }
                }
            }

            if (!MayNameThePerProfileTables.ContainsKey(path))
            {
                foreach (var table in PerProfileTables)
                {
                    if (text.Contains(table, StringComparison.Ordinal))
                    {
                        offences.Add($"{path} mentions {table}; a library is on or off per household only, there is no per-profile library or area table");
                    }
                }

                if (path.StartsWith("src/MediaEngine.Contracts/", StringComparison.Ordinal) || path.StartsWith("src/MediaEngine.Web/", StringComparison.Ordinal))
                {
                    foreach (var name in PerProfileWireNames)
                    {
                        if (text.Contains(name, StringComparison.Ordinal))
                        {
                            offences.Add($"{path} carries {name}; a profile has no library list or area switches of its own");
                        }
                    }
                }
            }

            if (!MayNameTheHouseholdTable.ContainsKey(path) && text.Contains("household_library_hidden", StringComparison.Ordinal))
            {
                offences.Add($"{path} mentions household_library_hidden; only the household repository may");
            }

            foreach (var call in new[] { "GetHiddenLibrariesAsync", "GetHiddenLibrariesForProfileAsync", "IsLibraryHiddenAsync" })
            {
                if (!MayReadTheHiddenSet.ContainsKey(path) && text.Contains(call, StringComparison.Ordinal))
                {
                    offences.Add($"{path} asks {call}; use LibraryVisibilityRule, or add the file to the reviewed list with a reason");
                }
            }
        }

        return offences;
    }

    private static List<(string Path, string Text)> ReadSourceFiles()
    {
        var root = FindRepoRoot();
        var files = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/node_modules/", StringComparison.Ordinal)
                || !ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add((relative, File.ReadAllText(file)));
        }

        return files;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
