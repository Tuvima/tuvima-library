using System.Data;
using Dapper;

namespace MediaEngine.Storage;

public static class WorkRedirects
{
    public static Guid Resolve(IDbConnection connection, Guid id)
    {
        var visited = new HashSet<Guid>();
        while (visited.Add(id))
        {
            var value = connection.QueryFirstOrDefault<string>(
                "SELECT value FROM canonical_values WHERE entity_id=@id AND key='merged_into_work_id'", new { id });
            if (!Guid.TryParse(value, out var target))
            {
                return id;
            }
            id = target;
        }
        throw new InvalidOperationException("A work redirect cycle was found.");
    }
}
