using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Components.Setup;

/// <summary>The rules setup uses to watch tool downloads and to choose which tools it lists.</summary>
public static class SetupToolRules
{
    /// <summary>True while a Required tool (FFmpeg) is being downloaded by the Library.</summary>
    public static bool IsDownloadingRequiredTool(ToolRequirementsDto? tools) =>
        tools is not null
        && tools.Tools.Any(tool => tool.Requirement == ToolRequirementLevels.Required
            && tool.State == ToolRequirementStates.Downloading);

    /// <summary>True once FFmpeg is installed and working, which is when the Graphics hardware row can be filled in.</summary>
    public static bool IsFfmpegReady(ToolRequirementsDto? tools) =>
        tools is not null
        && tools.Tools.Any(tool => tool.Id == ToolRequirementIds.Ffmpeg && tool.State == ToolRequirementStates.Ready);

    /// <summary>
    /// The tools setup lists: every tool, ready or not. Local AI is left out on purpose: its engine is installed by a script and its
    /// models are chosen in Settings after setup.
    /// </summary>
    public static IReadOnlyList<ToolRequirementDto> ListedTools(ToolRequirementsDto? tools) =>
        tools?.Tools.Where(tool => tool.Kind == "tool" && tool.Id != ToolRequirementIds.AiRuntime).ToList() ?? [];
}
