using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>What one person did with a plugin or source.</summary>
/// <param name="Action">A stable word: <c>chose-lyrics</c>, <c>marked-subtitles-wrong</c>, <c>looked-up-lyrics</c>, <c>added-lyrics-file</c>, <c>linked-account</c>, <c>switched-plugin-off</c>.</param>
public sealed record PluginPeopleAction(
    Guid? ProfileId,
    string PluginId,
    string Action,
    string Summary,
    Guid? ItemId = null,
    string? Before = null,
    string? After = null,
    string? Device = null);

/// <summary>
/// The People-action log (Plugin architecture spec C25). Whenever a person does something with a plugin or source that changes shared
/// Library data, one standard record goes to the existing Activity ledger: who, what, which item, which plugin, a short before and
/// after, the device and the time. It records only changes to shared data, never what someone watches, reads or listens to. Shown to
/// administrators under "People's actions", kept for <c>plugin_people_action_retention_days</c>.
/// </summary>
public sealed class PluginPeopleActionLog(ISystemActivityRepository activity, ILogger<PluginPeopleActionLog> logger)
{
    private sealed record Changes(
        [property: JsonPropertyName("plugin")] string Plugin,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("before")] string? Before,
        [property: JsonPropertyName("after")] string? After,
        [property: JsonPropertyName("device")] string? Device);

    public async Task RecordAsync(PluginPeopleAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await activity.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PluginPeopleAction,
                CollectionName = action.PluginId,
                EntityId = action.ItemId,
                ProfileId = action.ProfileId,
                Detail = action.Summary,
                ChangesJson = JsonSerializer.Serialize(new Changes(action.PluginId, action.Action, Trim(action.Before), Trim(action.After), Trim(action.Device))),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The log must never be the reason a person's action fails; the Engine log keeps the failure.
            logger.LogWarning(ex, "Could not record the people action {Action} for plugin {PluginId}", action.Action, action.PluginId);
        }
    }

    /// <summary>The newest records, optionally for one plugin, one person or one kind of action.</summary>
    public async Task<PluginPeopleActionsResponse> GetAsync(string? pluginId, Guid? profileId, string? action, int limit, CancellationToken ct = default)
    {
        var rows = await activity.GetRecentByTypesAsync([SystemActionType.PluginPeopleAction], Math.Clamp(limit, 1, 500) * 4, ct).ConfigureAwait(false);
        var result = new PluginPeopleActionsResponse();
        foreach (var row in rows)
        {
            Changes? changes = null;
            try
            {
                changes = row.ChangesJson is null ? null : JsonSerializer.Deserialize<Changes>(row.ChangesJson);
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Skipped a people action record that could not be read");
            }

            if (changes is null
                || (pluginId is not null && !string.Equals(changes.Plugin, pluginId, StringComparison.OrdinalIgnoreCase))
                || (profileId is not null && row.ProfileId != profileId)
                || (action is not null && !string.Equals(changes.Action, action, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Actions.Add(new PluginPeopleActionDto
            {
                OccurredAt = row.OccurredAt,
                ProfileId = row.ProfileId,
                PluginId = changes.Plugin,
                Action = changes.Action,
                Summary = row.Detail ?? "",
                ItemId = row.EntityId,
                Before = changes.Before,
                After = changes.After,
                Device = changes.Device,
            });
            if (result.Actions.Count >= limit)
            {
                break;
            }
        }

        return result;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is { Length: > 200 } text ? text[..200] : value.Trim();
}
