using System.Data;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite GUID conversion helpers.
/// Internal GUIDs are stored as 16-byte BLOBs using RFC4122/network byte order.
/// API contracts still expose ordinary string GUIDs.
/// </summary>
public static class GuidSql
{
    public static byte[] ToBlob(Guid value)
    {
        var bytes = new byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes;
    }

    /// <summary>
    /// Many ids as one JSON array of upper-case hex strings, for one statement parameter instead of one value per id
    /// (SQLite accepts at most 32,766 values in a statement). In SQL turn it back into id blobs with
    /// <c>SELECT unhex(value) FROM json_each(@parameter)</c>.
    /// </summary>
    public static string ToJsonHexArray(IEnumerable<Guid> values) =>
        System.Text.Json.JsonSerializer.Serialize(
            values.Select(value => value.ToString("N").ToUpperInvariant()));

    public static object? ToDb(Guid? value) =>
        value.HasValue ? ToBlob(value.Value) : DBNull.Value;

    public static Guid FromDb(object value)
    {
        if (value is byte[] { Length: 16 } bytes)
        {
            return new Guid(bytes, bigEndian: true);
        }

        if (value is byte[] invalidBytes)
        {
            throw new InvalidCastException(
                $"Cannot convert SQLite BLOB with length {invalidBytes.Length} to Guid; expected exactly 16 bytes.");
        }

        throw new InvalidCastException(
            $"Cannot convert SQLite value of type {value.GetType().Name} to Guid; guid-blob-v1 requires a 16-byte BLOB.");
    }

    public static Guid? FromDbNullable(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return FromDb(value);
    }

    public static string ToText(object value) => FromDb(value).ToString("D");

    /// <summary>
    /// SQL CASE expression that projects an <c>entity_id</c> column into an RFC4122-dashed,
    /// lowercase GUID string, whether the column is stored as a 16-byte BLOB (guid-blob-v1) or
    /// legacy TEXT. Column name is fixed to <c>entity_id</c> — this expression is meant to be
    /// selected/aliased (e.g. <c>{GuidSql.EntityIdProjection} AS EntityId</c>) against a query
    /// where the source column is literally named <c>entity_id</c>.
    /// </summary>
    public const string EntityIdProjection = """
        CASE
            WHEN typeof(entity_id) = 'blob' THEN lower(
                substr(hex(entity_id), 1, 8) || '-' ||
                substr(hex(entity_id), 9, 4) || '-' ||
                substr(hex(entity_id), 13, 4) || '-' ||
                substr(hex(entity_id), 17, 4) || '-' ||
                substr(hex(entity_id), 21, 12))
            ELSE CAST(entity_id AS TEXT)
        END
        """;

    /// <summary>
    /// Builds a SQL expression that converts a BLOB (or TEXT) GUID column into lowercase hex
    /// text, or NULL when the column is empty. This is the raw-hex sibling of
    /// <see cref="EntityIdProjection"/> — it does not add RFC4122 dashes, so it's appropriate
    /// for hex-based joins/comparisons and compact identifier output rather than user-facing
    /// GUID strings.
    /// </summary>
    public static string TextProjection(string column) => $"NULLIF(lower(hex({column})), '')";
}
