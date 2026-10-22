using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Tools;

/// <summary>
/// Starts the download of a Required tool the Library cannot find (FFmpeg), and of ExifTool, which is Optional but marked
/// <c>auto_install</c>, so a fresh install never has to wait for someone to press a button. It only starts what the installer
/// already offers on this machine (a pinned build for this system, never inside a container), so every checksum, host and
/// activity-log rule of <see cref="IToolInstallerService"/> still applies. No other Optional tool is ever started by itself.
/// </summary>
public interface IToolAutoInstaller
{
    /// <summary>
    /// Starts a download for each Required tool in <paramref name="tools"/> that is missing and offered, and for ExifTool when it
    /// is missing, offered and marked <c>auto_install</c>. Returns the tools list as it stands afterwards (a started download
    /// shows as downloading). Does nothing when <c>auto_install_required</c> is off in <c>config/tool_downloads.json</c>.
    /// </summary>
    Task<ToolRequirementsDto> StartMissingRequiredAsync(ToolRequirementsDto tools, CancellationToken ct);
}

public static class ToolAutoInstallerExtensions
{
    /// <summary>
    /// True when the list holds a Required tool that is not ready, or any tool that is missing and could be fetched, so a caller
    /// that otherwise acts only on a Required gap also gives the auto-installer a look at ExifTool.
    /// </summary>
    public static bool MayHaveAutoInstallWork(this ToolRequirementsDto tools) =>
        tools.NeedsAttention
        || tools.Tools.Any(tool => tool.Kind == "tool" && tool.State == ToolRequirementStates.Missing && tool.CanInstall);
}

public sealed class ToolAutoInstaller(
    IToolInstallerService installer,
    IToolRequirementsService toolRequirements,
    IConfigurationLoader configuration,
    ILogger<ToolAutoInstaller> logger) : IToolAutoInstaller
{
    public async Task<ToolRequirementsDto> StartMissingRequiredAsync(ToolRequirementsDto tools, CancellationToken ct)
    {
        if (!configuration.LoadToolDownloads().AutoInstallRequired)
        {
            return tools;
        }

        var started = false;
        var downloads = configuration.LoadToolDownloads();
        foreach (var tool in tools.Tools.Where(tool =>
                     tool.Kind == "tool"
                     && tool.State == ToolRequirementStates.Missing
                     && tool.CanInstall
                     && (tool.Requirement == ToolRequirementLevels.Required
                         || (tool.Id == ToolRequirementIds.ExifTool && downloads.ExifTool is { AutoInstall: true }))))
        {
            var result = await installer.StartAsync(tool.Id, ct).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case ToolInstallOutcome.Accepted:
                    started = true;
                    logger.LogInformation("{Tool} was missing, so the Library started downloading it.", tool.Name);
                    break;
                case ToolInstallOutcome.AlreadyRunning:
                    started = true;
                    break;
                default:
                    logger.LogInformation("{Tool} is missing and could not be downloaded automatically: {Outcome}.", tool.Name, result.Outcome);
                    break;
            }
        }

        return started ? await toolRequirements.GetAsync(ct).ConfigureAwait(false) : tools;
    }
}
