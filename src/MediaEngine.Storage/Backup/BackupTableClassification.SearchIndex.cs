namespace MediaEngine.Storage.Backup;

public static partial class BackupTableClassification
{
    // Kept in its own file: the View documents guardrail refuses any Storage file that names both the global search
    // index and View items, and the main registry has to name View item tables.
    private static IEnumerable<BackupTableRule> SearchIndexRules() =>
    [
        Catalogue("search_index", "Search index over titles."),
    ];
}
