using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Plugins.AiFeatures;

/// <summary>
/// Takes an AI feature's suggestions away when its plugin is switched off: what the feature published and still owns
/// is removed, and anything a person or a provider has since set is left alone.
/// </summary>
public sealed class AiFeatureRetractionService(
    IAiFeaturePersistenceRepository persistence,
    ILogger<AiFeatureRetractionService> logger)
{
    /// <summary>Retracts the results of the plugin's feature; a plugin that records none does nothing.</summary>
    public async Task<int> RetractForPluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        var retracted = 0;
        foreach (var key in AiFeaturePluginIds.ResultKeysFor(pluginId))
        {
            retracted += await persistence.RetractAiFeatureAsync(key, cancellationToken).ConfigureAwait(false);
        }

        if (retracted > 0)
        {
            logger.LogInformation("Removed the suggestions of {PluginId} from {Count} items.", pluginId, retracted);
        }

        return retracted;
    }
}
