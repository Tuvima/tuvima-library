using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Components.Setup;

/// <summary>The one rule setup uses to decide whether to keep watching a tool download.</summary>
public static class SetupToolRules
{
    /// <summary>True while a Required tool (FFmpeg) is being downloaded by the Library.</summary>
    public static bool IsDownloadingRequiredTool(ToolRequirementsDto? tools) =>
        tools is not null
        && tools.Tools.Any(tool => tool.Requirement == ToolRequirementLevels.Required
            && tool.State == ToolRequirementStates.Downloading);
}
