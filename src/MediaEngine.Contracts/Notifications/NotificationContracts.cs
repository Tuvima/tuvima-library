using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Notifications;

/// <summary>The built-in channels. A channel plugin uses its own id.</summary>
public static class NotificationChannelIds
{
    public const string Dashboard = "dashboard";
    public const string Email = "email";
}

/// <summary>How a channel is doing.</summary>
public static class NotificationChannelStates
{
    public const string Healthy = "healthy";
    public const string NeedsAttention = "needs_attention";
    public const string Off = "off";
}

/// <summary>One message in the Dashboard inbox (Developer tools spec WP23).</summary>
public sealed class NotificationItemDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    /// <summary>How many times it happened in a short window. Repeats are folded into one message.</summary>
    [JsonPropertyName("occurrence_count")]
    public int OccurrenceCount { get; set; } = 1;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("is_read")]
    public bool IsRead { get; set; }
}

public sealed class NotificationListResponse
{
    [JsonPropertyName("items")]
    public List<NotificationItemDto> Items { get; set; } = [];

    [JsonPropertyName("unread_count")]
    public int UnreadCount { get; set; }
}

/// <summary>Messages to mark read. An empty list marks every unread message read.</summary>
public sealed class MarkNotificationsReadRequest
{
    [JsonPropertyName("ids")]
    public List<Guid> Ids { get; set; } = [];
}

/// <summary>Where one kind of event goes, with the plain words the settings page shows for it.</summary>
public sealed class NotificationEventDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>The channel ids this event goes to (<c>dashboard</c>, <c>email</c>, a channel plugin id).</summary>
    [JsonPropertyName("channels")]
    public List<string> Channels { get; set; } = [];
}

/// <summary>A way a message can leave the Library, and how it is doing: <c>healthy</c>, <c>needs_attention</c> or <c>off</c>.</summary>
public sealed class NotificationChannelDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>True when messages leave this computer by this channel (the Dashboard inbox does not).</summary>
    [JsonPropertyName("is_external")]
    public bool IsExternal { get; set; }

    [JsonPropertyName("is_configured")]
    public bool IsConfigured { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "healthy";

    /// <summary>One plain line for the state ("Working", "Needs attention: the mail server could not be reached", "Switched off after repeated failures").</summary>
    [JsonPropertyName("state_label")]
    public string StateLabel { get; set; } = "";

    [JsonPropertyName("last_success_at")]
    public DateTimeOffset? LastSuccessAt { get; set; }
}

public sealed class NotificationQuietHoursDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Local time as HH:mm.</summary>
    [JsonPropertyName("start")]
    public string Start { get; set; } = "22:00";

    [JsonPropertyName("end")]
    public string End { get; set; } = "07:00";
}

public sealed class NotificationSettingsResponse
{
    [JsonPropertyName("events")]
    public List<NotificationEventDto> Events { get; set; } = [];

    [JsonPropertyName("channels")]
    public List<NotificationChannelDto> Channels { get; set; } = [];

    [JsonPropertyName("quiet_hours")]
    public NotificationQuietHoursDto QuietHours { get; set; } = new();

    /// <summary>When off (the default), messages that leave this computer carry only a short generic title.</summary>
    [JsonPropertyName("include_details")]
    public bool IncludeDetails { get; set; }
}

public sealed class UpdateNotificationSettingsRequest
{
    /// <summary>Event id to the channel ids it should go to. Events left out keep their current routes.</summary>
    [JsonPropertyName("routes")]
    public Dictionary<string, List<string>> Routes { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("quiet_hours")]
    public NotificationQuietHoursDto? QuietHours { get; set; }

    [JsonPropertyName("include_details")]
    public bool? IncludeDetails { get; set; }
}

public sealed class NotificationTestResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
