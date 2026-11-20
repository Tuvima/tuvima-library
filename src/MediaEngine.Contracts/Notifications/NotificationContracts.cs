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

    /// <summary>A calm kind of event the daily summary may batch into one message.</summary>
    [JsonPropertyName("quiet")]
    public bool Quiet { get; set; }
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

    /// <summary>The channel type: <c>dashboard</c>, <c>email</c>, <c>plugin</c> or the id of a type added from Settings (<c>discord</c>, <c>ntfy</c>).</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>One plain line on what leaves the computer through this channel and who can read it.</summary>
    [JsonPropertyName("what_leaves")]
    public string WhatLeaves { get; set; } = "";

    /// <summary>Whether messages through this channel carry details. Off by default; a short generic title leaves otherwise.</summary>
    [JsonPropertyName("include_details")]
    public bool IncludeDetails { get; set; }

    /// <summary>True for a channel added from Settings, which can be edited and removed there.</summary>
    [JsonPropertyName("can_edit")]
    public bool CanEdit { get; set; }
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

/// <summary>The optional daily summary: quiet events batched into one message at a time the person picks.</summary>
public sealed class NotificationDailySummaryDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Local time as HH:mm.</summary>
    [JsonPropertyName("time")]
    public string Time { get; set; } = "08:00";
}

public sealed class NotificationSettingsResponse
{
    [JsonPropertyName("events")]
    public List<NotificationEventDto> Events { get; set; } = [];

    [JsonPropertyName("channels")]
    public List<NotificationChannelDto> Channels { get; set; } = [];

    [JsonPropertyName("quiet_hours")]
    public NotificationQuietHoursDto QuietHours { get; set; } = new();

    /// <summary>When off (the default), events offered to webhooks and plugins carry only a short generic title. Each channel has its own switch.</summary>
    [JsonPropertyName("include_details")]
    public bool IncludeDetails { get; set; }

    [JsonPropertyName("daily_summary")]
    public NotificationDailySummaryDto DailySummary { get; set; } = new();
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

    [JsonPropertyName("daily_summary")]
    public NotificationDailySummaryDto? DailySummary { get; set; }

    /// <summary>Channel id to whether that channel carries details. Channels left out keep their choice.</summary>
    [JsonPropertyName("channel_details")]
    public Dictionary<string, bool> ChannelDetails { get; set; } = new(StringComparer.Ordinal);
}

public sealed class NotificationTestResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>One input in a channel type's setup form.</summary>
public sealed class NotificationChannelFieldDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("help")]
    public string Help { get; set; } = "";

    /// <summary><c>text</c>, <c>url</c> or <c>token</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "text";

    /// <summary>Written once and never shown again.</summary>
    [JsonPropertyName("secret")]
    public bool Secret { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("placeholder")]
    public string Placeholder { get; set; } = "";

    [JsonPropertyName("default")]
    public string Default { get; set; } = "";

    /// <summary>The form offers a long random value (a private topic).</summary>
    [JsonPropertyName("suggest_random")]
    public bool SuggestRandom { get; set; }
}

/// <summary>A kind of channel a person can add, with the plain words the "Add a channel" tile and form show.</summary>
public sealed class NotificationChannelTypeDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Shown under Advanced rather than as a main tile.</summary>
    [JsonPropertyName("advanced")]
    public bool Advanced { get; set; }

    [JsonPropertyName("cost")]
    public string Cost { get; set; } = "";

    [JsonPropertyName("effort")]
    public string Effort { get; set; } = "";

    /// <summary>The tile's one line: what it costs and what it needs.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("help")]
    public string Help { get; set; } = "";

    [JsonPropertyName("what_leaves")]
    public string WhatLeaves { get; set; } = "";

    [JsonPropertyName("fields")]
    public List<NotificationChannelFieldDto> Fields { get; set; } = [];
}

public sealed class NotificationChannelTypesResponse
{
    [JsonPropertyName("types")]
    public List<NotificationChannelTypeDto> Types { get; set; } = [];
}

/// <summary>
/// Creates or changes a channel. <see cref="Values"/> holds each form field by key; on a change, a secret field left out or null
/// keeps its saved value.
/// </summary>
public sealed class SaveNotificationChannelRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("values")]
    public Dictionary<string, string?> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A channel added from Settings, for the edit form. Secret values are never returned, only which are set.</summary>
public sealed class NotificationChannelDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("secrets_set")]
    public List<string> SecretsSet { get; set; } = [];
}
