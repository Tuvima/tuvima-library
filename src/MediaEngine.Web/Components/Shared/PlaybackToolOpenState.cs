namespace MediaEngine.Web.Components.Shared;

/// <summary>Which surface a tool popover opened or closed on, reported to the host that coordinates the open tools (the sleep timer menu, the Speed panel).</summary>
public sealed record PlaybackToolOpenState(string Surface, bool IsOpen);
