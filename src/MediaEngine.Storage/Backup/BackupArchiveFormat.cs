using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MediaEngine.Storage.Backup;

/// <summary>
/// Writes and reads a <see cref="BackupExport"/> as the <c>data/</c> part of a backup archive:
/// <c>data/configuration/&lt;table&gt;.json</c>, <c>data/personal/&lt;table&gt;.json</c>,
/// <c>data/choices/&lt;table&gt;.json</c>, <c>data/titles.json</c> and <c>data/contents.json</c>. Values keep their
/// SQLite storage class: JSON null, number (an integer has no fraction or exponent, a real always does),
/// string, <c>{"b64": "..."}</c> for a BLOB and <c>{"title": "t1"}</c> for a reference to a title.
/// </summary>
public static class BackupArchiveFormat
{
    /// <summary>Folder of Configuration tables.</summary>
    public const string ConfigurationFolder = "data/configuration/";

    /// <summary>Folder of PersonalState tables.</summary>
    public const string PersonalFolder = "data/personal/";

    /// <summary>Folder of a person's own choices on catalogue rows.</summary>
    public const string ChoicesFolder = "data/choices/";

    /// <summary>The recognition data of every title the rows refer to.</summary>
    public const string TitlesEntry = "data/titles.json";

    /// <summary>The counts and the kind of backup.</summary>
    public const string ContentsEntry = "data/contents.json";

    /// <summary>What kind of backup this is, as written to <c>contents.json</c>.</summary>
    public const string ContentsKind = "settings-and-personal-state";

    /// <summary>The largest entry accepted when reading, in bytes.</summary>
    public const long MaxEntryBytes = 512L * 1024 * 1024;

    private const string ConfigurationKind = "configuration";
    private const string PersonalKind = "personal_state";
    private const string ChoicesKind = "choices";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>Writes the export into <paramref name="archive"/> (create mode).</summary>
    public static void Write(ZipArchive archive, BackupExport export)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(export);

        WriteEntry(archive, ContentsEntry, writer => WriteContents(writer, export.Contents));
        WriteEntry(archive, TitlesEntry, writer => WriteTitles(writer, export.Titles));
        foreach (var table in export.Tables)
        {
            var (folder, kind) = table.Kind switch
            {
                BackupDataKind.Configuration => (ConfigurationFolder, ConfigurationKind),
                BackupDataKind.PersonalState => (PersonalFolder, PersonalKind),
                _ => throw new InvalidOperationException($"Table '{table.Table}' is {table.Kind}, which a backup never holds."),
            };
            WriteEntry(archive, folder + table.Table + ".json", writer => WriteTable(writer, table, kind));
        }

        foreach (var table in export.Choices)
        {
            WriteEntry(archive, ChoicesFolder + table.Table + ".json", writer => WriteTable(writer, table, ChoicesKind));
        }
    }

    /// <summary>
    /// Reads what <see cref="Write"/> wrote. Entries outside <c>data/</c>, oversized entries, unknown value shapes and
    /// references to a title that is not listed are refused with <see cref="InvalidDataException"/>.
    /// </summary>
    public static BackupExport Read(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var tables = new List<BackupExportTable>();
        var choices = new List<BackupExportTable>();
        JsonDocument? titlesDocument = null;
        JsonDocument? contentsDocument = null;
        try
        {
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith('/'))
                {
                    continue;
                }

                if (!name.StartsWith("data/", StringComparison.Ordinal)
                    || name.Contains("..", StringComparison.Ordinal)
                    || name.Contains('\\', StringComparison.Ordinal)
                    || name.Contains('\0', StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The backup holds an entry outside its data folder: {name}.");
                }

                if (name == ContentsEntry)
                {
                    contentsDocument = ParseEntry(entry);
                }
                else if (name == TitlesEntry)
                {
                    titlesDocument = ParseEntry(entry);
                }
                else if (TryTableEntry(name, out var folder, out var table))
                {
                    using var document = ParseEntry(entry);
                    var (parsed, kind) = ReadTable(document.RootElement, table);
                    var expected = folder switch
                    {
                        ConfigurationFolder => ConfigurationKind,
                        PersonalFolder => PersonalKind,
                        _ => ChoicesKind,
                    };
                    if (kind != expected)
                    {
                        throw new InvalidDataException($"Table '{table}' is in the wrong folder for its kind.");
                    }

                    (folder == ChoicesFolder ? choices : tables).Add(parsed);
                }
                else
                {
                    throw new InvalidDataException($"The backup holds an entry this version does not recognise: {name}.");
                }
            }

            if (titlesDocument is null || contentsDocument is null)
            {
                throw new InvalidDataException("The backup is missing its titles or contents entry.");
            }

            var titles = ReadTitles(titlesDocument.RootElement);
            var known = titles.Select(title => title.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var table in tables.Concat(choices))
            {
                foreach (var value in table.Rows.SelectMany(row => row))
                {
                    if (value is BackupValue.TitleRef reference && !known.Contains(reference.Key))
                    {
                        throw new InvalidDataException($"Table '{table.Table}' refers to a title that the backup does not list.");
                    }
                }
            }

            return new BackupExport(tables, choices, titles, ReadContents(contentsDocument.RootElement));
        }
        finally
        {
            titlesDocument?.Dispose();
            contentsDocument?.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Writing
    // ---------------------------------------------------------------------------------------------------------

    private static void WriteEntry(ZipArchive archive, string name, Action<Utf8JsonWriter> write)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);
        write(writer);
        writer.Flush();
    }

    private static void WriteContents(Utf8JsonWriter writer, BackupExportContents contents)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", ContentsKind);
        writer.WriteString("scope", contents.Scope.IsProfile ? "profile" : "library");
        if (contents.Scope.ProfileId is { } profileId)
        {
            // The id only; never the profile's name.
            writer.WriteString("profile_id", profileId.ToString("D", CultureInfo.InvariantCulture));
        }

        writer.WriteNumber("configuration_tables", contents.ConfigurationTables);
        writer.WriteNumber("configuration_rows", contents.ConfigurationRows);
        writer.WriteNumber("personal_rows", contents.PersonalRows);
        writer.WriteNumber("choice_rows", contents.ChoiceRows);
        writer.WriteNumber("titles", contents.Titles);
        writer.WriteNumber("titles_recognised_by_file_only", contents.TitlesRecognisedByFileOnly);
        writer.WriteNumber("profiles", contents.Profiles);
        writer.WriteNumber("dropped_rows", contents.DroppedRows);
        writer.WriteEndObject();
    }

    private static void WriteTitles(Utf8JsonWriter writer, IReadOnlyList<BackupTitle> titles)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("titles");
        foreach (var title in titles)
        {
            writer.WriteStartObject();
            writer.WriteString("key", title.Key);
            writer.WriteString("target", title.Target);
            WriteOptionalString(writer, "media_type", title.MediaType);
            WriteOptionalMap(writer, "ids", title.Ids);
            WriteOptionalMap(writer, "parent_ids", title.ParentIds);
            WriteOptionalNumber(writer, "ordinal", title.Ordinal);
            WriteOptionalNumber(writer, "parent_ordinal", title.ParentOrdinal);
            if (title.RunningTimeSeconds is { } seconds)
            {
                writer.WritePropertyName("running_time_seconds");
                WriteReal(writer, seconds);
            }

            WriteOptionalString(writer, "edition", title.Edition);
            if (title.FileHashes.Count > 0)
            {
                writer.WriteStartArray("file_hashes");
                foreach (var hash in title.FileHashes)
                {
                    writer.WriteStringValue(hash);
                }

                writer.WriteEndArray();
            }

            WriteOptionalString(writer, "name", title.Name);
            WriteOptionalString(writer, "parent_name", title.ParentName);
            WriteOptionalNumber(writer, "year", title.Year);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static void WriteOptionalMap(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0)
        {
            return;
        }

        writer.WriteStartObject(name);
        foreach (var pair in map.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteTable(Utf8JsonWriter writer, BackupExportTable table, string kind)
    {
        writer.WriteStartObject();
        writer.WriteString("table", table.Table);
        writer.WriteString("kind", kind);
        writer.WriteStartArray("columns");
        foreach (var column in table.Columns)
        {
            writer.WriteStringValue(column);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("rows");
        foreach (var row in table.Rows)
        {
            if (row.Count != table.Columns.Count)
            {
                throw new InvalidOperationException($"A row of '{table.Table}' does not have one value per column.");
            }

            writer.WriteStartArray();
            foreach (var value in row)
            {
                WriteValue(writer, value);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, BackupValue value)
    {
        switch (value)
        {
            case BackupValue.NullValue:
                writer.WriteNullValue();
                break;
            case BackupValue.Integer integer:
                writer.WriteNumberValue(integer.Value);
                break;
            case BackupValue.Real real:
                WriteReal(writer, real.Value);
                break;
            case BackupValue.Text text:
                writer.WriteStringValue(text.Value);
                break;
            case BackupValue.Blob blob:
                writer.WriteStartObject();
                writer.WriteString("b64", Convert.ToBase64String(blob.Value));
                writer.WriteEndObject();
                break;
            case BackupValue.TitleRef reference:
                writer.WriteStartObject();
                writer.WriteString("title", reference.Key);
                writer.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException($"Unknown backup value {value.GetType().Name}.");
        }
    }

    /// <summary>
    /// A real is always written with a fraction or exponent so it reads back as a real, never an integer. A value JSON
    /// cannot hold (infinity) is written as <c>{"real": "inf"}</c>.
    /// </summary>
    private static void WriteReal(Utf8JsonWriter writer, double value)
    {
        if (double.IsPositiveInfinity(value) || double.IsNegativeInfinity(value))
        {
            writer.WriteStartObject();
            writer.WriteString("real", value > 0 ? "inf" : "-inf");
            writer.WriteEndObject();
            return;
        }

        if (double.IsNaN(value))
        {
            writer.WriteNullValue();
            return;
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.IndexOfAny(['.', 'E', 'e']) < 0)
        {
            text += ".0";
        }

        writer.WriteRawValue(text);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------------------------

    private static JsonDocument ParseEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes)
        {
            throw new InvalidDataException($"The backup entry {entry.FullName} is too large.");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxEntryBytes)
            {
                throw new InvalidDataException($"The backup entry {entry.FullName} is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        try
        {
            return JsonDocument.Parse(buffer);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The backup entry {entry.FullName} is not valid.", ex);
        }
    }

    private static bool TryTableEntry(string name, out string folder, out string table)
    {
        foreach (var candidate in new[] { ConfigurationFolder, PersonalFolder, ChoicesFolder })
        {
            if (name.StartsWith(candidate, StringComparison.Ordinal)
                && name.EndsWith(".json", StringComparison.Ordinal)
                && name.Length > candidate.Length + ".json".Length)
            {
                var stem = name[candidate.Length..^".json".Length];
                if (!stem.Contains('/', StringComparison.Ordinal))
                {
                    folder = candidate;
                    table = stem;
                    return true;
                }
            }
        }

        folder = string.Empty;
        table = string.Empty;
        return false;
    }

    private static (BackupExportTable Table, string Kind) ReadTable(JsonElement root, string expectedTable)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Table '{expectedTable}' is not an object.");
        }

        var table = RequiredString(root, "table");
        if (!string.Equals(table, expectedTable, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Table '{expectedTable}' names a different table inside.");
        }

        var kind = RequiredString(root, "kind");
        var dataKind = kind switch
        {
            ConfigurationKind => BackupDataKind.Configuration,
            PersonalKind => BackupDataKind.PersonalState,
            ChoicesKind => BackupDataKind.Catalogue,
            _ => throw new InvalidDataException($"Table '{table}' has an unknown kind."),
        };

        var columns = new List<string>();
        foreach (var column in RequiredArray(root, "columns").EnumerateArray())
        {
            columns.Add(column.ValueKind == JsonValueKind.String
                ? column.GetString()!
                : throw new InvalidDataException($"Table '{table}' has a column name that is not text."));
        }

        var rows = new List<IReadOnlyList<BackupValue>>();
        foreach (var rowElement in RequiredArray(root, "rows").EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Array || rowElement.GetArrayLength() != columns.Count)
            {
                throw new InvalidDataException($"A row of '{table}' does not have one value per column.");
            }

            rows.Add(rowElement.EnumerateArray().Select(ReadValue).ToList());
        }

        return (new BackupExportTable(table, dataKind, columns, rows), kind);
    }

    private static BackupValue ReadValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return BackupValue.Null;
            case JsonValueKind.String:
                return BackupValue.FromText(element.GetString()!);
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                if (raw.IndexOfAny(['.', 'E', 'e']) >= 0)
                {
                    return BackupValue.FromReal(element.GetDouble());
                }

                return element.TryGetInt64(out var integer)
                    ? BackupValue.FromInteger(integer)
                    : throw new InvalidDataException("A backup value is an integer outside the supported range.");
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                if (properties.Count == 1 && properties[0].Value.ValueKind == JsonValueKind.String)
                {
                    var text = properties[0].Value.GetString()!;
                    switch (properties[0].Name)
                    {
                        case "b64":
                            return BackupValue.FromBlob(DecodeBase64(text));
                        case "title":
                            return BackupValue.FromTitleRef(text);
                        case "real" when text == "inf":
                            return BackupValue.FromReal(double.PositiveInfinity);
                        case "real" when text == "-inf":
                            return BackupValue.FromReal(double.NegativeInfinity);
                    }
                }

                throw new InvalidDataException("A backup value has a shape this version does not recognise.");
            default:
                throw new InvalidDataException("A backup value has a shape this version does not recognise.");
        }
    }

    private static byte[] DecodeBase64(string text)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("A backup value holds bytes that are not valid.", ex);
        }
    }

    private static List<BackupTitle> ReadTitles(JsonElement root)
    {
        var titles = new List<BackupTitle>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in RequiredArray(root, "titles").EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A title in the backup is not an object.");
            }

            var key = RequiredString(element, "key");
            if (!keys.Add(key))
            {
                throw new InvalidDataException("The backup lists the same title key twice.");
            }

            titles.Add(new BackupTitle(
                key,
                RequiredString(element, "target"),
                OptionalString(element, "media_type"),
                OptionalMap(element, "ids"),
                OptionalMap(element, "parent_ids"),
                OptionalInt(element, "ordinal"),
                OptionalInt(element, "parent_ordinal"),
                OptionalDouble(element, "running_time_seconds"),
                OptionalString(element, "edition"),
                OptionalStrings(element, "file_hashes"),
                OptionalString(element, "name"),
                OptionalString(element, "parent_name"),
                OptionalInt(element, "year")));
        }

        return titles;
    }

    private static BackupExportContents ReadContents(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || RequiredString(root, "kind") != ContentsKind)
        {
            throw new InvalidDataException("The backup is not a settings and personal state backup.");
        }

        var scope = RequiredString(root, "scope") switch
        {
            "library" => BackupScope.WholeLibrary,
            "profile" => Guid.TryParse(OptionalString(root, "profile_id"), out var profileId) && profileId != Guid.Empty
                ? BackupScope.ForProfile(profileId)
                : throw new InvalidDataException("A profile backup does not say which profile it holds."),
            _ => throw new InvalidDataException("The backup has an unknown scope."),
        };

        return new BackupExportContents(
            scope,
            RequiredInt(root, "configuration_tables"),
            RequiredInt(root, "configuration_rows"),
            RequiredInt(root, "personal_rows"),
            RequiredInt(root, "choice_rows"),
            RequiredInt(root, "titles"),
            RequiredInt(root, "titles_recognised_by_file_only"),
            RequiredInt(root, "profiles"),
            RequiredInt(root, "dropped_rows"));
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"The backup is missing '{name}'.");

    private static JsonElement RequiredArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidDataException($"The backup is missing '{name}'.");

    private static int RequiredInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidDataException($"The backup is missing '{name}'.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException($"'{name}' is not text.")
            : null;

    private static int? OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw new InvalidDataException($"'{name}' is not a number.")
            : null;

    private static double? OptionalDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        return ReadValue(value) is BackupValue.Real real ? real.Value : throw new InvalidDataException($"'{name}' is not a number.");
    }

    private static Dictionary<string, string> OptionalMap(JsonElement element, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!element.TryGetProperty(name, out var value))
        {
            return map;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"'{name}' is not an object.");
        }

        foreach (var property in value.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()!
                : throw new InvalidDataException($"'{name}' holds a value that is not text.");
        }

        return map;
    }

    private static List<string> OptionalStrings(JsonElement element, string name)
    {
        var list = new List<string>();
        if (!element.TryGetProperty(name, out var value))
        {
            return list;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"'{name}' is not a list.");
        }

        foreach (var item in value.EnumerateArray())
        {
            list.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new InvalidDataException($"'{name}' holds a value that is not text."));
        }

        return list;
    }
}
