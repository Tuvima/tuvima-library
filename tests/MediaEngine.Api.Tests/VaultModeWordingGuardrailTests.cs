using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Owner decision, 2026-10-21: the feature people see is called "Vault mode". The old working name ("NSFW mode") must not appear in
/// anything a person can read: Dashboard pages and components, scripts, resources, Engine messages, native clients, or the product
/// and reference docs. Internal identifiers keep their names on purpose (NsfwModeState, NsfwAccessMode, the /api/v1/nsfw routes, the
/// profile_nsfw_settings table, wire fields), so only the upper-case word and the old phrases are searched for, never "Nsfw" inside a
/// code name.
/// </summary>
public sealed class VaultModeWordingGuardrailTests
{
    // Scanned: everything a person or a developer reads as product text.
    private static readonly string[] ScannedRoots = ["src", "clients", "docs", "engineering/architecture", "engineering/product", "engineering/design", "engineering/validation"];

    private static readonly string[] ScannedExtensions =
        [".cs", ".razor", ".js", ".mjs", ".ts", ".css", ".html", ".json", ".resx", ".md", ".kt", ".swift", ".xml", ".txt", ".yml", ".yaml"];

    // Allowlist, each with its reason:
    //  - historical upgrade steps are immutable once released (their fingerprints are checked), and the schema snapshot is generated from them;
    //  - build outputs and installed packages are not ours;
    //  - this file names the old words in order to forbid them.
    private static readonly string[] AllowedPathParts =
    [
        "/Schema/Upgrades/", "schema.snapshot.sql", "released-steps.txt", "/bin/", "/obj/", "/node_modules/", "/.tmp/", "/site/",
        "VaultModeWordingGuardrailTests.cs",
    ];

    // Locked spec bodies are never rewritten (CLAUDE.md rule 10), so engineering/specs is not scanned at all.
    private static readonly Regex OldName = new(
        @"\b(NSFW|SFW)\b|\b[Nn]sfw (?i:mode|access|session|state|check|lock|settings|idle|pin|switch|toggle)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void NothingAPersonCanRead_UsesTheOldNsfwName_InsteadOfVaultMode()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var scanned in ScannedRoots)
        {
            var directory = Path.Combine(root, scanned);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = "/" + Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) ||
                    AllowedPathParts.Any(part => relative.Contains(part, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (OldName.IsMatch(lines[index]))
                    {
                        offenders.Add($"{relative}:{index + 1}: {lines[index].Trim()}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These lines still use the old name instead of \"Vault mode\":\n" + string.Join('\n', offenders.Take(40)));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
