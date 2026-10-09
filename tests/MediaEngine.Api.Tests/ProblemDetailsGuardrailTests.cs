using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

public sealed class ProblemDetailsGuardrailTests
{
    // Ad-hoc handled-error shapes banned from src/MediaEngine.Api/Endpoints/*.cs. Every one of
    // these predates MediaEngine.Api.Http.ApiErrors and produces a non-RFC7807 response body:
    // Results.BadRequest(...)/NotFound(...)/Conflict(...)/UnprocessableEntity(...) (with or
    // without an ad-hoc `new { error }` body, including bare no-arg calls), and
    // Results.Json(new { error ... }). TypedResults has the same surface and is banned
    // identically. Results.Problem(...), Results.ValidationProblem(...), and ApiErrors.* are the
    // allowed replacements and are intentionally not matched below.
    private static readonly Regex BannedErrorShapeRegex = new(
        @"\b(?:Results|TypedResults)\.(?:BadRequest\s*\(|NotFound\s*\(|Conflict\s*\(|UnprocessableEntity\s*\(|Json\s*\(\s*new\s*\{\s*error\b)",
        RegexOptions.Compiled);

    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Program_RegistersStructuredSafeExceptionHandling()
    {
        var source = Read(@"src/MediaEngine.Api/Program.cs");

        Assert.Contains("builder.Services.AddProblemDetails", source, StringComparison.Ordinal);
        Assert.Contains("app.UseExceptionHandler", source, StringComparison.Ordinal);
        Assert.Contains("application/problem+json", source, StringComparison.Ordinal);
        Assert.Contains("traceId", source, StringComparison.Ordinal);
        Assert.Contains("The request failed. Check Engine logs with the trace id for details.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every handled endpoint error must go through <c>MediaEngine.Api.Http.ApiErrors</c> so the
    /// wire shape is uniformly <c>application/problem+json</c>. This scans every
    /// <c>src/MediaEngine.Api/Endpoints/*.cs</c> file (DevSupport/ is a separate, non-Endpoints
    /// directory and is out of scope for this scan) for the ad-hoc shapes that predate ApiErrors.
    /// Files not yet converted are tracked in <c>ProblemDetailsGuardrailAllowlist.txt</c>, seeded
    /// from the pre-standardization state (stage 5A wave 1); that list must shrink to empty as
    /// stage 5A wave 2 converts each file, and must never grow.
    /// </summary>
    [Fact]
    public void EndpointFiles_DoNotUseAdHocErrorShapes_UnlessAllowlisted()
    {
        var allowlist = ReadAllowlist();
        var endpointDir = Path.Combine(RepoRoot, "src", "MediaEngine.Api", "Endpoints");

        var offenders = new List<string>();
        var unmatchedAllowlistEntries = new HashSet<string>(allowlist, StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(endpointDir, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = ToRelativePath(path);

            // DevSupport/ holds dev-only harnesses, not production endpoint handlers, and is
            // excluded from this scan even if a future reorganization nests it under Endpoints.
            if (relativePath.Contains("/DevSupport/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            if (!BannedErrorShapeRegex.IsMatch(text))
            {
                continue;
            }

            if (allowlist.Contains(relativePath))
            {
                unmatchedAllowlistEntries.Remove(relativePath);
                continue;
            }

            offenders.Add(relativePath);
        }

        Assert.True(offenders.Count == 0,
            "Ad-hoc handled-error shapes found outside the allowlist (convert to MediaEngine.Api.Http.ApiErrors): "
            + string.Join(", ", offenders));

        // An allowlist entry whose file no longer contains a banned shape means the file was
        // converted — the entry must be removed, not left behind as dead cover for new offenders.
        Assert.True(unmatchedAllowlistEntries.Count == 0,
            "Stale ProblemDetailsGuardrailAllowlist.txt entries — these files no longer contain a "
            + "banned error shape and must be removed from the allowlist: "
            + string.Join(", ", unmatchedAllowlistEntries));
    }

    private static HashSet<string> ReadAllowlist()
    {
        var path = Path.Combine(RepoRoot, "tests", "MediaEngine.Api.Tests", "ProblemDetailsGuardrailAllowlist.txt");
        Assert.True(File.Exists(path), "Missing ProblemDetails guardrail allowlist.");

        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .Select(line => line.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    private static string ToRelativePath(string path) =>
        Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
