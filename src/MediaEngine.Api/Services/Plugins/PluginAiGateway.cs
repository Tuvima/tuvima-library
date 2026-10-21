using System.Text.Json;
using MediaEngine.AI.EngineRoom;
using MediaEngine.Domain.Enums;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>How a plugin's Local AI request ended, in words a plugin (or a screen) can show as they are.</summary>
internal enum PluginAiStatus
{
    Succeeded,

    /// <summary>The plugin has no right to this ability: not approved, switched off, or the role was never declared.</summary>
    Refused,

    /// <summary>Local AI cannot serve the request right now (off, no model, still loading). The plugin should carry on without it.</summary>
    AiUnavailable,

    /// <summary>The machine is busy with the person's own work. Try again later.</summary>
    Busy,

    /// <summary>The plugin used up its Local AI allowance for the hour.</summary>
    OverAllowance,

    TimedOut,

    /// <summary>The model answered, but not with the JSON that was asked for.</summary>
    InvalidAnswer,

    Failed,
}

internal sealed record PluginAiGatewayResult(PluginAiStatus Status, string Message, string? Text = null)
{
    public bool IsSuccess => Status == PluginAiStatus.Succeeded;
}

/// <summary>
/// The one door a plugin that is not C# code goes through to reach Local AI (developer-tools WP10). JSON-only plugins use it
/// in the server; the separate-program host puts a plugin-scoped key in front of the same calls. It checks the plugin's
/// declared AI permission first, then asks the engine room as that plugin, so the plugin queues behind the Library's own work
/// and keeps to its hourly allowance. A refusal or an unavailable engine comes back as a status, never as a thrown error:
/// a plugin must always be able to carry on without AI.
/// </summary>
internal sealed class PluginAiGateway(IPluginPermissionGate permissions, IAiEngineRoom engineRoom)
{
    public async Task<PluginAiGatewayResult> InferTextAsync(
        string pluginId,
        string role,
        string prompt,
        PluginAiOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (Authorise(pluginId, role, options) is not { } ability)
        {
            return Refusal(pluginId, role, options);
        }

        var result = await engineRoom
            .WriteTextAsync(AiCaller.Plugin(pluginId), ability, prompt, grammar: null, ToRequestOptions(options), cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome.IsSuccess && result.Text is not null
            ? new PluginAiGatewayResult(PluginAiStatus.Succeeded, result.Outcome.Message, result.Text)
            : FromOutcome(result.Outcome);
    }

    /// <summary>Asks for JSON under a grammar. The answer comes back as JSON text, checked to be valid JSON.</summary>
    public async Task<PluginAiGatewayResult> InferJsonAsync(
        string pluginId,
        string role,
        string prompt,
        string grammar,
        PluginAiOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (Authorise(pluginId, role, options) is not { } ability)
        {
            return Refusal(pluginId, role, options);
        }

        var result = await engineRoom
            .WriteTextAsync(AiCaller.Plugin(pluginId), ability, prompt, grammar, ToRequestOptions(options), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Outcome.IsSuccess || result.Text is null)
        {
            return FromOutcome(result.Outcome);
        }

        return IsJson(result.Text)
            ? new PluginAiGatewayResult(PluginAiStatus.Succeeded, result.Outcome.Message, result.Text)
            : new PluginAiGatewayResult(PluginAiStatus.InvalidAnswer, "Local AI answered, but not in the format the plugin asked for.");
    }

    /// <summary>Whether the plugin could use this role right now, so it can choose its fallback before asking.</summary>
    public async Task<PluginAiGatewayResult> CheckAvailabilityAsync(
        string pluginId,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (Authorise(pluginId, role, options: null) is not { } ability)
        {
            return Refusal(pluginId, role, options: null);
        }

        var availability = await engineRoom.GetAvailabilityAsync(ability, cancellationToken).ConfigureAwait(false);
        return availability.IsAvailable
            ? new PluginAiGatewayResult(PluginAiStatus.Succeeded, "Local AI is ready for this plugin.")
            : new PluginAiGatewayResult(PluginAiStatus.AiUnavailable, availability.Reason ?? "Local AI is not available right now.");
    }

    private AiAbility? Authorise(string pluginId, string role, PluginAiOptions? options)
    {
        try
        {
            permissions.DemandAi(pluginId, role, options);
            return PluginAiClient.ResolveAbility(role);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentOutOfRangeException or NotSupportedException)
        {
            return null;
        }
    }

    private PluginAiGatewayResult Refusal(string pluginId, string role, PluginAiOptions? options)
    {
        try
        {
            permissions.DemandAi(pluginId, role, options);
            PluginAiClient.ResolveAbility(role);
            return new PluginAiGatewayResult(PluginAiStatus.Refused, "This plugin may not use Local AI for that.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return new PluginAiGatewayResult(PluginAiStatus.Refused, exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or NotSupportedException)
        {
            return new PluginAiGatewayResult(PluginAiStatus.Refused, $"Local AI has no ability called '{role}' for plugins.");
        }
    }

    private static PluginAiGatewayResult FromOutcome(AiRequestOutcome outcome)
    {
        if (outcome.InvalidResponse)
        {
            return new PluginAiGatewayResult(PluginAiStatus.InvalidAnswer, outcome.Message);
        }

        var status = outcome.Status switch
        {
            AiRequestStatus.Unavailable => PluginAiStatus.AiUnavailable,
            AiRequestStatus.RefusedOverBudget => PluginAiStatus.OverAllowance,
            AiRequestStatus.Deferred or AiRequestStatus.Cancelled => PluginAiStatus.Busy,
            AiRequestStatus.TimedOut => PluginAiStatus.TimedOut,
            _ => PluginAiStatus.Failed,
        };
        return new PluginAiGatewayResult(status, outcome.Message);
    }

    private static AiRequestOptions? ToRequestOptions(PluginAiOptions? options) =>
        options?.MaxTokens is null ? null : new AiRequestOptions(MaxTokens: options.MaxTokens);

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
