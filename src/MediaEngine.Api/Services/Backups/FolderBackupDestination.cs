using MediaEngine.Contracts.Backups;

namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// Another folder, such as an external drive, or a folder on another computer reached by its network path (Developer tools spec WP26).
/// Both are the same thing to the Library: a place it can write files. A copy is written under a temporary name and
/// renamed so a half-written file is never mistaken for a backup.
/// </summary>
internal sealed class FolderBackupDestination(string id, string name, string path, bool isShare) : IBackupDestination
{
    private string Where => isShare ? "network share" : "folder";

    public string Id => id;

    public string Name => name;

    public bool LeavesMachine => true;

    public bool IsConfigured => BackupDestinationRules.FolderProblem(path) is null;

    public Task<DestinationTestResult> TestAsync(CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return Task.FromResult(new DestinationTestResult(false, $"The {Where} was not found. Check the path, and that the drive or computer is switched on."));
            }

            var probe = Path.Combine(path, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Task.FromResult(new DestinationTestResult(true, $"The {Where} is ready."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(new DestinationTestResult(false, $"The {Where} cannot be written to. Check the path and your permissions."));
        }
    }

    public async Task SendAsync(string fileName, Stream content, long length, CancellationToken ct)
    {
        var target = PathFor(fileName);
        var partial = target + ".part";
        try
        {
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await content.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            File.Move(partial, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            TryDelete(partial);
            throw new BackupDestinationException($"The copy could not be written to the {Where}.", ex);
        }
    }

    public Task<IReadOnlyList<DestinationBackup>> ListAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<DestinationBackup> items = [.. new DirectoryInfo(path).EnumerateFiles()
                .Where(file => !file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .Select(file => new DestinationBackup(file.Name, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)))];
            return Task.FromResult(items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupDestinationException($"The {Where} could not be read.", ex);
        }
    }

    public Task<Stream> FetchAsync(string fileName, CancellationToken ct)
    {
        try
        {
            return Task.FromResult<Stream>(new FileStream(PathFor(fileName), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupDestinationException($"That backup could not be read from the {Where}.", ex);
        }
    }

    public Task DeleteAsync(string fileName, CancellationToken ct)
    {
        try
        {
            File.Delete(PathFor(fileName));
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupDestinationException($"An old backup could not be removed from the {Where}.", ex);
        }
    }

    public Task<long?> FreeSpaceAsync(CancellationToken ct)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return Task.FromResult<long?>(string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A network path often has no drive to ask; "unknown" is the honest answer.
            return Task.FromResult<long?>(null);
        }
    }

    private string PathFor(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new BackupDestinationException("That backup name is not allowed.");
        }

        return Path.Combine(path, fileName);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The half-written file is only clutter; the failure that matters has already been raised.
        }
    }
}
