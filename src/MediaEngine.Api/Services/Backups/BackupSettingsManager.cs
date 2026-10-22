using System.Globalization;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// Backup schedule, retention and destination choices (Developer tools spec WP25). Backups are never encrypted (Backup and restore
/// spec D15); a destination's own login is stored through the secret store.
/// </summary>
internal sealed class BackupSettingsManager(
    BackupSettingsAccessor accessor,
    BackupOrchestrator orchestrator,
    IBackupDestinationRepository repository,
    ISecretStore secrets,
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
            NextRunAt = NextRun(settings),
            LastRestoreCheckAt = settings.LastRestoreCheckAt,
            LastRestoreCheckResult = settings.LastRestoreCheckResult,
        };
        foreach (var destination in orchestrator.Destinations)
        {
            var latest = copies.FirstOrDefault(copy => copy.DestinationId == destination.Id && copy.State != "deleted");
            var added = settings.Destinations.FirstOrDefault(item => item.Id == destination.Id);
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
                Kind = added?.Kind ?? BackupDestinationIds.Local,
                Location = added?.Location ?? string.Empty,
                Removable = added is not null,
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

    /// <summary>
    /// Adds a destination, or changes one. A web destination is only saved when the host the person confirmed matches its address,
    /// so the address was in front of them first. Returns a plain-words problem, or null when it was saved.
    /// </summary>
    public async Task<string?> SaveDestinationAsync(SaveBackupDestinationRequest request, CancellationToken ct)
    {
        if (!BackupDestinationKinds.IsKnown(request.Kind))
        {
            return "Choose what kind of destination this is.";
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > BackupDestinationRules.MaxNameLength || name.Any(char.IsControl))
        {
            return $"Give it a name of up to {BackupDestinationRules.MaxNameLength} characters.";
        }

        var location = request.Location?.Trim() ?? string.Empty;
        string? problem;
        if (BackupDestinationKinds.IsWeb(request.Kind))
        {
            var uri = BackupDestinationRules.ParseWebAddress(location, out problem);
            if (uri is null)
            {
                return problem;
            }

            if (!string.Equals(request.ConfirmedHost?.Trim(), uri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return $"Confirm that backups may be sent to {uri.Host}.";
            }

            if (request.Kind == BackupDestinationKinds.S3 && string.IsNullOrWhiteSpace(request.Bucket))
            {
                return "Enter the bucket name.";
            }

            if (request.Prefix?.Split('/').Any(part => part is ".." or ".") == true || request.Prefix?.Contains("//", StringComparison.Ordinal) == true)
            {
                return "The folder inside the bucket cannot contain \"..\", \".\" or empty parts.";
            }

            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return request.Kind == BackupDestinationKinds.S3 ? "Enter the access key." : "Enter the user name.";
            }
        }
        else if ((problem = BackupDestinationRules.FolderProblem(location)) is not null)
        {
            return problem;
        }

        await accessor.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = await accessor.LoadAsync(ct).ConfigureAwait(false);
            var existing = request.Id is null ? null : settings.Destinations.FirstOrDefault(item => item.Id == request.Id);
            if (request.Id is not null && existing is null)
            {
                return "That destination was not found.";
            }

            if (BackupDestinationKinds.IsWeb(request.Kind) && string.IsNullOrEmpty(request.Secret) && string.IsNullOrEmpty(existing?.EncryptedSecret))
            {
                return request.Kind == BackupDestinationKinds.S3 ? "Enter the secret key." : "Enter the password.";
            }

            if (existing is null)
            {
                existing = new ConfiguredDestination { Id = $"{request.Kind}-{Guid.NewGuid():N}"[..(request.Kind.Length + 9)], Kind = request.Kind };
                settings.Destinations.Add(existing);
                settings.EnabledDestinations.Add(existing.Id);
            }
            else if (existing.Kind != request.Kind)
            {
                return "A destination's kind cannot be changed. Add a new one instead.";
            }
            else if (BackupDestinationKinds.IsWeb(request.Kind) && string.IsNullOrEmpty(request.Secret)
                && (!SameServer(existing.Location, location) || !string.Equals(existing.Username, request.Username?.Trim(), StringComparison.Ordinal)))
            {
                // A saved login only ever goes to the server and user it was entered for.
                return request.Kind == BackupDestinationKinds.S3
                    ? "Enter the secret key again to change the address or access key."
                    : "Enter the password again to change the address or user name.";
            }

            existing.Name = name;
            existing.Location = location;
            existing.Bucket = request.Bucket?.Trim();
            existing.Region = request.Region?.Trim();
            existing.Prefix = request.Prefix?.Trim();
            existing.Username = request.Username?.Trim();
            if (!string.IsNullOrEmpty(request.Secret))
            {
                existing.EncryptedSecret = secrets.Encrypt(request.Secret);
            }

            await accessor.SaveAsync(settings, ct).ConfigureAwait(false);
            return null;
        }
        finally
        {
            accessor.Gate.Release();
        }
    }

    private static bool SameServer(string saved, string wanted) =>
        Uri.TryCreate(saved, UriKind.Absolute, out var a) && Uri.TryCreate(wanted, UriKind.Absolute, out var b)
        && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    /// <summary>Removes a destination the administrator added. Copies already there are left alone. Returns false when it does not exist.</summary>
    public async Task<bool> RemoveDestinationAsync(string id, CancellationToken ct)
    {
        var removed = false;
        await accessor.UpdateAsync(settings =>
        {
            removed = settings.Destinations.RemoveAll(item => item.Id == id) > 0;
            settings.EnabledDestinations.RemoveAll(item => item == id);
        }, ct).ConfigureAwait(false);
        return removed;
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
