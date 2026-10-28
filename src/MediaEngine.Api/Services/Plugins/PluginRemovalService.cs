using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Removes a plugin cleanly (Plugin architecture spec C23): its files, settings, schedule, keys and logins, its own records and event
/// position, its run history and library limits, and, if the person chooses, the output it saved. A short note in Activity is kept for 30
/// days. Approval records stay as an audit trail of what was agreed.
/// </summary>
public sealed class PluginRemovalService(
    PluginCatalog catalog,
    ProviderCapabilityCatalogue sources,
    IPluginDataRepository data,
    IConfigSecretsStore secrets,
    AiFeatureRetractionService retraction,
    PluginActivityService activity,
    ILogger<PluginRemovalService> logger)
{
    /// <summary>Everything removal would do, counted. Null when the plugin is not installed.</summary>
    public async Task<PluginRemovalPreviewResponse?> PreviewAsync(string pluginId, CancellationToken ct)
    {
        var registration = catalog.Get(pluginId);
        if (registration is null)
        {
            return null;
        }

        var footprint = await data.GetFootprintAsync(registration.Manifest.Id, ct).ConfigureAwait(false);
        var resolved = PluginDeclarationRules.Resolve(registration.Manifest.Declaration, false, null, null);
        var secretsSet = await CountSecretsAsync(registration.Manifest.Id, ct).ConfigureAwait(false);
        var savedOutput = registration.Manifest.Declaration?.Stores?.SavedOutput?.Trim();
        var neededBy = PluginDependencyGraph.Dependants(registration.Manifest.Id, catalog.List()).Select(plugin => plugin.Manifest.Name).ToList();

        var preview = new PluginRemovalPreviewResponse
        {
            PluginId = registration.Manifest.Id,
            Name = registration.Manifest.Name,
            Records = footprint.Records,
            RecordBytes = footprint.RecordBytes,
            SecretsSet = secretsSet,
            SavedOutput = string.IsNullOrWhiteSpace(savedOutput) ? null : savedOutput,
            SavedOutputCount = footprint.SkipMarkers,
            DefaultOutput = resolved.RemoveOutputOnRemoval ? "remove" : "keep",
            NeededBy = neededBy,
            WouldLose = [.. catalog.DescribeRemovalImpact(registration.Manifest.Id)],
        };

        preview.Lines.Add("Its files, settings and schedule are removed.");
        if (footprint.Records > 0)
        {
            preview.Lines.Add($"{footprint.Records:N0} {(footprint.Records == 1 ? "record" : "records")} it kept for itself ({FormatBytes(footprint.RecordBytes)}) are removed.");
        }

        if (secretsSet > 0)
        {
            preview.Lines.Add($"{secretsSet} saved {(secretsSet == 1 ? "key or login" : "keys and logins")} are removed.");
        }

        if (preview.SavedOutput is not null)
        {
            preview.Lines.Add(footprint.SkipMarkers > 0
                ? $"{footprint.SkipMarkers:N0} {preview.SavedOutput} it saved can go with it or stay. Anything you confirmed or edited stays either way."
                : $"It has no unreviewed {preview.SavedOutput} saved.");
        }

        if (neededBy.Count > 0)
        {
            preview.Lines.Add($"{string.Join(" and ", neededBy)} needs this plugin and will be switched off.");
        }

        preview.Lines.AddRange(preview.WouldLose);
        preview.Lines.Add("Anything it already added to your metadata stays. Your original files are never touched.");
        return preview;
    }

    /// <summary>Removes the plugin. The caller has already dealt with plugins that need it.</summary>
    public async Task RemoveAsync(string pluginId, bool removeOutput, CancellationToken ct)
    {
        var registration = catalog.Get(pluginId)
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' is not registered.");
        var id = registration.Manifest.Id;

        catalog.DeletePlugin(id);
        await CleanDataAsync(id, removeOutput, ct).ConfigureAwait(false);

        // An AI feature's suggestions go with its plugin when the person chooses to remove its output.
        if (removeOutput)
        {
            await retraction.RetractForPluginAsync(id, ct).ConfigureAwait(false);
        }

        await activity.ForgetAsync(id, deleteHistory: false, ct).ConfigureAwait(false);
        await activity.RecordNoteAsync(new PluginActivityContext(id, registration.Manifest.Version), "Removed.", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans what plugins that are no longer installed left behind (a folder deleted by hand, an interrupted removal). Saved output stays:
    /// nobody chose to remove it.
    /// </summary>
    public async Task<int> SweepOrphansAsync(CancellationToken ct)
    {
        var swept = 0;
        foreach (var id in await data.ListPluginIdsAsync(ct).ConfigureAwait(false))
        {
            if (catalog.Get(id) is not null || sources.Sources().Any(source => string.Equals(source.Name, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            await CleanDataAsync(id, removeOutput: false, ct).ConfigureAwait(false);
            swept++;
        }

        if (swept > 0)
        {
            logger.LogInformation("Cleaned data left behind by {Count} plugins that are no longer installed", swept);
        }

        return swept;
    }

    private async Task CleanDataAsync(string pluginId, bool removeOutput, CancellationToken ct)
    {
        await data.RemoveAsync(pluginId, removeOutput, ct).ConfigureAwait(false);
        try
        {
            await secrets.RemoveAllAsync(SecretOwner.Plugin(pluginId), ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // An id that cannot name a secrets file never had one.
        }
    }

    private async Task<int> CountSecretsAsync(string pluginId, CancellationToken ct)
    {
        try
        {
            return (await secrets.ListNamesAsync(SecretOwner.Plugin(pluginId), ct).ConfigureAwait(false)).Count;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException)
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
