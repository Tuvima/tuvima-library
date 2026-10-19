namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// The outcome of a plugin action. <see cref="Message"/> carries the Engine's plain-English reason when it
/// refused (for example a plugin that does not fit this Library version), so the page can show it.
/// </summary>
public sealed record PluginActionResult(bool Succeeded, string? Message = null)
{
    public static PluginActionResult Success { get; } = new(true);

    public static PluginActionResult Failure(string? message = null) => new(false, message);
}
