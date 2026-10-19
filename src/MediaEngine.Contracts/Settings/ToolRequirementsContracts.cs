using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>
/// The Engine's one catalogue of the outside tools and downloads the Library depends on, with what each one
/// affects and how to fix it. The same list drives setup, the Settings page and the start-up log.
/// </summary>
/// <param name="InstallKind">How this install was set up; one of <see cref="ToolInstallKinds"/>.</param>
/// <param name="CheckedAt">When the Engine last looked.</param>
/// <param name="NeedsAttention">True when any Required row is not ready.</param>
public sealed record ToolRequirementsDto(
    [property: JsonPropertyName("install_kind")] string InstallKind,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("needs_attention")] bool NeedsAttention,
    [property: JsonPropertyName("tools")] IReadOnlyList<ToolRequirementDto> Tools);

/// <summary>
/// One tool or on-demand download. Wording is plain: <see cref="Effect"/> says what is affected, <see cref="Fix"/>
/// says what to do; neither carries file paths or error text.
/// </summary>
/// <param name="Id">A stable id, see <see cref="ToolRequirementIds"/>.</param>
/// <param name="Kind"><c>tool</c> or <c>ai_model</c>.</param>
/// <param name="Requirement">One of <see cref="ToolRequirementLevels"/>.</param>
/// <param name="State">One of <see cref="ToolRequirementStates"/>.</param>
/// <param name="StateReason">A short plain reason when the state needs one (for example a chosen location that no longer has the tool).</param>
/// <param name="WaitingFiles">How many files are held back until this tool is ready.</param>
/// <param name="CanLocate">The person may point the Library at the tool themselves.</param>
/// <param name="CanInstall">The Library can fetch and install the tool on request.</param>
/// <param name="CanDownloadAgain">The Library can download this item again on request.</param>
/// <param name="DownloadPercent">Progress while <c>downloading</c>.</param>
public sealed record ToolRequirementDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requirement")] string Requirement,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("effect")] string Effect,
    [property: JsonPropertyName("state_reason")] string? StateReason,
    [property: JsonPropertyName("fix")] string Fix,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("waiting_files")] int WaitingFiles,
    [property: JsonPropertyName("can_locate")] bool CanLocate,
    [property: JsonPropertyName("can_install")] bool CanInstall,
    [property: JsonPropertyName("can_download_again")] bool CanDownloadAgain,
    [property: JsonPropertyName("download_percent")] int? DownloadPercent);

public static class ToolRequirementIds
{
    public const string Ffmpeg = "ffmpeg";
    public const string AdaptiveStreaming = "adaptive_streaming";
    public const string Tesseract = "tesseract";
    public const string ImageEngine = "image_engine";
    public const string AiRuntime = "ai_runtime";

    /// <summary>Prefix of an AI model row id; the model's role follows it.</summary>
    public const string AiModelPrefix = "ai_model:";
}

public static class ToolRequirementStates
{
    public const string Ready = "ready";
    public const string Missing = "missing";

    /// <summary>The tool was found but did not start or answer.</summary>
    public const string Problem = "problem";

    public const string Downloading = "downloading";

    /// <summary>Downloaded and waiting for its installer to be finished.</summary>
    public const string InstallerReady = "installer_ready";
}

public static class ToolRequirementLevels
{
    public const string Required = "required";
    public const string Optional = "optional";
}

public static class ToolInstallKinds
{
    public const string WindowsInstaller = "windows_installer";
    public const string Docker = "docker";
    public const string Source = "source";
}
