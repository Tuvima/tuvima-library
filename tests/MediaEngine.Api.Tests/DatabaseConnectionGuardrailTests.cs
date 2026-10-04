using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

public sealed class DatabaseConnectionGuardrailTests
{
    [Fact]
    public void EndpointFiles_DoNotUseSharedDatabaseOpen()
    {
        var repoRoot = FindRepoRoot();
        var endpointDir = Path.Combine(repoRoot, "src", "MediaEngine.Api", "Endpoints");
        var offenders = Directory.EnumerateFiles(endpointDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => IsActiveSourcePath(repoRoot, path))
            .Select(path => new
            {
                Path = path,
                Text = File.ReadAllText(path),
            })
            .Where(file => file.Text.Contains("db.Open()", StringComparison.Ordinal)
                           || file.Text.Contains(".Open();", StringComparison.Ordinal)
                              && file.Text.Contains("IDatabaseConnection", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(repoRoot, file.Path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ActiveSourceFiles_DoNotUseSharedDatabaseOpenOutsideStartupSchemaOrFixtures()
    {
        var repoRoot = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => IsActiveSourcePath(repoRoot, path))
            .Where(path => !AllowedOpenUsageFiles.Contains(ToRelativePath(repoRoot, path), StringComparer.OrdinalIgnoreCase))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("IDatabaseConnection", StringComparison.Ordinal)
                    && (text.Contains(".Open()", StringComparison.Ordinal)
                        || text.Contains(".Open();", StringComparison.Ordinal)
                        || text.Contains("db.Open(", StringComparison.Ordinal)
                        || text.Contains("_db.Open(", StringComparison.Ordinal));
            })
            .Select(path => ToRelativePath(repoRoot, path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ActiveSourceFiles_DoNotCallRawBeginTransactionOutsideDatabaseConnection()
    {
        var repoRoot = FindRepoRoot();
        var allowlist = ReadBeginTransactionAllowlist(repoRoot);
        const string ownerFile = "src/MediaEngine.Storage/DatabaseConnection.cs";

        var offenders = Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => IsActiveSourcePath(repoRoot, path))
            .Select(path => ToRelativePath(repoRoot, path))
            .Where(relative => !relative.Equals(ownerFile, StringComparison.OrdinalIgnoreCase))
            .Where(relative => !allowlist.Contains(relative))
            .Where(relative => !OfflineMaintenanceTransactionPolicy.Owns(relative))
            .Where(relative => File.ReadAllText(Path.Combine(repoRoot, relative))
                .Contains(".BeginTransaction()", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void SilentCatchBlocks_AreLimitedToDocumentedBestEffortLocations()
    {
        var repoRoot = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.razor", SearchOption.AllDirectories))
            .Where(path => IsActiveSourcePath(repoRoot, path))
            .Where(path =>
            {
                var relative = ToRelativePath(repoRoot, path);
                return !AllowedSilentCatchFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)
                    && SilentCatchRegex.IsMatch(File.ReadAllText(path));
            })
            .Select(path => ToRelativePath(repoRoot, path))
            .ToList();

        Assert.Empty(offenders);
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

    private static string ToRelativePath(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

    private static bool IsActiveSourcePath(string repoRoot, string path)
    {
        var segments = ToRelativePath(repoRoot, path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return !segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] AllowedOpenUsageFiles =
    [
        // Startup/schema lifecycle owns the shared connection.
        "src/MediaEngine.Api/Program.cs",
        "src/MediaEngine.Storage/DatabaseConnection.cs",
        // Dev harness reset is a non-production database maintenance path.
        "src/MediaEngine.Api/DevSupport/DevHarnessResetService.cs",
    ];

    private static readonly string[] AllowedSilentCatchFiles =
    [
    ];

    private static readonly Regex SilentCatchRegex =
        new(@"catch\s*\{\s*\}", RegexOptions.Compiled);

    /// <summary>
    /// Reads the seeded list of files still calling <c>SqliteConnection.BeginTransaction()</c>
    /// directly. See <c>BeginTransactionGuardrailAllowlist.txt</c> for the migration note —
    /// entries are removed as wave 2 of stage 2 converts each call site to
    /// <c>IDatabaseConnection.ExecuteWriteAsync</c>; new entries are forbidden.
    /// </summary>
    private static HashSet<string> ReadBeginTransactionAllowlist(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "tests", "MediaEngine.Api.Tests", "BeginTransactionGuardrailAllowlist.txt");
        Assert.True(File.Exists(path), "Missing BeginTransaction guardrail allowlist.");

        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

// Offline maintenance owns a caller-supplied, non-pooled connection and its
// foreign-key reset lifecycle. These are exact exceptions, not production sites.
internal static class OfflineMaintenanceTransactionPolicy
{
    internal static readonly string[] Files =
    [
        "src/MediaEngine.Api/DevSupport/RealMediaHarness.cs",
        "src/MediaEngine.Api/DevSupport/RealMediaRepair.cs",
    ];

    internal static bool Owns(string relative) => Files.Contains(relative, StringComparer.Ordinal);
}
