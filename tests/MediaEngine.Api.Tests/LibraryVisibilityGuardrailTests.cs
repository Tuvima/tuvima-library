namespace MediaEngine.Api.Tests;

/// <summary>
/// One rule answers "may this account see this library" (simpler access D11): everyone sees every library in the areas they have
/// unless an administrator hid it from them, read through <c>LibraryVisibilityRule</c> / the hidden-library reads. These tests fail the
/// build when a source file reads the hidden-library table directly, when anything brings back the earlier per-library grants, or
/// when a new file starts asking for the hidden set without being reviewed here.
/// </summary>
public sealed class LibraryVisibilityGuardrailTests
{
    private const string RuleType = "src/MediaEngine.Api/Security/LibraryVisibilityRule.cs";

    // The only files that may name the table itself: the repository that owns it, the rule type, and the places that must list every
    // table by name (the backup and user export classifications, the development reset list, the generated snapshot and its step).
    private static readonly Dictionary<string, string> MayNameTheTable = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Storage/AccountRepository.cs"] = "the repository that owns the table",
        [RuleType] = "the one visibility rule",
        ["src/MediaEngine.Storage/Backup/BackupTableClassification.cs"] = "every table is classified for backup by name",
        ["src/MediaEngine.Storage/Backup/UserExportScope.cs"] = "every access table is named as left out of a user export",
        ["src/MediaEngine.Api/DevSupport/DevHarnessResetService.cs"] = "the development reset lists every table it empties",
        ["src/MediaEngine.Storage/Schema/schema.snapshot.sql"] = "the generated data structure",
        ["src/MediaEngine.Storage/Schema/Upgrades/0045_simpler_access_hidden_libraries.sql"] = "the step that creates the table",
    };

    // The earlier per-library grants are gone; only the released steps that created and dropped the table may spell them.
    private static readonly HashSet<string> MayNameTheOldGrants = new(StringComparer.Ordinal)
    {
        "src/MediaEngine.Storage/Schema/Upgrades/0001_baseline.sql",
        "src/MediaEngine.Storage/Schema/Upgrades/0045_simpler_access_hidden_libraries.sql",
    };

    // The files that may ask for an account's hidden libraries, each a reviewed enforcement or administration site that uses the
    // rule's answer (or its pure MaySee) rather than keeping its own copy of it.
    private static readonly Dictionary<string, string> MayReadTheHiddenSet = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Domain/Contracts/IAccessRepository.cs"] = "the repository contract",
        ["src/MediaEngine.Storage/AccountRepository.cs"] = "the repository that owns the table",
        [RuleType] = "the one visibility rule",
        ["src/MediaEngine.Api/Security/AuthorizedDisplayProjectionReadService.cs"] = "browse, search, Home, counts: loads the set once per request, applies LibraryVisibilityRule.MaySee",
        ["src/MediaEngine.Api/Realtime/IntercomAudience.cs"] = "Dashboard event audiences: applies LibraryVisibilityRule.MaySee",
        ["src/MediaEngine.Api/Services/Playback/PlaybackTelemetryReadService.cs"] = "playback telemetry scope: hands the set to the repository query",
        ["src/MediaEngine.Api/Security/AccountAccessMutationService.cs"] = "a household administrator's save keeps what the main sign-in hides",
        ["src/MediaEngine.Api/Endpoints/AccountEndpoints.cs"] = "the administrator's read of a person's access and the library picker",
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
            ("src/MediaEngine.Api/Services/Foo.cs", "var rows = conn.Query(\"SELECT library_id FROM account_library_grants\");"),
            ("src/MediaEngine.Api/Services/Bar.cs", "var set = await accounts.GetLibraryGrantsAsync(id, ct);"),
            ("src/MediaEngine.Api/Services/Baz.cs", "if (await accounts.HasLibraryGrantAsync(id, library, ct)) { }"),
        ]);

        Assert.Equal(3, offences.Count);
        Assert.All(offences, offence => Assert.Contains("earlier per-library grants", offence));
    }

    [Fact]
    public void TheScanner_FlagsASeededReadOfTheHiddenTable_AndAnUnreviewedAskForTheHiddenSet()
    {
        var offences = FindOffences(
        [
            ("src/MediaEngine.Api/Services/Foo.cs", "conn.Query(\"SELECT library_id FROM account_library_hidden\");"),
            ("src/MediaEngine.Api/Services/Bar.cs", "var hidden = await accounts.GetHiddenLibrariesAsync(id, ct);"),
            ("src/MediaEngine.Api/Services/Baz.cs", "var gone = await accounts.IsLibraryHiddenAsync(id, library, ct);"),
        ]);

        Assert.Equal(3, offences.Count);
        Assert.Contains(offences, offence => offence.Contains("Services/Foo.cs", StringComparison.Ordinal) && offence.Contains("account_library_hidden", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("Services/Bar.cs", StringComparison.Ordinal) && offence.Contains("GetHiddenLibrariesAsync", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("Services/Baz.cs", StringComparison.Ordinal) && offence.Contains("IsLibraryHiddenAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScanner_AcceptsTheRepositoryTheRuleAndTheReviewedReaders()
    {
        var offences = FindOffences(
        [
            ("src/MediaEngine.Storage/AccountRepository.cs", "SELECT library_id FROM account_library_hidden; GetHiddenLibrariesAsync IsLibraryHiddenAsync"),
            (RuleType, "access.GetHiddenLibrariesAsync(accountId, ct); access.IsLibraryHiddenAsync(accountId, libraryId, ct);"),
            ("src/MediaEngine.Api/Realtime/IntercomAudience.cs", "accounts.IsLibraryHiddenAsync(account.Id, library, ct)"),
        ]);

        Assert.Empty(offences);
    }

    [Fact]
    public void TheRealSourceTree_IsScanned()
    {
        var files = ReadSourceFiles();

        Assert.Contains(files, file => file.Path == RuleType);
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Storage/AccountRepository.cs");
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Storage/Schema/Upgrades/0001_baseline.sql");
    }

    private static List<string> FindOffences(IEnumerable<(string Path, string Text)> files)
    {
        var offences = new List<string>();
        foreach (var (path, text) in files)
        {
            if (!MayNameTheOldGrants.Contains(path) &&
                (text.Contains("account_library_grants", StringComparison.Ordinal)
                 || text.Contains("GetLibraryGrantsAsync", StringComparison.Ordinal)
                 || text.Contains("HasLibraryGrantAsync", StringComparison.Ordinal)))
            {
                offences.Add($"{path} mentions the earlier per-library grants (account_library_grants / GetLibraryGrantsAsync / HasLibraryGrantAsync)");
            }

            if (!MayNameTheTable.ContainsKey(path) && text.Contains("account_library_hidden", StringComparison.Ordinal))
            {
                offences.Add($"{path} mentions account_library_hidden; only the repository and the rule type may");
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
