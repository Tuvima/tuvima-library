namespace MediaEngine.Storage.Backup;

/// <summary>
/// The kind of catalogue row a column points at. A restore swaps such a column for recognition data (external ids,
/// running time, edition, file fingerprint) because catalogue row ids do not survive a rescan.
/// </summary>
public enum CatalogueTarget
{
    Work,
    Edition,
    MediaAsset,
    Collection,
    Person,
    LocalItem,

    /// <summary>A catalogue row whose kind depends on another column (for example an entity kind and id pair).</summary>
    Other,
}

/// <summary>A column of a kept table that holds the id of a catalogue row.</summary>
/// <param name="Column">The column name.</param>
/// <param name="Target">The kind of catalogue row it points at.</param>
public sealed record CatalogueReference(string Column, CatalogueTarget Target);

/// <summary>
/// How a backup treats one data store table. Tables that mix kinds are split by column
/// (<see cref="CatalogueReferences"/>, <see cref="ExcludedColumns"/>) and by row (<see cref="KeepWhere"/>).
/// </summary>
/// <param name="Table">The table name.</param>
/// <param name="Kind">What the table holds.</param>
/// <param name="Reason">One plain sentence saying why the table has this kind.</param>
/// <param name="CatalogueReferences">
/// Columns of a Configuration or PersonalState table that point at catalogue rows. Empty for Catalogue and Transient.
/// </param>
/// <param name="ExcludedColumns">Secret or machine-bound columns left out of a backup. Empty for Catalogue and Transient.</param>
/// <param name="OwnerColumn">
/// For PersonalState tables, the column that says whose state the row is (a profile or user id), so one profile's
/// state can be exported on its own. Null when the table has no owner column of its own, and for every other kind.
/// </param>
/// <param name="KeepWhere">
/// Optional SQL predicate (over the table's own columns) for tables whose rows mix kinds; only matching rows are kept.
/// Null means every row is kept. Always null for Catalogue and Transient.
/// </param>
public sealed record BackupTableRule(
    string Table,
    BackupDataKind Kind,
    string Reason,
    IReadOnlyList<CatalogueReference> CatalogueReferences,
    IReadOnlyList<string> ExcludedColumns,
    string? OwnerColumn,
    string? KeepWhere);
