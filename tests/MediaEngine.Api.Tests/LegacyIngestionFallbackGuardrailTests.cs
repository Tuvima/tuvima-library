namespace MediaEngine.Api.Tests;

public sealed class LegacyIngestionFallbackGuardrailTests
{
    [Fact]
    public void ProductionSource_DoesNotReintroduceLegacyImageStorageFallbacks()
    {
        var repoRoot = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => IsActiveSourcePath(repoRoot, path))
            .Select(path => new
            {
                Path = ToRelativePath(repoRoot, path),
                Text = File.ReadAllText(path),
            })
            .Where(file => ForbiddenTokens.Any(token => file.Text.Contains(token, StringComparison.Ordinal))
                || UsesObsoleteWatchFolderAsFallback(file.Text))
            .Select(file => file.Path)
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

    private static readonly string[] ForbiddenTokens =
    [
        "ImagePathService",
        ".data/images",
        "sweep-orphan-images",
        "_provisional",
        "PromoteToQid",
        "SweepPendingToQid",
        "GetWorkImageDir",
        "GetPersonImageDir",
        "\".people\"",
        ".people/",
        "person.xml",
    ];

    internal static bool UsesObsoleteWatchFolderAsFallback(string text)
    {
        const string explicitRejection = """
        foreach (var name in new[] { "TUVIMA_DB_PATH", "TUVIMA_LIBRARY_ROOT", "TUVIMA_WATCH_FOLDER" })
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            {
                throw new InvalidOperationException($"Remove {name}: real-media mode uses the validated configuration only.");
            }
        }
        """;
        // Remove only the exact rejection statement, then reject every other use.
        var normalized = string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()));
        var rejection = string.Join('\n', explicitRejection.Split('\n').Select(line => line.Trim()));
        return normalized.Replace(rejection, string.Empty, StringComparison.Ordinal).Contains("TUVIMA_WATCH_FOLDER", StringComparison.Ordinal);
    }

    [Fact]
    public void ObsoleteWatchFolderReadStillFailsEvenBesideAnExplicitRejection()
    {
        var root = FindRepoRoot();
        var protectedSource = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Api/DevSupport/RealMediaHarness.cs"));
        Assert.False(UsesObsoleteWatchFolderAsFallback(protectedSource));
        Assert.True(UsesObsoleteWatchFolderAsFallback(protectedSource + "\nvar folder = Environment.GetEnvironmentVariable(\"TUVIMA_WATCH_FOLDER\");"));
    }
}
