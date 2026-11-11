using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Player;

/// <summary>
/// What the "..." menu hands a panel its host draws for it (Playback settings for music and audiobooks): whether to be open, how to report
/// being opened or closed, and the "..." button to open from, so the panel sits above the bar beside it and focus returns to it.
/// </summary>
public sealed record PlaybackAnchoredPanel(bool Open, EventCallback<bool> OpenChanged, ElementReference? Trigger);
