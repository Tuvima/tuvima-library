namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Fields the selected physical-file adapter actually attempts to write. This is
/// an admission contract, not proof that a subsequent save can be read back.
/// </summary>
public sealed class MetadataTaggerCapabilities
{
    private readonly HashSet<string> _fields;
    private readonly HashSet<string> _unsignedFields;
    private readonly HashSet<string> _integerFields;

    public MetadataTaggerCapabilities(
        string format,
        IEnumerable<string> fields,
        bool canWriteArtwork,
        int adapterVersion,
        bool acceptsCustomOpfFields = false,
        IEnumerable<string>? unsignedFields = null,
        IEnumerable<string>? integerFields = null)
    {
        Format = format;
        _fields = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
        _unsignedFields = new HashSet<string>(unsignedFields ?? [], StringComparer.OrdinalIgnoreCase);
        _integerFields = new HashSet<string>(integerFields ?? [], StringComparer.OrdinalIgnoreCase);
        CanWriteArtwork = canWriteArtwork;
        AdapterVersion = adapterVersion;
        AcceptsCustomOpfFields = acceptsCustomOpfFields;
    }

    public string Format { get; }
    public IReadOnlySet<string> WritableFields => _fields;
    public bool CanWriteArtwork { get; }
    public int AdapterVersion { get; }
    public bool AcceptsCustomOpfFields { get; }

    public bool CanWriteField(string key) =>
        !string.IsNullOrWhiteSpace(key) && (AcceptsCustomOpfFields || _fields.Contains(key));

    public void ValidateTags(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var unsupported = tags.Keys.Where(key => !CanWriteField(key)).ToArray();
        if (unsupported.Length != 0)
        {
            throw new NotSupportedException($"{Format} cannot write: {string.Join(", ", unsupported)}.");
        }

        foreach (var (key, value) in tags)
        {
            if (_unsignedFields.Contains(key) && !uint.TryParse(value, out _))
            {
                throw new FormatException($"{Format} requires an unsigned number for {key}.");
            }
            if (_integerFields.Contains(key) && !int.TryParse(value, out _))
            {
                throw new FormatException($"{Format} requires an integer for {key}.");
            }
        }
    }
}
