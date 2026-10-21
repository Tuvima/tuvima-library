using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Tools;

/// <summary>
/// Starts the download of a Required tool the Library cannot find, so a fresh install never has to wait for someone to
/// press a button. It only starts what the installer already offers on this machine (pinned 64-bit Windows build, never
/// inside a container), so every checksum, host and activity-log rule of <see cref="IToolInstallerService"/> still applies.
/// </summary>
public interface IToolAutoInstaller
{
    /// <summary>
    /// Starts a download for each Required tool in <paramref name="tools"/> that is missing and offered, and returns the
    /// tools list as it stands afterwards (a started download shows as downloading). Does nothing when
    /// <c>auto_install_required</c> is off in <c>config/tool_downloads.json</c>.
    /// </summary>
    Task<ToolRequirementsDto> StartMissingRequiredAsync(ToolRequirementsDto tools, CancellationToken ct);
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
        foreach (var tool in tools.Tools.Where(tool =>
                     tool.Kind == "tool"
                     && tool.Requirement == ToolRequirementLevels.Required
                     && tool.State == ToolRequirementStates.Missing
                     && tool.CanInstall))
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
