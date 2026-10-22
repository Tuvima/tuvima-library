using System.Text.Json;

namespace MediaEngine.Api.Services.LocalAssets.FileWrites;

/// <summary>One hidden copy that is the only copy of a file: where it sits, and the file it stands in for.</summary>
internal sealed record StrandedCopy(string CopyPath, string OriginalPath);

/// <summary>
/// The durable list of hidden copies the View writer must never sweep (batched write-back D9). A copy is on it when a swap took the
/// original away and the copy could neither be moved back nor renamed, so it is the person's only copy and keeps its hidden name. The
/// list is kept in a small file in the Engine's data folder, so it survives a restart. It is written whole to a temporary file beside
/// it and moved into place, so a crash never leaves half a list. A copy leaves the list only when its file no longer exists.
/// </summary>
internal sealed class StrandedCopyRecord
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Dictionary<string, StrandedCopy> _copies = new(StringComparer.OrdinalIgnoreCase);

    private StrandedCopyRecord(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>
    /// False when the list on disk could not be read. The sweep then deletes nothing, because a stranded copy it cannot see might
    /// be the only copy of a file.
    /// </summary>
    public bool Readable { get; private set; }

    /// <summary>Loads the list and names every copy still on it in an error, once, so a person can find it.</summary>
    public static StrandedCopyRecord Open(string path, ILogger logger)
    {
        var record = new StrandedCopyRecord(path, logger);
        record.Refresh();
        foreach (var copy in record._copies.Values)
        {
            logger.LogError(
                "A copy of {OriginalPath} is kept at {CopyPath} because it could not be put back; nothing will delete it.",
                copy.OriginalPath, copy.CopyPath);
        }

        return record;
    }

    public bool Contains(string copyPath) => _copies.ContainsKey(copyPath);

    /// <summary>Reads the list again (a fault in the file may have been mended), drops copies whose file is gone, and keeps what was added since.</summary>
    public void Refresh()
    {
        List<StrandedCopy> stored;
        try
        {
            stored = Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Readable = false;
            _logger.LogWarning(ex, "The list of kept copies at {RecordPath} could not be read; no leftover temporary copy will be cleared until it can.", _path);
            return;
        }

        Readable = true;
        foreach (var copy in stored)
        {
            _copies.TryAdd(copy.CopyPath, copy);
        }

        foreach (var copy in _copies.Values.ToList())
        {
            // Gone means its folder is there and the file is not. A folder that cannot be seen (a drive that is away) proves nothing.
            var folder = Path.GetDirectoryName(copy.CopyPath);
            if (folder is not null && Directory.Exists(folder) && !File.Exists(copy.CopyPath))
            {
                _copies.Remove(copy.CopyPath);
            }
        }

        var storedPaths = new HashSet<string>(stored.Select(copy => copy.CopyPath), StringComparer.OrdinalIgnoreCase);
        if (!storedPaths.SetEquals(_copies.Keys))
        {
            TrySave();
        }
    }

    /// <summary>Records a copy that is now the only copy of a file. It stays in memory even if the list cannot be written.</summary>
    public void Add(StrandedCopy copy)
    {
        _copies[copy.CopyPath] = copy;
        if (Readable)
        {
            TrySave();
        }
        else
        {
            // Writing would replace a list that could not be read, and the copies on it would be lost.
            _logger.LogError(
                "The list of kept copies at {RecordPath} could not be read, so {CopyPath} is protected only until the Engine stops.",
                _path, copy.CopyPath);
        }
    }

    private List<StrandedCopy> Read()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var stored = JsonSerializer.Deserialize<List<StrandedCopy>>(File.ReadAllText(_path));
        return stored is not null && stored.All(copy => copy is { CopyPath.Length: > 0 })
            ? stored
            : throw new JsonException("The list of kept copies does not hold a list of copies.");
    }

    private void TrySave()
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_copies.Values, Json));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The list of kept copies at {RecordPath} could not be written; the kept copies are protected only until the Engine stops.", _path);
            DiscardTemporary(temporary);
        }
    }

    private void DiscardTemporary(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "A temporary file of the kept-copies list could not be removed.");
        }
    }
}
