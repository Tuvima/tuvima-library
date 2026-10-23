namespace MediaEngine.Storage;

/// <summary>
/// The one place that says, in SQL, when a file is in a folder whose first check is held (Library setup spec D35, D61). The folder's check row
/// carries the prefix precomputed (<see cref="MediaEngine.Domain.Models.FolderFirstCheckPaths"/>), so the test is a range on
/// <c>ix_media_assets_path_folded</c> rather than string work on every row. Outside Windows paths differ by case, so the case-sensitive prefix
/// is checked as well, as the rest of the Engine compares source paths (<c>PathSafety.Comparer</c>); on Windows the range alone is the answer.
/// </summary>
internal static class FolderPathSql
{
    /// <summary>The path in the form the index holds: separators as <c>/</c>, ASCII letters lower-cased.</summary>
    public static string Folded(string pathExpression) => $"lower(replace({pathExpression}, '\\', '/'))";

    /// <summary>
    /// SQL that is true when the path held by <paramref name="pathExpression"/> is inside the folder whose bounds are the SQL operands
    /// <paramref name="low"/> (folded prefix), <paramref name="high"/> (folded end of the range) and <paramref name="prefix"/> (case-kept prefix).
    /// </summary>
    public static string InFolder(string pathExpression, string low, string high, string prefix)
    {
        var folded = Folded(pathExpression);
        var range = $"{folded} >= {low} AND {folded} < {high}";
        return OperatingSystem.IsWindows()
            ? $"({range})"
            : $"({range} AND substr(replace({pathExpression}, '\\', '/'), 1, length({prefix})) = {prefix})";
    }

    /// <summary>
    /// The same test against the <c>folder_first_checks</c> row aliased <paramref name="checkAlias"/>: for a path already found by its key (a
    /// claim looking at one job's file), where the bounds come from the row. A range over many files needs <see cref="InFolderOfBounds"/>.
    /// </summary>
    public static string InFolder(string pathExpression, string checkAlias) =>
        InFolder(pathExpression, $"{checkAlias}.path_prefix_folded", $"{checkAlias}.path_range_end_folded", $"{checkAlias}.path_prefix");

    /// <summary>
    /// The same test against bounds passed as the parameters <c>@folderLow</c>, <c>@folderHigh</c> and <c>@folderPrefix</c> (see
    /// <see cref="FolderFirstCheckRepository.LoadBounds"/>). SQLite answers a range on an expression index only when the bounds are values it
    /// knows when it plans the query, not columns of another table, so a search over many files binds them.
    /// </summary>
    public static string InFolderOfBounds(string pathExpression) => InFolder(pathExpression, "@folderLow", "@folderHigh", "@folderPrefix");
}
