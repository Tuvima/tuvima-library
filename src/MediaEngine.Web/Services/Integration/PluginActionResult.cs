using MediaEngine.Contracts.Approvals;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// The outcome of a plugin action. <see cref="Message"/> carries the Engine's plain-English reason when it
/// refused (for example a plugin that does not fit this Library version), so the page can show it.
/// <see cref="Approval"/> is set when the Engine wants the person to confirm what the plugin can do first: show each
/// preview, then repeat the action with the confirmed hash.
/// </summary>
public sealed record PluginActionResult(bool Succeeded, string? Message = null, ApprovalRequiredResponse? Approval = null)
{
    public static PluginActionResult Success { get; } = new(true);

    public static PluginActionResult Failure(string? message = null) => new(false, message);

    public static PluginActionResult ApprovalRequired(ApprovalRequiredResponse approval) => new(false, null, approval);
}
