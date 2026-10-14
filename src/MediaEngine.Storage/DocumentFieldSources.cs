using Dapper;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// Remembers who last set a document's correspondent, type or custom field value, so an auto-filing rule never
/// replaces something a person chose. A value with no row here is treated as the person's.
/// </summary>
internal static class DocumentFieldSources
{
    public const string Correspondent = "correspondent";
    public const string DocumentType = "document_type";
    public const string Field = "field";

    public const string Person = "person";
    public const string Rule = "rule";

    public static void Mark(
        SqliteConnection connection, SqliteTransaction transaction, Guid itemId, string slot, Guid? fieldId, string setBy)
    {
        Clear(connection, transaction, itemId, slot, fieldId);
        connection.Execute("""
            INSERT INTO doc_item_field_sources (item_id, slot, field_id, set_by)
            VALUES (@itemId, @slot, @fieldId, @setBy);
            """, new { itemId, slot, fieldId, setBy }, transaction);
    }

    public static void Clear(SqliteConnection connection, SqliteTransaction transaction, Guid itemId, string slot, Guid? fieldId)
    {
        if (fieldId is { } id)
        {
            connection.Execute("""
                DELETE FROM doc_item_field_sources WHERE item_id = @itemId AND slot = @slot AND field_id = @id;
                """, new { itemId, slot, id }, transaction);
        }
        else
        {
            connection.Execute("""
                DELETE FROM doc_item_field_sources WHERE item_id = @itemId AND slot = @slot AND field_id IS NULL;
                """, new { itemId, slot }, transaction);
        }
    }

    /// <summary>Who set the value last, or null when nothing is recorded.</summary>
    public static string? Get(
        SqliteConnection connection, SqliteTransaction transaction, Guid itemId, string slot, Guid? fieldId) =>
        fieldId is { } id
            ? connection.ExecuteScalar<string?>("""
                SELECT set_by FROM doc_item_field_sources WHERE item_id = @itemId AND slot = @slot AND field_id = @id;
                """, new { itemId, slot, id }, transaction)
            : connection.ExecuteScalar<string?>("""
                SELECT set_by FROM doc_item_field_sources WHERE item_id = @itemId AND slot = @slot AND field_id IS NULL;
                """, new { itemId, slot }, transaction);
}
