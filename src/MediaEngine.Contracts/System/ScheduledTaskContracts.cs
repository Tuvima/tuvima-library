using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.System;

/// <summary>Every recurring background task with its schedule, last run and next run (<c>GET /system/scheduled-tasks</c>).</summary>
public sealed class ScheduledTaskListResponse
{
    [JsonPropertyName("tasks")]
    public List<ScheduledTaskDto> Tasks { get; set; } = [];
}

public sealed class ScheduledTaskDto
{
    /// <summary>Stable snake_case key, for example <c>vibe_tags</c>.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Plain-English name, for example "Vibe tags".</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Who owns the task: "Library", "Local AI" or "Plugin".</summary>
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; set; }

    /// <summary>The cron expression currently in force, or plain text such as "every 5 seconds" for continuous work.</summary>
    [JsonPropertyName("schedule")]
    public string Schedule { get; set; } = string.Empty;

    /// <summary>True for work that runs all the time rather than on a schedule (webhook delivery).</summary>
    [JsonPropertyName("is_continuous")]
    public bool IsContinuous { get; set; }

    /// <summary>Plain-text pointer to where the schedule is set, for example "Local AI &gt; Automation".</summary>
    [JsonPropertyName("settings_place")]
    public string SettingsPlace { get; set; } = string.Empty;

    /// <summary>Next scheduled start, UTC. Null for continuous work or when the schedule cannot be read.</summary>
    [JsonPropertyName("next_run_at")]
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>The most recent run, or null when the task has never run.</summary>
    [JsonPropertyName("last_run")]
    public ScheduledTaskLastRunDto? LastRun { get; set; }
}

public sealed class ScheduledTaskLastRunDto
{
    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary><c>running</c>, <c>succeeded</c>, <c>failed</c>, <c>skipped</c> or <c>interrupted</c>.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("items_processed")]
    public int? ItemsProcessed { get; set; }

    /// <summary>Plain-English result, failure message or skip reason.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
