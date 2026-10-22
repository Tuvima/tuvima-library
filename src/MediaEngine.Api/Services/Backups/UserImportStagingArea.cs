using System.Collections.Concurrent;
using System.Globalization;

namespace MediaEngine.Api.Services.Backups;

/// <summary>A user export kept between the preview and the import.</summary>
/// <param name="OperationId">The id the preview handed out.</param>
/// <param name="ActorAccountId">The administrator who uploaded it; only they can import it.</param>
/// <param name="Path">Where the file is kept.</param>
/// <param name="OriginalName">The name the file was uploaded with (a file name only), recorded with the import.</param>
/// <param name="ExpiresAt">When it stops being offered.</param>
internal sealed record StagedUserImport(Guid OperationId, Guid ActorAccountId, string Path, string OriginalName, DateTimeOffset ExpiresAt);

/// <summary>
/// Keeps an uploaded user export in the backup folder's staging area for a short while, so the import step uses exactly the file the
/// preview showed. The file holds sign-in material, so it lives for an hour at most and is removed as soon as it is imported, refused or
/// replaced. Only the administrator who uploaded it can use it. Held in memory: after a restart the file is simply uploaded again.
/// </summary>
public sealed class UserImportStagingArea
{
    /// <summary>How long a checked file waits for the administrator to confirm.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly string _folder;
    private readonly TimeProvider _clock;
    private readonly ILogger<UserImportStagingArea> _logger;
    private readonly ConcurrentDictionary<Guid, StagedUserImport> _staged = new();

    /// <param name="backupDirectory">The backup folder; the staging area is a folder inside it, apart from the backups themselves.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">Says when a kept file could not be removed (never what it held).</param>
    public UserImportStagingArea(string backupDirectory, TimeProvider clock, ILogger<UserImportStagingArea> logger)
    {
        _folder = Path.Combine(Path.GetFullPath(backupDirectory), "user-imports");
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The staging folder.</summary>
    public string Folder => _folder;

    /// <summary>Makes room for a new upload: removes expired files and any file left from before a restart.</summary>
    public void Sweep()
    {
        if (!Directory.Exists(_folder))
        {
            return;
        }

        var now = _clock.GetUtcNow();
        foreach (var expired in _staged.Values.Where(item => item.ExpiresAt <= now).ToList())
        {
            Discard(expired.OperationId);
        }

        var known = _staged.Values.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(_folder, "*.zip", SearchOption.TopDirectoryOnly))
        {
            if (!known.Contains(path))
            {
                DeleteFile(path);
            }
        }
    }

    /// <summary>The path a new upload should be written to.</summary>
    public string NewPath(Guid operationId)
    {
        Directory.CreateDirectory(_folder);
        return Path.Combine(_folder, operationId.ToString("N", CultureInfo.InvariantCulture) + ".zip");
    }

    /// <summary>Records an uploaded file the administrator can import until it expires. Their earlier kept file, if any, is replaced and removed.</summary>
    public void Keep(Guid operationId, Guid actorAccountId, string path, string originalName)
    {
        foreach (var earlier in _staged.Values.Where(item => item.ActorAccountId == actorAccountId && item.OperationId != operationId).ToList())
        {
            Discard(earlier.OperationId);
        }

        _staged[operationId] = new StagedUserImport(operationId, actorAccountId, path, SafeName(originalName), _clock.GetUtcNow().Add(Lifetime));
    }

    /// <summary>A file name only (no folders), short enough to show; a blank name becomes a plain one.</summary>
    private static string SafeName(string name)
    {
        var fileName = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "user-export.zip";
        }

        return fileName.Length <= 120 ? fileName : fileName[..120];
    }

    /// <summary>The kept file for an operation, only for the administrator who uploaded it and only before it expires; otherwise null.</summary>
    internal StagedUserImport? Find(Guid operationId, Guid actorAccountId)
    {
        if (_staged.TryGetValue(operationId, out var item)
            && item.ActorAccountId == actorAccountId
            && item.ExpiresAt > _clock.GetUtcNow()
            && File.Exists(item.Path))
        {
            return item;
        }

        return null;
    }

    /// <summary>Forgets an operation and removes its file.</summary>
    public void Discard(Guid operationId)
    {
        if (_staged.TryRemove(operationId, out var item))
        {
            DeleteFile(item.Path);
        }
    }

    /// <summary>Removes a file this store made. A file that cannot be removed is logged and swept on the next upload; it never changes the outcome of an import.</summary>
    public void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A kept user file could not be removed ({Type}); it is removed on the next upload.", ex.GetType().Name);
        }
    }
}
