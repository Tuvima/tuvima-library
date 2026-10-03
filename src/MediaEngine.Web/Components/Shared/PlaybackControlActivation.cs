using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Shared;

public sealed record PlaybackControlActivation(
    PlaybackControlDefinition Control,
    ElementReference Trigger);
