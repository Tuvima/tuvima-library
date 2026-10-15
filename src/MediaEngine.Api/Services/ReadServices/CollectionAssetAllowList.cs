namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Limits which files may stand in for a collection tile. A show, comic series, album or book series takes its
/// picture, title and creator from one file below it; for a profile with a content limit that file must belong to a
/// work the profile may see, never to a restricted child.
/// </summary>
internal static class CollectionAssetAllowList
{
    /// <summary>The allowed work ids as a JSON array of upper-case hex strings, matched against <c>hex(works.id)</c>.</summary>
    public static string? ToJson(IReadOnlySet<Guid>? allowedWorkIds) =>
        allowedWorkIds is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(
                allowedWorkIds.Select(workId => workId.ToString("N").ToUpperInvariant()));

    /// <summary>The extra condition for the representative-file query, or nothing when every work is allowed.</summary>
    public static string Clause(string workIdSql, IReadOnlySet<Guid>? allowedWorkIds) =>
        allowedWorkIds is null
            ? string.Empty
            : $"AND hex({workIdSql}) IN (SELECT value FROM json_each(@AllowedHex))";
}
