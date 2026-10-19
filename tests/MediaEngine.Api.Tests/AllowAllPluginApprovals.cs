using MediaEngine.Api.Services.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A plugin approval check that covers every plugin, for tests about something other than approval. Tests of the approval
/// rule itself use the real check over a real record store.
/// </summary>
internal sealed class AllowAllPluginApprovals : IPluginApprovalCheck
{
    public static AllowAllPluginApprovals Instance { get; } = new();

    public bool IsCovered(PluginManifest manifest, PluginTrustLevel trust) => true;
}
