using Dapper;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Storage;

/// <summary>The SQL parameters that go with <c>content_kind NOT IN @blockedKinds</c> (View kinds a profile may not use).</summary>
internal static class ViewKindSql
{
    public static DynamicParameters AddBlockedKinds(this DynamicParameters parameters, IReadOnlyCollection<string>? blocked)
    {
        var any = blocked is { Count: > 0 };
        parameters.Add("hasBlockedKinds", any ? 1 : 0);
        // An empty list would make the SQL invalid, so a placeholder stands in while hasBlockedKinds is 0.
        parameters.Add("blockedKinds", any ? blocked!.ToArray() : new[] { ViewContentKinds.Photos });
        return parameters;
    }
}
