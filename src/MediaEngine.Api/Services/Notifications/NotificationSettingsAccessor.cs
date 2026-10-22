using System.Globalization;
using System.Text.Json;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>The saved routing, quiet hours and privacy choice.</summary>
internal sealed class NotificationSettingsDocument
{
    /// <summary>Event id to channel ids. An event with no entry uses its catalogue default; an empty list means nowhere.</summary>
    public Dictionary<string, List<string>> Routes { get; set; } = new(StringComparer.Ordinal);

    public bool QuietHoursEnabled { get; set; }

    public string QuietStart { get; set; } = "22:00";

    public string QuietEnd { get; set; } = "07:00";

    /// <summary>False by default: only a short generic title leaves the computer.</summary>
    public bool IncludeDetails { get; set; }

    public IReadOnlyList<string> RouteFor(NotificationEventDefinition definition) =>
        Routes.TryGetValue(definition.Id, out var channels) ? channels : definition.DefaultChannels;

    public bool IsQuiet(DateTimeOffset localNow)
    {
        if (!QuietHoursEnabled
            || !TimeOnly.TryParseExact(QuietStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(QuietEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || start == end)
        {
            return false;
        }

        var now = TimeOnly.FromTimeSpan(localNow.TimeOfDay);
        return start < end ? now >= start && now < end : now >= start || now < end;
    }
}

/// <summary>Reads and writes <see cref="NotificationSettingsDocument"/> in the data store.</summary>
internal sealed class NotificationSettingsAccessor(INotificationRepository repository, TimeProvider clock)
{
    private const string Key = "settings";

    public async Task<NotificationSettingsDocument> LoadAsync(CancellationToken ct)
    {
        var json = await repository.GetSettingAsync(Key, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new NotificationSettingsDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<NotificationSettingsDocument>(json, MediaEngineJson.Web) ?? new NotificationSettingsDocument();
        }
        catch (JsonException)
        {
            // A damaged row falls back to the defaults rather than silencing every notification.
            return new NotificationSettingsDocument();
        }
    }

    public Task SaveAsync(NotificationSettingsDocument document, CancellationToken ct) =>
        repository.SetSettingAsync(Key, JsonSerializer.Serialize(document, MediaEngineJson.Web), clock.GetUtcNow(), ct);
}
