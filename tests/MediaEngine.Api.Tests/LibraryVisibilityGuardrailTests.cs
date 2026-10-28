namespace MediaEngine.Api.Tests;

/// <summary>
/// One rule answers "may this profile see this library" (simpler access D11, access held by the profile): a profile sees every library
/// in the areas it has unless an administrator hid it from that profile, read through <c>LibraryVisibilityRule</c> / the hidden-library
/// reads. These tests fail the build when a source file reads the hidden-library table directly, when anything brings back the earlier
/// per-library grants or the per-account hidden table, or when a new file starts asking for the hidden set without being reviewed here.
/// </summary>
public sealed class LibraryVisibilityGuardrailTests
{
    private const string RuleType = "src/MediaEngine.Api/Security/LibraryVisibilityRule.cs";

    // The only files that may name the table itself: the repositories that own it, the rule type, and the places that must list every
    // table by name (the backup and user export classifications, the development reset list, the generated snapshot and its step).
    private static readonly Dictionary<string, string> MayNameTheTable = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Storage/ProfileAccessRepository.cs"] = "the repository that owns the table",
        ["src/MediaEngine.Storage/AccountRepository.cs"] = "copies a person's hidden libraries to a new person in the same household",
        [RuleType] = "the one visibility rule",
        ["src/MediaEngine.Storage/Backup/BackupTableClassification.cs"] = "every table is classified for backup by name",
        ["src/MediaEngine.Storage/Backup/UserExportScope.cs"] = "every access table is named as left out of a user export",
        ["src/MediaEngine.Api/DevSupport/DevHarnessResetService.cs"] = "the development reset lists every table it empties",
        ["src/MediaEngine.Storage/Schema/schema.snapshot.sql"] = "the generated data structure",
        ["src/MediaEngine.Storage/Schema/Upgrades/0046_profile_held_access.sql"] = "the step that creates the table",
    };

    // The earlier per-library grants and the per-account hidden table are gone; only the steps that created and dropped them may spell them.
    private static readonly HashSet<string> MayNameTheOldTables = new(StringComparer.Ordinal)
    {
        "src/MediaEngine.Storage/Schema/Upgrades/0001_baseline.sql",
        "src/MediaEngine.Storage/Schema/Upgrades/0045_simpler_access_hidden_libraries.sql",
        "src/MediaEngine.Storage/Schema/Upgrades/0048_drop_account_held_access.sql",
    };

    // The files that may ask for a profile's hidden libraries, each a reviewed enforcement or administration site that uses the
    // rule's answer (or its pure MaySee) rather than keeping its own copy of it.
    private static readonly Dictionary<string, string> MayReadTheHiddenSet = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Domain/Contracts/IProfileAccessRepository.cs"] = "the repository contract",
        ["src/MediaEngine.Storage/ProfileAccessRepository.cs"] = "the repository that owns the table",
        [RuleType] = "the one visibility rule",
        ["src/MediaEngine.Api/Security/AccountAccessMutationService.cs"] = "a household administrator's save keeps what is hidden from their own profile hidden",
        ["src/MediaEngine.Api/Endpoints/AccountEndpoints.cs"] = "the administrator's read of a person's access and the library picker",
    };

    // The files that may read which profiles a Selected-profiles library is given to, or which libraries are Selected. Viewer-facing reads
    // go through LibraryVisibilityRule (its hidden set already includes every Selected library the profile was not chosen for), so only
    // the rule, the catalog behind it, the repository, and administration screens appear here (Library pages D9).
    private static readonly Dictionary<string, string> MayReadTheSelectedProfileChoices = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Domain/Contracts/ILibraryProfileGrantRepository.cs"] = "the repository contract",
        ["src/MediaEngine.Domain/Contracts/ILibraryAccessCatalog.cs"] = "the catalog contract",
        ["src/MediaEngine.Storage/LibraryProfileGrantRepository.cs"] = "the repository that owns the table",
        ["src/MediaEngine.Api/Security/ConfigurationLibraryAccessCatalog.cs"] = "reads which libraries are Selected from the libraries file",
        [RuleType] = "the one visibility rule",
        ["src/MediaEngine.Api/Endpoints/LibraryAccessEndpoints.cs"] = "the administrator's read and save of the chosen profiles",
        ["src/MediaEngine.Api/Services/Libraries/LibraryAccessChangeService.cs"] = "clears the chosen profiles when a library's access changes",
        ["src/MediaEngine.Api/Endpoints/AccountEndpoints.cs"] = "the library picker leaves out Selected libraries a household administrator was not chosen for",
        ["src/MediaEngine.Storage/Backup/BackupTableClassification.cs"] = "every table is classified for backup by name",
        ["src/MediaEngine.Storage/Backup/UserExportScope.cs"] = "every access table is named as left out of a user export",
        ["src/MediaEngine.Api/DevSupport/DevHarnessResetService.cs"] = "the development reset lists every table it empties",
        ["src/MediaEngine.Storage/Schema/schema.snapshot.sql"] = "the generated data structure",
        ["src/MediaEngine.Storage/Schema/Upgrades/0056_library_profile_grants.sql"] = "the step that creates the table",
        ["src/MediaEngine.Api/DependencyInjection/TuvimaStorageServiceCollectionExtensions.cs"] = "registers the repository",
        ["src/MediaEngine.Api/Program.cs"] = "registers the catalog",
    };

    private static readonly string[] ScannedExtensions = [".cs", ".razor", ".sql", ".json", ".cshtml", ".js"];

    [Fact]
    public void NoSourceFile_ReadsTheHiddenLibraryTable_OutsideTheRepositoryAndTheRule()
    {
        var offences = FindOffences(ReadSourceFiles());

        Assert.True(offences.Count == 0, "Library visibility has one rule (simpler access D11). Fix these, do not widen the lists: "
            + string.Join("; ", offences));
    }

    [Fact]
    public void TheScanner_FlagsASeededReadOfTheOldGrantTable()
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
    public void TheScanner_AcceptsTheRepositoryTheRuleAndTheReviewedReaders()
    {
        var offences = FindOffences(
        [
            ("src/MediaEngine.Storage/ProfileAccessRepository.cs", "SELECT library_id FROM profile_library_hidden; GetHiddenLibrariesAsync IsLibraryHiddenAsync"),
            (RuleType, "access.GetHiddenLibrariesAsync(profileId, ct); access.IsLibraryHiddenAsync(profileId, libraryId, ct);"),
            ("src/MediaEngine.Api/Security/AccountAccessMutationService.cs", "profileAccess.GetHiddenLibrariesAsync(profile, ct)"),
        ]);

        Assert.Empty(offences);
    }

    [Fact]
    public void TheScanner_FlagsASeededReadOfTheSelectedProfileChoices_AndAcceptsTheRule()
    {
        var offences = FindOffences(
        [
            ("src/MediaEngine.Api/Services/Foo.cs", "var rows = conn.Query(\"SELECT library_id FROM library_profile_grants\");"),
            ("src/MediaEngine.Api/Services/Bar.cs", "var granted = await grants.GetGrantedLibrariesAsync(id, ct);"),
            (RuleType, "grants.GetGrantedLibrariesAsync(profileId, ct); catalog.GetSelectedLibraryIdsAsync(ct);"),
        ]);

        Assert.Equal(2, offences.Count);
        Assert.Contains(offences, offence => offence.Contains("Services/Foo.cs", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("Services/Bar.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRealSourceTree_IsScanned()
    {
        var files = ReadSourceFiles();

        Assert.Contains(files, file => file.Path == RuleType);
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Storage/ProfileAccessRepository.cs");
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Storage/Schema/Upgrades/0001_baseline.sql");
    }

    private static List<string> FindOffences(IEnumerable<(string Path, string Text)> files)
    {
        var offences = new List<string>();
        foreach (var (path, text) in files)
        {
            if (!MayNameTheOldTables.Contains(path) &&
                (text.Contains("account_library_grants", StringComparison.Ordinal)
                 || text.Contains("profile_library_grants", StringComparison.Ordinal)
                 || text.Contains("account_library_hidden", StringComparison.Ordinal)
                 || text.Contains("GetLibraryGrantsAsync", StringComparison.Ordinal)
                 || text.Contains("HasLibraryGrantAsync", StringComparison.Ordinal)))
            {
                offences.Add($"{path} mentions the earlier per-library grants (account_library_grants / profile_library_grants / account_library_hidden / GetLibraryGrantsAsync / HasLibraryGrantAsync)");
            }

            if (!MayNameTheTable.ContainsKey(path) && text.Contains("profile_library_hidden", StringComparison.Ordinal))
            {
                offences.Add($"{path} mentions profile_library_hidden; only the repository and the rule type may");
            }

            foreach (var name in new[] { "library_profile_grants", "GetGrantedLibrariesAsync", "GetGrantedProfilesAsync", "GetSelectedLibraryIdsAsync", "ILibraryProfileGrantRepository", "ILibraryAccessCatalog" })
            {
                if (!MayReadTheSelectedProfileChoices.ContainsKey(path) && text.Contains(name, StringComparison.Ordinal))
                {
                    offences.Add($"{path} mentions {name}; Selected-profiles visibility has one rule, use LibraryVisibilityRule, or add the file to the reviewed list with a reason");
                }
            }

            foreach (var call in new[] { "GetHiddenLibrariesAsync", "IsLibraryHiddenAsync" })
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
