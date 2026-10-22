namespace MediaEngine.Storage.Backup;

/// <summary>
/// One value of an exported row. It keeps the SQLite storage class exactly (a 16-byte BLOB id stays a BLOB, a TEXT id
/// stays TEXT), because a restore must write back what was read. A reference to a catalogue row is a
/// <see cref="TitleRef"/> to a <see cref="BackupTitle"/>, never the catalogue row id itself.
/// </summary>
public abstract record BackupValue
{
    private BackupValue()
    {
    }

    /// <summary>SQL NULL.</summary>
    public static BackupValue Null { get; } = new NullValue();

    /// <summary>An INTEGER.</summary>
    public static BackupValue FromInteger(long value) => new Integer(value);

    /// <summary>A REAL.</summary>
    public static BackupValue FromReal(double value) => new Real(value);

    /// <summary>A TEXT.</summary>
    public static BackupValue FromText(string value) => new Text(value);

    /// <summary>A BLOB.</summary>
    public static BackupValue FromBlob(byte[] value) => new Blob(value);

    /// <summary>A reference to an exported title. <paramref name="wasText"/> is true when the original value was a TEXT id.</summary>
    public static BackupValue FromTitleRef(string key, bool wasText = false) => new TitleRef(key, wasText);

    /// <summary>A TEXT that was protected with this computer's key and has been written readable so any computer can use it.</summary>
    public static BackupValue FromPortable(string value) => new Portable(value);

    /// <summary>SQL NULL.</summary>
    public sealed record NullValue : BackupValue;

    /// <summary>An INTEGER.</summary>
    public sealed record Integer(long Value) : BackupValue;

    /// <summary>A REAL.</summary>
    public sealed record Real(double Value) : BackupValue;

    /// <summary>A TEXT.</summary>
    public sealed record Text(string Value) : BackupValue;

    /// <summary>
    /// A reference to a <see cref="BackupTitle"/> by its key (for example <c>t1</c>). <paramref name="WasText"/> records
    /// that the original value was a TEXT id rather than a 16-byte BLOB, so a restore writes back the same storage class.
    /// </summary>
    public sealed record TitleRef(string Key, bool WasText) : BackupValue;

    /// <summary>
    /// A TEXT that is machine-protected in the data store and was made readable for the backup (see
    /// <see cref="IBackupPortableValueCodec"/>). A restore protects it again with the new computer's key. It appears
    /// only in a column the table's rule lists as portable.
    /// </summary>
    public sealed record Portable(string Value) : BackupValue;

    /// <summary>A BLOB. Equality compares the bytes.</summary>
    public sealed record Blob(byte[] Value) : BackupValue
    {
        /// <inheritdoc/>
        public bool Equals(Blob? other) => other is not null && Value.AsSpan().SequenceEqual(other.Value);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(Value);
            return hash.ToHashCode();
        }
    }
}

/// <summary>One exported table: its kind, the columns written (excluded columns are absent) and its rows.</summary>
public sealed record BackupExportTable(
    string Table,
    BackupDataKind Kind,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<BackupValue>> Rows);

/// <summary>
/// How to recognise a title again after a rescan: external ids, running time, edition and file fingerprints, plus a
/// name and year so a waiting item is readable. Only titles that a kept row refers to are ever exported.
/// </summary>
/// <param name="Key">The key rows use to refer to this title (<c>t1</c>, <c>t2</c>, in the order first met).</param>
/// <param name="Target">The kind of catalogue row it was (a <see cref="CatalogueTarget"/> name).</param>
/// <param name="MediaType">The media type of the work, when known.</param>
/// <param name="Ids">External ids, id type (lower case) to value.</param>
/// <param name="ParentIds">The same ids for the parent work (an album, a show, a series), when there is one.</param>
/// <param name="Ordinal">The position within the parent (track, episode, volume).</param>
/// <param name="ParentOrdinal">The parent's own position (for example a season number).</param>
/// <param name="RunningTimeSeconds">The running time of the file, when it has been inspected.</param>
/// <param name="Edition">The edition label (for example a cut or a format).</param>
/// <param name="FileHashes">Fingerprints of the original files, the fallback when there are no ids.</param>
/// <param name="Name">The title's name; null for a withheld title.</param>
/// <param name="ParentName">The parent's name; null for a withheld title.</param>
/// <param name="Year">The release year; null for a withheld title.</param>
public sealed record BackupTitle(
    string Key,
    string Target,
    string? MediaType,
    IReadOnlyDictionary<string, string> Ids,
    IReadOnlyDictionary<string, string> ParentIds,
    int? Ordinal,
    int? ParentOrdinal,
    double? RunningTimeSeconds,
    string? Edition,
    IReadOnlyList<string> FileHashes,
    string? Name,
    string? ParentName,
    int? Year)
{
    /// <summary>
    /// True when the title has no external id of its own or its parent's, so a restore can only re-attach state by
    /// exact file fingerprint (D4 of the backup spec).
    /// </summary>
    public bool IsRecognisedByFileOnly => Ids.Count == 0 && ParentIds.Count == 0;

    /// <summary>A title with only its target set, for rows whose catalogue row has no recognition data.</summary>
    public static BackupTitle Empty(string key, string target) =>
        new(key, target, null, new Dictionary<string, string>(), new Dictionary<string, string>(), null, null, null, null, [], null, null, null);
}

/// <summary>What an export holds, in counts. Never names a profile.</summary>
/// <param name="Scope">The scope exported.</param>
/// <param name="ConfigurationTables">Configuration tables written.</param>
/// <param name="ConfigurationRows">Configuration rows written.</param>
/// <param name="PersonalRows">Personal state rows written.</param>
/// <param name="ChoiceRows">Rows of a person's own choices on catalogue rows (for example a favourite View item).</param>
/// <param name="Titles">Titles written.</param>
/// <param name="TitlesRecognisedByFileOnly">Titles with no external id, matched by exact file only on restore.</param>
/// <param name="Profiles">Profiles in scope.</param>
/// <param name="DroppedRows">Rows left out because the catalogue row they pointed at no longer exists.</param>
/// <param name="CorrectionRows">Rows of a person's own corrections on catalogue rows (a locked title field, a renamed chapter).</param>
/// <param name="PortableValuesKeptMachineBound">
/// Portable values that could not be read with this computer's key (for example the key is gone), so they were kept as
/// they are and only work on this computer.
/// </param>
public sealed record BackupExportContents(
    BackupScope Scope,
    int ConfigurationTables,
    int ConfigurationRows,
    int PersonalRows,
    int ChoiceRows,
    int Titles,
    int TitlesRecognisedByFileOnly,
    int Profiles,
    int DroppedRows,
    int CorrectionRows = 0,
    int PortableValuesKeptMachineBound = 0);

/// <summary>
/// An export of configuration and personal state, with personal state keyed by recognition data.
/// <see cref="Choices"/> hold a person's own choices on catalogue rows: each table's first column is <c>ref</c>, a
/// title reference to the row itself, followed by the choice columns (their kind is <see cref="BackupDataKind.Catalogue"/>).
/// <see cref="Corrections"/> hold rows a person made on catalogue tables, written like personal rows (every kept column,
/// catalogue references as title references).
/// </summary>
public sealed record BackupExport(
    IReadOnlyList<BackupExportTable> Tables,
    IReadOnlyList<BackupExportTable> Choices,
    IReadOnlyList<BackupTitle> Titles,
    BackupExportContents Contents,
    IReadOnlyList<BackupExportTable> Corrections);
