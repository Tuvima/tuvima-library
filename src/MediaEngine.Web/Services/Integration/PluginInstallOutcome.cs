using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// The outcome of installing or updating a plugin from the directory: the Engine's answer when it worked, otherwise
/// its plain-English reason (a package that did not check out, no connection) in <see cref="Problem"/>.
/// </summary>
public sealed record PluginInstallOutcome(PluginInstallResponse? Result, string? Problem = null)
{
    public bool Succeeded => Result is not null;
}
