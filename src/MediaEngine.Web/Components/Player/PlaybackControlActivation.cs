using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Player;

public sealed record PlaybackControlActivation(
    PlaybackControlDefinition Control,
    ElementReference Trigger);
