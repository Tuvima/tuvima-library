using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MediaEngine.Storage.Backup;

/// <summary>
/// Writes and reads a <see cref="BackupExport"/> as the <c>data/</c> part of a backup archive:
/// <c>data/configuration/&lt;table&gt;.json</c>, <c>data/personal/&lt;table&gt;.json</c>,
/// <c>data/choices/&lt;table&gt;.json</c>, <c>data/corrections/&lt;table&gt;.json</c>, <c>data/titles.json</c> and
/// <c>data/contents.json</c>. Values keep their SQLite storage class: JSON null, number (an integer has no fraction or
/// exponent, a real always does), string, <c>{"b64": "..."}</c> for a BLOB, <c>{"title": "t1"}</c> for a reference to a
/// title (with <c>"text": true</c> when the original id was TEXT rather than a BLOB) and <c>{"portable": "..."}</c> for
/// a value that was protected with one computer's key and is written readable so any computer can use it.
/// </summary>
public static class BackupArchiveFormat
{
    /// <summary>Folder of Configuration tables.</summary>
    public const string ConfigurationFolder = "data/configuration/";

    /// <summary>Folder of PersonalState tables.</summary>
    public const string PersonalFolder = "data/personal/";

    /// <summary>Folder of a person's own choices on catalogue rows.</summary>
    public const string ChoicesFolder = "data/choices/";

    /// <summary>Folder of a person's own rows on catalogue tables (a locked title field, a renamed chapter).</summary>
    public const string CorrectionsFolder = "data/corrections/";

    /// <summary>The recognition data of every title the rows refer to.</summary>
    public const string TitlesEntry = "data/titles.json";

    /// <summary>The counts and the kind of backup.</summary>
    public const string ContentsEntry = "data/contents.json";

    /// <summary>What kind of backup this is, as written to <c>contents.json</c>.</summary>
    public const string ContentsKind = "settings-and-personal-state";

    /// <summary>The largest single entry accepted when reading, in bytes.</summary>
    public const long MaxEntryBytes = 256L * 1024 * 1024;

    /// <summary>The most uncompressed bytes accepted across all <c>data/</c> entries when reading.</summary>
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The most entries accepted in an archive when reading.</summary>
    public const int MaxEntries = 1000;

    private const string ConfigurationKind = "configuration";
    private const string PersonalKind = "personal_state";
    private const string ChoicesKind = "choices";
    private const string CorrectionsKind = "corrections";

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

        foreach (var table in export.Corrections)
        {
            WriteEntry(archive, CorrectionsFolder + table.Table + ".json", writer => WriteTable(writer, table, CorrectionsKind));
        }
    }

    /// <summary>
    /// Reads what <see cref="Write"/> wrote. Entries outside <c>data/</c>, duplicate entries, too many or oversized
    /// entries, files for a table the backup never holds (or in the wrong folder), unknown columns or value shapes,
    /// out-of-range numbers and references to a title that is not listed are refused with
    /// <see cref="InvalidDataException"/>.
    /// </summary>
    public static BackupExport Read(ZipArchive archive) => Read(archive, MaxEntries, MaxEntryBytes, MaxTotalBytes);

    /// <summary>
    /// Reads the <c>data/</c> part of a whole backup file, which also holds <c>manifest.json</c> and the <c>config/</c> files. Those two
    /// are not data and are left alone (the Engine checks and stages them itself); every other rule of <see cref="Read(ZipArchive)"/>
    /// applies, including the limits on entries and size.
    /// </summary>
    public static BackupExport ReadBackupFile(ZipArchive archive) => Read(archive, MaxEntries, MaxEntryBytes, MaxTotalBytes, skipBackupFileParts: true);

    /// <summary><see cref="Read(ZipArchive)"/> with explicit limits, so a test can use small ones.</summary>
    internal static BackupExport Read(ZipArchive archive, int maxEntries, long maxEntryBytes, long maxTotalBytes, bool skipBackupFileParts = false)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var tables = new List<BackupExportTable>();
        var choices = new List<BackupExportTable>();
        var corrections = new List<BackupExportTable>();
        JsonDocument? titlesDocument = null;
        JsonDocument? contentsDocument = null;
        try
        {
            if (archive.Entries.Count > maxEntries)
            {
                throw new InvalidDataException("The backup holds too many entries.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            var declaredBytes = 0L;
            foreach (var entry in archive.Entries)
            {
                if (!names.Add(entry.FullName))
                {
                    throw new InvalidDataException($"The backup holds the entry {entry.FullName} more than once.");
                }

                declaredBytes += entry.Length;
                if (declaredBytes > maxTotalBytes)
                {
                    throw new InvalidDataException("The backup is too large.");
                }
            }

            var budget = new ReadBudget(maxEntryBytes, maxTotalBytes);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith('/'))
                {
                    continue;
                }

                if (skipBackupFileParts
                    && (string.Equals(name, "manifest.json", StringComparison.Ordinal) || name.StartsWith("config/", StringComparison.Ordinal)))
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
                    contentsDocument = ParseEntry(entry, budget);
                }
                else if (name == TitlesEntry)
                {
                    titlesDocument = ParseEntry(entry, budget);
                }
                else if (TryTableEntry(name, out var folder, out var table))
                {
                    var rule = RuleFor(folder, table);
                    using var document = ParseEntry(entry, budget);
                    var (parsed, kind) = ReadTable(document.RootElement, table, rule, folder);
                    var expected = folder switch
                    {
                        ConfigurationFolder => ConfigurationKind,
                        PersonalFolder => PersonalKind,
                        CorrectionsFolder => CorrectionsKind,
                        _ => ChoicesKind,
                    };
                    if (kind != expected)
                    {
                        throw new InvalidDataException($"Table '{table}' is in the wrong folder for its kind.");
                    }

                    (folder switch
                    {
                        ChoicesFolder => choices,
                        CorrectionsFolder => corrections,
                        _ => tables,
                    }).Add(parsed);
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
            foreach (var table in tables.Concat(choices).Concat(corrections))
            {
                foreach (var value in table.Rows.SelectMany(row => row))
                {
                    if (value is BackupValue.TitleRef reference && !known.Contains(reference.Key))
                    {
                        throw new InvalidDataException($"Table '{table.Table}' refers to a title that the backup does not list.");
                    }
                }
            }

            return new BackupExport(tables, choices, titles, ReadContents(contentsDocument.RootElement), corrections);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
        {
            // A JSON value the reader cannot convert (a number or string of the wrong shape) is a bad backup, not a bug.
            throw new InvalidDataException("The backup holds a value that is not valid.", ex);
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
        writer.WriteNumber("correction_rows", contents.CorrectionRows);
        writer.WriteNumber("portable_values_kept_machine_bound", contents.PortableValuesKeptMachineBound);
        writer.WriteNumber("withheld_rows", contents.WithheldRows);
        writer.WriteEndObject();
    }

    internal static void WriteTitles(Utf8JsonWriter writer, IReadOnlyList<BackupTitle> titles)
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
            if (title.Withheld)
            {
                writer.WriteBoolean("withheld", true);
            }

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

    internal static void WriteValue(Utf8JsonWriter writer, BackupValue value)
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
            case BackupValue.Portable portable:
                writer.WriteStartObject();
                writer.WriteString("portable", portable.Value);
                writer.WriteEndObject();
                break;
            case BackupValue.TitleRef reference:
                writer.WriteStartObject();
                writer.WriteString("title", reference.Key);
                if (reference.WasText)
                {
                    writer.WriteBoolean("text", true);
                }

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

    /// <summary>What is left of the read budget: the per-entry cap, and the bytes still allowed across all entries.</summary>
    private sealed class ReadBudget(long maxEntryBytes, long totalBytes)
    {
        public long MaxEntryBytes { get; } = maxEntryBytes;

        public long Remaining { get; set; } = totalBytes;
    }

    private static JsonDocument ParseEntry(ZipArchiveEntry entry, ReadBudget budget)
    {
        if (entry.Length > budget.MaxEntryBytes)
        {
            throw new InvalidDataException($"The backup entry {entry.FullName} is too large.");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            // The declared length can lie, so the bytes actually read are counted too.
            budget.Remaining -= read;
            if (buffer.Length + read > budget.MaxEntryBytes || budget.Remaining < 0)
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

    /// <summary>
    /// The rule for a table file, or a refusal. The table must be classified under exactly that name; a file in the
    /// configuration folder must be for a Configuration table, in the personal folder a PersonalState table, in the
    /// choices folder a table that has choice columns (a Catalogue table, or a Configuration table with a keep condition)
    /// and in the corrections folder a Catalogue table that has a correction condition. A file for any other table is
    /// never read.
    /// </summary>
    private static BackupTableRule RuleFor(string folder, string table)
    {
        var rule = BackupTableClassification.Find(table);
        if (rule is null || !string.Equals(rule.Table, table, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The backup holds a table this version does not know: {table}.");
        }

        var allowed = folder switch
        {
            ConfigurationFolder => rule.Kind == BackupDataKind.Configuration,
            PersonalFolder => rule.Kind == BackupDataKind.PersonalState,
            CorrectionsFolder => rule.Kind == BackupDataKind.Catalogue && rule.CorrectionWhere is not null,
            _ => rule.ChoiceColumns.Count > 0
                && (rule.Kind == BackupDataKind.Catalogue || (rule.Kind == BackupDataKind.Configuration && rule.KeepWhere is not null)),
        };
        return allowed ? rule : throw new InvalidDataException($"Table '{table}' is not one a backup holds in that folder.");
    }

    private static bool TryTableEntry(string name, out string folder, out string table)
    {
        foreach (var candidate in new[] { ConfigurationFolder, PersonalFolder, ChoicesFolder, CorrectionsFolder })
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

    private static (BackupExportTable Table, string Kind) ReadTable(JsonElement root, string expectedTable, BackupTableRule rule, string folder)
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
            CorrectionsKind => BackupDataKind.Catalogue,
            _ => throw new InvalidDataException($"Table '{table}' has an unknown kind."),
        };

        var columns = new List<string>();
        foreach (var column in RequiredArray(root, "columns").EnumerateArray())
        {
            columns.Add(column.ValueKind == JsonValueKind.String
                ? column.GetString()!
                : throw new InvalidDataException($"Table '{table}' has a column name that is not text."));
        }

        CheckColumns(table, columns, rule, folder == ChoicesFolder);

        var rows = new List<IReadOnlyList<BackupValue>>();
        foreach (var rowElement in RequiredArray(root, "rows").EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Array || rowElement.GetArrayLength() != columns.Count)
            {
                throw new InvalidDataException($"A row of '{table}' does not have one value per column.");
            }

            var values = rowElement.EnumerateArray().Select(ReadValue).ToList();
            for (var i = 0; i < values.Count; i++)
            {
                // An excluded column (a file path, someone else's id) is written empty by every export; a file that fills one is refused,
                // so no file can make the Engine open, serve or delete a path it chose.
                if (values[i] is not BackupValue.NullValue
                    && rule.ExcludedColumns.Contains(columns[i], StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Table '{table}' has columns this version does not recognise.");
                }

                // A portable value is a protected secret made readable; it is accepted only where the rule says so.
                if (values[i] is BackupValue.Portable
                    && (folder != ConfigurationFolder || !rule.PortableColumns.Contains(columns[i], StringComparer.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"Table '{table}' holds a portable value in a column that is not portable.");
                }
            }

            rows.Add(values);
        }

        return (new BackupExportTable(table, dataKind, columns, rows), kind);
    }

    /// <summary>
    /// A choices file is <c>ref</c> followed by the rule's choice columns. Any other file's columns must be ones the rule
    /// names (references, excluded, which are written as NULL) or plain lower-case identifiers: a live column name is not
    /// known at read time, but nothing that could change the meaning of a restore statement gets through.
    /// </summary>
    private static void CheckColumns(string table, List<string> columns, BackupTableRule rule, bool isChoices)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            if (!seen.Add(column))
            {
                throw new InvalidDataException($"Table '{table}' lists the column '{column}' twice.");
            }
        }

        if (isChoices)
        {
            if (columns.Count == 0 || columns[0] != "ref"
                || columns.Skip(1).Any(column => !rule.ChoiceColumns.Contains(column, StringComparer.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"Table '{table}' has columns this version does not recognise.");
            }

            return;
        }

        foreach (var column in columns)
        {
            var named = rule.CatalogueReferences.Any(reference => string.Equals(reference.Column, column, StringComparison.OrdinalIgnoreCase))
                || rule.ExcludedColumns.Contains(column, StringComparer.OrdinalIgnoreCase);
            if (!named && !IsPlainIdentifier(column))
            {
                throw new InvalidDataException($"Table '{table}' has a column name this version does not accept.");
            }
        }
    }

    /// <summary>Lower-case letters, digits and underscores, not starting with a digit (<c>^[a-z_][a-z0-9_]*$</c>).</summary>
    private static bool IsPlainIdentifier(string name)
    {
        if (name.Length == 0 || name[0] is >= '0' and <= '9')
        {
            return false;
        }

        return name.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
    }

    internal static BackupValue ReadValue(JsonElement element)
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
                    return BackupValue.FromReal(FiniteDouble(element));
                }

                return element.TryGetInt64(out var integer)
                    ? BackupValue.FromInteger(integer)
                    : throw new InvalidDataException("A backup value is an integer outside the supported range.");
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                if (properties.Count == 2
                    && element.TryGetProperty("title", out var titleKey) && titleKey.ValueKind == JsonValueKind.String
                    && element.TryGetProperty("text", out var textFlag) && textFlag.ValueKind == JsonValueKind.True)
                {
                    return BackupValue.FromTitleRef(titleKey.GetString()!, wasText: true);
                }

                if (properties.Count == 1 && properties[0].Value.ValueKind == JsonValueKind.String)
                {
                    var text = properties[0].Value.GetString()!;
                    switch (properties[0].Name)
                    {
                        case "b64":
                            return BackupValue.FromBlob(DecodeBase64(text));
                        case "title":
                            return BackupValue.FromTitleRef(text);
                        case "portable":
                            return BackupValue.FromPortable(text);
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

    /// <summary>The number as a double; one JSON cannot hold as a finite double (for example <c>1e400</c>) is refused.</summary>
    private static double FiniteDouble(JsonElement element) =>
        element.TryGetDouble(out var value) && double.IsFinite(value)
            ? value
            : throw new InvalidDataException("A backup value is a number outside the supported range.");

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

    internal static List<BackupTitle> ReadTitles(JsonElement root)
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
                OptionalInt(element, "year"),
                OptionalBool(element, "withheld")));
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
            RequiredInt(root, "dropped_rows"),
            RequiredInt(root, "correction_rows"),
            RequiredInt(root, "portable_values_kept_machine_bound"),
            RequiredInt(root, "withheld_rows"));
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

    private static bool OptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new InvalidDataException($"'{name}' is not true or false.")
            : false;

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
            return FiniteDouble(value);
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
