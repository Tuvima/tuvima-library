using System.Globalization;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Contracts.Backups;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// Backup schedule, retention, destination choices and the backup passphrase (Developer tools spec WP25). The passphrase is only
/// ever used to make the key pair and to prove a change is allowed; it is never stored or logged.
/// </summary>
internal sealed class BackupSettingsManager(
    BackupSettingsAccessor accessor,
    BackupOrchestrator orchestrator,
    IBackupDestinationRepository repository,
    TimeProvider clock)
{
    public async Task<BackupSettingsResponse> GetAsync(CancellationToken ct)
    {
        var settings = await accessor.LoadAsync(ct).ConfigureAwait(false);
        var copies = await repository.ListCopiesAsync(null, 200, ct).ConfigureAwait(false);
        var response = new BackupSettingsResponse
        {
            Schedule = new BackupScheduleDto
            {
                Enabled = settings.ScheduleEnabled,
                Frequency = settings.Frequency,
                Time = settings.Time,
                Weekday = settings.Weekday,
            },
            KeepLast = settings.KeepLast,
            PassphraseSet = settings.PassphraseSet,
            NextRunAt = NextRun(settings),
            LastRestoreCheckAt = settings.LastRestoreCheckAt,
            LastRestoreCheckResult = settings.LastRestoreCheckResult,
        };
        foreach (var destination in orchestrator.Destinations)
        {
            var latest = copies.FirstOrDefault(copy => copy.DestinationId == destination.Id && copy.State != "deleted");
            var enabled = !destination.LeavesMachine || settings.EnabledDestinations.Contains(destination.Id, StringComparer.Ordinal);
            var dto = new BackupDestinationDto
            {
                Id = destination.Id,
                Name = destination.Name,
                LeavesMachine = destination.LeavesMachine,
                IsConfigured = destination.IsConfigured,
                Enabled = enabled,
                LastCopyAt = latest?.CreatedAt,
                LastCopyVerified = latest?.State == "verified",
            };
            (dto.State, dto.StateLabel) = StateOf(destination, enabled, latest);
            response.Destinations.Add(dto);
        }

        return response;
    }

    /// <summary>Saves changes. Returns a plain-words problem, or null when everything was saved.</summary>
    public async Task<string?> UpdateAsync(UpdateBackupSettingsRequest request, CancellationToken ct)
    {
        if (request.Schedule is { } schedule)
        {
            if (schedule.Frequency is not (BackupFrequencies.Daily or BackupFrequencies.Weekly))
            {
                return "Choose daily or weekly.";
            }

            if (!TimeOnly.TryParseExact(schedule.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return "The backup time needs to look like 02:00.";
            }

            if (schedule.Weekday is < 0 or > 6)
            {
                return "Choose a day of the week.";
            }
        }

        if (request.KeepLast is { } keep && keep is < 1 or > 365)
        {
            return "Keep between 1 and 365 backups.";
        }

        if (request.EnabledDestinations is { } enabledIds)
        {
            foreach (var id in enabledIds)
            {
                var destination = orchestrator.Find(id);
                if (destination is null || !destination.LeavesMachine)
                {
                    return "One of those destinations does not exist.";
                }
            }
        }

        await accessor.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = await accessor.LoadAsync(ct).ConfigureAwait(false);
            if (request.Schedule is { } wanted)
            {
                if (wanted.Enabled && !settings.ScheduleEnabled)
                {
                    // Turning the schedule on starts the clock now; it does not run straight away.
                    settings.LastScheduledRunAt = clock.GetUtcNow();
                }

                settings.ScheduleEnabled = wanted.Enabled;
                settings.Frequency = wanted.Frequency;
                settings.Time = wanted.Time;
                settings.Weekday = wanted.Weekday;
            }

            if (request.KeepLast is { } keepLast)
            {
                settings.KeepLast = keepLast;
            }

            if (request.EnabledDestinations is { } ids)
            {
                settings.EnabledDestinations = [.. ids.Distinct(StringComparer.Ordinal)];
            }

            await accessor.SaveAsync(settings, ct).ConfigureAwait(false);
            return null;
        }
        finally
        {
            accessor.Gate.Release();
        }
    }

    /// <summary>Sets or changes the passphrase. Returns a plain-words problem, or null on success.</summary>
    public async Task<string?> SetPassphraseAsync(SetBackupPassphraseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Passphrase) || request.Passphrase.Length < BackupEncryption.MinimumPassphraseLength)
        {
            return $"The passphrase needs at least {BackupEncryption.MinimumPassphraseLength} characters.";
        }

        await accessor.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = await accessor.LoadAsync(ct).ConfigureAwait(false);
            if (settings.Keys is { } existing
                && (string.IsNullOrEmpty(request.CurrentPassphrase) || !BackupEncryption.CheckPassphrase(request.CurrentPassphrase, existing.EncryptedPrivateKey)))
            {
                return "The current passphrase is not right. Enter it to change the passphrase.";
            }

            var material = await Task.Run(() => BackupEncryption.CreateKeyMaterial(request.Passphrase), ct).ConfigureAwait(false);
            settings.PublicKey = Convert.ToBase64String(material.PublicKey);
            settings.EncryptedPrivateKey = Convert.ToBase64String(material.EncryptedPrivateKey);
            await accessor.SaveAsync(settings, ct).ConfigureAwait(false);
            return null;
        }
        finally
        {
            accessor.Gate.Release();
        }
    }

    private DateTimeOffset? NextRun(BackupSettingsDocument settings)
    {
        var cron = settings.Cron();
        return string.IsNullOrEmpty(cron)
            ? null
            : CronScheduler.NextOccurrence(cron, settings.LastScheduledRunAt ?? clock.GetUtcNow());
    }

    private static (string State, string Label) StateOf(IBackupDestination destination, bool enabled, StoredBackupCopy? latest)
    {
        if (!destination.IsConfigured)
        {
            return ("off", "Not set up");
        }

        if (!enabled)
        {
            return ("off", "Off");
        }

        if (latest is null)
        {
            return ("ok", destination.LeavesMachine ? "On. Nothing copied yet." : "Ready");
        }

        return latest.State switch
        {
            "failed" => ("needs_attention", $"Needs attention: {latest.LastError}"),
            "verified" => ("ok", "Working. The last copy was checked."),
            _ => ("ok", "Working"),
        };
    }
}
