namespace MediaEngine.Storage;

/// <summary>
/// SQL pieces that keep Vault items out of a raw query (Videos spec D17). An item is Vault because its file sits in a library listed
/// in <c>vault_libraries</c>. A query that does not know who is asking uses these, so the Vault is absent from it for everyone;
/// the viewer-aware surfaces go through the authorized projection instead.
/// </summary>
/// <remarks>
/// A series or section is a parent work with no file of its own. It counts as Vault when any work beneath it (through
/// <c>works.parent_work_id</c>, at any depth) has a file in a Vault library, so its title and records are hidden with its episodes
/// (security review L2).
/// </remarks>
public static class VaultSql
{
    // The work itself and every work beneath it. UNION (not UNION ALL) so a malformed parent loop ends.
    private static string WorkTree(string idExpression) => $"""
        WITH RECURSIVE vault_tree(id) AS (
            SELECT {idExpression}
            UNION
            SELECT vault_w.id FROM works vault_w JOIN vault_tree vault_t ON vault_w.parent_work_id = vault_t.id)
        """;

    /// <summary>
    /// True for anything that is not a media asset in a Vault library, not a work with a file in one, and not a parent work
    /// (series, section) that has such a work beneath it.
    /// <paramref name="idExpression"/> is a column or expression holding an asset id or a work id.
    /// </summary>
    public static string NotVaultEntity(string idExpression) => $"""
        NOT EXISTS (
            {WorkTree(idExpression)}
            SELECT 1 FROM media_assets vault_ma
            JOIN vault_libraries vault_vl ON vault_vl.library_id = vault_ma.library_id COLLATE NOCASE
            WHERE vault_ma.id = {idExpression}
               OR vault_ma.edition_id IN (SELECT vault_e.id FROM editions vault_e WHERE vault_e.work_id IN (SELECT id FROM vault_tree)))
        """;

    /// <summary>True for anything that is not a media asset in a Vault library.</summary>
    public static string NotVaultAsset(string assetIdExpression) => $"""
        NOT EXISTS (
            SELECT 1 FROM media_assets vault_ma
            JOIN vault_libraries vault_vl ON vault_vl.library_id = vault_ma.library_id COLLATE NOCASE
            WHERE vault_ma.id = {assetIdExpression})
        """;

    /// <summary>
    /// True for anything that is not a Vault media asset, a Vault edition, a work with a file in a Vault library or a parent work
    /// with such a work beneath it.
    /// <paramref name="idExpression"/> may hold any of the three kinds of id, so it fits the owner column of
    /// <c>canonical_values</c>, <c>canonical_value_arrays</c> and <c>entity_assets</c>, which are keyed by any of them.
    /// </summary>
    public static string NotVaultAnyEntity(string idExpression) => $"""
        NOT EXISTS (
            {WorkTree(idExpression)}
            SELECT 1 FROM media_assets vault_ma
            JOIN vault_libraries vault_vl ON vault_vl.library_id = vault_ma.library_id COLLATE NOCASE
            WHERE vault_ma.id = {idExpression}
               OR vault_ma.edition_id = {idExpression}
               OR vault_ma.edition_id IN (SELECT vault_e.id FROM editions vault_e WHERE vault_e.work_id IN (SELECT id FROM vault_tree)))
        """;

    /// <summary>
    /// True for an id that is Vault by any of those rules, as a complete statement with the id as parameter <c>@id</c>.
    /// The same rule as <see cref="NotVaultAnyEntity"/>, used by <c>IsInVaultAsync</c>.
    /// </summary>
    public static string IsVaultAnyEntityStatement => $"""
        SELECT NOT {NotVaultAnyEntity("@id")};
        """;
}
