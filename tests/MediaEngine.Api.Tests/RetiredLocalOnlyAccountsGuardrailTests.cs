namespace MediaEngine.Api.Tests;

/// <summary>
/// Every sign-in has an email: a person without their own sign-in is a profile in someone's household. The
/// email-less "local-only" account and its Profile ID sign-in are gone and must not come back.
/// </summary>
public sealed class RetiredLocalOnlyAccountsGuardrailTests
{
    private static readonly string[] RetiredNames =
    [
        "IsLocalOnly",
        "AllowLocalOnlyAccounts",
        "DisabledLocalOnly",
        "ProfileEntry",
        "allow_local_only_accounts",
        "is_local_only",
        "GetLocalOnlyAccountIdForProfile",
        "IsLocalOnlyMode",
        "AuthenticatePinAsync",
    ];

    // The data-store migration must name the old column to rebuild the table, and the session check names the retired
    // "ProfileEntry" method so it can refuse any session that still claims it.
    private static readonly (string Path, string Name)[] Allowed =
    [
        ("src/MediaEngine.Storage/SchemaMigrator.cs", "is_local_only"),
        ("src/MediaEngine.Identity/FirstPartyIdentityService.cs", "ProfileEntry"),
    ];

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".json", ".sql", ".js", ".yml", ".yaml", ".sh", ".ps1",
    };

    [Fact]
    public void ProductSourceAndConfig_DoNotMentionLocalOnlyAccountsOrProfileEntrySignIn()
    {
        var repoRoot = FindRepoRoot();
        var separator = Path.DirectorySeparatorChar;
        string[] skipped = [$"{separator}bin{separator}", $"{separator}obj{separator}", $"{separator}node_modules{separator}"];
        var offenders = new List<string>();
        foreach (var root in new[] { "src", "config", "docker", "deploy" })
        {
            var directory = Path.Combine(repoRoot, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .Where(file => Extensions.Contains(Path.GetExtension(file)))
                         .Where(file => skipped.All(segment => !file.Contains(segment, StringComparison.OrdinalIgnoreCase))))
            {
                var relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
                var source = File.ReadAllText(path);
                foreach (var name in RetiredNames)
                {
                    if (source.Contains(name, StringComparison.Ordinal)
                        && !Allowed.Contains((relative, name)))
                    {
                        offenders.Add($"{relative} mentions {name}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Local-only accounts and Profile ID sign-in were retired. Remove:\n" + string.Join("\n", offenders.OrderBy(value => value, StringComparer.Ordinal)));
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
