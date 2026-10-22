using System.Text;
using System.Text.Json;

namespace MediaEngine.Storage.Backup;

/// <summary>A row of a table that hangs off a waiting row (for example an item of a bookmark pack), kept with its parent.</summary>
/// <param name="Table">The child's table.</param>
/// <param name="Columns">Its columns.</param>
/// <param name="Values">Its values, in the backup's value encoding.</param>
internal sealed record WaitingRowChild(string Table, IReadOnlyList<string> Columns, IReadOnlyList<BackupValue> Values);

/// <summary>
/// One parked row: its columns and values with every reference to a title as a <see cref="BackupValue.TitleRef"/> whose key is the
/// index of the title in the row's own title list, plus the rows that hang off it.
/// </summary>
internal sealed record WaitingRowData(
    IReadOnlyList<string> Columns,
    IReadOnlyList<BackupValue> Values,
    IReadOnlyList<WaitingRowChild> Children);

/// <summary>
/// Reads and writes the <c>row_json</c> and <c>titles_json</c> columns of a waiting row. Values use the backup archive's own
/// encoding (<see cref="BackupArchiveFormat.WriteValue"/>), so there is one encoder for a value, not two.
/// </summary>
internal static class WaitingRowJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    public static string WriteRow(WaitingRowData row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Write(writer =>
        {
            writer.WriteStartObject();
            WriteColumnsAndValues(writer, row.Columns, row.Values);
            if (row.Children.Count > 0)
            {
                writer.WriteStartArray("children");
                foreach (var child in row.Children)
                {
                    writer.WriteStartObject();
                    writer.WriteString("table", child.Table);
                    WriteColumnsAndValues(writer, child.Columns, child.Values);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        });
    }

    public static WaitingRowData ReadRow(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        var (columns, values) = ReadColumnsAndValues(root);
        var children = new List<WaitingRowChild>();
        if (root.TryGetProperty("children", out var childArray))
        {
            if (childArray.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("A waiting row's children are not a list.");
            }

            foreach (var child in childArray.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object
                    || !child.TryGetProperty("table", out var table)
                    || table.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("A waiting row's child does not name its table.");
                }

                var (childColumns, childValues) = ReadColumnsAndValues(child);
                children.Add(new WaitingRowChild(table.GetString()!, childColumns, childValues));
            }
        }

        return new WaitingRowData(columns, values, children);
    }

    public static string WriteTitles(IReadOnlyList<BackupTitle> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        return Write(writer => BackupArchiveFormat.WriteTitles(writer, titles));
    }

    public static IReadOnlyList<BackupTitle> ReadTitles(string json)
    {
        using var document = Parse(json);
        return BackupArchiveFormat.ReadTitles(document.RootElement);
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteColumnsAndValues(Utf8JsonWriter writer, IReadOnlyList<string> columns, IReadOnlyList<BackupValue> values)
    {
        if (columns.Count != values.Count)
        {
            throw new InvalidOperationException("A waiting row does not have one value per column.");
        }

        writer.WriteStartArray("columns");
        foreach (var column in columns)
        {
            writer.WriteStringValue(column);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("values");
        foreach (var value in values)
        {
            BackupArchiveFormat.WriteValue(writer, value);
        }

        writer.WriteEndArray();
    }

    private static (List<string> Columns, List<BackupValue> Values) ReadColumnsAndValues(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("columns", out var columnArray) || columnArray.ValueKind != JsonValueKind.Array
            || !element.TryGetProperty("values", out var valueArray) || valueArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("A waiting row has no columns and values.");
        }

        var columns = columnArray.EnumerateArray()
            .Select(column => column.ValueKind == JsonValueKind.String ? column.GetString()! : throw new InvalidDataException("A waiting row has a column name that is not text."))
            .ToList();
        var values = valueArray.EnumerateArray().Select(BackupArchiveFormat.ReadValue).ToList();
        return columns.Count == values.Count
            ? (columns, values)
            : throw new InvalidDataException("A waiting row does not have one value per column.");
    }

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("A waiting row holds JSON that cannot be read.", ex);
        }
    }
}
