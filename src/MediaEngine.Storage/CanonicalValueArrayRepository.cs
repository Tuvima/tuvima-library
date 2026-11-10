using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="ICanonicalValueArrayRepository"/>.
///
/// Multi-valued canonical fields (genre, characters, cast_member, etc.) are stored
/// as individual rows in <c>canonical_value_arrays</c> rather than as
/// packed-delimiter strings. Each row carries an ordinal for display ordering
/// and an optional QID for entity-valued items.
/// </summary>
public sealed class CanonicalValueArrayRepository : ICanonicalValueArrayRepository
{
    private readonly IDatabaseConnection _db;

    public CanonicalValueArrayRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public async Task SetValuesAsync(
        Guid entityId,
        string key,
        IReadOnlyList<CanonicalArrayEntry> entries,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(entries);

        if (!MetadataFieldConstants.IsMultiValued(key))
        {
            throw new ArgumentException($"Canonical key '{key}' is scalar and cannot be stored as an array.", nameof(key));
        }

        if (entries.Any(entry => entry.Ordinal < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(entries), "Canonical array ordinals must be non-negative.");
        }

        if (entries.Any(entry => string.IsNullOrWhiteSpace(entry.Value)))
        {
            throw new ArgumentException("Canonical array values cannot be empty or whitespace.", nameof(entries));
        }

        if (entries.Select(entry => entry.Ordinal).Distinct().Count() != entries.Count)
        {
            throw new ArgumentException("Canonical array ordinals must be unique within an entity and key.", nameof(entries));
        }

        entries = SplitJoinedGenres(key, entries);

        await _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            // Delete existing entries for this (entity, key) pair.
            conn.Execute("""
                DELETE FROM canonical_value_arrays
                WHERE entity_id = @entityId AND key = @key;
                """,
                new { entityId, key },
                transaction: tx);

            if (entries.Count > 0)
            {
                innerCt.ThrowIfCancellationRequested();
                conn.Execute("""
                    INSERT INTO canonical_value_arrays
                        (entity_id, key, ordinal, value, value_qid)
                    VALUES
                        (@EntityId, @Key, @Ordinal, @Value, @ValueQid);
                    """,
                    entries.Select(e => new
                    {
                        EntityId = entityId,
                        Key = key,
                        e.Ordinal,
                        e.Value,
                        ValueQid = e.ValueQid,
                    }),
                    transaction: tx);
            }

        }, ct).ConfigureAwait(false);
    }

    /// <summary>A provider can send a whole category list as one genre value ("Action, Drama"). Stores each genre on its own row; entries linked to Wikidata stay as they are.</summary>
    private static IReadOnlyList<CanonicalArrayEntry> SplitJoinedGenres(string key, IReadOnlyList<CanonicalArrayEntry> entries)
    {
        if (!key.Equals(MetadataFieldConstants.Genre, StringComparison.OrdinalIgnoreCase)
            || !entries.Any(entry => entry.ValueQid is null && MediaEngine.Domain.Text.GenreLabels.SplitKeepingCase(entry.Value).Count > 1))
        {
            return entries;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expanded = new List<CanonicalArrayEntry>();
        foreach (var entry in entries.OrderBy(entry => entry.Ordinal))
        {
            var labels = entry.ValueQid is null ? MediaEngine.Domain.Text.GenreLabels.SplitKeepingCase(entry.Value) : [entry.Value];
            foreach (var label in labels)
            {
                if (seen.Add(label))
                {
                    expanded.Add(new CanonicalArrayEntry { Ordinal = expanded.Count, Value = label, ValueQid = entry.ValueQid });
                }
            }
        }

        return expanded;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CanonicalArrayEntry>> GetValuesAsync(
        Guid entityId,
        string key,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        // Use intermediate row type because CanonicalArrayEntry has init-only properties
        // which Dapper cannot set directly.
        var rows = conn.Query<CanonicalArrayKeyedRow>("""
            SELECT ordinal   AS Ordinal,
                   value     AS Value,
                   value_qid AS ValueQid
            FROM canonical_value_arrays
            WHERE entity_id = @entityId AND key = @key
            ORDER BY ordinal ASC;
            """, new { entityId, key }).AsList();

        var results = rows.ConvertAll(r => new CanonicalArrayEntry
        {
            Ordinal = r.Ordinal,
            Value = r.Value,
            ValueQid = r.ValueQid,
        });

        return Task.FromResult<IReadOnlyList<CanonicalArrayEntry>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>>> GetAllByEntityAsync(
        Guid entityId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<CanonicalArrayKeyedRow>("""
            SELECT key       AS Key,
                   ordinal   AS Ordinal,
                   value     AS Value,
                   value_qid AS ValueQid
            FROM canonical_value_arrays
            WHERE entity_id = @entityId
            ORDER BY key ASC, ordinal ASC;
            """, new { entityId }).AsList();

        var grouped = new Dictionary<string, List<CanonicalArrayEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!grouped.TryGetValue(row.Key, out var list))
            {
                list = [];
                grouped[row.Key] = list;
            }
            list.Add(new CanonicalArrayEntry
            {
                Ordinal = row.Ordinal,
                Value = row.Value,
                ValueQid = row.ValueQid,
            });
        }

        var readOnly = new Dictionary<string, IReadOnlyList<CanonicalArrayEntry>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (k, list) in grouped)
        {
            readOnly[k] = list;
        }

        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>>>(readOnly);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>>>> GetAllByEntitiesAsync(
        IReadOnlyList<Guid> entityIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (entityIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>>>>(
                new Dictionary<Guid, IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>>>());
        }

        using var conn = _db.CreateConnection();
        var rows = new List<CanonicalArrayEntityKeyedRow>();
        foreach (var batch in entityIds.Where(id => id != Guid.Empty).Distinct().Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters();
            var placeholders = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                var name = $"entityId{index}";
                placeholders[index] = "@" + name;
                parameters.Add(name, GuidSql.ToBlob(batch[index]));
            }

            rows.AddRange(conn.Query<CanonicalArrayEntityKeyedRow>("""
                SELECT entity_id AS EntityId,
                       key       AS Key,
                       ordinal   AS Ordinal,
                       value     AS Value,
                       value_qid AS ValueQid
                FROM canonical_value_arrays
                WHERE entity_id IN (
                """ + string.Join(", ", placeholders) + """
                )
                ORDER BY entity_id, key, ordinal;
                """, parameters));
        }

        var result = new Dictionary<Guid, IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>>>();
        foreach (var entityGroup in rows.GroupBy(row => row.EntityId))
        {
            result[entityGroup.Key] = entityGroup
                .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<CanonicalArrayEntry>)group.Select(row => new CanonicalArrayEntry
                    {
                        Ordinal = row.Ordinal,
                        Value = row.Value,
                        ValueQid = row.ValueQid,
                    }).ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>>>>(result);
    }

    public Task<IReadOnlyList<CanonicalArrayEntry>> FindValuesByKeyAsync(
        string key,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!MetadataFieldConstants.IsMultiValued(key))
        {
            throw new ArgumentException($"Canonical key '{key}' is scalar and cannot be queried as an array.", nameof(key));
        }

        using var conn = _db.CreateConnection();
        var rows = conn.Query<CanonicalArrayKeyedRow>(
            """
            SELECT ordinal AS Ordinal,
                   value AS Value,
                   value_qid AS ValueQid
            FROM canonical_value_arrays
            WHERE key = @key
            ORDER BY entity_id, ordinal;
            """,
            new { key }).AsList();
        return Task.FromResult<IReadOnlyList<CanonicalArrayEntry>>(rows.Select(row => new CanonicalArrayEntry
        {
            Ordinal = row.Ordinal,
            Value = row.Value,
            ValueQid = row.ValueQid,
        }).ToList());
    }

    /// <inheritdoc/>
    public async Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await _db.AcquireWriteLockAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = _db.CreateConnection();
            conn.Execute(
                "DELETE FROM canonical_value_arrays WHERE entity_id = @entityId;",
                new { entityId });
        }
        finally
        {
            _db.ReleaseWriteLock();
        }
    }

    // ── Private intermediate row type ─────────────────────────────────────────

    /// <summary>
    /// Intermediate row type for <see cref="GetAllByEntityAsync"/> that includes
    /// the grouping key alongside the entry fields.
    /// </summary>
    private sealed class CanonicalArrayKeyedRow
    {
        public string Key { get; set; } = string.Empty;
        public int Ordinal { get; set; }
        public string Value { get; set; } = string.Empty;
        public string? ValueQid { get; set; }
    }

    private sealed class CanonicalArrayEntityKeyedRow
    {
        public Guid EntityId { get; set; }
        public string Key { get; set; } = string.Empty;
        public int Ordinal { get; set; }
        public string Value { get; set; } = string.Empty;
        public string? ValueQid { get; set; }
    }
}
