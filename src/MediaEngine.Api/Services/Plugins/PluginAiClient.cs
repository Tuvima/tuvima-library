using MediaEngine.AI.EngineRoom;
using MediaEngine.Domain.Enums;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Sends a plugin's AI requests to the Local AI engine room as that plugin. Today's manifest role words are mapped to
/// abilities here; the engine room decides which engine serves them, queues the request behind the Library's own work and
/// holds the plugin to its hourly allowance.
/// </summary>
internal sealed class PluginAiClient
{
    private readonly IAiEngineRoom _engineRoom;

    public PluginAiClient(IAiEngineRoom engineRoom)
    {
        _engineRoom = engineRoom;
    }

    public async Task<string?> InferTextAsync(
        string pluginId,
        string role,
        string prompt,
        int? maxTokens = null,
        CancellationToken cancellationToken = default)
    {
        var ability = ResolveAbility(role);
        return await _engineRoom
            .WriteTextOrThrowAsync(AiCaller.Plugin(pluginId), ability, prompt, grammar: null, ToOptions(maxTokens), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<T?> InferJsonAsync<T>(
        string pluginId,
        string role,
        string prompt,
        string grammar,
        int? maxTokens = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var ability = ResolveAbility(role);
        return await _engineRoom
            .ExtractJsonOrThrowAsync<T>(AiCaller.Plugin(pluginId), ability, prompt, grammar, ToOptions(maxTokens), cancellationToken)
            .ConfigureAwait(false);
    }

    private static AiRequestOptions? ToOptions(int? maxTokens) =>
        maxTokens is null ? null : new AiRequestOptions(MaxTokens: maxTokens);

    internal static AiAbility ResolveAbility(string role)
    {
        if (AiAbilityCatalog.TryFromPluginRole(role, out var ability))
        {
            return ability;
        }

        return role.Trim().ToLowerInvariant() switch
        {
            "audio" => throw new NotSupportedException("Plugin audio AI access is reserved for a future Whisper bridge and is not exposed through the text LLM client."),
            "vision" => throw new NotSupportedException("Plugin vision access is reserved for a future multimodal runtime and is not exposed through the text LLM client."),
            _ => throw new ArgumentOutOfRangeException(nameof(role), $"Unknown AI role '{role}'."),
        };
    }
}
