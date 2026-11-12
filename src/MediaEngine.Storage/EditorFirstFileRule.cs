namespace MediaEngine.Storage;

/// <summary>
/// Which file stands for an item in the editor (universal editor v6 D10): the Work's own first file, the one with the lowest asset id. The editor's
/// launch read and the navigation read both choose the file through here, so the Match tab and the family dropdowns read the same file's identifiers.
/// </summary>
public static class EditorFirstFileRule
{
    /// <summary>The <c>ORDER BY</c> term that puts the first file first, for a query that names the asset <c>ma</c>.</summary>
    public const string AssetOrder = "ma.id";

    /// <summary>
    /// A SQL expression giving the id of a Work's first file. <paramref name="workIdExpression"/> is a column or parameter that holds the Work's id
    /// (for example <c>e.work_id</c>); it must come from the caller's own query text, never from user input.
    /// </summary>
    public static string FirstAssetIdOfWork(string workIdExpression) =>
        $"(SELECT MIN(first_asset.id) FROM media_assets first_asset INNER JOIN editions first_edition ON first_edition.id = first_asset.edition_id WHERE first_edition.work_id = {workIdExpression})";
}
