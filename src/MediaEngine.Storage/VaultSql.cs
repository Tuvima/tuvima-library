namespace MediaEngine.Storage;

/// <summary>
/// SQL pieces that keep Vault items out of a raw query (Videos spec D17). An item is Vault because its file sits in a library listed
/// in <c>vault_libraries</c>. A query that does not know who is asking uses these, so the Vault is absent from it for everyone;
/// the viewer-aware surfaces go through the authorized projection instead.
/// </summary>
public static class VaultSql
{
    /// <summary>
    /// True for anything that is not a media asset in a Vault library and not a work with a file in one.
    /// <paramref name="idExpression"/> is a column or expression holding an asset id or a work id.
    /// </summary>
    public static string NotVaultEntity(string idExpression) => $"""
        NOT EXISTS (
            SELECT 1 FROM media_assets vault_ma
            JOIN vault_libraries vault_vl ON vault_vl.library_id = vault_ma.library_id COLLATE NOCASE
            WHERE vault_ma.id = {idExpression}
               OR vault_ma.edition_id IN (SELECT vault_e.id FROM editions vault_e WHERE vault_e.work_id = {idExpression}))
        """;

    /// <summary>True for anything that is not a media asset in a Vault library.</summary>
    public static string NotVaultAsset(string assetIdExpression) => $"""
        NOT EXISTS (
            SELECT 1 FROM media_assets vault_ma
            JOIN vault_libraries vault_vl ON vault_vl.library_id = vault_ma.library_id COLLATE NOCASE
            WHERE vault_ma.id = {assetIdExpression})
        """;

    /// <summary>
    /// True for anything that is not a Vault media asset, a Vault edition or a work with a file in a Vault library.
    /// <paramref name="idExpression"/> may hold any of the three kinds of id, so it fits the owner column of
    /// <c>canonical_values</c>, <c>canonical_value_arrays</c> and <c>entity_assets</c>, which are keyed by any of them.
    /// </summary>
    public static string NotVaultAnyEntity(string idExpression) => $"""
        NOT EXISTS (
            SELECT 1 FROM media_assets vault_ma
            JOIN vault_libraries vault_vl ON vault_vl.library_id = vault_ma.library_id COLLATE NOCASE
            WHERE vault_ma.id = {idExpression}
               OR vault_ma.edition_id = {idExpression}
               OR vault_ma.edition_id IN (SELECT vault_e.id FROM editions vault_e WHERE vault_e.work_id = {idExpression}))
        """;
}
