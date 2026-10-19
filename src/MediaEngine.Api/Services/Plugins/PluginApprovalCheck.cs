using MediaEngine.Api.Services.Approvals;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Whether an administrator has approved a plugin's current statement (what it may do and where its data goes). The
/// catalog asks this every time it loads, so a plugin whose manifest gained a permission stops running until it is
/// approved again, whichever way the manifest changed.
/// </summary>
public interface IPluginApprovalCheck
{
    /// <summary>True when an approval record holds exactly the statement the plugin has now.</summary>
    bool IsCovered(PluginManifest manifest, PluginTrustLevel trust);
}

/// <summary>Reads the approval record. Anything it cannot read counts as not approved.</summary>
internal sealed class PluginApprovalCheck(IApprovalRecordRepository records, ILogger<PluginApprovalCheck> logger) : IPluginApprovalCheck
{
    public bool IsCovered(PluginManifest manifest, PluginTrustLevel trust)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var hash = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(manifest, trust));
        try
        {
            // The record store answers synchronously (a local SQLite read), so this does not wait on anything.
            return records.ExistsAsync(ApprovalKinds.Plugin, manifest.Id, hash).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The approval record for plugin {PluginId} could not be read; treating it as not approved", manifest.Id);
            return false;
        }
    }
}
