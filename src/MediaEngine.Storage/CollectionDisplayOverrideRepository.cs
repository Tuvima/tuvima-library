using System.Text.Json;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>Presentation overrides belong to the exact collection ID, never a representative work.</summary>
public sealed class CollectionDisplayOverrideRepository(IDatabaseConnection db)
{
    public Task<bool> SaveAsync(Guid collectionId, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
        => db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var row = connection.QuerySingleOrDefault<OverrideRow>(new CommandDefinition(
                "SELECT display_overrides_json AS Json FROM collections WHERE id = @id;",
                new { id = collectionId }, transaction, cancellationToken: token));
            if (row is null)
            {
                return false;
            }
            var values = string.IsNullOrWhiteSpace(row.Json)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(row.Json) ?? [];
            foreach (var (key, value) in fields)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    values.Remove(key);
                }
                else
                {
                    values[key] = value.Trim();
                }
            }
            return connection.Execute(new CommandDefinition(
                "UPDATE collections SET display_overrides_json = @json WHERE id = @id;",
                new { id = collectionId, json = JsonSerializer.Serialize(values) }, transaction,
                cancellationToken: token)) == 1;
        }, ct);

    /// <summary>The local display overrides stored on a collection (empty when it has none), or null when the collection does not exist.</summary>
    public Task<IReadOnlyDictionary<string, string>?> LoadAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<OverrideRow>(new CommandDefinition(
            "SELECT display_overrides_json AS Json FROM collections WHERE id = @id;",
            new { id = collectionId }, cancellationToken: ct));
        if (row is null)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
        }

        IReadOnlyDictionary<string, string> values = string.IsNullOrWhiteSpace(row.Json)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(
                JsonSerializer.Deserialize<Dictionary<string, string>>(row.Json) ?? [],
                StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyDictionary<string, string>?>(values);
    }

    private sealed record OverrideRow(string? Json);
}
