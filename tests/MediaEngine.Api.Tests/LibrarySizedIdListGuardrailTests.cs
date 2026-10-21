using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// SQLite refuses a statement with more than 32,766 bound values, so a list of "every work (or file) this caller may
/// see" must never be bound one value per id. It travels as one JSON list (<c>GuidSql.ToJsonHexArray</c>) and the query
/// reads it with <c>json_each</c>. The allow-lists named here grow with the library, so they are the ones this pins.
/// </summary>
public sealed partial class LibrarySizedIdListGuardrailTests
{
    [GeneratedRegex(@"\bIN\s+@(AllowedWorkIds|AllowedAssetIds|authorizedAssetIds)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PerIdBinding();

    [Fact]
    public void LibrarySizedAllowLists_AreNeverBoundOneValuePerId()
    {
        var root = RepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => PerIdBinding().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These files bind a library-sized id list one value per id, which fails past 32,766 values. "
            + "Send it with GuidSql.ToJsonHexArray and read it with (SELECT unhex(value) FROM json_each(@param)):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("MediaEngine.slnx was not found above the test output folder.");
    }
}
