using System.Security.Cryptography;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// Makes a backup and gets it to every destination (Developer tools spec WP25, Backup and restore spec D15): the archive is made on
/// this computer first, then for each switched-on destination that leaves the computer the same plain archive is sent and read
/// back to check it arrived intact; finally the oldest copies beyond "keep the last N" are removed. Backups are never encrypted,
/// so keeping the copies safe is the person's job. A destination that fails does not stop the others, shows Needs attention,
/// and sends a notification.
/// </summary>
internal sealed class BackupOrchestrator(
    DatabaseBackupService backups,
    IEnumerable<IBackupDestination> destinations,
    ConfiguredBackupDestinations configured,
    BackupSettingsAccessor settingsAccessor,
    IBackupDestinationRepository repository,
    INotificationPublisher notifications,
    TimeProvider clock,
    ILogger<BackupOrchestrator> logger)
{
    private const string NamePrefix = "tuvima-backup-";

    /// <summary>The file ending of the sealed copies older versions made. They can no longer be opened.</summary>
    internal const string OlderSealedExtension = ".tvbk";

    internal const string OlderSealedCopyMessage = "This copy was made by an older version and can't be restored.";
    private readonly IReadOnlyList<IBackupDestination> _destinations = destinations.ToList();
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>The Library's own folder first, then every destination an administrator added. Read again each time so a change takes effect at once.</summary>
    public IReadOnlyList<IBackupDestination> Destinations => [.. _destinations, .. configured.Current()];

    public IBackupDestination? Find(string id) =>
        Destinations.FirstOrDefault(destination => string.Equals(destination.Id, id, StringComparison.Ordinal));

    /// <summary>Makes a backup now and copies it everywhere it should go. Only one runs at a time.</summary>
    public async Task<BackupRunResponse> RunAsync(CancellationToken ct)
    {
        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A backup is already running.");
        }

        try
        {
            var settings = await settingsAccessor.LoadAsync(ct).ConfigureAwait(false);
            var archivePath = await backups.CreateAsync(ct).ConfigureAwait(false);
            var archiveName = Path.GetFileName(archivePath);
            var response = new BackupRunResponse { ArchiveName = archiveName };
            foreach (var destination in Destinations)
            {
                ct.ThrowIfCancellationRequested();
                var local = !destination.LeavesMachine;
                if (!local && !settings.EnabledDestinations.Contains(destination.Id, StringComparer.Ordinal))
                {
                    continue;
                }

                var outcome = new BackupDestinationOutcomeDto { Id = destination.Id, Name = destination.Name };
                response.Destinations.Add(outcome);
                try
                {
                    if (local)
                    {
                        outcome.Outcome = "copied";
                        outcome.Message = "Saved on this computer.";
                    }
                    else if (!destination.IsConfigured)
                    {
                        outcome.Outcome = "skipped";
                        outcome.Message = "Not set up yet.";
                    }
                    else
                    {
                        await SendAndVerifyAsync(destination, archivePath, archiveName, ct).ConfigureAwait(false);
                        outcome.Outcome = "copied";
                        outcome.Message = "Copied and checked.";
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Backup copy to {Destination} failed.", destination.Id);
                    outcome.Outcome = "failed";
                    outcome.Message = ex is BackupDestinationException or InvalidDataException ? ex.Message : "The copy could not be completed.";
                    await repository.AddCopyAsync(new StoredBackupCopy(
                        Guid.NewGuid(), destination.Id, archiveName, 0, string.Empty, "failed", null, outcome.Message, clock.GetUtcNow()), ct).ConfigureAwait(false);
                }

                if (outcome.Outcome != "failed")
                {
                    await ApplyRetentionAsync(destination, settings.KeepLast, ct).ConfigureAwait(false);
                }
            }

            response.AllOk = response.Destinations.All(item => item.Outcome != "failed");
            if (response.AllOk)
            {
                notifications.Publish(new NotificationRequest(NotificationEventIds.BackupCompleted, "scheduled", "A backup was written and copied."));
            }
            else
            {
                var failed = string.Join("; ", response.Destinations.Where(item => item.Outcome == "failed").Select(item => $"{item.Name}: {item.Message}"));
                notifications.Publish(new NotificationRequest(NotificationEventIds.BackupFailed, "copy", $"A backup was made but a copy failed. {failed}"));
            }

            return response;
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async Task<BackupDestinationTestResponse> TestAsync(IBackupDestination destination, CancellationToken ct)
    {
        try
        {
            var result = await destination.TestAsync(ct).ConfigureAwait(false);
            return new BackupDestinationTestResponse { Success = result.Success, Message = result.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Testing backup destination {Destination} failed.", destination.Id);
            return new BackupDestinationTestResponse { Success = false, Message = ex is BackupDestinationException ? ex.Message : "The destination could not be reached." };
        }
    }

    /// <summary>
    /// Fetches a backup from a destination and stages it with the existing safe restore, which refuses unsafe entries, secrets and
    /// zip bombs exactly as for an uploaded backup (applied at the next start; the current data is kept as a pre-restore copy).
    /// </summary>
    public async Task<MediaEngine.Contracts.System.ScheduleRestoreResultDto> RestoreFromDestinationAsync(IBackupDestination destination, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) || !name.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            throw new BackupDestinationException("That backup name is not valid.");
        }

        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupDestinationException(name.EndsWith(OlderSealedExtension, StringComparison.OrdinalIgnoreCase)
                ? OlderSealedCopyMessage
                : "That file is not a Library backup.");
        }

        var restoredName = $"{NamePrefix}restored-{clock.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.zip";
        var restoredPath = Path.Combine(backups.BackupDirectory, restoredName);
        Directory.CreateDirectory(backups.BackupDirectory);
        try
        {
            await using (var source = await destination.FetchAsync(name, ct).ConfigureAwait(false))
            await using (var target = new FileStream(restoredPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            return backups.ScheduleRestore(restoredName);
        }
        catch
        {
            if (File.Exists(restoredPath))
            {
                File.Delete(restoredPath);
            }

            throw;
        }
    }

    /// <summary>
    /// The monthly restore check: validates the newest backup on this computer (a dry run, nothing is scheduled) and records the
    /// result. A failure tells the administrators.
    /// </summary>
    public async Task RunRestoreCheckAsync(CancellationToken ct)
    {
        var settings = await settingsAccessor.LoadAsync(ct).ConfigureAwait(false);
        var newest = backups.List().FirstOrDefault();
        if (newest is null)
        {
            return;
        }

        string result;
        try
        {
            var check = backups.ValidateRestore(newest.FileName);
            result = check.Valid ? "Passed" : $"Failed: {check.Message}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The restore check of {Backup} failed.", newest.FileName);
            result = "Failed: the newest backup could not be opened and checked.";
        }

        var checkedAt = clock.GetUtcNow();
        await settingsAccessor.UpdateAsync(document =>
        {
            document.LastRestoreCheckAt = checkedAt;
            document.LastRestoreCheckResult = result;
        }, ct).ConfigureAwait(false);
        if (result.StartsWith("Failed", StringComparison.Ordinal))
        {
            notifications.Publish(new NotificationRequest(NotificationEventIds.BackupFailed, "restore-check", $"The monthly restore check did not pass. {result}"));
        }
    }

    private async Task SendAndVerifyAsync(IBackupDestination destination, string filePath, string remoteName, CancellationToken ct)
    {
        var info = new FileInfo(filePath);
        string sha256;
        await using (var hashSource = File.OpenRead(filePath))
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashSource, ct).ConfigureAwait(false));
        }

        await using (var content = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
        {
            await destination.SendAsync(remoteName, content, info.Length, ct).ConfigureAwait(false);
        }

        // Read it back: the size it reports, then every byte, so a copy that arrived damaged is caught now and not at restore time.
        var held = (await destination.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(item => item.Name == remoteName)
            ?? throw new BackupDestinationException("The copy was sent but the destination does not list it.");
        if (held.SizeBytes != info.Length)
        {
            throw new BackupDestinationException("The copy arrived at a different size than it was sent.");
        }

        await using (var fetched = await destination.FetchAsync(remoteName, ct).ConfigureAwait(false))
        {
            var remoteHash = Convert.ToHexString(await SHA256.HashDataAsync(fetched, ct).ConfigureAwait(false));
            if (!string.Equals(remoteHash, sha256, StringComparison.Ordinal))
            {
                throw new BackupDestinationException("The copy arrived damaged. It does not match what was sent.");
            }
        }

        var now = clock.GetUtcNow();
        await repository.AddCopyAsync(new StoredBackupCopy(Guid.NewGuid(), destination.Id, remoteName, info.Length, sha256, "verified", now, null, now), ct).ConfigureAwait(false);
    }

    private async Task ApplyRetentionAsync(IBackupDestination destination, int keep, CancellationToken ct)
    {
        try
        {
            var ours = (await destination.ListAsync(ct).ConfigureAwait(false))
                .Where(item => item.Name.StartsWith(NamePrefix, StringComparison.Ordinal)
                    && !item.Name.StartsWith(NamePrefix + "restored-", StringComparison.Ordinal)
                    && item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.ModifiedAt)
                .ThenByDescending(item => item.Name, StringComparer.Ordinal)
                .Skip(Math.Max(1, keep))
                .ToList();
            foreach (var old in ours)
            {
                await destination.DeleteAsync(old.Name, ct).ConfigureAwait(false);
                await repository.MarkDeletedAsync(destination.Id, old.Name, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Failing to tidy up old copies never fails the backup that just succeeded.
            logger.LogWarning(ex, "Removing old backups at {Destination} failed.", destination.Id);
        }
    }
}
