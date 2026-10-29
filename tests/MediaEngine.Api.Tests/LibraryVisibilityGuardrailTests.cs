namespace MediaEngine.Api.Tests;

/// <summary>
/// One rule answers "may this profile see this library" (library access D2; simpler access D11, access held by the profile): a profile
/// sees every library in the areas it has unless it is hidden for the profile's household or hidden from that profile, read through
/// <c>LibraryVisibilityRule</c> / the hidden-library reads. These tests fail the build when a source file reads the hidden-library tables
/// directly, when anything brings back the earlier per-library grants ("Selected profiles") or the per-account hidden table, or when a new
/// file starts asking for a hidden set without being reviewed here.
/// </summary>
public sealed class LibraryVisibilityGuardrailTests
{
    private const string RuleType = "src/MediaEngine.Api/Security/LibraryVisibilityRule.cs";

    // The only files that may name the profile table itself: the repository that owns it, the rule type, and the places that must list
    // every table by name (the backup and user export classifications, the development reset list, the generated snapshot and its step).
    private static readonly Dictionary<string, string> MayNameTheTable = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Storage/ProfileAccessRepository.cs"] = "the repository that owns the table",
        [RuleType] = "the one visibility rule",
        ["src/MediaEngine.Storage/Backup/BackupTableClassification.cs"] = "every table is classified for backup by name",
        ["src/MediaEngine.Api/DevSupport/DevHarnessResetService.cs"] = "the development reset lists every table it empties",
        ["src/MediaEngine.Storage/Schema/schema.snapshot.sql"] = "the generated data structure",
        ["src/MediaEngine.Storage/Schema/Upgrades/0046_profile_held_access.sql"] = "the step that creates the table",
        ["src/MediaEngine.Storage/Schema/Upgrades/0066_household_library_hidden.sql"] = "the step that creates the household table, which says what it adds to",
    };

    // The only files that may name the household table: the repository that owns it and the places that list every table by name.
    private static readonly Dictionary<string, string> MayNameTheHouseholdTable = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Storage/HouseholdLibraryAccessRepository.cs"] = "the repository that owns the table",
        ["src/MediaEngine.Storage/Backup/BackupTableClassification.cs"] = "every table is classified for backup by name",
        ["src/MediaEngine.Api/DevSupport/DevHarnessResetService.cs"] = "the development reset lists every table it empties",
        ["src/MediaEngine.Storage/Schema/schema.snapshot.sql"] = "the generated data structure",
        ["src/MediaEngine.Storage/Schema/Upgrades/0066_household_library_hidden.sql"] = "the step that creates the table",
    };

    // The earlier per-library grants, the per-account hidden table and the library-side "Selected profiles" choice are gone; only the steps
    // that created and dropped them may spell them.
    private static readonly HashSet<string> MayNameTheOldTables = new(StringComparer.Ordinal)
    {
        "src/MediaEngine.Storage/Schema/Upgrades/0001_baseline.sql",
        "src/MediaEngine.Storage/Schema/Upgrades/0045_simpler_access_hidden_libraries.sql",
        "src/MediaEngine.Storage/Schema/Upgrades/0048_drop_account_held_access.sql",
        "src/MediaEngine.Storage/Schema/Upgrades/0057_library_profile_grants.sql",
        "src/MediaEngine.Storage/Schema/Upgrades/0066_household_library_hidden.sql",
    };

    private static readonly string[] OldNames =
    [
        "account_library_grants", "profile_library_grants", "account_library_hidden", "library_profile_grants",
        "GetLibraryGrantsAsync", "HasLibraryGrantAsync", "GetGrantedLibrariesAsync", "GetGrantedProfilesAsync",
        "GetSelectedLibraryIdsAsync", "ILibraryProfileGrantRepository", "ILibraryAccessCatalog", "LibraryAccessLevels",
    ];

    // The files that may ask for a profile's or a household's hidden libraries, each a reviewed enforcement or administration site that
    // uses the rule's answer (or its pure MaySee) rather than keeping its own copy of it.
    private static readonly Dictionary<string, string> MayReadTheHiddenSet = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Domain/Contracts/IProfileAccessRepository.cs"] = "the repository contract",
        ["src/MediaEngine.Domain/Contracts/IHouseholdLibraryAccessRepository.cs"] = "the household repository contract",
        ["src/MediaEngine.Storage/ProfileAccessRepository.cs"] = "the repository that owns the table",
        ["src/MediaEngine.Storage/HouseholdLibraryAccessRepository.cs"] = "the repository that owns the household table",
        [RuleType] = "the one visibility rule",
        ["src/MediaEngine.Api/Security/AccountAccessMutationService.cs"] = "a save keeps what is hidden from a household administrator's own profile hidden, and never shows a person what their household hides",
        ["src/MediaEngine.Api/Security/HouseholdLibraryAccessService.cs"] = "the server administrator's read and save of a household's hidden libraries",
        ["src/MediaEngine.Api/Endpoints/AccountEndpoints.cs"] = "the administrator's read of a person's access, the household list and the library picker",
    };

    private static readonly string[] ScannedExtensions = [".cs", ".razor", ".sql", ".json", ".cshtml", ".js"];

    [Fact]
    public void NoSourceFile_ReadsTheHiddenLibraryTables_OutsideTheRepositoriesAndTheRule()
    {
        var offences = FindOffences(ReadSourceFiles());

        Assert.True(offences.Count == 0, "Library visibility has one rule (library access D2, simpler access D11). Fix these, do not widen the lists: "
            + string.Join("; ", offences));
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
    public void TheScanner_FlagsASeededReadOfTheHiddenTable_AndAnUnreviewedAskForTheHiddenSet()
    {
        var offences = FindOffences(
        [
            ("src/MediaEngine.Api/Services/Foo.cs", "conn.Query(\"SELECT library_id FROM profile_library_hidden\");"),
            ("src/MediaEngine.Api/Services/Bar.cs", "var hidden = await profileAccess.GetHiddenLibrariesAsync(id, ct);"),
            ("src/MediaEngine.Api/Services/Baz.cs", "var gone = await profileAccess.IsLibraryHiddenAsync(id, library, ct);"),
        ]);

        Assert.Equal(3, offences.Count);
        Assert.Contains(offences, offence => offence.Contains("Services/Foo.cs", StringComparison.Ordinal) && offence.Contains("profile_library_hidden", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("Services/Bar.cs", StringComparison.Ordinal) && offence.Contains("GetHiddenLibrariesAsync", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("Services/Baz.cs", StringComparison.Ordinal) && offence.Contains("IsLibraryHiddenAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScanner_FlagsASeededSecondRuleThatReadsTheHouseholdListOutsideTheRule()
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
    public void TheScanner_AcceptsTheRepositoriesTheRuleAndTheReviewedReaders()
    {
        var offences = FindOffences(
        [
            ("src/MediaEngine.Storage/ProfileAccessRepository.cs", "SELECT library_id FROM profile_library_hidden; GetHiddenLibrariesAsync IsLibraryHiddenAsync"),
            ("src/MediaEngine.Storage/HouseholdLibraryAccessRepository.cs", "SELECT library_id FROM household_library_hidden; GetHiddenLibrariesAsync GetHiddenLibrariesForProfileAsync"),
            (RuleType, "access.GetHiddenLibrariesAsync(profileId, ct); access.IsLibraryHiddenAsync(profileId, libraryId, ct); householdLibraries.GetHiddenLibrariesForProfileAsync(profileId, ct);"),
            ("src/MediaEngine.Api/Security/AccountAccessMutationService.cs", "profileAccess.GetHiddenLibrariesAsync(profile, ct)"),
        ]);

        Assert.Empty(offences);
    }

    [Fact]
    public void TheRealSourceTree_IsScanned()
    {
        var files = ReadSourceFiles();

        Assert.Contains(files, file => file.Path == RuleType);
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Storage/ProfileAccessRepository.cs");
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

            if (!MayNameTheTable.ContainsKey(path) && text.Contains("profile_library_hidden", StringComparison.Ordinal))
            {
                offences.Add($"{path} mentions profile_library_hidden; only the repository and the rule type may");
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
