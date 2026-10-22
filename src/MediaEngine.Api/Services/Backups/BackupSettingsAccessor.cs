using System.Globalization;
using System.Text.Json;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Backups;

/// <summary>The saved backup schedule, retention and destination choices.</summary>
internal sealed class BackupSettingsDocument
{
    public bool ScheduleEnabled { get; set; }

    public string Frequency { get; set; } = BackupFrequencies.Daily;

    public string Time { get; set; } = "02:00";

    public int Weekday { get; set; }

    public int KeepLast { get; set; } = 10;

    /// <summary>Destination ids besides the local folder (which always receives the backup).</summary>
    public List<string> EnabledDestinations { get; set; } = [];

    /// <summary>Destinations an administrator added: another folder, a network share, S3-compatible storage or WebDAV (WP26).</summary>
    public List<ConfiguredDestination> Destinations { get; set; } = [];

    public DateTimeOffset? LastScheduledRunAt { get; set; }

    public DateTimeOffset? LastRestoreCheckAt { get; set; }

    public string? LastRestoreCheckResult { get; set; }

    /// <summary>The schedule as a cron expression, or an empty string when it is off or the time is not valid.</summary>
    public string Cron()
    {
        if (!ScheduleEnabled || !TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return string.Empty;
        }

        var day = Frequency == BackupFrequencies.Weekly ? Math.Clamp(Weekday, 0, 6).ToString(CultureInfo.InvariantCulture) : "*";
        return string.Create(CultureInfo.InvariantCulture, $"{time.Minute} {time.Hour} * * {day}");
    }
}

/// <summary>Reads and writes <see cref="BackupSettingsDocument"/> in the data store.</summary>
internal sealed class BackupSettingsAccessor(IBackupDestinationRepository repository, TimeProvider clock)
{
    private const string Key = "settings";

    /// <summary>Held while settings are changed, so two changes at once (a save, a schedule tick, a destination change) never overwrite each other.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Loads, changes and saves the settings as one step.</summary>
    public async Task UpdateAsync(Action<BackupSettingsDocument> change, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(ct).ConfigureAwait(false);
            change(document);
            await SaveAsync(document, ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<BackupSettingsDocument> LoadAsync(CancellationToken ct)
    {
        var json = await repository.GetSettingAsync(Key, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BackupSettingsDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<BackupSettingsDocument>(json, MediaEngineJson.Web) ?? new BackupSettingsDocument();
        }
        catch (JsonException)
        {
            return new BackupSettingsDocument();
        }
    }

    /// <summary>The same as <see cref="LoadAsync"/> for callers that cannot await (the Scheduled tasks list). The data store call completes synchronously.</summary>
    public BackupSettingsDocument Load() => LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public Task SaveAsync(BackupSettingsDocument document, CancellationToken ct) =>
        repository.SetSettingAsync(Key, JsonSerializer.Serialize(document, MediaEngineJson.Web), clock.GetUtcNow(), ct);
}
