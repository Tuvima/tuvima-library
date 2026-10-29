using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Components.Setup;

/// <summary>What a tool row in setup can offer next.</summary>
public enum SetupToolAction
{
    None,
    Download,
    RunInstaller,
}

/// <summary>The rules setup uses to watch tool downloads and to word every tool's state in one place.</summary>
public static class SetupToolRules
{
    /// <summary>True while a Required tool (FFmpeg) is being downloaded by the Library.</summary>
    public static bool IsDownloadingRequiredTool(ToolRequirementsDto? tools) =>
        tools is not null
        && tools.Tools.Any(tool => tool.Requirement == ToolRequirementLevels.Required
            && tool.State == ToolRequirementStates.Downloading);

    /// <summary>
    /// The tools setup lists: every tool, ready or not. Local AI is left out on purpose: its engine is installed by a script and its
    /// models are chosen in Settings after setup.
    /// </summary>
    public static IReadOnlyList<ToolRequirementDto> ListedTools(ToolRequirementsDto? tools) =>
        tools?.Tools.Where(tool => tool.Kind == "tool" && tool.Id != ToolRequirementIds.AiRuntime).ToList() ?? [];

    /// <summary>The one sentence a tool shows under its name. It depends on the tool's state alone.</summary>
    public static string StateLine(ToolRequirementDto tool) => tool.State switch
    {
        ToolRequirementStates.Ready => string.IsNullOrWhiteSpace(tool.Version) ? "Ready." : $"Ready. Version {tool.Version}.",
        ToolRequirementStates.Downloading => tool.DownloadPercent is { } percent
            ? $"Downloading ({percent}%). Library finishes this in the background."
            : "Downloading. Library finishes this in the background.",
        ToolRequirementStates.InstallerReady => $"Downloaded. One more step: run its installer.",
        ToolRequirementStates.Problem => "Found, but it did not start. You can check it later in Settings, System, Configuration.",
        _ => tool.Requirement == ToolRequirementLevels.Required
            ? $"{tool.Effect} Install it from Settings, System, Configuration."
            : $"{tool.Effect} You can download it now or later in Settings, System, Configuration.",
    };

    /// <summary>The short word on the right of a tool row.</summary>
    public static string BadgeText(ToolRequirementDto tool) => tool.State switch
    {
        ToolRequirementStates.Ready => "Ready",
        ToolRequirementStates.Downloading => "Downloading",
        ToolRequirementStates.InstallerReady => "One more step",
        ToolRequirementStates.Problem => "Needs a look",
        _ => "Not installed",
    };

    /// <summary>The button a row offers. Required tools are installed from Settings, so only optional ones offer a download here.</summary>
    public static SetupToolAction Action(ToolRequirementDto tool) => tool.State switch
    {
        ToolRequirementStates.InstallerReady => SetupToolAction.RunInstaller,
        ToolRequirementStates.Missing when tool.Requirement == ToolRequirementLevels.Optional => SetupToolAction.Download,
        _ => SetupToolAction.None,
    };
}
