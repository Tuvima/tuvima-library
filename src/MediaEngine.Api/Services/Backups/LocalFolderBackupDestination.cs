using MediaEngine.Contracts.Backups;

namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// The Library's own backup folder, as the first destination (WP25). Every backup is written here first; "sending" to it is
/// therefore a no-op for the orchestrator, but the destination still lists, fetches and deletes so retention and restore treat it
/// like any other. It stays on this computer, so its files are the plain archives the Library has always made.
/// </summary>
internal sealed class LocalFolderBackupDestination(DatabaseBackupService backups) : IBackupDestination
{
    public string Id => BackupDestinationIds.Local;

    public string Name => "This computer";

    public bool LeavesMachine => false;

    public bool IsConfigured => true;

    public Task<DestinationTestResult> TestAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(backups.BackupDirectory);
            var probe = Path.Combine(backups.BackupDirectory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Task.FromResult(new DestinationTestResult(true, "The backup folder is ready."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new DestinationTestResult(false, "The backup folder cannot be written to."));
        }
    }

    public async Task SendAsync(string name, Stream content, long length, CancellationToken ct)
    {
        Directory.CreateDirectory(backups.BackupDirectory);
        await using var target = new FileStream(PathFor(name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        await content.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DestinationBackup>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<DestinationBackup> items = backups.List()
            .Select(archive => new DestinationBackup(archive.FileName, archive.SizeBytes, archive.CreatedAt))
            .ToList();
        return Task.FromResult(items);
    }

    public Task<Stream> FetchAsync(string name, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(backups.ResolveArchive(name), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true));

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        File.Delete(backups.ResolveArchive(name));
        return Task.CompletedTask;
    }

    public Task<long?> FreeSpaceAsync(CancellationToken ct)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(backups.BackupDirectory));
            return Task.FromResult<long?>(string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return Task.FromResult<long?>(null);
        }
    }

    private string PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal))
        {
            throw new BackupDestinationException("That backup name is not allowed.");
        }

        return Path.Combine(backups.BackupDirectory, name);
    }
}
